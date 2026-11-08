using System.Globalization;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private const int MaxStockMovementExportRows = 1000;

    private sealed record StockMovementCursor(DateTime OccurredAt, string Kind, Guid DocumentId,
        Guid RegisterId, Guid ProductId, Guid LotId)
    {
        public static StockMovementCursor FromEntry(StockMovementEntry row) =>
            new(row.OccurredAt, row.Kind, row.DocumentId, row.RegisterId ?? Guid.Empty, row.ProductId, row.LotId);
    }

    public async Task<StockMovementReport> GetStockMovementReport(HttpContext context, Guid store, DateOnly from,
        DateOnly to, Guid? product, Guid? lot, string? kind, string? after, int limit, CancellationToken ct,
        bool export = false, bool lossesOnly = false)
    {
        ValidateStockMovementFilters(from, to, product, lot, kind);
        if (limit is < 1 or > 100)
            throw new PosError(400, "invalid_stock_movement_filter");
        var cursor = DecodeStockMovementCursor(after);

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await RequireStockMovementFilterAccess(db, tx, scope, product, lot, ct);

        var generatedAt = DateTime.UtcNow;
        var take = export ? MaxStockMovementExportRows : limit;
        var entries = await ReadStockMovementEntries(db, tx, scope, from, to, product, lot, kind, cursor, take + 1,
            lossesOnly, ct);
        var more = entries.Count > take;
        if (export)
        {
            if (more)
                throw new PosError(409, lossesOnly ? "stock_loss_export_limit" : "stock_movement_export_limit");
            var filter = FormattableString.Invariant(
                $"{scope.Role}/{from:yyyy-MM-dd}/{to:yyyy-MM-dd}/{product?.ToString() ?? "all"}/{lot?.ToString() ?? "all"}/{kind ?? "all"}");
            await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
                lossesOnly ? "reports.stock_losses_csv_exported" : "reports.stock_movements_csv_exported",
                store, ct, filter);
            await tx.CommitAsync(ct);
        }
        if (more)
            entries.RemoveAt(take);
        var next = more ? WebEncoders.Base64UrlEncode(
            JsonSerializer.SerializeToUtf8Bytes(StockMovementCursor.FromEntry(entries[^1]))) : null;
        return new StockMovementReport(store, scope.TimeZone, from, to, product, lot, kind, generatedAt, entries, next);
    }

    private static void ValidateStockMovementFilters(DateOnly from, DateOnly to, Guid? product, Guid? lot, string? kind)
    {
        if (!IsValidReportPeriod(from, to) || product == Guid.Empty || lot == Guid.Empty ||
            lot.HasValue && !product.HasValue || kind is not (null or "writeOff") && !IsStockMovementKind(kind))
            throw new PosError(400, "invalid_stock_movement_filter");
    }

    private static async Task RequireStockMovementFilterAccess(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid? product, Guid? lot, CancellationToken ct)
    {
        if (product is not { } selected)
            return;
        await using var filter = db.Sql(tx, """
            SELECT 1 FROM store_prices p WHERE (p.org_id, p.store_id, p.product_id) = ($1, $2, $3)
                AND ($4 OR EXISTS (SELECT 1 FROM stock_lots l
                    WHERE (l.org_id, l.store_id, l.product_id, l.id) = ($1, $2, $3, $5)))
            """, scope.Organization, scope.Store, selected, lot is null, lot ?? Guid.Empty);
        if (await filter.ExecuteScalarAsync(ct) is null)
            throw new PosError(404, lot is null ? "product_not_found" : "stock_lot_not_found");
    }

    private static bool IsStockMovementKind(string? kind) =>
        kind is "receipt" or "sale" or "customerRefund" or "quarantine" or "inventory" or
            "writeOffStock" or "writeOffQuarantine" or "supplierReturn" or "opening";

    private static StockMovementCursor? DecodeStockMovementCursor(string? cursor)
    {
        if (cursor is null)
            return null;
        if (cursor.Length is > 0 and <= 512)
        {
            try
            {
                var value = JsonSerializer.Deserialize<StockMovementCursor>(WebEncoders.Base64UrlDecode(cursor));
                if (value is not null && IsStockMovementKind(value.Kind) && value.OccurredAt.Kind == DateTimeKind.Utc &&
                    value.DocumentId != Guid.Empty && value.ProductId != Guid.Empty && value.LotId != Guid.Empty)
                    return value;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            {
                // The cursor affects only ordering. Tenant and object filters are always applied again.
            }
        }
        throw new PosError(400, "invalid_stock_movement_cursor");
    }

    private static async Task<List<StockMovementEntry>> ReadStockMovementEntries(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, DateOnly from, DateOnly to, Guid? product, Guid? lot, string? kind,
        StockMovementCursor? cursor, int limit, bool lossesOnly, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            WITH bounds AS (
                SELECT $4::timestamp AT TIME ZONE $3 AS start_at,
                       (($5::date + 1)::timestamp AT TIME ZONE $3) AS end_at,
                       '00000000-0000-0000-0000-000000000000'::uuid AS no_register
            ), movements AS (
                SELECT 'receipt' AS kind, d.id AS document_id, b.no_register AS register_id, d.posted_by AS actor_id,
                    d.posted_at AS occurred_at, l.product_id, l.lot_id, l.quantity AS stock_delta,
                    0::numeric AS quarantine_delta, NULL::text AS reason, NULL::uuid AS source_document_id,
                    NULL::uuid AS source_register_id, c.total AS receiving_cost, NULL::numeric AS supplier_credit,
                    NULL::numeric AS opening_cost, c.total AS inventory_cost_delta
                FROM receiving_documents d CROSS JOIN bounds b
                JOIN receiving_movements l ON (l.org_id, l.store_id, l.receipt_id) = (d.org_id, d.store_id, d.id)
                LEFT JOIN receiving_movement_costs c
                    ON (c.org_id, c.store_id, c.receipt_id, c.product_id, c.lot_id) =
                       (l.org_id, l.store_id, l.receipt_id, l.product_id, l.lot_id)
                WHERE (d.org_id, d.store_id) = ($1, $2) AND d.status = 'posted'
                    AND d.posted_at >= b.start_at AND d.posted_at < b.end_at
                UNION ALL
                SELECT 'sale', s.id, s.register_id, s.cashier_id, s.created_at, l.product_id, l.lot_id,
                    -l.quantity, 0, NULL, NULL, NULL, NULL, NULL, NULL, -l.inventory_cost
                FROM sales s CROSS JOIN bounds b
                JOIN sale_lines l ON (l.org_id, l.store_id, l.register_id, l.sale_id) = (s.org_id, s.store_id, s.register_id, s.id)
                WHERE (s.org_id, s.store_id) = ($1, $2) AND s.created_at >= b.start_at AND s.created_at < b.end_at
                UNION ALL
                SELECT 'customerRefund', r.id, r.register_id, r.cashier_id, r.created_at, l.product_id, l.lot_id,
                    CASE WHEN l.destination = 'stock' THEN l.quantity ELSE 0 END,
                    CASE WHEN l.destination = 'quarantine' THEN l.quantity ELSE 0 END,
                    r.reason, r.sale_id, r.source_register_id, NULL, NULL, NULL, l.inventory_cost
                FROM refunds r CROSS JOIN bounds b
                JOIN refund_lines l ON (l.org_id, l.store_id, l.refund_id) = (r.org_id, r.store_id, r.id)
                WHERE (r.org_id, r.store_id) = ($1, $2) AND r.created_at >= b.start_at AND r.created_at < b.end_at
                UNION ALL
                SELECT o.kind, o.id, b.no_register, o.actor_id, o.created_at, l.product_id, l.lot_id,
                    l.stock_after - l.stock_before, l.quarantine_after - l.quarantine_before,
                    o.reason, r.receipt_id, NULL, NULL, r.credit_amount, opening.total_cost,
                    coalesce(l.inventory_cost_delta, opening.total_cost)
                FROM stock_operations o CROSS JOIN bounds b
                JOIN stock_operation_lines l ON (l.org_id, l.store_id, l.operation_id) = (o.org_id, o.store_id, o.id)
                LEFT JOIN supplier_return_lines r
                    ON o.kind = 'supplierReturn' AND (r.org_id, r.store_id, r.return_id, r.product_id, r.lot_id) =
                       (l.org_id, l.store_id, l.operation_id, l.product_id, l.lot_id)
                LEFT JOIN opening_stock_lines opening
                    ON o.kind = 'opening' AND (opening.org_id, opening.store_id, opening.operation_id, opening.product_id, opening.lot_id) =
                       (l.org_id, l.store_id, l.operation_id, l.product_id, l.lot_id)
                WHERE (o.org_id, o.store_id) = ($1, $2) AND o.created_at >= b.start_at AND o.created_at < b.end_at
            ), selected AS (
                SELECT * FROM movements m
                WHERE ($6 OR m.product_id = $7) AND ($8 OR m.lot_id = $9)
                  AND ($10 = '' OR m.kind = $10 OR ($10 = 'writeOff' AND m.kind IN ('writeOffStock', 'writeOffQuarantine')))
                  AND (NOT $19 OR (m.kind IN ('writeOffStock', 'writeOffQuarantine', 'inventory')
                      AND m.stock_delta + m.quarantine_delta < 0))
                  AND ($11 OR (m.occurred_at, m.kind, m.document_id, m.register_id, m.product_id, m.lot_id) <
                              ($12, $13, $14, $15, $16, $17))
                ORDER BY m.occurred_at DESC, m.kind DESC, m.document_id DESC, m.register_id DESC, m.product_id DESC, m.lot_id DESC
                LIMIT $18
            )
            SELECT m.kind, m.document_id, m.register_id, m.actor_id, m.occurred_at, (m.occurred_at AT TIME ZONE $3)::date,
                m.product_id, p.name, p.unit, m.lot_id, l.expiry_date, m.stock_delta, m.quarantine_delta, m.reason,
                m.source_document_id, m.source_register_id, m.receiving_cost, m.supplier_credit, m.opening_cost, m.inventory_cost_delta
            FROM selected m JOIN products p ON (p.org_id, p.id) = ($1, m.product_id)
            JOIN stock_lots l ON (l.org_id, l.store_id, l.product_id, l.id) = ($1, $2, m.product_id, m.lot_id)
            ORDER BY m.occurred_at DESC, m.kind DESC, m.document_id DESC, m.register_id DESC, m.product_id DESC, m.lot_id DESC
            """, scope.Organization, scope.Store, scope.TimeZone, from, to, product is null, product ?? Guid.Empty,
            lot is null, lot ?? Guid.Empty, kind ?? "", cursor is null, cursor?.OccurredAt ?? DateTime.UnixEpoch,
            cursor?.Kind ?? "", cursor?.DocumentId ?? Guid.Empty, cursor?.RegisterId ?? Guid.Empty,
            cursor?.ProductId ?? Guid.Empty, cursor?.LotId ?? Guid.Empty, limit, lossesOnly);

        await using var rows = await command.ExecuteReaderAsync(ct);
        List<StockMovementEntry> result = [];
        while (await rows.ReadAsync(ct))
        {
            var register = rows.GetGuid(2);
            result.Add(new StockMovementEntry(rows.GetString(0), rows.GetGuid(1), register == Guid.Empty ? null : register,
                rows.GetGuid(3), rows.GetDateTime(4), rows.GetFieldValue<DateOnly>(5), rows.GetGuid(6), rows.GetString(7),
                rows.GetString(8), rows.GetGuid(9), ReadDate(rows, 10), rows.GetDecimal(11), rows.GetDecimal(12),
                rows.IsDBNull(13) ? null : rows.GetString(13), rows.IsDBNull(14) ? null : rows.GetGuid(14),
                rows.IsDBNull(15) ? null : rows.GetGuid(15), rows.DecimalOrNull(16), rows.DecimalOrNull(17))
            {
                OpeningCost = rows.DecimalOrNull(18),
                InventoryCostDelta = rows.DecimalOrNull(19)
            });
        }
        return result;
    }

    internal static byte[] FormatStockMovementCsv(StockMovementReport report)
    {
        var csv = new StringBuilder("\uFEFF");
        csv.Append("store_id,time_zone,from,to,filter_product_id,filter_lot_id,filter_kind,generated_at,")
            .Append("local_date,occurred_at,kind,document_id,register_id,actor_id,")
            .Append("product_id,product_name,unit,lot_id,expiry_date,stock_delta,quarantine_delta,total_delta,reason,")
            .Append("source_document_id,source_register_id,receiving_cost,supplier_credit\r\n");
        var filters = FormattableString.Invariant(
            $"{report.StoreId},{QuoteCsvText(report.TimeZone)},{report.From:yyyy-MM-dd},{report.To:yyyy-MM-dd},{report.ProductId},{report.LotId},{report.Kind},{report.GeneratedAt:O}");
        foreach (var row in report.Items)
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{filters},{row.LocalDate:yyyy-MM-dd},{row.OccurredAt:O},{row.Kind},{row.DocumentId},{row.RegisterId},{row.ActorId},");
            csv.Append(CultureInfo.InvariantCulture,
                $"{row.ProductId},{QuoteCsvText(row.ProductName)},{row.Unit},{row.LotId},{row.ExpiryDate:yyyy-MM-dd},");
            csv.Append(CultureInfo.InvariantCulture,
                $"{row.StockDelta:F3},{row.QuarantineDelta:F3},{row.TotalDelta:F3},{QuoteCsvText(row.Reason ?? "")},");
            csv.Append(CultureInfo.InvariantCulture,
                $"{row.SourceDocumentId},{row.SourceRegisterId},{row.ReceivingCost:F2},{row.SupplierCredit:F2}\r\n");
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }
}
