using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<ParkedCart> SaveCart(HttpContext context, Guid shift, Guid id, SaveParkedCart request, CancellationToken ct)
    {
        if (id == Guid.Empty || request.ExpectedVersion is < 0 or int.MaxValue)
            throw new PosError(400, "invalid_parked_cart");
        var items = ValidateBasketItems(request.Items);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, true, ct);
        var current = await FindCart(db, tx, scope, shift, id, ct);
        if (current is { Status: "parked" } && current.Version == request.ExpectedVersion + 1 &&
            current.Items.SequenceEqual(items))
            return current;
        await RequireOpenShift(db, tx, scope, shift, ct);

        var body = JsonSerializer.Serialize(items);
        if (current is null && request.ExpectedVersion == 0)
        {
            var inserted = await db.Run(tx, ct, """
                INSERT INTO parked_carts (org_id, store_id, register_id, shift_id, cashier_id, id, body)
                VALUES ($1, $2, $3, $4, $5, $6, $7::jsonb) ON CONFLICT DO NOTHING
                """, scope.Organization, scope.Store, scope.Register, shift, scope.Staff, id, body);
            if (inserted != 1)
                throw new PosError(409, "parked_cart_id_reused");
        }
        else
        {
            if (current is null || current.Status != "parked" || current.Version != request.ExpectedVersion)
                throw new PosError(409, "parked_cart_version_conflict");
            await db.Run(tx, ct, """
                UPDATE parked_carts SET body = $7::jsonb, version = version + 1
                WHERE (org_id, store_id, register_id, shift_id, cashier_id, id) = ($1, $2, $3, $4, $5, $6)
                """, scope.Organization, scope.Store, scope.Register, shift, scope.Staff, id, body);
        }
        await Audit(db, tx, context, scope, current is null ? "cart.parked" : "cart.updated", id, ct);
        var result = await FindCart(db, tx, scope, shift, id, ct)
            ?? throw new InvalidOperationException("Saved cart missing.");
        await tx.CommitAsync(ct);
        return result;
    }

    public async Task<IReadOnlyList<ParkedCart>> ListParkedCarts(HttpContext context, Guid shift, bool? includeInactive,
        int? offset, CancellationToken ct)
    {
        ValidateStockOffset(offset);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        return await ReadCarts(db, tx, scope, shift, null, includeInactive == true, offset ?? 0, ct);
    }

    public async Task<ParkedCartReview> ReviewCart(HttpContext context, Guid shift, Guid id, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead, ct);
        var scope = await Access.RequireCashier(db, tx, context, false, ct);
        var cart = await FindCart(db, tx, scope, shift, id, ct) ?? throw new PosError(404, "parked_cart_not_found");
        if (cart.Status is not ("parked" or "expired"))
            return new ParkedCartReview(cart, []);

        await using var command = db.Sql(tx, """
            SELECT i.product_id, i.lot_id, i.price_version, sp.version, sp.unit_price, coalesce(l.quantity, 0),
                   CASE WHEN NOT coalesce(sp.active, false) THEN 'productUnavailable'
                        WHEN sp.unit_price IS NULL THEN 'priceNotSet'
                        WHEN l.id IS NULL THEN 'lotUnavailable'
                        WHEN l.quantity < i.quantity THEN 'insufficientStock'
                        WHEN sp.version <> i.price_version THEN 'priceChanged' ELSE 'ready' END
            FROM unnest($3::uuid[], $4::uuid[], $5::integer[], $6::numeric[])
                WITH ORDINALITY i(product_id, lot_id, price_version, quantity, position)
            LEFT JOIN store_prices sp ON (sp.org_id, sp.store_id, sp.product_id) = ($1, $2, i.product_id)
            LEFT JOIN products p ON (p.org_id, p.id) = (sp.org_id, sp.product_id)
            LEFT JOIN stock_lots l ON (l.org_id, l.store_id, l.product_id, l.id) =
                  (sp.org_id, sp.store_id, sp.product_id, i.lot_id) AND sp.active AND NOT l.quarantined
                  AND (NOT p.requires_expiry OR l.expiry_date IS NOT NULL)
                  AND (l.expiry_date IS NULL OR l.expiry_date >= (statement_timestamp() AT TIME ZONE $7)::date)
            ORDER BY i.position
            """, scope.Organization, scope.Store, cart.Items.Select(item => item.ProductId).ToArray(),
            cart.Items.Select(item => item.LotId).ToArray(), cart.Items.Select(item => item.PriceVersion).ToArray(),
            cart.Items.Select(item => item.Quantity).ToArray(), scope.TimeZone);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ParkedCartLineReview> lines = [];
        while (await rows.ReadAsync(ct))
            lines.Add(new ParkedCartLineReview(rows.GetGuid(0), rows.GetGuid(1), rows.GetInt32(2),
                rows.IsDBNull(3) ? null : rows.GetInt32(3), rows.DecimalOrNull(4), rows.GetDecimal(5), rows.GetString(6)));
        return new ParkedCartReview(cart, lines);
    }

    public async Task<ParkedCart> CancelCart(HttpContext context, Guid shift, Guid id, CancelParkedCart request,
        CancellationToken ct)
    {
        ValidateReceiptVersion(request.ExpectedVersion);
        if (!IsText(request.Reason, 500))
            throw new PosError(400, "invalid_cart_cancel_reason");
        var reason = request.Reason.Trim();
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, true, ct);
        var cart = await FindCart(db, tx, scope, shift, id, ct) ?? throw new PosError(404, "parked_cart_not_found");
        if (cart.Status == "cancelled" && cart.Version == request.ExpectedVersion + 1 && cart.CancelReason == reason)
            return cart;
        if (cart.Status is not ("parked" or "expired") || cart.Version != request.ExpectedVersion)
            throw new PosError(409, "parked_cart_version_conflict");
        await db.Run(tx, ct, """
            UPDATE parked_carts SET status = 'cancelled', cancel_reason = $7, version = version + 1
            WHERE (org_id, store_id, register_id, shift_id, cashier_id, id) = ($1, $2, $3, $4, $5, $6)
            """, scope.Organization, scope.Store, scope.Register, shift, scope.Staff, id, reason);
        await Audit(db, tx, context, scope, "cart.cancelled", id, ct);
        await tx.CommitAsync(ct);
        return cart with { Status = "cancelled", Version = cart.Version + 1, CancelReason = reason };
    }

    private static async Task RequireCheckoutCart(NpgsqlConnection db, NpgsqlTransaction tx, RegisterScope scope,
        Guid shift, Guid id, int version, SaleItem[] items, CancellationToken ct)
    {
        var cart = await FindCart(db, tx, scope, shift, id, ct) ?? throw new PosError(404, "parked_cart_not_found");
        if (cart.Status != "parked" || cart.Version != version)
            throw new PosError(409, "parked_cart_version_conflict");
        if (!cart.Items.Select(item => (item.ProductId, item.LotId, item.Quantity))
            .SequenceEqual(items.Select(item => (item.ProductId, item.LotId, item.Quantity))))
            throw new PosError(409, "parked_cart_items_changed");
    }

    private static async Task MarkCartSold(NpgsqlConnection db, NpgsqlTransaction tx, HttpContext context,
        RegisterScope scope, Guid shift, Guid id, Guid sale, CancellationToken ct)
    {
        var changed = await db.Run(tx, ct, """
            UPDATE parked_carts SET status = 'sold', sale_id = $7, version = version + 1
            WHERE (org_id, store_id, register_id, shift_id, cashier_id, id) = ($1, $2, $3, $4, $5, $6)
                AND status = 'parked' AND expires_at > statement_timestamp()
            """, scope.Organization, scope.Store, scope.Register, shift, scope.Staff, id, sale);
        if (changed != 1)
            throw new PosError(409, "parked_cart_version_conflict");
        await Audit(db, tx, context, scope, "cart.sold", id, ct);
    }

    private static async Task<ParkedCart?> FindCart(NpgsqlConnection db, NpgsqlTransaction tx, RegisterScope scope,
        Guid shift, Guid id, CancellationToken ct) =>
        (await ReadCarts(db, tx, scope, shift, id, true, 0, ct)).SingleOrDefault();

    private static async Task<IReadOnlyList<ParkedCart>> ReadCarts(NpgsqlConnection db, NpgsqlTransaction tx,
        RegisterScope scope, Guid shift, Guid? id, bool includeInactive, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT id, version, CASE WHEN status = 'parked' AND expires_at <= statement_timestamp() THEN 'expired'
                       ELSE status END, created_at, expires_at, sale_id, cancel_reason, body
            FROM parked_carts
            WHERE (org_id, store_id, register_id, shift_id, cashier_id) = ($1, $2, $3, $4, $5)
              AND ($6 OR id = $7) AND ($8 OR (status = 'parked' AND expires_at > statement_timestamp()))
            ORDER BY created_at DESC, id LIMIT 25 OFFSET $9
            """, scope.Organization, scope.Store, scope.Register, shift, scope.Staff, id is null, id ?? Guid.Empty,
            includeInactive, offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ParkedCart> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new ParkedCart(rows.GetGuid(0), shift, rows.GetInt32(1), rows.GetString(2), rows.GetDateTime(3),
                rows.GetDateTime(4), rows.IsDBNull(5) ? null : rows.GetGuid(5), rows.IsDBNull(6) ? null : rows.GetString(6),
                JsonSerializer.Deserialize<SaleItem[]>(rows.GetString(7)) ?? throw new InvalidOperationException("Cart missing.")));
        return result;
    }
}
