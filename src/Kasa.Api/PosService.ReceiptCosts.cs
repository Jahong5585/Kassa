using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<ReceiptCosts> GetReceiptCosts(HttpContext context, Guid store, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        var document = await ReadReceipt(db, tx, scope, id, ct) ?? throw new PosError(404, "receipt_not_found");
        var costs = await ReadCosts(db, tx, scope, id, ct) ?? throw new PosError(404, "receipt_costs_not_found");
        var status = document.Status == "posted" ? "posted" :
            costs.ReceiptVersion == document.Version ? "reviewed" : "stale";
        return costs with { Status = status };
    }

    public async Task<ReceiptCosts> SaveReceiptCosts(HttpContext context, Guid store, Guid id,
        WriteReceiptCosts request, CancellationToken ct)
    {
        ValidateReceiptVersion(request.ExpectedVersion);
        if (request.Items is not { Length: >= 1 and <= 100 })
            throw new PosError(400, "invalid_receipt_costs");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockReceipt(db, tx, scope, id, ct);
        var document = await ReadReceipt(db, tx, scope, id, ct) ?? throw new PosError(404, "receipt_not_found");
        if (document.Status == "draft")
            throw new PosError(409, "receipt_not_under_review");
        var lines = CalculateReceiptCosts(document.Items, request.Items);
        var total = lines.Sum(line => line.Total);
        if (!IsMoney(total))
            throw new PosError(400, "invalid_receipt_cost_total");

        var existing = await ReadCosts(db, tx, scope, id, ct);
        if (existing is not null && existing.ReceiptVersion == request.ExpectedVersion + 1 &&
            existing.Lines.SequenceEqual(lines) &&
            (document.Version == existing.ReceiptVersion ||
                document.Status == "posted" && document.Version == existing.ReceiptVersion + 1))
            return existing with { Status = document.Status == "posted" ? "posted" : "reviewed" };
        if (document.Status != "submitted" || document.Version != request.ExpectedVersion)
            throw new PosError(409, "receipt_version_conflict");

        var version = document.Version + 1;
        await db.Run(tx, ct, """
            INSERT INTO receipt_costs (org_id, store_id, receipt_id, receipt_version, updated_by, body, total)
            VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7)
            ON CONFLICT (org_id, store_id, receipt_id) DO UPDATE SET receipt_version = EXCLUDED.receipt_version,
                updated_by = EXCLUDED.updated_by, body = EXCLUDED.body, total = EXCLUDED.total
            """, scope.Organization, store, id, version, scope.Staff, JsonSerializer.Serialize(lines), total);
        await db.Run(tx, ct, """
            UPDATE receiving_documents SET version = $4 WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, store, id, version);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "receipt.costs_reviewed", id, ct);
        await tx.CommitAsync(ct);
        return new ReceiptCosts(id, version, "reviewed", total, lines);
    }

    private static ReceiptCostLine[] CalculateReceiptCosts(ReceiptItem[] items, ReceiptCostItem[] costs)
    {
        var quantities = items.ToDictionary(item => (item.ProductId, item.LotId), item => item.Quantity);
        if (quantities.Count != costs.Length)
            throw new PosError(400, "receipt_costs_require_all_items");
        List<ReceiptCostLine> lines = [];
        foreach (var cost in costs)
        {
            if (cost is null || !IsMoney(cost.UnitPrice) || !IsMoney(cost.Discount) ||
                !IsMoney(cost.IncludedTax) || !IsMoney(cost.AllocatedCharges) ||
                !quantities.Remove((cost.ProductId, cost.LotId), out var quantity))
                throw new PosError(400, "invalid_receipt_cost_item");
            var gross = decimal.Round(quantity * cost.UnitPrice, 2, MidpointRounding.AwayFromZero);
            var total = gross - cost.Discount + cost.IncludedTax + cost.AllocatedCharges;
            if (cost.Discount > gross || !IsMoney(gross) || !IsMoney(total))
                throw new PosError(400, "invalid_receipt_cost_amount");
            lines.Add(new ReceiptCostLine(cost.ProductId, cost.LotId, quantity, cost.UnitPrice, cost.Discount,
                cost.IncludedTax, cost.AllocatedCharges, total));
        }
        return lines.OrderBy(line => line.ProductId).ThenBy(line => line.LotId).ToArray();
    }

    private static async Task<ReceiptCosts?> ReadCosts(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT receipt_version, total, body FROM receipt_costs WHERE (org_id, store_id, receipt_id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? new ReceiptCosts(id, rows.GetInt32(0), "reviewed", rows.GetDecimal(1),
            JsonSerializer.Deserialize<ReceiptCostLine[]>(rows.GetString(2))
                ?? throw new InvalidOperationException("Receipt cost lines missing.")) : null;
    }

    private async Task<ReceiptCosts?> CostsForPosting(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        ReceivingDocument document, CancellationToken ct)
    {
        var costs = await ReadCosts(db, tx, scope, document.ReceiptId, ct);
        if (costs is not null && costs.ReceiptVersion != document.Version)
            throw new PosError(409, "receipt_costs_stale");
        if (costs is null && configuration.GetValue<bool>("Purchasing:RequireReceiptCosts"))
            throw new PosError(409, "receipt_costs_required");
        return costs;
    }

    private static async Task RecordPostedCosts(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        ReceiptCosts? costs, CancellationToken ct)
    {
        if (costs is null)
            return;
        foreach (var line in costs.Lines)
        {
            await db.Run(tx, ct, """
                INSERT INTO receiving_movement_costs (org_id, store_id, receipt_id, product_id, lot_id, total)
                VALUES ($1, $2, $3, $4, $5, $6)
                """, scope.Organization, scope.Store, costs.ReceiptId, line.ProductId, line.LotId, line.Total);
        }
    }
}
