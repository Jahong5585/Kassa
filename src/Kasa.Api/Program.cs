using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Kasa.Api;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using Microsoft.OpenApi;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var development = builder.Environment.IsDevelopment();
var developmentKey = configuration["Auth:DevelopmentKey"];
var authority = configuration["Auth:Authority"];
var audience = configuration["Auth:Audience"];
var adminAcr = configuration["Auth:AdminAcr"];
if (string.IsNullOrWhiteSpace(audience) ||
    (developmentKey is null && (!Uri.TryCreate(authority, UriKind.Absolute, out var issuer) || issuer.Scheme != "https")))
    throw new InvalidOperationException("Configure Auth:Audience and an HTTPS Auth:Authority.");
if (developmentKey is not null && (!development || Encoding.UTF8.GetByteCount(developmentKey) < 32))
    throw new InvalidOperationException("Development JWT keys must have at least 32 bytes and are development-only.");

if (!development && string.IsNullOrWhiteSpace(adminAcr))
    throw new InvalidOperationException("Configure Auth:AdminAcr to require the provider's WebAuthn assurance level.");

var connectionString = configuration.GetConnectionString("Pos")
    ?? throw new InvalidOperationException("Configure ConnectionStrings:Pos.");
var procurementAiEndpoint = configuration["Ai:Endpoint"] ?? "https://api.groq.com/openai/v1/";
if (!Uri.TryCreate(procurementAiEndpoint, UriKind.Absolute, out var procurementAiUri) ||
    procurementAiUri.Scheme is not ("http" or "https") ||
    (!development && (procurementAiUri.Scheme != "https" || procurementAiUri.Host != "api.groq.com")))
    throw new InvalidOperationException("Configure Ai:Endpoint as Groq HTTPS. Local HTTP is development-only.");
builder.Services.AddSingleton(_ => NpgsqlDataSource.Create(connectionString));
builder.Services.AddSingleton<InvoiceScanner>();
builder.Services.AddScoped<PosService>();
builder.Services.AddHttpClient<ProcurementExplainer>(client =>
{
    client.BaseAddress = procurementAiUri;
    client.Timeout = TimeSpan.FromSeconds(10);
    client.MaxResponseContentBufferSize = 32 * 1024;
})
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false })
    .RedactLoggedHeaders(_ => true);
builder.Services.AddHostedService<ReplenishmentWorker>();
builder.Services.AddHostedService<StockMovementExportWorker>();
builder.Services.AddProblemDetails(options =>
    options.CustomizeProblemDetails = context =>
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier);
builder.Services.ConfigureHttpJsonOptions(options =>
{
    options.SerializerOptions.UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow;
    options.SerializerOptions.NumberHandling = JsonNumberHandling.Strict;
});
builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(context =>
        RateLimitPartition.GetFixedWindowLimiter(
            context.User.FindFirst("sub")?.Value ?? context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = 180, Window = TimeSpan.FromMinutes(1) }));
    options.AddPolicy("procurement-ai", context => RateLimitPartition.GetFixedWindowLimiter(
        context.User.FindFirst("sub")?.Value ?? "unknown",
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 3, Window = TimeSpan.FromMinutes(1) }));
});
builder.Services.AddOpenApi(options => options.AddDocumentTransformer((document, _, _) =>
{
    document.Info.Title = "Kasa POS API";
    document.Components ??= new OpenApiComponents();
    document.Components.SecuritySchemes = new Dictionary<string, IOpenApiSecurityScheme>
    {
        ["Bearer"] = new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" },
        ["RegisterId"] = new OpenApiSecurityScheme
            { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Register-Id" },
        ["RegisterKey"] = new OpenApiSecurityScheme
            { Type = SecuritySchemeType.ApiKey, In = ParameterLocation.Header, Name = "X-Register-Key" }
    };
    foreach (var (path, pathItem) in document.Paths)
    {
        if (!path.StartsWith("/v1/", StringComparison.Ordinal) || pathItem.Operations is null)
            continue;
        foreach (var operation in pathItem.Operations.Values)
        {
            var security = new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            };
            if (!path.StartsWith("/v1/reports/", StringComparison.Ordinal) &&
                !path.StartsWith("/v1/stores/", StringComparison.Ordinal))
            {
                security[new OpenApiSecuritySchemeReference("RegisterId", document)] = [];
                security[new OpenApiSecuritySchemeReference("RegisterKey", document)] = [];
            }
            operation.Security = [security];
            if (operation.OperationId == "UploadInvoiceFile")
            {
                operation.RequestBody = new OpenApiRequestBody
                {
                    Required = true,
                    Content = new[] { "application/pdf", "image/jpeg", "image/png" }.ToDictionary(
                        type => type, _ => new OpenApiMediaType
                        {
                            Schema = new OpenApiSchema { Type = JsonSchemaType.String, Format = "binary" }
                        })
                };
            }
        }
    }
    return Task.CompletedTask;
}));
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer(options =>
{
    options.MapInboundClaims = false;
    options.SaveToken = false;
    options.Audience = audience;
    options.TokenValidationParameters.ClockSkew = TimeSpan.FromSeconds(30);
    if (developmentKey is null)
    {
        options.Authority = authority;
        options.TokenValidationParameters.ValidIssuer = authority;
    }
    else
    {
        options.TokenValidationParameters.ValidIssuer = "kasa-local";
        options.TokenValidationParameters.IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(developmentKey));
        options.TokenValidationParameters.ValidAlgorithms = [SecurityAlgorithms.HmacSha256];
    }
});
builder.Services.AddAuthorizationBuilder().SetFallbackPolicy(
    new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

var app = builder.Build();
app.UseExceptionHandler();
app.UseStatusCodePages();
if (!development)
{
    app.Use(async (context, next) =>
    {
        if (!context.Request.IsHttps)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        await next(context);
    });
}
app.Use(async (context, next) =>
{
    context.Response.Headers.CacheControl = "no-store";
    context.Response.Headers["X-Request-Id"] = context.TraceIdentifier;
    try
    {
        await next(context);
    }
    catch (BadHttpRequestException ex) when (!context.Response.HasStarted)
    {
        await WriteError(context, ex.StatusCode, "invalid_http_request");
    }
    catch (PosError ex) when (!context.Response.HasStarted)
    {
        await WriteError(context, ex.Status, ex.Code, ex.ImportRow);
    }
    catch (NpgsqlException ex) when (ex.IsTransient && !context.Response.HasStarted)
    {
        app.Logger.LogWarning("Transient database error for request {TraceId}", context.TraceIdentifier);
        await WriteError(context, StatusCodes.Status503ServiceUnavailable, "database_temporarily_unavailable");
    }
});
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();
app.MapGet("/health/live", () => TypedResults.Ok(new { status = "ok" })).AllowAnonymous();
app.MapOpenApi();

var api = app.MapGroup("/v1").RequireAuthorization()
    .ProducesProblem(400).ProducesProblem(401).ProducesProblem(403).ProducesProblem(404)
    .ProducesProblem(409).ProducesProblem(429).ProducesProblem(503);
api.MapPost("/shifts", async (OpenShift request, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.OpenShift(context, request, ct))).WithName("OpenShift");
api.MapPost("/shifts/{id:guid}/close", async (Guid id, CloseShift request, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.CloseShift(context, id, request, ct))).WithName("CloseShift");
api.MapGet("/shifts/{id:guid}", async (Guid id, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetShift(context, id, ct))).WithName("ReadCashierShift");
api.MapGet("/reports/stores/{storeId:guid}/registers/{registerId:guid}/shifts/{id:guid}",
    async (Guid storeId, Guid registerId, Guid id, PosService pos, HttpContext context, CancellationToken ct) =>
        TypedResults.Ok(await pos.GetAdminShift(context, storeId, registerId, id, ct))).WithName("ReadAdminShift");
var salesReport = api.MapGroup("/reports/stores/{storeId:guid}/sales");
salesReport.MapGet("", async (Guid storeId, DateOnly from, DateOnly to, Guid? registerId, Guid? cashierId,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetSalesPeriod(context, storeId, from, to, registerId, cashierId, ct)))
    .WithName("ReadSalesPeriod");
salesReport.MapGet("/csv", async (Guid storeId, DateOnly from, DateOnly to, Guid? registerId, Guid? cashierId,
    PosService pos, HttpContext context, CancellationToken ct) =>
{
    var report = await pos.GetSalesPeriod(context, storeId, from, to, registerId, cashierId, ct, export: true);
    context.Response.Headers.XContentTypeOptions = "nosniff";
    var filename = FormattableString.Invariant($"sales-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.csv");
    return TypedResults.File(PosService.FormatSalesPeriodCsv(report), "text/csv; charset=utf-8", filename);
}).WithName("DownloadSalesPeriodCsv").Produces<string>(200, contentType: "text/csv");
var profitReport = api.MapGroup("/reports/stores/{storeId:guid}/profit");
profitReport.MapGet("", async (Guid storeId, DateOnly from, DateOnly to,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetProfitReport(context, storeId, from, to, ct))).WithName("ReadProfitReport");
profitReport.MapGet("/csv", async (Guid storeId, DateOnly from, DateOnly to,
    PosService pos, HttpContext context, CancellationToken ct) =>
{
    var report = await pos.GetProfitReport(context, storeId, from, to, ct, export: true);
    context.Response.Headers.XContentTypeOptions = "nosniff";
    var filename = FormattableString.Invariant($"profit-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.csv");
    return TypedResults.File(PosService.FormatProfitReportCsv(report), "text/csv; charset=utf-8", filename);
}).WithName("DownloadProfitReportCsv").Produces<string>(200, contentType: "text/csv");
var stockMovements = api.MapGroup("/reports/stores/{storeId:guid}/stock-movements");
stockMovements.MapGet("", async (Guid storeId, DateOnly from, DateOnly to, Guid? productId, Guid? lotId,
    string? kind, string? after, int? limit, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetStockMovementReport(context, storeId, from, to, productId, lotId, kind, after, limit ?? 50, ct)))
    .WithName("ReadStockMovementReport");
stockMovements.MapGet("/csv", async (Guid storeId, DateOnly from, DateOnly to, Guid? productId, Guid? lotId,
    string? kind, PosService pos, HttpContext context, CancellationToken ct) =>
{
    var report = await pos.GetStockMovementReport(context, storeId, from, to, productId, lotId, kind, null, 50, ct, export: true);
    context.Response.Headers.XContentTypeOptions = "nosniff";
    var filename = FormattableString.Invariant($"stock-movements-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.csv");
    return TypedResults.File(PosService.FormatStockMovementCsv(report), "text/csv; charset=utf-8", filename);
}).WithName("DownloadStockMovementCsv").Produces<string>(200, contentType: "text/csv");
var stockMovementExports = stockMovements.MapGroup("/exports");
stockMovementExports.MapPost("", async (Guid storeId, CreateStockMovementExport request, PosService pos,
    HttpContext context, CancellationToken ct) =>
{
    var job = await pos.QueueStockMovementExport(context, storeId, request, ct);
    return TypedResults.Accepted($"/v1/reports/stores/{storeId}/stock-movements/exports/{job.OperationId}", job);
}).WithName("QueueStockMovementExport").ProducesProblem(409);
stockMovementExports.MapGet("/{id:guid}", async (Guid storeId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.GetStockMovementExport(context, storeId, id, ct)))
    .WithName("ReadStockMovementExport").ProducesProblem(404);
stockMovementExports.MapGet("/{id:guid}/file", async (Guid storeId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) =>
{
    var file = await pos.DownloadStockMovementExport(context, storeId, id, ct);
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return TypedResults.File(file, "text/csv; charset=utf-8", $"stock-movements-{id}.csv");
}).WithName("DownloadStockMovementExport").Produces<string>(200, contentType: "text/csv")
    .ProducesProblem(404).ProducesProblem(409).ProducesProblem(410);
var stockLosses = api.MapGroup("/reports/stores/{storeId:guid}/stock-losses");
stockLosses.MapGet("", async (Guid storeId, DateOnly from, DateOnly to, Guid? productId, string? after, int? limit,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetStockLossReport(context, storeId, from, to, productId, after, limit ?? 50, ct)))
    .WithName("ReadStockLossReport");
stockLosses.MapGet("/csv", async (Guid storeId, DateOnly from, DateOnly to, Guid? productId,
    PosService pos, HttpContext context, CancellationToken ct) =>
{
    var report = await pos.GetStockLossReport(context, storeId, from, to, productId, null, 50, ct, export: true);
    context.Response.Headers.XContentTypeOptions = "nosniff";
    var filename = FormattableString.Invariant($"stock-losses-{from:yyyy-MM-dd}-{to:yyyy-MM-dd}.csv");
    return TypedResults.File(PosService.FormatStockLossCsv(report), "text/csv; charset=utf-8", filename);
}).WithName("DownloadStockLossReportCsv").Produces<string>(200, contentType: "text/csv");
var stockExpiry = api.MapGroup("/reports/stores/{storeId:guid}/stock-expiry");
stockExpiry.MapGet("", async (Guid storeId, string? filter, int? offset, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.GetStockExpiryReport(context, storeId, filter, offset ?? 0, ct)))
    .WithName("ReadStockExpiryReport");
stockExpiry.MapGet("/csv", async (Guid storeId, string? filter, PosService pos, HttpContext context, CancellationToken ct) =>
{
    var report = await pos.GetStockExpiryReport(context, storeId, filter, 0, ct, export: true);
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return TypedResults.File(PosService.FormatStockExpiryCsv(report), "text/csv; charset=utf-8",
        $"stock-expiry-{report.Filter}.csv");
}).WithName("DownloadStockExpiryCsv").Produces<string>(200, contentType: "text/csv");
api.MapGet("/reports/stores/{storeId:guid}/stock-valuation", async (Guid storeId, Guid? productId, int? offset,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetStockValuation(context, storeId, productId, offset ?? 0, ct)))
    .WithName("ReadStockValuation");
api.MapGet("/reports/stores/{storeId:guid}/stock-valuation/csv", async (Guid storeId, Guid? productId,
    PosService pos, HttpContext context, CancellationToken ct) =>
{
    var report = await pos.GetStockValuation(context, storeId, productId, 0, ct, export: true);
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return TypedResults.File(PosService.FormatStockValuationCsv(report), "text/csv; charset=utf-8",
        $"stock-valuation-{storeId}.csv");
}).WithName("DownloadStockValuationCsv").Produces<string>(200, contentType: "text/csv");
var stockSnapshots = api.MapGroup("/reports/stores/{storeId:guid}/stock-valuation/snapshots");
stockSnapshots.MapPost("", async (Guid storeId, CaptureStockSnapshot request, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.CaptureStockSnapshot(context, storeId, request, ct)))
    .WithName("CaptureStockSnapshot");
stockSnapshots.MapGet("", async (Guid storeId, DateOnly date, Guid? productId, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetStockSnapshot(context, storeId, date, productId, offset ?? 0, ct)))
    .WithName("ReadStockSnapshot");
api.MapGet("/reports/stores/{storeId:guid}/supplier-settlements", async (Guid storeId, string? supplierCode,
    int? offset, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetSupplierSettlementReport(context, storeId, supplierCode, offset ?? 0, ct)))
    .WithName("ReadSupplierSettlementReport");
api.MapGet("/reports/stores/{storeId:guid}/exceptions", async (Guid storeId, string? code, int? offset, int? limit,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetStoreExceptions(context, storeId, code, offset ?? 0, limit ?? 50, ct)))
    .WithName("ReadStoreExceptionReport");
api.MapGet("/catalog/barcodes/{barcode}", async (string barcode, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetProduct(context, barcode, ct))).WithName("FindProductByBarcode");
api.MapGet("/catalog/search", async (string q, int? offset, PosService pos, HttpContext context,
    CancellationToken ct) =>
    TypedResults.Ok(await pos.SearchCashierProducts(context, q, offset, ct))).WithName("SearchCashierProducts");
api.MapGet("/catalog/products/{id:guid}", async (Guid id, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetProductById(context, id, ct))).WithName("ReadCashierProduct");
var checkoutShifts = api.MapGroup("/shifts/{shiftId:guid}");
checkoutShifts.MapPost("/cash-movements", async (Guid shiftId, CreateCashMovement request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveCashMovement(context, shiftId, request, ct))).WithName("SaveCashMovement");
checkoutShifts.MapGet("/cash-movements", async (Guid shiftId, int? offset, PosService pos, HttpContext context,
    CancellationToken ct) =>
    TypedResults.Ok(await pos.ListCashierMovements(context, shiftId, offset, ct))).WithName("ListCashierMovements");
checkoutShifts.MapGet("/carts", async (Guid shiftId, bool? includeInactive, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListParkedCarts(context, shiftId, includeInactive, offset, ct))).WithName("ListParkedCarts");
checkoutShifts.MapGet("/carts/{id:guid}", async (Guid shiftId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) =>
    TypedResults.Ok(await pos.ReviewCart(context, shiftId, id, ct))).WithName("ReviewCart");
checkoutShifts.MapPut("/carts/{id:guid}", async (Guid shiftId, Guid id, SaveParkedCart request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveCart(context, shiftId, id, request, ct))).WithName("SaveCart");
checkoutShifts.MapPost("/carts/{id:guid}/cancel", async (Guid shiftId, Guid id, CancelParkedCart request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.CancelCart(context, shiftId, id, request, ct))).WithName("CancelCart");
api.MapGet("/sales/history", async (Guid? receiptId, Guid? shiftId, DateOnly? date, decimal? amount, int? offset,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetCashierSales(context, receiptId, shiftId, date, amount, offset, ct))).WithName("CashierSalesHistory");
api.MapPost("/sales", async (CreateSale request, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.CreateSale(context, request, ct))).WithName("CommitSale").WithDescription(
        "Use cash with tendered, or manualCashless with tendered null. Retry the same operationId with the same data.");
api.MapGet("/sales/{id:guid}", async (Guid id, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetSale(context, id, ct))).WithName("ReconcileSale").WithDescription("Read the saved operation before attempting to resubmit.");
api.MapPost("/register/diagnostics", async (ReportRegisterDiagnostic request, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.ReportRegisterDiagnostic(context, request, ct)))
    .WithName("ReportRegisterDiagnostic").ProducesProblem(400).ProducesProblem(403).ProducesProblem(404).ProducesProblem(409);
api.MapGet("/sales/{sourceRegisterId:guid}/{id:guid}/refund-options",
    async (Guid sourceRegisterId, Guid id, PosService pos, HttpContext context, CancellationToken ct) =>
        TypedResults.Ok(await pos.GetRefundOptions(context, sourceRegisterId, id, ct))).WithName("ReadRefundOptions");
api.MapPost("/refunds", async (CreateRefund request, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.CreateRefund(context, request, ct))).WithName("CommitRefund");
api.MapGet("/refunds/{id:guid}", async (Guid id, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetRefund(context, id, ct))).WithName("ReconcileRefund");

api.MapGet("/expiry/cashier", async (string? filter, int? offset, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetCashierExpiry(context, filter, offset, ct))).WithName("ListCashierExpiry");
api.MapPost("/expiry/cashier/shelf-tasks", async (CreateShelfTask request, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.MarkShelfRemoval(context, request, ct))).WithName("MarkShelfRemoval");

var storeApi = api.MapGroup("/stores/{storeId:guid}");
storeApi.MapGet("/audit", async (Guid storeId, DateOnly from, DateOnly to, string? action, string? after, int? limit,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetStoreAudit(context, storeId, from, to, action, after, limit ?? 50, ct)))
    .WithName("ReadStoreAudit");
var storeStaff = storeApi.MapGroup("/staff");
storeStaff.MapGet("", async (Guid storeId, int? offset, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListStoreStaff(context, storeId, offset, ct))).WithName("ListStoreStaff");
storeStaff.MapPost("", async (Guid storeId, ConnectStoreStaff request, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.ConnectStaffToStore(context, storeId, request, ct)))
    .WithName("ConnectStoreStaff");
storeStaff.MapPost("/{id:guid}/revoke", async (Guid storeId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.RevokeStoreStaff(context, storeId, id, ct)))
    .WithName("RevokeStoreStaff");
var registerDevices = storeApi.MapGroup("/registers");
registerDevices.MapGet("", async (Guid storeId, int? offset, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.ListRegisterDevices(context, storeId, offset, ct)))
    .WithName("ListRegisterDevices");
registerDevices.MapGet("/{id:guid}", async (Guid storeId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.GetRegisterDevice(context, storeId, id, ct)))
    .WithName("ReadRegisterDevice");
registerDevices.MapPost("", async (Guid storeId, RegisterNewDevice request, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.RegisterDevice(context, storeId, request, ct)))
    .WithName("RegisterDevice");
registerDevices.MapPost("/{id:guid}/rotate-key", async (Guid storeId, Guid id, RotateDeviceKey request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.RotateRegisterDeviceKey(context, storeId, id, request, ct))).WithName("RotateRegisterKey");
registerDevices.MapPost("/{id:guid}/revoke", async (Guid storeId, Guid id, RevokeDevice request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.RevokeRegisterDevice(context, storeId, id, request, ct))).WithName("RevokeRegisterDevice");
storeApi.MapGet("/sales/history", async (Guid storeId, Guid? registerId, Guid? cashierId, Guid? receiptId,
    Guid? shiftId, DateOnly? date, decimal? amount, int? offset, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetAdminSales(context, storeId, registerId, cashierId, receiptId, shiftId, date, amount, offset, ct)))
    .WithName("AdminSalesHistory");
storeApi.MapGet("/sales/{registerId:guid}/{id:guid}", async (Guid storeId, Guid registerId, Guid id, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetAdminSale(context, storeId, registerId, id, ct))).WithName("ReadAdminSale");
storeApi.MapGet("/registers/{registerId:guid}/shifts/{shiftId:guid}/cash-movements", async (Guid storeId,
    Guid registerId, Guid shiftId, int? offset, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListAdminMovements(context, storeId, registerId, shiftId, offset, ct)))
    .WithName("ListAdminMovements");
storeApi.MapGet("/sales/{sourceRegisterId:guid}/{id:guid}/refund-options",
    async (Guid storeId, Guid sourceRegisterId, Guid id, PosService pos, HttpContext context, CancellationToken ct) =>
        TypedResults.Ok(await pos.GetAdminRefundOptions(context, storeId, sourceRegisterId, id, ct)))
    .WithName("ReadAdminRefundOptions");
storeApi.MapPost("/refund-approvals", async (Guid storeId, ApproveRefund request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ApproveRefund(context, storeId, request, ct))).WithName("ApproveRefund");
storeApi.MapGet("/receiving/catalog/barcodes/{barcode}", async (Guid storeId, string barcode, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetReceivingProduct(context, storeId, barcode, ct))).WithName("ReadReceivingProduct");
storeApi.MapPost("/purchase-orders", async (Guid storeId, CreatePurchaseOrder request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.CreatePurchaseOrder(context, storeId, request, ct))).WithName("CreatePurchaseOrder");
storeApi.MapGet("/purchase-orders", async (Guid storeId, bool? openOnly, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListPurchaseOrders(context, storeId, openOnly, offset, ct))).WithName("ListPurchaseOrders");
storeApi.MapGet("/purchase-orders/{id:guid}", async (Guid storeId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) =>
    TypedResults.Ok(await pos.GetPurchaseOrder(context, storeId, id, ct))).WithName("ReadPurchaseOrder");
storeApi.MapPost("/receipts", async (Guid storeId, CreateReceipt request, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.CreateReceipt(context, storeId, request, ct))).WithName("CreateReceipt");
storeApi.MapGet("/receipts", async (Guid storeId, string? status, int? offset, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.ListReceipts(context, storeId, status, offset, ct))).WithName("ListReceipts");
storeApi.MapGet("/receipts/{id:guid}", async (Guid storeId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.GetReceipt(context, storeId, id, ct))).WithName("ReadReceipt");
storeApi.MapPut("/receipts/{id:guid}", async (Guid storeId, Guid id, EditReceipt request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.EditReceipt(context, storeId, id, request, ct))).WithName("EditReceipt");
storeApi.MapPost("/receipts/{id:guid}/submit", async (Guid storeId, Guid id, ReceiptTransition request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SubmitReceipt(context, storeId, id, request, ct))).WithName("SubmitReceipt");
storeApi.MapPost("/receipts/{id:guid}/return-to-draft", async (Guid storeId, Guid id, ReturnReceipt request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ReturnReceipt(context, storeId, id, request, ct))).WithName("ReturnReceiptToDraft");
storeApi.MapPost("/receipts/{id:guid}/post", async (Guid storeId, Guid id, ReceiptTransition request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.PostReceipt(context, storeId, id, request, ct))).WithName("PostReceipt");
storeApi.MapPost("/receipts/{id:guid}/cancel", async (Guid storeId, Guid id, CancelReceipt request, PosService pos,
    HttpContext context, CancellationToken ct) => TypedResults.Ok(await pos.CancelReceipt(context, storeId, id, request, ct)))
    .WithName("CancelReceipt");
storeApi.MapGet("/suppliers", async (Guid storeId, bool? activeOnly, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListSuppliers(context, storeId, activeOnly, offset, ct))).WithName("ListSuppliers");
storeApi.MapGet("/suppliers/{code}", async (Guid storeId, string code, PosService pos, HttpContext context,
    CancellationToken ct) =>
    TypedResults.Ok(await pos.GetSupplier(context, storeId, code, ct))).WithName("ReadSupplier");
storeApi.MapPut("/suppliers/{code}", async (Guid storeId, string code, WriteSupplier request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.WriteSupplier(context, storeId, code, request, ct))).WithName("WriteSupplier");

var procurement = storeApi.MapGroup("/procurement");
procurement.MapGet("/replenishment-policies/{code}/{sku}", async (Guid storeId, string code, string sku,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetReplenishmentPolicy(context, storeId, code, sku, ct))).WithName("ReadReplenishmentPolicy");
procurement.MapPut("/replenishment-policies/{code}/{sku}", async (Guid storeId, string code, string sku,
    WriteReplenishmentPolicy request, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveReplenishmentPolicy(context, storeId, code, sku, request, ct))).WithName("SaveReplenishmentPolicy");
procurement.MapGet("/recommendations/{code}/{sku}/snapshot", async (Guid storeId, string code, string sku,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetReplenishmentSnapshot(context, storeId, code, sku, ct))).WithName("ReadReplenishmentSnapshot");
procurement.MapGet("/recommendations/{code}/{sku}", async (Guid storeId, string code, string sku,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.RecommendFromPolicy(context, storeId, code, sku, ct))).WithName("RecommendFromPolicy");
procurement.MapPost("/recommendations/{code}/{sku}", async (Guid storeId, string code, string sku,
    ReplenishmentRequest request, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.RecommendPurchase(context, storeId, code, sku, request, ct))).WithName("RecommendPurchase");
procurement.MapPost("/recommendations/{code}/{sku}/explain", async (Guid storeId, string code, string sku,
    string? language, PosService pos, ProcurementExplainer explainer, HttpContext context, CancellationToken ct) =>
{
    var languageName = language switch
    {
        null or "ru" => "Russian",
        "uz" => "Uzbek in Latin script",
        _ => throw new PosError(400, "unsupported_explanation_language")
    };
    var recommendation = await pos.RecommendFromPolicy(context, storeId, code, sku, ct, requireAiAccess: true);
    return TypedResults.Ok(await explainer.Explain(recommendation, languageName, ct));
}).WithName("ExplainPurchaseRecommendation").RequireRateLimiting("procurement-ai");
var stockoutDays = procurement.MapGroup("/products/{productId:guid}/stockout-days");
stockoutDays.MapGet("/{day}", async (Guid storeId, Guid productId, DateOnly day, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetStockoutDay(context, storeId, productId, day, ct))).WithName("ReadStockoutDay");
stockoutDays.MapPut("/{day}", async (Guid storeId, Guid productId, DateOnly day, WriteStockoutDay request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveStockoutDay(context, storeId, productId, day, request, ct))).WithName("SaveStockoutDay");
procurement.MapGet("/suppliers/{code}/prices", async (Guid storeId, string code, DateOnly? asOf, int? offset,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListSupplierPrices(context, storeId, code, asOf, offset, ct))).WithName("ListSupplierPrices");
procurement.MapGet("/suppliers/{code}/prices/{sku}", async (Guid storeId, string code, string sku, int? offset,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetSupplierPriceHistory(context, storeId, code, sku, offset, ct))).WithName("SupplierPriceHistory");
procurement.MapPut("/suppliers/{code}/prices/{sku}", async (Guid storeId, string code, string sku,
    WriteSupplierPrice request, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveSupplierPrice(context, storeId, code, sku, request, ct))).WithName("SaveSupplierPrice");
procurement.MapGet("/orders", async (Guid storeId, string? stage, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListPurchaseDrafts(context, storeId, stage, offset, ct))).WithName("ListPurchaseDrafts");
procurement.MapGet("/orders/{id:guid}", async (Guid storeId, Guid id, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetPurchaseDraft(context, storeId, id, ct))).WithName("GetPurchaseDraft");
procurement.MapPut("/orders/{id:guid}", async (Guid storeId, Guid id, SavePurchaseDraft request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SavePurchaseDraft(context, storeId, id, request, ct))).WithName("SavePurchaseDraft");
procurement.MapPost("/orders/{id:guid}/approve", async (Guid storeId, Guid id, ApprovePurchaseOrder request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ApprovePurchaseDraft(context, storeId, id, request, ct))).WithName("ApprovePurchaseDraft");
procurement.MapPost("/orders/{id:guid}/mark-sent", async (Guid storeId, Guid id, DispatchPurchaseOrder request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.MarkPurchaseSent(context, storeId, id, request, ct))).WithName("MarkPurchaseSent");
procurement.MapPost("/returns", async (Guid storeId, CreateSupplierReturn request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ReturnToSupplier(context, storeId, request, ct))).WithName("ReturnToSupplier");
procurement.MapGet("/returns/{id:guid}", async (Guid storeId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) =>
    TypedResults.Ok(await pos.GetSupplierReturn(context, storeId, id, ct))).WithName("GetSupplierReturn");
procurement.MapGet("/suppliers/{code}/balance", async (Guid storeId, string code, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ReadSupplierBalance(context, storeId, code, ct))).WithName("ReadSupplierBalance");
procurement.MapGet("/suppliers/{code}/payments", async (Guid storeId, string code, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListSupplierPayments(context, storeId, code, offset, ct))).WithName("ListSupplierPayments");
procurement.MapPost("/suppliers/{code}/payments", async (Guid storeId, string code, RecordSupplierPayment request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveSupplierPayment(context, storeId, code, request, ct))).WithName("SaveSupplierPayment");

var supplierSettlement = procurement.MapGroup("/suppliers/{code}");
supplierSettlement.MapGet("/invoices", async (Guid storeId, string code, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListSupplierInvoices(context, storeId, code, offset, ct))).WithName("ListSupplierInvoices");
supplierSettlement.MapGet("/invoices/{id:guid}", async (Guid storeId, string code, Guid id, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetSupplierInvoice(context, storeId, code, id, ct))).WithName("ReadSupplierInvoice");
supplierSettlement.MapPut("/invoices/{id:guid}/due-date", async (Guid storeId, string code, Guid id,
    SetSupplierInvoiceDueDate request, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveSupplierInvoiceDueDate(context, storeId, code, id, request, ct))).WithName("SetSupplierInvoiceDueDate");
supplierSettlement.MapGet("/invoices/{id:guid}/allocations", async (Guid storeId, string code, Guid id, int? offset,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListSupplierInvoiceAllocations(context, storeId, code, id, offset, ct))).WithName("ListSupplierInvoiceAllocations");
supplierSettlement.MapGet("/payments/{id:guid}/allocation-balance", async (Guid storeId, string code, Guid id,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetSupplierPaymentAllocationBalance(context, storeId, code, id, ct))).WithName("ReadSupplierPaymentAllocationBalance");
supplierSettlement.MapPost("/payment-allocations", async (Guid storeId, string code, AllocateSupplierPayment request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.AllocateSupplierPaymentToInvoices(context, storeId, code, request, ct))).WithName("AllocateSupplierPayment");
supplierSettlement.MapGet("/payment-allocations/{id:guid}", async (Guid storeId, string code, Guid id, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetSupplierPaymentAllocation(context, storeId, code, id, ct))).WithName("ReadSupplierPaymentAllocation");
supplierSettlement.MapPost("/payment-allocations/{id:guid}/reverse", async (Guid storeId, string code, Guid id,
    ReverseSupplierPaymentAllocation request, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ReversePaymentAllocation(context, storeId, code, id, request, ct))).WithName("ReverseSupplierPaymentAllocation");

supplierSettlement.MapGet("/invoices/{id:guid}/credit-transfers", async (Guid storeId, string code, Guid id, int? offset,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListSupplierInvoiceCreditTransfers(context, storeId, code, id, offset, ct))).WithName("ListInvoiceCreditTransfers");
supplierSettlement.MapPost("/credit-transfers", async (Guid storeId, string code, TransferSupplierCredit request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveSupplierCreditTransfer(context, storeId, code, request, ct))).WithName("TransferSupplierCredit");
supplierSettlement.MapGet("/credit-transfers/{id:guid}", async (Guid storeId, string code, Guid id, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetSupplierCreditTransfer(context, storeId, code, id, ct))).WithName("ReadSupplierCreditTransfer");
supplierSettlement.MapPost("/credit-transfers/{id:guid}/reverse", async (Guid storeId, string code, Guid id,
    ReverseSupplierCreditTransfer request, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ReverseCreditTransfer(context, storeId, code, id, request, ct))).WithName("ReverseSupplierCreditTransfer");

var receiptApi = storeApi.MapGroup("/receipts/{id:guid}");
receiptApi.MapGet("/costs", async (Guid storeId, Guid id, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetReceiptCosts(context, storeId, id, ct))).WithName("ReadReceiptCosts");
receiptApi.MapPut("/costs", async (Guid storeId, Guid id, WriteReceiptCosts request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveReceiptCosts(context, storeId, id, request, ct))).WithName("SaveReceiptCosts");
receiptApi.MapGet("/files", async (Guid storeId, Guid id, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListInvoiceFiles(context, storeId, id, ct))).WithName("ListInvoiceFiles");
receiptApi.MapPut("/files/{fileId:guid}", async (Guid storeId, Guid id, Guid fileId,
    [FromHeader(Name = "X-File-Name")] string fileName, PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.UploadInvoiceFile(context, storeId, id, fileId, fileName, ct)))
    .WithName("UploadInvoiceFile").ProducesProblem(413).ProducesProblem(415);
receiptApi.MapPost("/files/{fileId:guid}/scan", async (Guid storeId, Guid id, Guid fileId, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ScanInvoiceFile(context, storeId, id, fileId, ct))).WithName("ScanInvoiceFile");
receiptApi.MapGet("/files/{fileId:guid}", async (Guid storeId, Guid id, Guid fileId, PosService pos,
    HttpContext context, CancellationToken ct) =>
{
    var file = await pos.DownloadInvoiceFile(context, storeId, id, fileId, ct);
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["Content-Security-Policy"] = "sandbox; default-src 'none'";
    return TypedResults.File(file.Contents, file.Metadata.ContentType, file.Metadata.FileName);
}).WithName("DownloadInvoiceFile");
receiptApi.MapDelete("/files/{fileId:guid}", async (Guid storeId, Guid id, Guid fileId, PosService pos,
    HttpContext context, CancellationToken ct) =>
{
    await pos.DeleteInvoiceFile(context, storeId, id, fileId, ct);
    return TypedResults.NoContent();
}).WithName("DeleteInvoiceFile");
var catalogApi = storeApi.MapGroup("/catalog");
catalogApi.MapGet("/products", async (Guid storeId, string? q, bool? includeArchived, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListStoreProducts(context, storeId, q, includeArchived, offset, ct))).WithName("ListStoreProducts");
catalogApi.MapGet("/products/{id:guid}", async (Guid storeId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.GetStoreProduct(context, storeId, id, ct))).WithName("ReadStoreProduct");
catalogApi.MapPut("/products/{id:guid}", async (Guid storeId, Guid id, WriteProductCard request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveStoreProduct(context, storeId, id, request, ct))).WithName("SaveStoreProduct");
catalogApi.MapPut("/products/{id:guid}/price", async (Guid storeId, Guid id, WriteProductPrice request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveStoreProductPrice(context, storeId, id, request, ct))).WithName("SaveStoreProductPrice");
catalogApi.MapGet("/products/{id:guid}/price-history", async (Guid storeId, Guid id, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListProductPrices(context, storeId, id, offset, ct))).WithName("ListProductPrices");
catalogApi.MapPost("/import", async (Guid storeId, ImportCatalog request, PosService pos, HttpContext context,
    CancellationToken ct) => TypedResults.Ok(await pos.ImportStoreCatalog(context, storeId, request, ct)))
    .WithName("ImportStoreCatalog");

storeApi.MapPost("/receiving/catalog/proposals", async (Guid storeId, ProposeProduct request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ProposeProduct(context, storeId, request, ct))).WithName("ProposeProduct");
storeApi.MapGet("/receiving/catalog/proposals", async (Guid storeId, string? status, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListProductProposals(context, storeId, status, offset, ct))).WithName("ListProductProposals");
catalogApi.MapPost("/proposals/{id:guid}/approve", async (Guid storeId, Guid id, ReviewProductProposal request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ReviewProductProposal(context, storeId, id, request, true, ct))).WithName("ApproveProductProposal");
catalogApi.MapPost("/proposals/{id:guid}/reject", async (Guid storeId, Guid id, ReviewProductProposal request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ReviewProductProposal(context, storeId, id, request, false, ct))).WithName("RejectProductProposal");

var stockApi = storeApi.MapGroup("/stock");
stockApi.MapPost("/opening-balances/preview", async (Guid storeId, ImportOpeningStock request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ImportOpeningStockBalances(context, storeId, request, preview: true, ct)))
    .WithName("PreviewOpeningStock");
stockApi.MapPost("/opening-balances", async (Guid storeId, ImportOpeningStock request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ImportOpeningStockBalances(context, storeId, request, preview: false, ct)))
    .WithName("PostOpeningStock");
stockApi.MapGet("/opening-balances/{id:guid}", async (Guid storeId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) =>
    TypedResults.Ok(await pos.GetOpeningStockReport(context, storeId, id, ct))).WithName("ReadOpeningStock");
stockApi.MapGet("/opening-balances/{id:guid}/csv", async (Guid storeId, Guid id, PosService pos, HttpContext context,
    CancellationToken ct) =>
{
    var report = await pos.GetOpeningStockReport(context, storeId, id, ct, export: true);
    context.Response.Headers.XContentTypeOptions = "nosniff";
    return TypedResults.File(PosService.FormatOpeningStockCsv(report), "text/csv; charset=utf-8", $"opening-stock-{id}.csv");
}).Produces<string>(200, contentType: "text/csv").WithName("ExportOpeningStock");
stockApi.MapGet("/lots", async (Guid storeId, Guid? productId, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListStockLots(context, storeId, productId, offset, ct))).WithName("ListStockLots");
stockApi.MapGet("/expiry", async (Guid storeId, string? filter, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetStoreExpiry(context, storeId, filter, offset, ct))).WithName("ListStoreExpiry");
stockApi.MapGet("/shelf-tasks", async (Guid storeId, bool? openOnly, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListShelfTasks(context, storeId, openOnly, offset, ct))).WithName("ListShelfTasks");
stockApi.MapPost("/shelf-tasks/{id:guid}/resolve", async (Guid storeId, Guid id, ResolveShelfTask request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ResolveShelfTask(context, storeId, id, request, ct))).WithName("ResolveShelfTask");
stockApi.MapPost("/operations", async (Guid storeId, CreateStockOperation request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ApplyStockOperation(context, storeId, request, ct))).WithName("ApplyStockOperation");
stockApi.MapGet("/operations", async (Guid storeId, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListStockOperations(context, storeId, offset, ct))).WithName("ListStockOperations");
stockApi.MapGet("/operations/{id:guid}", async (Guid storeId, Guid id, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetStockOperation(context, storeId, id, ct))).WithName("ReadStockOperation");
stockApi.MapPost("/inventories", async (Guid storeId, CreateInventory request, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.CreateInventory(context, storeId, request, ct))).WithName("CreateInventory");
stockApi.MapGet("/inventories", async (Guid storeId, string? status, int? offset, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.ListInventories(context, storeId, status, offset, ct))).WithName("ListInventories");
stockApi.MapGet("/inventories/{id:guid}", async (Guid storeId, Guid id, bool? blind, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.GetInventory(context, storeId, id, blind, ct))).WithName("ReadInventory");
stockApi.MapPost("/inventories/{id:guid}/refresh", async (Guid storeId, Guid id, PosService pos,
    HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.RefreshInventoryScope(context, storeId, id, ct))).WithName("RefreshInventoryScope");
stockApi.MapPost("/inventories/{id:guid}/count", async (Guid storeId, Guid id, CountInventory request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.SaveInventoryCount(context, storeId, id, request, ct))).WithName("SaveInventoryCount");
stockApi.MapPost("/inventories/{id:guid}/post", async (Guid storeId, Guid id, ReceiptTransition request,
    PosService pos, HttpContext context, CancellationToken ct) =>
    TypedResults.Ok(await pos.PostInventory(context, storeId, id, request, ct))).WithName("PostInventory");
app.Run();

static Task WriteError(HttpContext context, int status, string code, int? importRow = null)
{
    var details = new Dictionary<string, object?> { ["code"] = code };
    if (importRow is not null)
        details["row"] = importRow;
    return Results.Problem(statusCode: status, title: code, extensions: details).ExecuteAsync(context);
}
