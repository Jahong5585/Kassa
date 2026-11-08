using System.Data;
using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<ReplenishmentPolicy> SaveReplenishmentPolicy(HttpContext context, Guid store, string code,
        string sku, WriteReplenishmentPolicy request, CancellationToken ct)
    {
        code = SupplierCode(code);
        sku = SupplierCode(sku);
        if (request.ExpectedVersion is < 0 or int.MaxValue || request.Settings is null)
            throw new PosError(400, "invalid_replenishment_policy");
        var settings = NormalizeReplenishmentSettings(request.Settings);

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await RequireActiveSupplier(db, tx, scope, code, ct);
        var current = await ReadReplenishmentPolicy(db, tx, scope, code, sku, ct);
        if (current is not null && current.Version == request.ExpectedVersion + 1 &&
            SameReplenishmentSettings(current.Settings, settings))
            return current;
        if ((current?.Version ?? 0) != request.ExpectedVersion)
            throw new PosError(409, "replenishment_policy_version_conflict");

        var offer = (await ReadSupplierPriceHistory(db, tx, scope, code, sku, 1, 0, ct)).SingleOrDefault()
            ?? throw new PosError(404, "supplier_sku_not_found");
        var product = await ReadReplenishmentProduct(db, tx, scope, offer.ProductId, ct);
        ValidateLotQuantity(product.Unit, settings.SafetyStock);
        var version = request.ExpectedVersion + 1;
        await using var command = db.Sql(tx, """
            INSERT INTO replenishment_policies (org_id, store_id, supplier_code, supplier_sku, product_id, version,
                settings, updated_by, updated_at)
            VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb, $8, statement_timestamp())
            ON CONFLICT (org_id, store_id, supplier_code, supplier_sku) DO UPDATE SET version = EXCLUDED.version,
                settings = EXCLUDED.settings, updated_by = EXCLUDED.updated_by, updated_at = EXCLUDED.updated_at
            RETURNING updated_at
            """, scope.Organization, store, code, sku, offer.ProductId, version, JsonSerializer.Serialize(settings), scope.Staff);
        var updatedAt = (DateTime)(await command.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Saved replenishment policy missing."));
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "procurement.replenishment_policy_saved",
            offer.ProductId, ct, $"{code}:{sku}:{version}");
        await tx.CommitAsync(ct);
        return new ReplenishmentPolicy(code, sku, offer.ProductId, version, settings, scope.Staff, updatedAt);
    }

    public async Task<ReplenishmentPolicy> GetReplenishmentPolicy(HttpContext context, Guid store, string code,
        string sku, CancellationToken ct)
    {
        code = SupplierCode(code);
        sku = SupplierCode(sku);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadReplenishmentPolicy(db, tx, scope, code, sku, ct)
            ?? throw new PosError(404, "replenishment_policy_not_found");
    }

    public async Task<ReplenishmentRecommendation> RecommendFromPolicy(HttpContext context, Guid store, string code,
        string sku, CancellationToken ct)
    {
        code = SupplierCode(code);
        sku = SupplierCode(sku);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        var policy = await ReadReplenishmentPolicy(db, tx, scope, code, sku, ct)
            ?? throw new PosError(404, "replenishment_policy_not_found");
        return await CalculateReplenishment(db, tx, scope, code, sku, policy.Settings, policy.Version, ct);
    }

    private static async Task<ReplenishmentPolicy?> ReadReplenishmentPolicy(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, string code, string sku, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT product_id, version, settings, updated_by, updated_at FROM replenishment_policies
            WHERE (org_id, store_id, supplier_code, supplier_sku) = ($1, $2, $3, $4)
            """, scope.Organization, scope.Store, code, sku);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct)
            ? new ReplenishmentPolicy(code, sku, rows.GetGuid(0), rows.GetInt32(1),
                JsonSerializer.Deserialize<ReplenishmentRequest>(rows.GetString(2))
                    ?? throw new InvalidOperationException("Replenishment settings missing."),
                rows.GetGuid(3), rows.GetDateTime(4))
            : null;
    }

    private static async Task VerifyPurchaseReplenishment(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        string code, DateOnly deliveryDate, OrderOffer item, PurchaseReplenishment origin, CancellationToken ct)
    {
        if (origin.PolicyVersion is { } version)
        {
            var policy = await ReadReplenishmentPolicy(db, tx, scope, code, item.SupplierSku, ct);
            if (policy is null || policy.Version != version || !SameReplenishmentSettings(policy.Settings, origin.Settings))
                throw new PosError(409, "replenishment_policy_changed");
        }
        var current = await CalculateReplenishment(db, tx, scope, code, item.SupplierSku, origin.Settings,
            origin.PolicyVersion, ct);
        if (current.Draft is not { } candidate || candidate.ExpectedDeliveryDate != deliveryDate ||
            candidate.Items.Single() != item)
            throw new PosError(409, "purchase_replenishment_changed");
    }

    private static bool SamePurchaseReplenishment(PurchaseReplenishment? first, PurchaseReplenishment? second) =>
        first is null || second is null
            ? first is null && second is null
            : first.PolicyVersion == second.PolicyVersion && SameReplenishmentSettings(first.Settings, second.Settings);

    private static bool SameReplenishmentSettings(ReplenishmentRequest first, ReplenishmentRequest second) =>
        first with { OrderWeekdays = null } == second with { OrderWeekdays = null } &&
        first.OrderWeekdays!.SequenceEqual(second.OrderWeekdays!);
}
