using System.Data;
using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<PurchaseDraft> SavePurchaseDraft(HttpContext context, Guid store, Guid id, SavePurchaseDraft request,
        CancellationToken ct)
    {
        if (id == Guid.Empty || request.ExpectedRevision is < 0 or int.MaxValue ||
            !IsText(request.SupplierCode, 32) || !IsBusinessDate(request.ExpectedDeliveryDate) ||
            request.Items is not { Length: >= 1 and <= 100 } ||
            request.Items.Any(item => item is null || item.PriceVersion < 1 || item.Packages <= 0 ||
                !IsText(item.SupplierSku, 32)))
            throw new PosError(400, "invalid_purchase_draft");
        var code = SupplierCode(request.SupplierCode);
        var items = request.Items.Select(item => item with { SupplierSku = SupplierCode(item.SupplierSku) })
            .OrderBy(item => item.SupplierSku, StringComparer.Ordinal).ToArray();
        if (items.Select(item => item.SupplierSku).Distinct().Count() != items.Length)
            throw new PosError(400, "duplicate_supplier_sku");
        var replenishment = request.Replenishment;
        if (replenishment is not null)
        {
            if (items.Length != 1 || replenishment.Settings is null || replenishment.PolicyVersion is <= 0)
                throw new PosError(400, "invalid_purchase_replenishment");
            replenishment = replenishment with { Settings = NormalizeReplenishmentSettings(replenishment.Settings) };
        }

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(
            replenishment is null ? IsolationLevel.ReadCommitted : IsolationLevel.RepeatableRead, ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockPurchaseOrder(db, tx, scope, id, ct);
        var current = await ReadPurchaseDraft(db, tx, scope, id, ct);
        if (current is not null && current.Revision == request.ExpectedRevision + 1 && current.SupplierCode == code &&
            current.ExpectedDeliveryDate == request.ExpectedDeliveryDate &&
            SamePurchaseReplenishment(current.Replenishment, replenishment) &&
            current.Lines.Select(line => new OrderOffer(line.SupplierSku, line.PriceVersion, line.Packages)).SequenceEqual(items))
            return current;
        if ((current?.Revision ?? 0) != request.ExpectedRevision || current?.Stage == "sent" ||
            current is not null && current.SupplierCode != code)
            throw new PosError(409, "purchase_order_revision_conflict");
        if (current?.Replenishment is not null && replenishment is null)
            throw new PosError(409, "purchase_replenishment_origin_required");

        await RequireActiveSupplier(db, tx, scope, code, ct);
        var today = await StoreDate(db, tx, scope, ct);
        if (request.ExpectedDeliveryDate < today)
            throw new PosError(400, "invalid_delivery_date");
        if (replenishment is not null)
            await VerifyPurchaseReplenishment(db, tx, scope, code, request.ExpectedDeliveryDate, items[0], replenishment, ct);
        var quoted = await QuotePurchaseOrder(db, tx, scope, code, items, today, ct);
        var total = quoted.Sum(line => line.Total);
        if (!IsMoney(total) || total == 0)
            throw new PosError(400, "invalid_purchase_order_total");
        var revision = request.ExpectedRevision + 1;
        if (current is null)
        {
            var inserted = await db.Run(tx, ct, """
                INSERT INTO purchase_orders (org_id, store_id, id, supplier_ref, created_by, stage, expected_delivery_date)
                VALUES ($1, $2, $3, $4, $5, 'draft', $6) ON CONFLICT DO NOTHING
                """, scope.Organization, store, id, code, scope.Staff, request.ExpectedDeliveryDate);
            if (inserted != 1)
                throw new PosError(409, "purchase_order_id_reused");
        }
        else
        {
            await db.Run(tx, ct, """
                UPDATE purchase_orders SET stage = 'draft', revision = $4, expected_delivery_date = $5,
                    approved_revision = NULL, approved_by = NULL, approved_at = NULL
                WHERE (org_id, store_id, id) = ($1, $2, $3)
                """, scope.Organization, store, id, revision, request.ExpectedDeliveryDate);
        }

        await db.Run(tx, ct, """
            INSERT INTO purchase_order_revisions (org_id, store_id, order_id, version, expected_delivery_date,
                body, total, created_by, replenishment) VALUES ($1, $2, $3, $4, $5, $6::jsonb, $7, $8, NULLIF($9, '')::jsonb)
            """, scope.Organization, store, id, revision, request.ExpectedDeliveryDate,
            JsonSerializer.Serialize(quoted), total, scope.Staff,
            replenishment is null ? "" : JsonSerializer.Serialize(replenishment));
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "purchase_order.draft_saved",
            id, ct, revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await CommitPurchaseDraft(db, tx, scope, id, ct);
    }

    public async Task<PurchaseDraft> ApprovePurchaseDraft(HttpContext context, Guid store, Guid id,
        ApprovePurchaseOrder request, CancellationToken ct)
    {
        ValidateReceiptVersion(request.ExpectedRevision);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockPurchaseOrder(db, tx, scope, id, ct);
        var draft = await ReadPurchaseDraft(db, tx, scope, id, ct) ?? throw new PosError(404, "purchase_draft_not_found");
        if (draft.Revision != request.ExpectedRevision)
            throw new PosError(409, "purchase_order_revision_conflict");
        if (draft.Stage is "approved" or "sent")
            return draft;

        await RequireActiveSupplier(db, tx, scope, draft.SupplierCode, ct);
        var today = await StoreDate(db, tx, scope, ct);
        if (draft.ExpectedDeliveryDate < today)
            throw new PosError(409, "purchase_delivery_date_passed");
        var items = draft.Lines.Select(line => new OrderOffer(line.SupplierSku, line.PriceVersion, line.Packages)).ToArray();
        await QuotePurchaseOrder(db, tx, scope, draft.SupplierCode, items, today, ct);
        if (draft.Replenishment is { } replenishment)
            await VerifyPurchaseReplenishment(db, tx, scope, draft.SupplierCode, draft.ExpectedDeliveryDate,
                items.Single(), replenishment, ct);
        await db.Run(tx, ct, """
            UPDATE purchase_orders SET stage = 'approved', approved_revision = revision, approved_by = $4,
                approved_at = statement_timestamp() WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, store, id, scope.Staff);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "purchase_order.approved", id, ct,
            draft.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await CommitPurchaseDraft(db, tx, scope, id, ct);
    }

    public async Task<PurchaseDraft> MarkPurchaseSent(HttpContext context, Guid store, Guid id,
        DispatchPurchaseOrder request, CancellationToken ct)
    {
        ValidateReceiptVersion(request.ExpectedRevision);
        if (!IsText(request.Reference, 300))
            throw new PosError(400, "invalid_dispatch_reference");
        var reference = request.Reference.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockPurchaseOrder(db, tx, scope, id, ct);
        var draft = await ReadPurchaseDraft(db, tx, scope, id, ct) ?? throw new PosError(404, "purchase_draft_not_found");
        if (draft.Revision != request.ExpectedRevision)
            throw new PosError(409, "purchase_order_revision_conflict");
        if (draft.Stage == "sent" && draft.DispatchReference == reference)
            return draft;
        if (draft.Stage != "approved")
            throw new PosError(409, "purchase_order_approval_required");

        foreach (var group in draft.Lines.GroupBy(line => line.ProductId).OrderBy(group => group.Key))
        {
            await db.Run(tx, ct, """
                INSERT INTO purchase_order_lines (org_id, store_id, order_id, product_id, ordered)
                VALUES ($1, $2, $3, $4, $5)
                """, scope.Organization, store, id, group.Key, group.Sum(line => line.BaseQuantity));
        }
        await db.Run(tx, ct, """
            UPDATE purchase_orders SET stage = 'sent', sent_by = $4, sent_at = statement_timestamp(), dispatch_reference = $5
            WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, store, id, scope.Staff, reference);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "purchase_order.sent", id, ct,
            draft.Revision.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return await CommitPurchaseDraft(db, tx, scope, id, ct);
    }

    public async Task<PurchaseDraft> GetPurchaseDraft(HttpContext context, Guid store, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadPurchaseDraft(db, tx, scope, id, ct) ?? throw new PosError(404, "purchase_draft_not_found");
    }

    public async Task<IReadOnlyList<PurchaseDraftSummary>> ListPurchaseDrafts(HttpContext context, Guid store, string? stage,
        int? offset, CancellationToken ct)
    {
        ValidateStockOffset(offset);
        if (stage is not (null or "draft" or "approved" or "sent"))
            throw new PosError(400, "invalid_purchase_order_stage");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, """
            SELECT p.id, p.supplier_ref, p.stage, p.revision, p.expected_delivery_date, r.total
            FROM purchase_orders p JOIN purchase_order_revisions r
                ON (r.org_id, r.store_id, r.order_id, r.version) = (p.org_id, p.store_id, p.id, p.revision)
            WHERE (p.org_id, p.store_id) = ($1, $2) AND ($3 = '' OR p.stage = $3)
            ORDER BY r.created_at DESC, p.id LIMIT 25 OFFSET $4
            """, scope.Organization, store, stage ?? "", offset ?? 0);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<PurchaseDraftSummary> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new PurchaseDraftSummary(rows.GetGuid(0), rows.GetString(1), rows.GetString(2), rows.GetInt32(3),
                rows.GetFieldValue<DateOnly>(4), rows.GetDecimal(5)));
        return result;
    }

    private static async Task<PurchaseDraftLine[]> QuotePurchaseOrder(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, string code, OrderOffer[] items, DateOnly today, CancellationToken ct)
    {
        List<PurchaseDraftLine> lines = [];
        foreach (var item in items)
        {
            var price = (await ReadEffectivePrices(db, tx, scope, code, today, item.SupplierSku, 0, ct)).SingleOrDefault();
            if (price is null || price.Version != item.PriceVersion)
                throw new PosError(409, "supplier_price_changed");
            var quantity = item.Packages * price.UnitsPerPackage;
            var amount = item.Packages * price.PackagePrice;
            if (!IsQuantity(quantity) || !IsMoney(amount))
                throw new PosError(400, "invalid_purchase_order_line");
            lines.Add(new PurchaseDraftLine(item.SupplierSku, item.PriceVersion, price.ProductId, price.PackageUnit,
                price.UnitsPerPackage, item.Packages, quantity, price.PackagePrice, amount));
        }
        var quantities = lines.GroupBy(line => line.ProductId).Select(group =>
            (ProductId: group.Key, Quantity: group.Sum(line => line.BaseQuantity), ExpiryDate: (DateOnly?)null)).ToArray();
        if (quantities.Any(item => !IsQuantity(item.Quantity)))
            throw new PosError(400, "invalid_purchase_order_quantity");
        await ValidateReceivingProducts(db, tx, scope, quantities, posting: false, ct);
        return lines.ToArray();
    }

    private static Task LockPurchaseOrder(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid id,
        CancellationToken ct) => LockOperation(db, tx, $"order:{scope.Organization}:{scope.Store}:{id}", ct);

    private static async Task<PurchaseDraft> CommitPurchaseDraft(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid id, CancellationToken ct)
    {
        var result = await ReadPurchaseDraft(db, tx, scope, id, ct)
            ?? throw new InvalidOperationException("Saved purchase draft missing.");
        await tx.CommitAsync(ct);
        return result;
    }

    private static async Task<PurchaseDraft?> ReadPurchaseDraft(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT p.supplier_ref, p.stage, p.revision, p.expected_delivery_date, r.total, p.created_by,
                r.created_at, p.approved_by, p.approved_at, p.sent_by, p.sent_at, p.dispatch_reference, r.body, r.replenishment
            FROM purchase_orders p JOIN purchase_order_revisions r
                ON (r.org_id, r.store_id, r.order_id, r.version) = (p.org_id, p.store_id, p.id, p.revision)
            WHERE (p.org_id, p.store_id, p.id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? new PurchaseDraft(id, rows.GetString(0), rows.GetString(1), rows.GetInt32(2),
            rows.GetFieldValue<DateOnly>(3), rows.GetDecimal(4), rows.GetGuid(5), rows.GetDateTime(6),
            rows.IsDBNull(7) ? null : rows.GetGuid(7), rows.TimeOrNull(8), rows.IsDBNull(9) ? null : rows.GetGuid(9),
            rows.TimeOrNull(10), rows.IsDBNull(11) ? null : rows.GetString(11),
            JsonSerializer.Deserialize<PurchaseDraftLine[]>(rows.GetString(12))
                ?? throw new InvalidOperationException("Purchase draft body missing."))
            {
                Replenishment = rows.IsDBNull(13) ? null
                    : JsonSerializer.Deserialize<PurchaseReplenishment>(rows.GetString(13))
                        ?? throw new InvalidOperationException("Purchase draft replenishment missing.")
            }
            : null;
    }
}
