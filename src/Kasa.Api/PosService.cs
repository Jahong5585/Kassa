using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService(NpgsqlDataSource source, IConfiguration configuration, InvoiceScanner invoiceScanner)
{
    private const decimal MaxMoney = 99_999_999_999_999.99m;
    private const decimal MaxQuantity = 99_999_999_999.999m;
    private const string ReceiptMark = "Тестовый • нефискальный";

    public async Task<ShiftSummary> OpenShift(HttpContext context, OpenShift request, CancellationToken ct)
    {
        if (request.ShiftId == Guid.Empty || !IsMoney(request.OpeningCash))
            throw new PosError(400, "invalid_open_shift");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, true, ct);
        var inserted = await db.Run(tx, ct, """
            INSERT INTO shifts (org_id, store_id, register_id, cashier_id, id, opening_cash)
            VALUES ($1, $2, $3, $4, $5, $6) ON CONFLICT DO NOTHING
            """, scope.Organization, scope.Store, scope.Register, scope.Staff, request.ShiftId, request.OpeningCash);
        var shift = await ReadShift(db, tx, scope.Organization, scope.Store, scope.Register, request.ShiftId,
            scope.Staff, ct);
        if (shift is null || shift.OpeningCash != request.OpeningCash)
            throw new PosError(409, "open_shift_conflict");
        if (inserted == 1)
            await Audit(db, tx, context, scope, "shift.opened", request.ShiftId, ct);
        await tx.CommitAsync(ct);
        return shift;
    }

    public async Task<ShiftSummary> CloseShift(HttpContext context, Guid id, CloseShift request, CancellationToken ct)
    {
        if (!IsMoney(request.CountedCash))
            throw new PosError(400, "invalid_counted_cash");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, true, ct);
        var current = await ReadShift(db, tx, scope.Organization, scope.Store, scope.Register, id, scope.Staff, ct)
            ?? throw new PosError(404, "shift_not_found");
        if (current.ClosedAt is null && current.OpenCartCount != 0)
            throw new PosError(409, "shift_has_parked_carts");
        var updated = await db.Run(tx, ct, """
            UPDATE shifts SET closed_at = now(), counted_cash = $6
            WHERE (org_id, store_id, register_id, id, cashier_id) = ($1, $2, $3, $4, $5) AND closed_at IS NULL
            """, scope.Organization, scope.Store, scope.Register, id, scope.Staff, request.CountedCash);
        var shift = await ReadShift(db, tx, scope.Organization, scope.Store, scope.Register, id, scope.Staff, ct)
            ?? throw new PosError(404, "shift_not_found");
        if (shift.CountedCash != request.CountedCash)
            throw new PosError(409, "closed_shift_conflict");
        if (updated == 1)
            await Audit(db, tx, context, scope, "shift.closed", id, ct);
        await tx.CommitAsync(ct);
        return shift;
    }

    public async Task<ShiftSummary> GetShift(HttpContext context, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        return await ReadShift(db, tx, scope.Organization, scope.Store, scope.Register, id, scope.Staff, ct)
            ?? throw new PosError(404, "shift_not_found");
    }

    public async Task<ShiftSummary> GetAdminShift(HttpContext context, Guid store, Guid register, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct, "shift_not_found");
        return await ReadShift(db, tx, scope.Organization, store, register, id, null, ct)
            ?? throw new PosError(404, "shift_not_found");
    }

    private const string ShiftExpectedCashSql = "s.opening_cash + paid.cash - returned.cash + movements.incoming - movements.outgoing";

    private const string ShiftMoneyJoins = """
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(total) FILTER (WHERE method = 'cash'), 0) AS cash,
                       coalesce(sum(total) FILTER (WHERE method = 'manualCashless'), 0) AS cashless,
                       count(*) AS count
                FROM sales
                WHERE (org_id, store_id, register_id, shift_id) = (s.org_id, s.store_id, s.register_id, s.id)
            ) paid
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(total) FILTER (WHERE method = 'cash'), 0) AS cash,
                       coalesce(sum(total) FILTER (WHERE method = 'manualCashless'), 0) AS cashless,
                       count(*) AS count
                FROM refunds
                WHERE (org_id, store_id, register_id, shift_id) = (s.org_id, s.store_id, s.register_id, s.id)
            ) returned
            CROSS JOIN LATERAL (
                SELECT coalesce(sum(amount) FILTER (WHERE direction = 'in'), 0) AS incoming,
                       coalesce(sum(amount) FILTER (WHERE direction = 'out'), 0) AS outgoing
                FROM cash_movements
                WHERE (org_id, store_id, register_id, shift_id) = (s.org_id, s.store_id, s.register_id, s.id)
            ) movements
            """;

    private static async Task<ShiftSummary?> ReadShift(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid organization, Guid store, Guid register, Guid id, Guid? cashier, CancellationToken ct)
    {
        await using var command = db.Sql(tx, $"""
            SELECT s.opened_at, s.closed_at, s.opening_cash, s.counted_cash,
                   paid.cash, paid.cashless, paid.count, returned.cash, returned.cashless, returned.count,
                   movements.incoming, movements.outgoing, pending.count, {ShiftExpectedCashSql}
            FROM shifts s
            {ShiftMoneyJoins}
            CROSS JOIN LATERAL (
                SELECT count(*) AS count FROM parked_carts
                WHERE (org_id, store_id, register_id, shift_id) = (s.org_id, s.store_id, s.register_id, s.id)
                    AND status = 'parked' AND expires_at > statement_timestamp()
            ) pending
            WHERE (s.org_id, s.store_id, s.register_id, s.id) = ($1, $2, $3, $4)
              AND ($5 OR s.cashier_id = $6)
            """, organization, store, register, id, cashier is null, cashier ?? Guid.Empty);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            return null;
        var opening = rows.GetDecimal(2);
        var counted = rows.DecimalOrNull(3);
        var cash = rows.GetDecimal(4);
        var cashRefunds = rows.GetDecimal(7);
        var cashIn = rows.GetDecimal(10);
        var cashOut = rows.GetDecimal(11);
        var expected = rows.GetDecimal(13);
        var showCash = cashier is null || counted.HasValue;
        return new ShiftSummary(id, rows.GetDateTime(0), rows.TimeOrNull(1), opening, showCash ? cash : null,
            rows.GetDecimal(5), showCash ? expected : null, counted, counted - expected, rows.GetInt64(6))
        {
            CashRefunds = showCash ? cashRefunds : null,
            ManualCashlessRefunds = rows.GetDecimal(8),
            RefundCount = rows.GetInt64(9),
            CashIn = showCash ? cashIn : null,
            CashOut = showCash ? cashOut : null,
            OpenCartCount = rows.GetInt64(12)
        };
    }

    public async Task<SaleResult> CreateSale(HttpContext context, CreateSale request, CancellationToken ct)
    {
        var items = ValidateSale(request);
        var discount = ValidateManualDiscount(request.Discount);
        var checkout = new
        {
            request.ShiftId,
            request.Method,
            Tendered = request.Tendered?.ToString("G29", CultureInfo.InvariantCulture),
            Items = items.Select(item => new
            {
                item.ProductId, item.LotId, item.PriceVersion,
                Quantity = item.Quantity.ToString("G29", CultureInfo.InvariantCulture)
            })
        };
        var fingerprint = discount is null && request.CartId is null
            ? JsonSerializer.SerializeToUtf8Bytes(checkout)
            : JsonSerializer.SerializeToUtf8Bytes(new
            {
                checkout, request.CartId, request.CartVersion,
                Discount = discount is null ? null : new
                {
                    discount.Kind, discount.Reason,
                    Value = discount.Value.ToString("G29", CultureInfo.InvariantCulture)
                }
            });
        var digest = SHA256.HashData(fingerprint);

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, true, ct);
        var existing = await ReadSale(db, tx, scope.Organization, scope.Store, scope.Register, scope.Staff,
            request.OperationId, ct);
        if (existing is not null)
        {
            if (!CryptographicOperations.FixedTimeEquals(existing.Value.Digest, digest))
                throw new PosError(409, "operation_id_reused");
            return existing.Value.Sale;
        }

        await RequireOpenShift(db, tx, scope, request.ShiftId, ct);
        if (request.CartId is { } cartId)
            await RequireCheckoutCart(db, tx, scope, request.ShiftId, cartId, request.CartVersion!.Value, items, ct);

        // The order is shared across registers so that two sales lock the same lots in the same order.
        var lines = new List<SaleLine>(items.Length);
        foreach (var item in items)
            lines.Add(await DebitLot(db, tx, scope, item, ct));

        if (discount is not null)
            ApplyManualDiscount(lines, discount);
        var total = lines.Sum(line => line.LineTotal);
        if (!IsMoney(total) || total == 0)
            throw new PosError(400, "invalid_sale_total");
        if (request.Tendered < total)
            throw new PosError(400, "cash_tendered_below_total");
        var change = request.Tendered - total;
        var created = DateTime.UtcNow;
        // PostgreSQL timestamps have microsecond precision. Return the same timestamp on a retry.
        created = created.AddTicks(-(created.Ticks % 10));
        var inserted = await db.Run(tx, ct, """
            INSERT INTO sales (org_id, store_id, register_id, id, shift_id, cashier_id, method,
                               total, tendered, change_due, request_digest, created_at, discount_reason)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, NULLIF($13, ''))
            ON CONFLICT DO NOTHING
            """, scope.Organization, scope.Store, scope.Register, request.OperationId, request.ShiftId,
            scope.Staff, request.Method, total, request.Tendered, change, digest, created, discount?.Reason ?? "");
        if (inserted != 1)
            throw new PosError(409, "operation_id_reused");

        foreach (var line in lines)
        {
            await db.Run(tx, ct, """
                INSERT INTO sale_lines (org_id, store_id, register_id, sale_id, product_id, lot_id,
                                        name, unit, quantity, unit_price, line_total, discount_amount, price_version,
                                        inventory_cost)
                VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14)
                """, scope.Organization, scope.Store, scope.Register, request.OperationId, line.ProductId,
                line.LotId, line.Name, line.Unit, line.Quantity, line.UnitPrice, line.LineTotal,
                line.DiscountAmount, line.PriceVersion, line.InventoryCost);
        }
        if (request.CartId is { } soldCart)
            await MarkCartSold(db, tx, context, scope, request.ShiftId, soldCart, request.OperationId, ct);
        await Audit(db, tx, context, scope, "sale.committed", request.OperationId, ct);
        await tx.CommitAsync(ct);
        return new SaleResult(request.OperationId, request.ShiftId, request.Method, total, request.Tendered,
            change, created, lines, ReceiptMark) { DiscountReason = discount?.Reason };
    }

    private static SaleItem[] ValidateSale(CreateSale request)
    {
        if (request.OperationId == Guid.Empty || request.ShiftId == Guid.Empty ||
            (request.CartId is null) != (request.CartVersion is null) || request.CartId == Guid.Empty ||
            request.CartVersion is <= 0 or int.MaxValue ||
            request.Method is not ("cash" or "manualCashless"))
            throw new PosError(400, "invalid_sale");
        if (request.Method == "cash" ? request.Tendered is not decimal tendered || !IsMoney(tendered)
            : request.Tendered is not null)
            throw new PosError(400, "invalid_tendered_for_method");

        return ValidateBasketItems(request.Items);
    }

    private static SaleItem[] ValidateBasketItems(SaleItem[] items)
    {
        if (items is not { Length: >= 1 and <= 100 })
            throw new PosError(400, "invalid_sale_item");
        var distinct = new HashSet<(Guid, Guid)>();
        foreach (var item in items)
        {
            if (item is null || item.ProductId == Guid.Empty || item.LotId == Guid.Empty || item.PriceVersion < 1 ||
                !IsQuantity(item.Quantity) ||
                !distinct.Add((item.ProductId, item.LotId)))
                throw new PosError(400, "invalid_sale_item");
        }
        return items.OrderBy(item => item.ProductId).ThenBy(item => item.LotId).ToArray();
    }

    private static async Task<SaleLine> DebitLot(NpgsqlConnection db, NpgsqlTransaction tx, RegisterScope scope,
        SaleItem item, CancellationToken ct)
    {
        await LockProduct(db, tx, scope.Organization, item.ProductId, false, ct);
        await using var command = db.Sql(tx, """
            SELECT p.name, p.unit, p.requires_expiry, sp.unit_price, sp.version
            FROM products p JOIN store_prices sp ON (sp.org_id, sp.product_id) = (p.org_id, p.id)
            WHERE (p.org_id, sp.store_id, p.id) = ($1, $2, $3) AND sp.active
            """, scope.Organization, scope.Store, item.ProductId);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(404, "product_not_found");
        var name = rows.GetString(0);
        var unit = rows.GetString(1);
        var requiresExpiry = rows.GetBoolean(2);
        if (rows.IsDBNull(3))
            throw new PosError(409, "sale_price_not_set");
        var price = rows.GetDecimal(3);
        if (rows.GetInt32(4) != item.PriceVersion)
            throw new PosError(409, "price_changed");
        await rows.CloseAsync();
        if (unit == "piece" && item.Quantity != decimal.Truncate(item.Quantity))
            throw new PosError(400, "fractional_piece_quantity");
        var lineTotal = decimal.Round(price * item.Quantity, 2, MidpointRounding.AwayFromZero);
        if (!IsMoney(lineTotal) || lineTotal == 0)
            throw new PosError(400, "invalid_line_total");

        await using var debit = db.Sql(tx, """
            WITH target AS (
                SELECT quantity + quarantine_quantity AS before_quantity,
                       kasa_weighted_cost_delta(inventory_value, quantity + quarantine_quantity, -$5) AS cost_delta
                FROM stock_lots WHERE (org_id, store_id, product_id, id) = ($1, $2, $3, $4)
                  AND quantity >= $5 AND NOT quarantined
                  AND (NOT $6 OR expiry_date IS NOT NULL)
                  AND (expiry_date IS NULL OR expiry_date >= (statement_timestamp() AT TIME ZONE $7)::date)
                FOR UPDATE
            )
            UPDATE stock_lots l
            SET quantity = l.quantity - $5,
                inventory_value = kasa_stock_value_after(l.inventory_value, t.before_quantity,
                    t.before_quantity - $5, t.cost_delta)
            FROM target t WHERE (l.org_id, l.store_id, l.product_id, l.id) = ($1, $2, $3, $4)
            RETURNING -t.cost_delta
            """, scope.Organization, scope.Store, item.ProductId, item.LotId, item.Quantity, requiresExpiry,
            scope.TimeZone);
        await using var debitRows = await debit.ExecuteReaderAsync(ct);
        if (!await debitRows.ReadAsync(ct))
            throw new PosError(409, "lot_not_sellable_or_insufficient_stock");
        return new SaleLine(item.ProductId, item.LotId, name, unit, item.Quantity, price, lineTotal)
            { PriceVersion = item.PriceVersion, InventoryCost = debitRows.DecimalOrNull(0) };
    }

    public async Task<SaleResult> GetSale(HttpContext context, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        var saved = await ReadSale(db, tx, scope.Organization, scope.Store, scope.Register, scope.Staff, id, ct)
            ?? throw new PosError(404, "sale_not_found");
        return saved.Sale;
    }

    private static async Task<(SaleResult Sale, byte[] Digest)?> ReadSale(NpgsqlConnection db, NpgsqlTransaction tx,
        Guid org, Guid store, Guid register, Guid? cashier, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT p.shift_id, p.method, p.total, p.tendered, p.change_due, p.created_at, p.request_digest,
                   l.product_id, l.lot_id, l.name, l.unit, l.quantity, l.unit_price, l.line_total,
                   l.discount_amount, l.price_version, p.discount_reason
            FROM sales p JOIN sale_lines l ON (l.org_id, l.store_id, l.register_id, l.sale_id) =
                                             (p.org_id, p.store_id, p.register_id, p.id)
            WHERE (p.org_id, p.store_id, p.register_id, p.id) = ($1, $2, $3, $4)
              AND ($5 OR p.cashier_id = $6)
            """, org, store, register, id, cashier is null, cashier ?? Guid.Empty);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            return null;
        List<SaleLine> lines = [];
        var sale = new SaleResult(id, rows.GetGuid(0), rows.GetString(1), rows.GetDecimal(2), rows.DecimalOrNull(3),
            rows.DecimalOrNull(4), rows.GetDateTime(5), lines, ReceiptMark)
            { DiscountReason = rows.IsDBNull(16) ? null : rows.GetString(16) };
        var digest = rows.GetFieldValue<byte[]>(6);
        do
        {
            lines.Add(new SaleLine(rows.GetGuid(7), rows.GetGuid(8), rows.GetString(9), rows.GetString(10),
                rows.GetDecimal(11), rows.GetDecimal(12), rows.GetDecimal(13))
            { DiscountAmount = rows.GetDecimal(14), PriceVersion = rows.IsDBNull(15) ? null : rows.GetInt32(15) });
        } while (await rows.ReadAsync(ct));
        return (sale with { Lines = lines.OrderBy(line => line.ProductId).ThenBy(line => line.LotId).ToArray() }, digest);
    }

    private static async Task RequireOpenShift(NpgsqlConnection db, NpgsqlTransaction tx, RegisterScope scope,
        Guid shiftId, CancellationToken ct)
    {
        await using var open = db.Sql(tx, """
            SELECT 1 FROM shifts
            WHERE (org_id, store_id, register_id, id, cashier_id) = ($1, $2, $3, $4, $5) AND closed_at IS NULL
            """, scope.Organization, scope.Store, scope.Register, shiftId, scope.Staff);
        if (await open.ExecuteScalarAsync(ct) is null)
            throw new PosError(409, "active_shift_required");
    }

    private static bool IsText(string? value, int limit) =>
        value?.Trim() is { Length: > 0 } trimmed && trimmed.Length <= limit && !trimmed.Contains('\0');

    private static bool IsQuantity(decimal value) =>
        value > 0 && value <= MaxQuantity && value == decimal.Round(value, 3);

    private static bool IsMoney(decimal value) => value >= 0 && value <= MaxMoney && value == decimal.Round(value, 2);

    private static Task<int> Audit(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        RegisterScope scope, string action, Guid id, CancellationToken ct) =>
        Audit(db, tx, context, scope.Organization, scope.Store, scope.Register, scope.Staff, action, id, ct);

    private static Task<int> Audit(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        Guid org, Guid store, Guid register, Guid staff, string action, Guid id, CancellationToken ct, string objectCode = "") =>
        Audit(db, tx, context.TraceIdentifier, org, store, register, staff, action, id, ct, objectCode);

    private static Task<int> Audit(NpgsqlConnection db, NpgsqlTransaction tx, string requestId,
        Guid org, Guid store, Guid register, Guid staff, string action, Guid id, CancellationToken ct, string objectCode = "") =>
        db.Run(tx, ct, """
            INSERT INTO audit_events (org_id, store_id, register_id, staff_id, action, object_id, request_id, object_code)
            VALUES ($1, $2, NULLIF($3, '00000000-0000-0000-0000-000000000000'::uuid), $4, $5, $6, $7, NULLIF($8, ''))
            """, org, store, register, staff, action, id, requestId, objectCode);
}
