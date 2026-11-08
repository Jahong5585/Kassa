using System.Globalization;
using System.Text;
using Microsoft.VisualBasic.FileIO;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private static readonly string[] OpeningStockCsvHeader =
        ["productId", "lotId", "expiryDate", "stockQuantity", "quarantineQuantity", "totalCost"];

    public async Task<OpeningStockReport> ImportOpeningStockBalances(HttpContext context, Guid store,
        ImportOpeningStock request, bool preview, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty || !IsText(request.SourceReference, 300) || !IsText(request.Reason, 500) ||
            (request.Items is null) == (request.Csv is null))
            throw new PosError(400, "invalid_opening_stock_import");
        var sourceReference = request.SourceReference.Trim();
        var reason = request.Reason.Trim();
        var input = request.Items ?? ParseOpeningStockCsv(request.Csv!);
        if (input is not { Length: >= 1 and <= 100 })
            throw new PosError(400, "invalid_opening_stock_import");
        var lineNumbers = new Dictionary<(Guid, Guid), int>();
        for (var i = 0; i < input.Length; i++)
        {
            var item = input[i];
            var row = request.Csv is null ? i + 1 : i + 2;
            if (item is null || item.ProductId == Guid.Empty || item.LotId == Guid.Empty ||
                item.ExpiryDate is { } expiry && !IsBusinessDate(expiry) ||
                !IsStockQuantity(item.StockQuantity) || !IsStockQuantity(item.QuarantineQuantity) ||
                item.StockQuantity + item.QuarantineQuantity == 0 || item.TotalCost is { } cost && !IsMoney(cost))
                throw new PosError(400, "invalid_opening_stock_line", row);
            if (!lineNumbers.TryAdd((item.ProductId, item.LotId), row))
                throw new PosError(400, "duplicate_opening_stock_lot", row);
        }
        var items = input.OrderBy(item => item.ProductId).ThenBy(item => item.LotId).ToArray();

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockStockOperation(db, tx, scope, request.OperationId, ct);
        var existing = await ReadOpeningStock(db, tx, scope, request.OperationId, ct);
        if (existing is not null)
        {
            if (existing.SourceReference != sourceReference || existing.Reason != reason ||
                !existing.Items.Select(item => new OpeningStockItem(item.ProductId, item.LotId, item.ExpiryDate,
                    item.StockQuantity, item.QuarantineQuantity, item.TotalCost)).SequenceEqual(items))
                throw new PosError(409, "opening_stock_id_reused");
            return existing;
        }

        var ids = items.Select(item => item.ProductId).Distinct().ToArray();
        await LockLotCreationProducts(db, tx, scope, ids, ct);
        var id = request.OperationId;
        DateTime? postedAt = null;
        if (preview)
        {
            await using var usedId = db.Sql(tx, """
                SELECT 1 FROM stock_operations WHERE (org_id, store_id, id) = ($1, $2, $3)
                """, scope.Organization, store, id);
            if (await usedId.ExecuteScalarAsync(ct) is not null)
                throw new PosError(409, "stock_operation_id_reused");
        }
        else
        {
            postedAt = await InsertStockOperation(db, tx, scope, id, "opening", reason, ct);
        }
        var today = await StoreDate(db, tx, scope, ct, postedAt);
        await using var products = db.Sql(tx, """
            SELECT p.id, p.unit, p.requires_expiry, EXISTS (
                SELECT 1 FROM stock_lots l WHERE (l.org_id, l.store_id, l.product_id) = (sp.org_id, sp.store_id, sp.product_id)
            )
            FROM store_prices sp JOIN products p ON (p.org_id, p.id) = (sp.org_id, sp.product_id)
            WHERE (sp.org_id, sp.store_id) = ($1, $2) AND sp.product_id = ANY($3)
            """, scope.Organization, store, ids);
        var available = new Dictionary<Guid, (string Unit, bool RequiresExpiry, bool HasLots)>();
        await using (var rows = await products.ExecuteReaderAsync(ct))
            while (await rows.ReadAsync(ct))
                available.Add(rows.GetGuid(0), (rows.GetString(1), rows.GetBoolean(2), rows.GetBoolean(3)));

        List<OpeningStockLine> lines = [];
        foreach (var item in items)
        {
            var row = lineNumbers[(item.ProductId, item.LotId)];
            if (!available.TryGetValue(item.ProductId, out var product))
                throw new PosError(404, "opening_stock_product_not_found", row);
            if (product.HasLots)
                throw new PosError(409, "opening_stock_product_has_lots", row);
            try
            {
                ValidateLotQuantity(product.Unit, item.StockQuantity);
                ValidateLotQuantity(product.Unit, item.QuarantineQuantity);
            }
            catch (PosError ex)
            {
                throw new PosError(ex.Status, ex.Code, row);
            }
            if (item.StockQuantity > 0 && (item.ExpiryDate < today || product.RequiresExpiry && item.ExpiryDate is null))
                throw new PosError(409, "opening_stock_expiry_invalid", row);
            lines.Add(new OpeningStockLine(item.ProductId, item.LotId, item.ExpiryDate, product.Unit,
                item.StockQuantity, item.QuarantineQuantity, item.TotalCost));
        }
        if (preview)
            return new OpeningStockReport(request.OperationId, sourceReference, reason, today, scope.Staff, null, lines);

        await db.Run(tx, ct, """
            INSERT INTO opening_stock_batches (org_id, store_id, id, opening_date, source_reference)
            VALUES ($1, $2, $3, $4, $5)
            """, scope.Organization, store, id, today, sourceReference);
        foreach (var item in lines)
        {
            var expiry = item.ExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
            await db.Run(tx, ct, """
                INSERT INTO stock_lots (org_id, store_id, product_id, id, expiry_date, quantity, quarantine_quantity, inventory_value)
                VALUES ($1, $2, $3, $4, NULLIF($5, '')::date, $6, $7, $8)
                """, scope.Organization, store, item.ProductId, item.LotId, expiry, item.StockQuantity, item.QuarantineQuantity,
                item.TotalCost);
            await RecordStockOperationLine(db, tx, scope, id,
                new StockOperationLine(item.ProductId, item.LotId, 0, item.StockQuantity, 0, item.QuarantineQuantity)
                { InventoryCostDelta = item.TotalCost }, ct);
            await db.Run(tx, ct, """
                INSERT INTO opening_stock_lines (org_id, store_id, operation_id, product_id, lot_id, expiry_date, total_cost)
                VALUES ($1, $2, $3, $4, $5, NULLIF($6, '')::date, $7)
                """, scope.Organization, store, id, item.ProductId, item.LotId, expiry, item.TotalCost);
        }
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "stock.opening_posted", id, ct);
        await tx.CommitAsync(ct);
        return new OpeningStockReport(id, sourceReference, reason, today, scope.Staff, postedAt, lines);
    }

    public async Task<OpeningStockReport> GetOpeningStockReport(HttpContext context, Guid store, Guid id,
        CancellationToken ct, bool export = false)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        var report = await ReadOpeningStock(db, tx, scope, id, ct)
            ?? throw new PosError(404, "opening_stock_not_found");
        if (export)
        {
            await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
                "stock.opening_csv_exported", id, ct);
            await tx.CommitAsync(ct);
        }
        return report;
    }

    private static async Task<OpeningStockReport?> ReadOpeningStock(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT b.source_reference, o.reason, b.opening_date, o.actor_id, o.created_at,
                   l.product_id, l.lot_id, l.expiry_date, p.unit, m.stock_after, m.quarantine_after, l.total_cost
            FROM opening_stock_batches b JOIN stock_operations o
              ON (o.org_id, o.store_id, o.id) = (b.org_id, b.store_id, b.id)
            JOIN opening_stock_lines l
              ON (l.org_id, l.store_id, l.operation_id) = (b.org_id, b.store_id, b.id)
            JOIN stock_operation_lines m
              ON (m.org_id, m.store_id, m.operation_id, m.product_id, m.lot_id) =
                 (l.org_id, l.store_id, l.operation_id, l.product_id, l.lot_id)
            JOIN products p ON (p.org_id, p.id) = (l.org_id, l.product_id)
            WHERE (b.org_id, b.store_id, b.id) = ($1, $2, $3)
            ORDER BY l.product_id, l.lot_id
            """, scope.Organization, scope.Store, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            return null;
        List<OpeningStockLine> items = [];
        var report = new OpeningStockReport(id, rows.GetString(0), rows.GetString(1), rows.GetFieldValue<DateOnly>(2),
            rows.GetGuid(3), rows.GetDateTime(4), items);
        do
        {
            items.Add(new OpeningStockLine(rows.GetGuid(5), rows.GetGuid(6), ReadDate(rows, 7), rows.GetString(8),
                rows.GetDecimal(9), rows.GetDecimal(10), rows.DecimalOrNull(11)));
        } while (await rows.ReadAsync(ct));
        return report;
    }

    private static OpeningStockItem[] ParseOpeningStockCsv(string csv)
    {
        if (!IsText(csv, 48_000))
            throw new PosError(400, "invalid_opening_stock_csv");
        using var parser = new TextFieldParser(new StringReader(csv.TrimStart('\uFEFF')));
        parser.SetDelimiters(",");
        parser.HasFieldsEnclosedInQuotes = true;
        parser.TrimWhiteSpace = true;
        List<OpeningStockItem> items = [];
        try
        {
            if (parser.ReadFields() is not { } header || !header.SequenceEqual(OpeningStockCsvHeader))
                throw new PosError(400, "invalid_opening_stock_csv_header", 1);
            while (!parser.EndOfData)
            {
                var row = items.Count + 2;
                if (items.Count == 100 || parser.ReadFields() is not { Length: 6 } fields ||
                    !Guid.TryParseExact(fields[0], "D", out var product) ||
                    !Guid.TryParseExact(fields[1], "D", out var lot) ||
                    !TryOpeningStockDate(fields[2], out var expiry) ||
                    !TryOpeningStockNumber(fields[3], out var stock) ||
                    !TryOpeningStockNumber(fields[4], out var quarantine) ||
                    !TryOpeningStockNumber(fields[5].Length == 0 ? "0" : fields[5], out var cost))
                    throw new PosError(400, "invalid_opening_stock_csv", row);
                items.Add(new OpeningStockItem(product, lot, expiry, stock, quarantine, fields[5].Length == 0 ? null : cost));
            }
        }
        catch (MalformedLineException ex)
        {
            throw new PosError(400, "invalid_opening_stock_csv", (int)ex.LineNumber);
        }
        return items.ToArray();
    }

    private static bool TryOpeningStockDate(string text, out DateOnly? date)
    {
        date = null;
        if (text.Length == 0)
            return true;
        if (!DateOnly.TryParseExact(text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return false;
        date = parsed;
        return true;
    }

    private static bool TryOpeningStockNumber(string text, out decimal value) =>
        decimal.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value);

    private static async Task LockLotCreationProducts(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        IEnumerable<Guid> products, CancellationToken ct)
    {
        // Normal receipts and opening balances serialize before checking or creating lots for the same product.
        await LockInventoryMembership(db, tx, scope.Organization, ct, shared: true);
        foreach (var product in products.Distinct().Order())
            await LockOperation(db, tx, $"lot_creation:{scope.Organization}:{scope.Store}:{product}", ct);
    }

    internal static byte[] FormatOpeningStockCsv(OpeningStockReport report)
    {
        var csv = new StringBuilder("\uFEFF");
        csv.Append("operation_id,source_reference,reason,opening_date,actor_id,posted_at,product_id,lot_id,expiry_date,")
            .Append("unit,stock_quantity,quarantine_quantity,total_cost\r\n");
        var document = FormattableString.Invariant(
            $"{report.OperationId},{QuoteCsvText(report.SourceReference)},{QuoteCsvText(report.Reason)},{report.OpeningDate:yyyy-MM-dd},{report.ActorId},{report.PostedAt:O}");
        foreach (var item in report.Items)
        {
            csv.Append(CultureInfo.InvariantCulture,
                $"{document},{item.ProductId},{item.LotId},{item.ExpiryDate:yyyy-MM-dd},{item.Unit},{item.StockQuantity:F3},{item.QuarantineQuantity:F3},{item.TotalCost:F2}\r\n");
        }
        return Encoding.UTF8.GetBytes(csv.ToString());
    }
}
