using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private const string MissingInventoryLots = """
        SELECT l.org_id, l.store_id, $3 AS inventory_id, l.product_id, l.id AS lot_id
        FROM stock_lots l JOIN products p ON (p.org_id, p.id) = (l.org_id, l.product_id)
        WHERE (l.org_id, l.store_id) = ($1, $2)
          AND ($4 = 'full' OR ($4 = 'product' AND l.product_id = $5) OR ($4 = 'category' AND p.category = $6))
          AND NOT EXISTS (SELECT 1 FROM inventory_lines n
              WHERE (n.org_id, n.store_id, n.inventory_id, n.product_id, n.lot_id) =
                    (l.org_id, l.store_id, $3, l.product_id, l.id))
        """;

    public async Task<InventoryDocument> CreateInventory(HttpContext context, Guid store, CreateInventory request,
        CancellationToken ct)
    {
        var selection = request.Scope;
        if (request.InventoryId == Guid.Empty || !IsText(request.Reason, 500) ||
            (selection is null) == (request.Lots is null))
            throw new PosError(400, "invalid_inventory");
        if (request.Lots is { } selected &&
            (selected.Length is < 1 or > 100 ||
             selected.Any(lot => lot is null || lot.ProductId == Guid.Empty || lot.LotId == Guid.Empty) ||
             selected.Distinct().Count() != selected.Length))
            throw new PosError(400, "invalid_inventory");
        if (selection is not null)
            selection = ValidateInventoryScope(selection);

        var lots = request.Lots?.OrderBy(lot => lot.ProductId).ThenBy(lot => lot.LotId).ToArray() ?? [];
        var reason = request.Reason.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockInventory(db, tx, scope, request.InventoryId, ct);
        var current = await ReadInventory(db, tx, scope, request.InventoryId, true, ct);
        if (current is not null)
        {
            if (current.Reason != reason || current.Scope != selection ||
                selection is null && !current.Lines.Select(line => new StockLotKey(line.ProductId, line.LotId))
                    .SequenceEqual(lots))
                throw new PosError(409, "inventory_id_reused");
            return current;
        }

        if (selection?.ProductId is { } product)
        {
            await using var assigned = db.Sql(tx, """
                SELECT 1 FROM store_prices WHERE (org_id, store_id, product_id) = ($1, $2, $3)
                """, scope.Organization, store, product);
            if (await assigned.ExecuteScalarAsync(ct) is null)
                throw new PosError(404, "product_not_found");
        }
        await db.Run(tx, ct, """
            INSERT INTO inventories (org_id, store_id, id, created_by, reason, scope_kind, scope_product_id, scope_category)
            VALUES ($1, $2, $3, $4, $5, $6, NULLIF($7, '00000000-0000-0000-0000-000000000000'::uuid), NULLIF($8, ''))
            """, scope.Organization, store, request.InventoryId, scope.Staff, reason, selection?.Kind ?? "selected",
            selection?.ProductId ?? Guid.Empty, selection?.Category ?? "");
        if (selection is not null && await AddInventoryLots(db, tx, scope, request.InventoryId, selection, ct) == 0)
            throw new PosError(409, "inventory_scope_empty");
        foreach (var lot in lots)
        {
            var inserted = await db.Run(tx, ct, """
                INSERT INTO inventory_lines (org_id, store_id, inventory_id, product_id, lot_id)
                SELECT org_id, store_id, $3, product_id, id FROM stock_lots
                WHERE (org_id, store_id, product_id, id) = ($1, $2, $4, $5)
                """, scope.Organization, store, request.InventoryId, lot.ProductId, lot.LotId);
            if (inserted != 1)
                throw new PosError(404, "stock_lot_not_found");
        }
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "inventory.created",
            request.InventoryId, ct);
        return await CommitInventory(db, tx, scope, request.InventoryId, true, ct);
    }

    private static InventoryScope ValidateInventoryScope(InventoryScope scope) => scope.Kind switch
    {
        "full" when scope.ProductId is null && scope.Category is null => scope,
        "product" when scope.ProductId is { } product && product != Guid.Empty && scope.Category is null => scope,
        "category" when scope.ProductId is null && IsText(scope.Category, 120) =>
            scope with { Category = scope.Category!.Trim() },
        _ => throw new PosError(400, "invalid_inventory_scope")
    };

    public async Task<IReadOnlyList<InventorySummary>> ListInventories(HttpContext context, Guid store, string? status,
        int? offset, CancellationToken ct)
    {
        ValidateStockOffset(offset);
        if (status is not (null or "draft" or "counted" or "posted"))
            throw new PosError(400, "invalid_inventory_status");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, """
            SELECT id, reason, status, version, created_by, created_at, scope_kind, scope_product_id, scope_category
            FROM inventories
            WHERE (org_id, store_id) = ($1, $2) AND ($3 = '' OR status = $3)
            ORDER BY created_at DESC, id LIMIT 25 OFFSET $4
            """, scope.Organization, store, status ?? "", offset ?? 0);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<InventorySummary> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new InventorySummary(rows.GetGuid(0), rows.GetString(1), rows.GetString(2), rows.GetInt32(3),
                rows.GetGuid(4), rows.GetDateTime(5)) { Scope = ReadInventoryScope(rows, 6) });
        return result;
    }

    public async Task<InventoryDocument> RefreshInventoryScope(HttpContext context, Guid store, Guid id,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockInventory(db, tx, scope, id, ct);
        var document = await ReadInventory(db, tx, scope, id, true, ct)
            ?? throw new PosError(404, "inventory_not_found");
        if (document.Status == "posted" || document.Scope is not { } selection)
            throw new PosError(409, "inventory_scope_refresh_unavailable");
        if (await AddInventoryLots(db, tx, scope, id, selection, ct) == 0)
            return document;
        await db.Run(tx, ct, """
            UPDATE inventories SET status = 'draft', version = version + 1, last_count_digest = NULL
            WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, store, id);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "inventory.scope_refreshed", id, ct);
        return await CommitInventory(db, tx, scope, id, true, ct);
    }

    public async Task<InventoryDocument> GetInventory(HttpContext context, Guid store, Guid id, bool? blind,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadInventory(db, tx, scope, id, blind == true, ct)
            ?? throw new PosError(404, "inventory_not_found");
    }

    public async Task<InventoryDocument> SaveInventoryCount(HttpContext context, Guid store, Guid id,
        CountInventory request, CancellationToken ct)
    {
        ValidateReceiptVersion(request.ExpectedVersion);
        if (request.Items is not { Length: >= 1 and <= 100 } ||
            request.Items.Any(item => item is null || item.ProductId == Guid.Empty || item.LotId == Guid.Empty ||
                item.LotRevision <= 0 || !IsStockQuantity(item.StockQuantity) || !IsStockQuantity(item.QuarantineQuantity)))
            throw new PosError(400, "invalid_inventory_count");
        var items = request.Items.OrderBy(item => item.ProductId).ThenBy(item => item.LotId).ToArray();

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockInventory(db, tx, scope, id, ct);
        var document = await ReadInventory(db, tx, scope, id, false, ct)
            ?? throw new PosError(404, "inventory_not_found");
        var lines = document.Lines.ToDictionary(line => (line.ProductId, line.LotId));
        var keys = items.Select(item => (item.ProductId, item.LotId)).ToArray();
        if (keys.Distinct().Count() != items.Length ||
            (document.Scope is null && lines.Count != items.Length) || keys.Any(key => !lines.ContainsKey(key)))
            throw new PosError(400, "inventory_count_requires_selected_lots");

        var digest = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(items.Select(item => new
        {
            item.ProductId, item.LotId, item.LotRevision,
            StockQuantity = item.StockQuantity.ToString("G29", CultureInfo.InvariantCulture),
            QuarantineQuantity = item.QuarantineQuantity.ToString("G29", CultureInfo.InvariantCulture)
        })));
        if (document.Status != "posted" && document.Version == request.ExpectedVersion + 1 &&
            (document.LastCountDigest is { } saved ? saved.AsSpan().SequenceEqual(digest) :
                document.Scope is null && keys.Select(key => lines[key]).Zip(items).All(pair =>
                    pair.First.CountedRevision == pair.Second.LotRevision &&
                    pair.First.CountedStock == pair.Second.StockQuantity &&
                    pair.First.CountedQuarantine == pair.Second.QuarantineQuantity)))
            return document;
        if (document.Status == "posted" || document.Version != request.ExpectedVersion)
            throw new PosError(409, "inventory_version_conflict");

        foreach (var item in items)
        {
            var current = await ReadLockedLot(db, tx, scope.Organization, store, item.ProductId, item.LotId, ct);
            ValidateLotQuantity(current.Unit, item.StockQuantity);
            ValidateLotQuantity(current.Unit, item.QuarantineQuantity);
            if (current.Revision != item.LotRevision)
                throw new PosError(409, "inventory_lot_changed");

            await db.Run(tx, ct, """
                UPDATE inventory_lines SET counted_revision = $6, expected_stock = $7, expected_quarantine = $8,
                    counted_stock = $9, counted_quarantine = $10
                WHERE (org_id, store_id, inventory_id, product_id, lot_id) = ($1, $2, $3, $4, $5)
                """, scope.Organization, store, id, item.ProductId, item.LotId, current.Revision,
                current.StockQuantity, current.QuarantineQuantity, item.StockQuantity, item.QuarantineQuantity);
        }
        await db.Run(tx, ct, """
            UPDATE inventories i SET version = i.version + 1, last_count_digest = $4, status = CASE WHEN EXISTS (
                SELECT 1 FROM inventory_lines n WHERE (n.org_id, n.store_id, n.inventory_id) = ($1, $2, $3)
                    AND n.counted_revision IS NULL
            ) THEN 'draft' ELSE 'counted' END
            WHERE (i.org_id, i.store_id, i.id) = ($1, $2, $3)
            """, scope.Organization, store, id, digest);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "inventory.counted", id, ct);
        return await CommitInventory(db, tx, scope, id, false, ct);
    }

    public async Task<InventoryDocument> PostInventory(HttpContext context, Guid store, Guid id,
        ReceiptTransition request, CancellationToken ct)
    {
        ValidateReceiptVersion(request.ExpectedVersion);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockInventory(db, tx, scope, id, ct);
        var document = await ReadInventory(db, tx, scope, id, false, ct)
            ?? throw new PosError(404, "inventory_not_found");
        if (document.Status == "posted" && document.Version == request.ExpectedVersion + 1)
            return document;
        if (document.Status != "counted" || document.Version != request.ExpectedVersion)
            throw new PosError(409, "inventory_version_conflict");

        await LockStockOperation(db, tx, scope, id, ct);
        if (document.Scope is { } selection)
        {
            // Hold membership stable through commit while normal sales can still update existing lots.
            await LockInventoryMembership(db, tx, scope.Organization, ct);
            await using var missing = db.Sql(tx, "SELECT EXISTS (" + MissingInventoryLots + ")",
                scope.Organization, store, id, selection.Kind, selection.ProductId ?? Guid.Empty, selection.Category ?? "");
            if (await missing.ExecuteScalarAsync(ct) is true)
                throw new PosError(409, "inventory_scope_changed");
        }
        await InsertStockOperation(db, tx, scope, id, "inventory", document.Reason, ct);
        await using (var revisions = db.Sql(tx, """
            SELECT l.revision IS NOT DISTINCT FROM n.counted_revision
            FROM inventory_lines n JOIN stock_lots l
              ON (l.org_id, l.store_id, l.product_id, l.id) = (n.org_id, n.store_id, n.product_id, n.lot_id)
            WHERE (n.org_id, n.store_id, n.inventory_id) = ($1, $2, $3)
            ORDER BY l.product_id, l.id FOR UPDATE OF l
            """, scope.Organization, store, id))
        {
            await using var rows = await revisions.ExecuteReaderAsync(ct);
            while (await rows.ReadAsync(ct))
                if (!rows.GetBoolean(0))
                    throw new PosError(409, "inventory_lot_changed");
        }
        await db.Run(tx, ct, """
            WITH valued AS (
                SELECT n.*, l.quantity + l.quarantine_quantity AS before_quantity,
                    kasa_weighted_cost_delta(l.inventory_value, l.quantity + l.quarantine_quantity,
                        n.counted_stock + n.counted_quarantine - l.quantity - l.quarantine_quantity) AS cost_delta
                FROM inventory_lines n JOIN stock_lots l
                    ON (l.org_id, l.store_id, l.product_id, l.id) = (n.org_id, n.store_id, n.product_id, n.lot_id)
                WHERE (n.org_id, n.store_id, n.inventory_id) = ($1, $2, $3)
                  AND (l.quantity, l.quarantine_quantity) IS DISTINCT FROM (n.counted_stock, n.counted_quarantine)
            ), changed AS (
                UPDATE stock_lots l SET quantity = v.counted_stock, quarantine_quantity = v.counted_quarantine,
                    inventory_value = kasa_stock_value_after(l.inventory_value, v.before_quantity,
                        v.counted_stock + v.counted_quarantine, v.cost_delta)
                FROM valued v WHERE (l.org_id, l.store_id, l.product_id, l.id) = (v.org_id, v.store_id, v.product_id, v.lot_id)
                RETURNING l.product_id, l.id, v.expected_stock, l.quantity, v.expected_quarantine, l.quarantine_quantity,
                    v.cost_delta
            )
            INSERT INTO stock_operation_lines (org_id, store_id, operation_id, product_id, lot_id,
                stock_before, stock_after, quarantine_before, quarantine_after, inventory_cost_delta)
            SELECT $1, $2, $3, product_id, id, expected_stock, quantity, expected_quarantine, quarantine_quantity, cost_delta
            FROM changed
            """, scope.Organization, store, id);
        await db.Run(tx, ct, """
            UPDATE inventories SET status = 'posted', version = version + 1, posted_operation_id = id
            WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, store, id);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "inventory.posted", id, ct);
        return await CommitInventory(db, tx, scope, id, false, ct);
    }

    private static async Task<InventoryDocument> CommitInventory(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid id, bool blind, CancellationToken ct)
    {
        var result = await ReadInventory(db, tx, scope, id, blind, ct)
            ?? throw new InvalidOperationException("Inventory document missing.");
        await tx.CommitAsync(ct);
        return result;
    }

    private static async Task<InventoryDocument?> ReadInventory(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid id, bool blind, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT i.reason, i.status, i.version, i.created_by, i.created_at, n.product_id, n.lot_id,
                   p.name, p.unit, l.revision, n.counted_revision, n.expected_stock, n.expected_quarantine,
                   n.counted_stock, n.counted_quarantine, i.scope_kind, i.scope_product_id, i.scope_category,
                   i.last_count_digest, CASE
                       WHEN $4 THEN NULL
                       WHEN i.status = 'posted' THEN m.inventory_cost_delta
                       WHEN n.counted_revision = l.revision THEN kasa_weighted_cost_delta(l.inventory_value,
                           l.quantity + l.quarantine_quantity,
                           n.counted_stock + n.counted_quarantine - l.quantity - l.quarantine_quantity)
                   END AS inventory_cost_delta
            FROM inventories i
            JOIN inventory_lines n ON (i.org_id, i.store_id, i.id) = (n.org_id, n.store_id, n.inventory_id)
            JOIN stock_lots l ON (n.org_id, n.store_id, n.product_id, n.lot_id) = (l.org_id, l.store_id, l.product_id, l.id)
            JOIN products p ON (n.org_id, n.product_id) = (p.org_id, p.id)
            LEFT JOIN stock_operation_lines m
                ON (m.org_id, m.store_id, m.operation_id, m.product_id, m.lot_id) =
                   (i.org_id, i.store_id, i.posted_operation_id, n.product_id, n.lot_id)
            WHERE (i.org_id, i.store_id, i.id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, id, blind);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            return null;
        var reason = rows.GetString(0);
        var status = rows.GetString(1);
        var version = rows.GetInt32(2);
        var createdBy = rows.GetGuid(3);
        var created = rows.GetDateTime(4);
        var selection = ReadInventoryScope(rows, 15);
        var digest = rows.IsDBNull(18) ? null : rows.GetFieldValue<byte[]>(18);
        List<InventoryLine> lines = [];
        do
        {
            var countRevision = rows.IsDBNull(10) ? (long?)null : rows.GetInt64(10);
            var currentRevision = rows.GetInt64(9);
            var revision = status == "posted" ? countRevision ?? currentRevision : currentRevision;
            lines.Add(new InventoryLine(rows.GetGuid(5), rows.GetGuid(6), rows.GetString(7), rows.GetString(8),
                revision, blind ? null : countRevision, blind ? null : rows.DecimalOrNull(11),
                blind ? null : rows.DecimalOrNull(12), blind ? null : rows.DecimalOrNull(13),
                blind ? null : rows.DecimalOrNull(14))
            {
                InventoryCostDelta = rows.DecimalOrNull(19),
                CountStatus = selection is null ? null : countRevision switch
                {
                    null => "pending",
                    _ when countRevision == revision => "counted",
                    _ => "stale"
                }
            });
        } while (await rows.ReadAsync(ct));
        return new InventoryDocument(id, reason, status, version, createdBy, created,
            lines.OrderBy(line => line.ProductId).ThenBy(line => line.LotId).ToArray())
        {
            Scope = selection,
            LastCountDigest = digest
        };
    }

    private static InventoryScope? ReadInventoryScope(NpgsqlDataReader rows, int index)
    {
        var kind = rows.GetString(index);
        return kind == "selected" ? null : new InventoryScope(kind,
            rows.IsDBNull(index + 1) ? null : rows.GetGuid(index + 1),
            rows.IsDBNull(index + 2) ? null : rows.GetString(index + 2));
    }

    private static Task<int> AddInventoryLots(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid id,
        InventoryScope selection, CancellationToken ct) =>
        db.Run(tx, ct, """
            INSERT INTO inventory_lines (org_id, store_id, inventory_id, product_id, lot_id)
            """ + "\n" + MissingInventoryLots, scope.Organization, scope.Store, id, selection.Kind,
            selection.ProductId ?? Guid.Empty, selection.Category ?? "");

    private static Task LockInventoryMembership(NpgsqlConnection db, NpgsqlTransaction tx, Guid organization,
        CancellationToken ct, bool shared = false) =>
        LockOperation(db, tx, $"inventory_membership:{organization}", ct, shared);

    private static Task LockInventory(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid id,
        CancellationToken ct) => LockOperation(db, tx, $"inventory:{scope.Organization}:{scope.Store}:{id}", ct);
}
