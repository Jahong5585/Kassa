using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<CatalogProduct> GetProduct(HttpContext context, string barcode, CancellationToken ct)
    {
        if (!IsBarcode(barcode))
            throw new PosError(400, "invalid_barcode");
        return await ReadCashierProduct(context, Guid.Empty, barcode, ct);
    }

    public Task<CatalogProduct> GetProductById(HttpContext context, Guid product, CancellationToken ct) =>
        ReadCashierProduct(context, product, "", ct);

    private async Task<CatalogProduct> ReadCashierProduct(HttpContext context, Guid product, string barcode,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        await using var command = db.Sql(tx, """
            WITH target AS (
                SELECT product_id, code, quantity_factor FROM product_barcodes
                WHERE (org_id, code) = ($1, $3) AND $3 <> ''
                UNION ALL SELECT $5::uuid, '', 1::numeric WHERE $3 = ''
            )
            SELECT p.id, p.name, p.unit, sp.unit_price, sp.version, p.requires_expiry, l.id, l.expiry_date, l.quantity,
                   EXISTS (SELECT 1 FROM stock_lots risk
                           WHERE (risk.org_id, risk.store_id, risk.product_id) = (p.org_id, sp.store_id, p.id)
                             AND (risk.quarantine_quantity > 0 OR (risk.quantity > 0
                                  AND (risk.quarantined OR risk.expiry_date < (now() AT TIME ZONE $4)::date
                                       OR (p.requires_expiry AND risk.expiry_date IS NULL))))),
                   coalesce(nullif(t.code, ''), p.barcode, ''), t.quantity_factor
            FROM target t JOIN products p ON (p.org_id, p.id) = ($1, t.product_id)
            JOIN store_prices sp ON (sp.org_id, sp.product_id) = (p.org_id, p.id)
            LEFT JOIN stock_lots l ON (l.org_id, l.store_id, l.product_id) = (sp.org_id, sp.store_id, sp.product_id)
                AND NOT l.quarantined AND l.quantity > 0
                AND (NOT p.requires_expiry OR l.expiry_date IS NOT NULL)
                AND (l.expiry_date IS NULL OR l.expiry_date >= (now() AT TIME ZONE $4)::date)
            WHERE sp.store_id = $2 AND sp.active
            ORDER BY l.expiry_date, l.id LIMIT 100
            """, scope.Organization, scope.Store, barcode, scope.TimeZone, product);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(404, "product_not_found");
        if (rows.IsDBNull(3))
            throw new PosError(409, "sale_price_not_set");
        List<CatalogLot> lots = [];
        var result = new CatalogProduct(rows.GetGuid(0), rows.GetString(1), rows.GetString(10), rows.GetString(2),
            rows.GetDecimal(3), rows.GetInt32(4), rows.GetBoolean(5), lots)
        {
            RequiresLotVerification = rows.GetBoolean(9),
            QuantityFactor = rows.GetDecimal(11)
        };
        do
        {
            if (!rows.IsDBNull(6))
                lots.Add(new CatalogLot(rows.GetGuid(6), ReadDate(rows, 7), rows.GetDecimal(8)));
        } while (await rows.ReadAsync(ct));
        return result;
    }

    public async Task<IReadOnlyList<ProductSearchResult>> SearchCashierProducts(HttpContext context, string query,
        int? offset, CancellationToken ct)
    {
        if (!IsText(query, 100))
            throw new PosError(400, "invalid_catalog_search");
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        return await SearchProducts(db, tx, scope.Organization, scope.Store, query.Trim(), false, offset ?? 0, ct);
    }

    public async Task<IReadOnlyList<ProductSearchResult>> ListStoreProducts(HttpContext context, Guid store, string? query,
        bool? includeArchived, int? offset, CancellationToken ct)
    {
        if (query is not null && !IsText(query, 100))
            throw new PosError(400, "invalid_catalog_search");
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await SearchProducts(db, tx, scope.Organization, store, query?.Trim() ?? "", includeArchived == true,
            offset ?? 0, ct);
    }

    private static async Task<IReadOnlyList<ProductSearchResult>> SearchProducts(NpgsqlConnection db,
        NpgsqlTransaction tx, Guid org, Guid store, string query, bool includeArchived, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT p.id, p.name, p.sku, coalesce(p.barcode, ''), p.unit, sp.unit_price, sp.version, sp.active
            FROM products p JOIN store_prices sp ON (sp.org_id, sp.product_id) = (p.org_id, p.id)
            WHERE (p.org_id, sp.store_id) = ($1, $2) AND ($4 OR sp.active)
              AND ($3 = '' OR strpos(lower(p.name), lower($3)) > 0 OR lower(p.sku) = lower($3)
                   OR EXISTS (SELECT 1 FROM product_barcodes b
                              WHERE (b.org_id, b.product_id, b.code) = (p.org_id, p.id, $3)))
            ORDER BY p.name, p.id LIMIT 25 OFFSET $5
            """, org, store, query, includeArchived, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ProductSearchResult> results = [];
        while (await rows.ReadAsync(ct))
            results.Add(new ProductSearchResult(rows.GetGuid(0), rows.GetString(1), rows.GetString(2), rows.GetString(3),
                rows.GetString(4), rows.DecimalOrNull(5), rows.GetInt32(6), rows.GetBoolean(7)));
        return results;
    }

    public async Task<ProductCard> GetStoreProduct(HttpContext context, Guid store, Guid product, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadProductCard(db, tx, scope, product, ct) ?? throw new PosError(404, "product_not_found");
    }

    public async Task<ProductCard> SaveStoreProduct(HttpContext context, Guid store, Guid product, WriteProductCard request,
        CancellationToken ct)
    {
        var card = ValidateProductCard(product, request);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockInventoryMembership(db, tx, scope.Organization, ct, shared: true);
        var result = await WriteCard(db, tx, context, scope, card, request.ExpectedVersion, ct);
        await tx.CommitAsync(ct);
        return result;
    }

    private static ProductCard ValidateProductCard(Guid product, WriteProductCard request)
    {
        if (product == Guid.Empty || request.ExpectedVersion is < 0 or int.MaxValue ||
            request.Unit is not ("piece" or "kg") || !IsText(request.Name, 160) ||
            request.Barcodes is not { Length: <= 30 })
            throw new PosError(400, "invalid_product_card");
        var codes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var barcode in request.Barcodes)
        {
            if (barcode is null || !IsBarcode(barcode.Code) || !codes.Add(barcode.Code) ||
                !IsQuantity(barcode.QuantityFactor))
                throw new PosError(400, "invalid_product_barcode");
            ValidateLotQuantity(request.Unit, barcode.QuantityFactor);
        }
        var ordered = request.Barcodes.Take(1).Concat(request.Barcodes.Skip(1).OrderBy(code => code.Code,
            StringComparer.Ordinal)).ToArray();
        return new ProductCard(product, request.Name.Trim(), request.Unit, request.RequiresExpiry,
            OptionalText(request.Sku, 64), OptionalText(request.Category, 120), OptionalText(request.Brand, 120),
            OptionalText(request.SupplierRef, 160), OptionalText(request.TaxProfile, 80), request.ExpectedVersion + 1,
            ordered, null, 1, true);
    }

    private static async Task<ProductCard> WriteCard(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        StoreScope scope, ProductCard card, int expectedVersion, CancellationToken ct)
    {
        var id = card.ProductId;
        await LockProduct(db, tx, scope.Organization, id, true, ct);
        var current = await ReadProductCard(db, tx, scope, id, ct);
        if (current is not null && SameProductCard(card, current))
            return current;
        if (current is null && expectedVersion != 0)
            throw new PosError(404, "product_not_found");
        if (current is not null)
        {
            if (current.Version != expectedVersion)
                throw new PosError(409, "product_version_conflict");
            if (current.Unit != card.Unit)
                throw new PosError(409, "product_unit_immutable");

            await using var unassigned = db.Sql(tx, """
                SELECT 1 FROM store_prices sp WHERE (sp.org_id, sp.product_id) = ($1, $2)
                AND NOT EXISTS (SELECT 1 FROM staff_stores a
                    WHERE (a.org_id, a.staff_id, a.store_id) = (sp.org_id, $3, sp.store_id) AND a.active)
                LIMIT 1
                """, scope.Organization, id, scope.Staff);
            if (await unassigned.ExecuteScalarAsync(ct) is not null)
                throw new PosError(403, "shared_product_edit_denied");
        }

        var primary = card.Barcodes.FirstOrDefault()?.Code ?? "";
        try
        {
            if (current is null)
            {
                var added = await db.Run(tx, ct, """
                    INSERT INTO products (org_id, id, name, unit, requires_expiry, barcode, sku, category,
                        brand, supplier_ref, tax_profile, revision)
                    VALUES ($1, $2, $3, $4, $5, NULLIF($6, ''), $7, $8, $9, $10, $11, $12)
                    ON CONFLICT (org_id, id) DO NOTHING
                    """, scope.Organization, id, card.Name, card.Unit, card.RequiresExpiry, primary, card.Sku, card.Category,
                    card.Brand, card.SupplierRef, card.TaxProfile, card.Version);
                if (added != 1)
                    throw new PosError(409, "product_id_reused");
                await db.Run(tx, ct, """
                    INSERT INTO store_prices (org_id, store_id, product_id, unit_price, version)
                    VALUES ($1, $2, $3, NULL, 1)
                    """, scope.Organization, scope.Store, id);
                await InsertPriceHistory(db, tx, scope, id, "product_created", ct);
            }
            else
            {
                await db.Run(tx, ct, """
                    UPDATE products SET name = $3, requires_expiry = $4, barcode = NULLIF($5, ''), sku = $6,
                        category = $7, brand = $8, supplier_ref = $9, tax_profile = $10, revision = $11
                    WHERE (org_id, id) = ($1, $2)
                    """, scope.Organization, id, card.Name, card.RequiresExpiry, primary, card.Sku, card.Category,
                    card.Brand, card.SupplierRef, card.TaxProfile, card.Version);
            }
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation &&
            ex.ConstraintName == "products_unique_sku")
        {
            throw new PosError(409, "product_sku_in_use");
        }

        if (current is null || !current.Barcodes.SequenceEqual(card.Barcodes))
        {
            await db.Run(tx, ct, "DELETE FROM product_barcodes WHERE (org_id, product_id) = ($1, $2)",
                scope.Organization, id);
            foreach (var barcode in card.Barcodes)
            {
                var added = await db.Run(tx, ct, """
                    INSERT INTO product_barcodes (org_id, code, product_id, quantity_factor)
                    VALUES ($1, $2, $3, $4) ON CONFLICT DO NOTHING
                    """, scope.Organization, barcode.Code, id, barcode.QuantityFactor);
                if (added != 1)
                    throw new PosError(409, "product_barcode_in_use");
            }
        }

        await Audit(db, tx, context, scope.Organization, scope.Store, Guid.Empty, scope.Staff,
            current is null ? "product.created" : "product.updated", id, ct);
        return current is null ? card :
            card with { UnitPrice = current.UnitPrice, PriceVersion = current.PriceVersion, Active = current.Active };
    }

    private static bool SameProductCard(ProductCard requested, ProductCard current) =>
        requested.Barcodes.SequenceEqual(current.Barcodes) &&
        requested with { Barcodes = current.Barcodes, UnitPrice = current.UnitPrice,
            PriceVersion = current.PriceVersion, Active = current.Active } == current;

    private static async Task<ProductCard?> ReadProductCard(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid product, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT p.name, p.unit, p.requires_expiry, p.sku, p.category, p.brand, p.supplier_ref, p.tax_profile,
                   p.revision, sp.unit_price, sp.version, sp.active, b.code, b.quantity_factor
            FROM products p JOIN store_prices sp ON (sp.org_id, sp.product_id) = (p.org_id, p.id)
            LEFT JOIN product_barcodes b ON (b.org_id, b.product_id) = (p.org_id, p.id)
            WHERE (p.org_id, sp.store_id, p.id) = ($1, $2, $3)
            ORDER BY (b.code = p.barcode) DESC NULLS LAST, b.code COLLATE "C"
            """, scope.Organization, scope.Store, product);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            return null;
        List<ProductBarcode> codes = [];
        var card = new ProductCard(product, rows.GetString(0), rows.GetString(1), rows.GetBoolean(2), rows.GetString(3),
            rows.GetString(4), rows.GetString(5), rows.GetString(6), rows.GetString(7), rows.GetInt32(8), codes,
            rows.DecimalOrNull(9), rows.GetInt32(10), rows.GetBoolean(11));
        do
        {
            if (!rows.IsDBNull(12))
                codes.Add(new ProductBarcode(rows.GetString(12), rows.GetDecimal(13)));
        } while (await rows.ReadAsync(ct));
        return card;
    }

    private static bool IsBarcode(string? value) => value is { Length: >= 1 and <= 48 } && value.All(char.IsAsciiDigit);

    private static async Task LockProduct(NpgsqlConnection db, NpgsqlTransaction tx, Guid org, Guid product,
        bool write, CancellationToken ct)
    {
        var sql = write ? "SELECT pg_advisory_xact_lock(hashtextextended($1, 1))"
            : "SELECT pg_advisory_xact_lock_shared(hashtextextended($1, 1))";
        await using var command = db.Sql(tx, sql, $"product:{org}:{product}");
        await command.ExecuteNonQueryAsync(ct);
    }
}
