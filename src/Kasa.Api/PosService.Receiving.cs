using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<ReceivingProduct> GetReceivingProduct(HttpContext context, Guid store, string barcode,
        CancellationToken ct)
    {
        if (barcode.Length is < 1 or > 48 || !barcode.All(char.IsAsciiDigit))
            throw new PosError(400, "invalid_barcode");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await using var command = db.Sql(tx, """
            SELECT p.id, p.name, p.unit, p.requires_expiry, b.quantity_factor
            FROM products p JOIN store_prices sp ON (sp.org_id, sp.product_id) = (p.org_id, p.id)
            JOIN product_barcodes b ON (b.org_id, b.product_id) = (p.org_id, p.id)
            WHERE (p.org_id, sp.store_id, b.code) = ($1, $2, $3) AND sp.active
            """, scope.Organization, store, barcode);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(404, "product_not_found");
        return new ReceivingProduct(rows.GetGuid(0), rows.GetString(1), barcode, rows.GetString(2), rows.GetBoolean(3))
            { QuantityFactor = rows.GetDecimal(4) };
    }

    public async Task<ReceivingDocument> CreateReceipt(HttpContext context, Guid store, CreateReceipt request,
        CancellationToken ct)
    {
        if (request.ReceiptId == Guid.Empty || request.PurchaseOrderId == Guid.Empty || request.InvoiceDate == DateOnly.MinValue ||
            request.InvoiceDate == DateOnly.MaxValue || !IsText(request.SupplierRef, 160) ||
            !IsText(request.InvoiceNumber, 100))
            throw new PosError(400, "invalid_receipt");
        var items = ValidateReceiptItems(request.Items);
        var supplier = request.SupplierRef.Trim();
        var invoice = request.InvoiceNumber.Trim();
        var body = JsonSerializer.Serialize(items);

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await LockReceipt(db, tx, scope, request.ReceiptId, ct);
        var existing = await ReadReceipt(db, tx, scope, request.ReceiptId, ct);
        if (existing is not null)
        {
            RequireReceiptEditor(scope, existing);
            await using var original = db.Sql(tx, """
                SELECT 1 FROM receiving_documents
                WHERE (org_id, store_id, id) = ($1, $2, $3) AND (supplier_ref, invoice_number, invoice_date) = ($4, $5, $6)
                  AND creation_body = $7::jsonb AND purchase_order_id IS NOT DISTINCT FROM
                    NULLIF($8, '00000000-0000-0000-0000-000000000000'::uuid)
                """, scope.Organization, store, request.ReceiptId, supplier, invoice, request.InvoiceDate, body,
                request.PurchaseOrderId ?? Guid.Empty);
            if (await original.ExecuteScalarAsync(ct) is null)
                throw new PosError(409, "receipt_id_reused");
            return existing;
        }

        await ValidateReceivingProducts(db, tx, scope, items.Select(item => (item.ProductId, item.Quantity, item.ExpiryDate)),
            posting: false, ct, allowArchived: request.PurchaseOrderId is not null);
        if (request.PurchaseOrderId is null)
            await ValidateSupplier(db, tx, scope, supplier, ct);
        await ValidateReceiptOrder(db, tx, scope, request.PurchaseOrderId, supplier, items, ct);
        var inserted = await db.Run(tx, ct, """
            INSERT INTO receiving_documents
                (org_id, store_id, id, created_by, supplier_ref, invoice_number, invoice_date, draft_body,
                    creation_body, purchase_order_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8::jsonb, $8::jsonb,
                NULLIF($9, '00000000-0000-0000-0000-000000000000'::uuid))
            ON CONFLICT DO NOTHING
            """, scope.Organization, store, request.ReceiptId, scope.Staff, supplier, invoice, request.InvoiceDate, body, request.PurchaseOrderId ?? Guid.Empty);
        if (inserted != 1)
            throw new PosError(409, "duplicate_invoice");
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "receipt.created", request.ReceiptId, ct);
        await tx.CommitAsync(ct);
        return new ReceivingDocument(request.ReceiptId, supplier, invoice, request.InvoiceDate, "draft", 1,
            scope.Staff, null, null, null, items, request.PurchaseOrderId);
    }

    public async Task<ReceivingDocument> EditReceipt(HttpContext context, Guid store, Guid id, EditReceipt request,
        CancellationToken ct)
    {
        ValidateReceiptVersion(request.ExpectedVersion);
        var items = ValidateReceiptItems(request.Items);

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await LockReceipt(db, tx, scope, id, ct);
        var document = await ReadReceipt(db, tx, scope, id, ct) ?? throw new PosError(404, "receipt_not_found");
        RequireReceiptEditor(scope, document);
        if (document.Status == "draft" && document.Version == request.ExpectedVersion + 1 && document.Items.SequenceEqual(items))
            return document;
        if (document.Status != "draft" || document.Version != request.ExpectedVersion)
            throw new PosError(409, "receipt_version_conflict");

        await ValidateReceivingProducts(db, tx, scope, items.Select(item => (item.ProductId, item.Quantity, item.ExpiryDate)),
            posting: false, ct, allowArchived: document.PurchaseOrderId is not null);
        await ValidateReceiptOrder(db, tx, scope, document.PurchaseOrderId, document.SupplierRef, items, ct);
        await db.Run(tx, ct, """
            UPDATE receiving_documents SET draft_body = $4::jsonb, version = version + 1
            WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, store, id, JsonSerializer.Serialize(items));
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "receipt.edited", id, ct);
        await tx.CommitAsync(ct);
        return document with { Version = document.Version + 1, Items = items };
    }

    public async Task<ReceivingDocument> GetReceipt(HttpContext context, Guid store, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        return await ReadReceipt(db, tx, scope, id, ct) ?? throw new PosError(404, "receipt_not_found");
    }

    public async Task<IReadOnlyList<ReceivingDocument>> ListReceipts(HttpContext context, Guid store, string? status,
        int? offset, CancellationToken ct)
    {
        if (status is not (null or "draft" or "submitted" or "posted" or "cancelled") || offset is < 0 or > 10_000)
            throw new PosError(400, "invalid_receipt_filter");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await using var command = db.Sql(tx, """
            SELECT id, supplier_ref, invoice_number, invoice_date, status, version, created_by, posted_by,
                   posted_at, review_note, draft_body, purchase_order_id, cancelled_by, cancelled_at, cancellation_reason
            FROM receiving_documents
            WHERE (org_id, store_id) = ($1, $2) AND ($3 = '' OR status = $3)
            ORDER BY invoice_date DESC, id LIMIT 25 OFFSET $4
            """, scope.Organization, store, status ?? "", offset ?? 0);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ReceivingDocument> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(ReadReceiptRow(rows));
        return result;
    }

    public Task<ReceivingDocument> SubmitReceipt(HttpContext context, Guid store, Guid id, ReceiptTransition request,
        CancellationToken ct) =>
        TransitionReceipt(context, store, id, request.ExpectedVersion, "draft", "submitted", "", ct);

    public Task<ReceivingDocument> ReturnReceipt(HttpContext context, Guid store, Guid id, ReturnReceipt request,
        CancellationToken ct)
    {
        if (!IsText(request.Reason, 500))
            throw new PosError(400, "invalid_review_reason");
        return TransitionReceipt(context, store, id, request.ExpectedVersion, "submitted", "draft", request.Reason.Trim(), ct);
    }

    public Task<ReceivingDocument> PostReceipt(HttpContext context, Guid store, Guid id, ReceiptTransition request,
        CancellationToken ct) =>
        TransitionReceipt(context, store, id, request.ExpectedVersion, "submitted", "posted", "", ct);

    public async Task<ReceivingDocument> CancelReceipt(HttpContext context, Guid store, Guid id,
        CancelReceipt request, CancellationToken ct)
    {
        ValidateReceiptVersion(request.ExpectedVersion);
        if (!IsText(request.Reason, 500))
            throw new PosError(400, "invalid_receipt_cancellation_reason");
        var reason = request.Reason.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await LockReceipt(db, tx, scope, id, ct);
        var document = await ReadReceipt(db, tx, scope, id, ct) ?? throw new PosError(404, "receipt_not_found");
        RequireReceiptEditor(scope, document);
        if (document.Status == "cancelled" && document.Version == request.ExpectedVersion + 1 &&
            document.Cancellation is { } cancellation && cancellation.Reason == reason &&
            cancellation.CancelledBy == scope.Staff)
            return document;
        if (document.Status == "submitted" && scope.Role != "admin")
            throw new PosError(403, "receipt_cancel_requires_admin");
        if (document.Version != request.ExpectedVersion || document.Status is not ("draft" or "submitted"))
            throw new PosError(409, "receipt_version_conflict");

        await using var update = db.Sql(tx, """
            UPDATE receiving_documents
            SET status = 'cancelled', version = version + 1, cancelled_by = $4,
                cancelled_at = statement_timestamp(), cancellation_reason = $5
            WHERE (org_id, store_id, id) = ($1, $2, $3)
            RETURNING cancelled_at
            """, scope.Organization, store, id, scope.Staff, reason);
        var cancelledAt = (DateTime)(await update.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Cancelled receipt missing."));
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "receipt.cancelled", id, ct);
        await tx.CommitAsync(ct);
        return document with
        {
            Status = "cancelled",
            Version = document.Version + 1,
            Cancellation = new ReceiptCancellation(scope.Staff, cancelledAt, reason)
        };
    }

    private async Task<ReceivingDocument> TransitionReceipt(HttpContext context, Guid store, Guid id, int version,
        string from, string to, string reviewNote, CancellationToken ct)
    {
        ValidateReceiptVersion(version);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, to != "submitted", ct);
        await LockReceipt(db, tx, scope, id, ct);
        var document = await ReadReceipt(db, tx, scope, id, ct) ?? throw new PosError(404, "receipt_not_found");
        if (to == "submitted")
            RequireReceiptEditor(scope, document);
        if (document.Status == to && document.Version == version + 1 &&
            (to != "draft" || document.ReviewNote == reviewNote))
            return document;
        if (document.Status != from || document.Version != version)
            throw new PosError(409, "receipt_version_conflict");

        if (to is "submitted" or "posted")
            await RequireVerifiedInvoiceFiles(db, tx, scope, id, ct);

        if (to == "posted")
        {
            await LockLotCreationProducts(db, tx, scope, document.Items.Select(item => item.ProductId), ct);
            var costs = await CostsForPosting(db, tx, scope, document, ct);
            await ValidateReceivingProducts(db, tx, scope,
                document.Items.Select(item => (item.ProductId, item.Quantity, item.ExpiryDate)), posting: true, ct);
            if (document.PurchaseOrderId is { } orderId)
                await ApplyOrderReceipt(db, tx, scope, orderId, document.Items, ct);
            var costByLot = costs?.Lines.ToDictionary(line => (line.ProductId, line.LotId), line => line.Total);
            foreach (var item in document.Items)
                await ReceiveLot(db, tx, scope, id, item, costByLot?[(item.ProductId, item.LotId)], ct);
            await RecordPostedCosts(db, tx, scope, costs, ct);
        }

        await db.Run(tx, ct, """
            UPDATE receiving_documents SET status = $4, version = version + 1, review_note = NULLIF($5, ''),
                posted_by = CASE WHEN $4 = 'posted' THEN $6 ELSE NULL END,
                posted_at = CASE WHEN $4 = 'posted' THEN statement_timestamp() ELSE NULL END
            WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, store, id, to, reviewNote, scope.Staff);
        var action = to == "draft" ? "receipt.returned_for_review" : "receipt." + to;
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, action, id, ct);
        var result = await ReadReceipt(db, tx, scope, id, ct)
            ?? throw new InvalidOperationException("Updated receipt missing.");
        await tx.CommitAsync(ct);
        return result;
    }

    private static ReceiptItem[] ValidateReceiptItems(ReceiptItem[]? items)
    {
        if (items is not { Length: >= 1 and <= 100 })
            throw new PosError(400, "invalid_receipt_items");
        var distinct = new HashSet<(Guid, Guid)>();
        foreach (var item in items)
        {
            if (item is null || item.ProductId == Guid.Empty || item.LotId == Guid.Empty || !IsQuantity(item.Quantity) ||
                item.ExpiryDate is { } date && (date == DateOnly.MinValue || date == DateOnly.MaxValue) ||
                !distinct.Add((item.ProductId, item.LotId)))
                throw new PosError(400, "invalid_receipt_item");
        }
        return items.OrderBy(item => item.ProductId).ThenBy(item => item.LotId).ToArray();
    }

    private static void ValidateReceiptVersion(int version)
    {
        if (version <= 0 || version == int.MaxValue)
            throw new PosError(400, "invalid_receipt_version");
    }

    private static void RequireReceiptEditor(StoreScope scope, ReceivingDocument document)
    {
        if (scope.Role != "admin" && scope.Staff != document.CreatedBy)
            throw new PosError(403, "receipt_edit_denied");
    }

    private static Task LockReceipt(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid id,
        CancellationToken ct) => LockOperation(db, tx, $"receipt:{scope.Organization}:{scope.Store}:{id}", ct);

    private static async Task ValidateReceivingProducts(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        IEnumerable<(Guid ProductId, decimal Quantity, DateOnly? ExpiryDate)> items, bool posting, CancellationToken ct,
        bool allowArchived = false)
    {
        await using var command = db.Sql(tx, """
            SELECT p.id, p.unit, p.requires_expiry, (statement_timestamp() AT TIME ZONE $4)::date
            FROM products p JOIN store_prices sp ON (sp.org_id, sp.product_id) = (p.org_id, p.id)
            WHERE (p.org_id, sp.store_id) = ($1, $2) AND p.id = ANY($3) AND (sp.active OR $5)
            """, scope.Organization, scope.Store, items.Select(item => item.ProductId).Distinct().ToArray(),
            scope.TimeZone, posting || allowArchived);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var products = new Dictionary<Guid, (string Unit, bool RequiresExpiry, DateOnly Today)>();
        while (await rows.ReadAsync(ct))
            products.Add(rows.GetGuid(0), (rows.GetString(1), rows.GetBoolean(2), rows.GetFieldValue<DateOnly>(3)));

        foreach (var item in items)
        {
            if (!products.TryGetValue(item.ProductId, out var product))
                throw new PosError(409, "receipt_product_not_available");
            if (product.Unit == "piece" && item.Quantity != decimal.Truncate(item.Quantity))
                throw new PosError(400, "fractional_piece_quantity");
            if (posting && (item.ExpiryDate < product.Today || (product.RequiresExpiry && item.ExpiryDate is null)))
                throw new PosError(409, "receipt_expiry_invalid");
        }
    }

    private static async Task ReceiveLot(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid id,
        ReceiptItem item, decimal? cost, CancellationToken ct)
    {
        var expiry = item.ExpiryDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "";
        var inserted = await db.Run(tx, ct, """
            INSERT INTO stock_lots (org_id, store_id, product_id, id, expiry_date, quantity, inventory_value)
            VALUES ($1, $2, $3, $4, NULLIF($5, '')::date, $6, $8)
            ON CONFLICT (org_id, store_id, product_id, id) DO UPDATE
            SET quantity = stock_lots.quantity + EXCLUDED.quantity,
                inventory_value = kasa_stock_value_after(stock_lots.inventory_value,
                    stock_lots.quantity + stock_lots.quarantine_quantity,
                    stock_lots.quantity + stock_lots.quarantine_quantity + EXCLUDED.quantity, EXCLUDED.inventory_value)
            WHERE stock_lots.expiry_date IS NOT DISTINCT FROM EXCLUDED.expiry_date
              AND NOT stock_lots.quarantined AND stock_lots.quantity + EXCLUDED.quantity <= $7
            """, scope.Organization, scope.Store, item.ProductId, item.LotId, expiry, item.Quantity, MaxQuantity, cost);
        if (inserted != 1)
            throw new PosError(409, "receipt_lot_conflict");
        await db.Run(tx, ct, """
            INSERT INTO receiving_movements (org_id, store_id, receipt_id, product_id, lot_id, quantity)
            VALUES ($1, $2, $3, $4, $5, $6)
            """, scope.Organization, scope.Store, id, item.ProductId, item.LotId, item.Quantity);
    }

    private static async Task<ReceivingDocument?> ReadReceipt(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT id, supplier_ref, invoice_number, invoice_date, status, version, created_by, posted_by,
                   posted_at, review_note, draft_body, purchase_order_id, cancelled_by, cancelled_at, cancellation_reason
            FROM receiving_documents WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? ReadReceiptRow(rows) : null;
    }

    private static ReceivingDocument ReadReceiptRow(NpgsqlDataReader rows) =>
        new(rows.GetGuid(0), rows.GetString(1), rows.GetString(2), rows.GetFieldValue<DateOnly>(3), rows.GetString(4),
            rows.GetInt32(5), rows.GetGuid(6), rows.IsDBNull(7) ? null : rows.GetGuid(7), rows.TimeOrNull(8),
            rows.IsDBNull(9) ? null : rows.GetString(9),
            JsonSerializer.Deserialize<ReceiptItem[]>(rows.GetString(10))
                ?? throw new InvalidOperationException("Receipt items missing."),
            rows.IsDBNull(11) ? null : rows.GetGuid(11))
        {
            Cancellation = rows.IsDBNull(12) ? null : new ReceiptCancellation(rows.GetGuid(12), rows.GetDateTime(13),
                rows.GetString(14))
        };
}
