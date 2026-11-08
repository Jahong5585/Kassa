using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private static string SupplierCode(string value)
    {
        if (value.Length is < 1 or > 32 || !char.IsAsciiLetterOrDigit(value[0]) ||
            !value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
            throw new PosError(400, "invalid_supplier_code");
        return value.ToUpperInvariant();
    }

    public async Task<SupplierCard> WriteSupplier(HttpContext context, Guid store, string code, WriteSupplier request,
        CancellationToken ct)
    {
        code = SupplierCode(code);
        if (!IsText(request.Name, 160) || request.ExpectedVersion is < 0 or int.MaxValue ||
            request.LeadTimeDays is < 0 or > 365)
            throw new PosError(400, "invalid_supplier");
        var card = new SupplierCard(code, request.Name.Trim(), OptionalText(request.TaxId, 40),
            OptionalText(request.Phone, 60), OptionalText(request.Email, 254), OptionalText(request.PaymentTerms, 500),
            request.LeadTimeDays, request.Active, request.ExpectedVersion + 1);

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"supplier:{scope.Organization}:{store}:{code}", ct);
        var current = await ReadSupplier(db, tx, scope, code, ct);
        if (current == card)
            return current;
        if ((current?.Version ?? 0) != request.ExpectedVersion)
            throw new PosError(409, "supplier_version_conflict");

        await db.Run(tx, ct, """
            INSERT INTO suppliers (org_id, store_id, code, name, tax_id, phone, email, payment_terms,
                lead_time_days, active, version)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11)
            ON CONFLICT (org_id, store_id, code) DO UPDATE SET name = EXCLUDED.name, tax_id = EXCLUDED.tax_id,
                phone = EXCLUDED.phone, email = EXCLUDED.email, payment_terms = EXCLUDED.payment_terms,
                lead_time_days = EXCLUDED.lead_time_days, active = EXCLUDED.active, version = EXCLUDED.version
            """, scope.Organization, store, code, card.Name, card.TaxId, card.Phone, card.Email, card.PaymentTerms,
            card.LeadTimeDays, card.Active, card.Version);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            current is null ? "supplier.created" : "supplier.updated", Guid.Empty, ct, code);
        await tx.CommitAsync(ct);
        return card;
    }

    public async Task<SupplierCard> GetSupplier(HttpContext context, Guid store, string code, CancellationToken ct)
    {
        code = SupplierCode(code);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadSupplier(db, tx, scope, code, ct) ?? throw new PosError(404, "supplier_not_found");
    }

    public async Task<IReadOnlyList<ReceivingSupplier>> ListSuppliers(HttpContext context, Guid store, bool? activeOnly,
        int? offset, CancellationToken ct)
    {
        if (offset is < 0 or > 10_000)
            throw new PosError(400, "invalid_supplier_filter");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await using var command = db.Sql(tx, """
            SELECT code, name FROM suppliers
            WHERE (org_id, store_id) = ($1, $2) AND (NOT $3 OR active)
            ORDER BY code LIMIT 25 OFFSET $4
            """, scope.Organization, store, scope.Role != "admin" || activeOnly != false, offset ?? 0);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ReceivingSupplier> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new ReceivingSupplier(rows.GetString(0), rows.GetString(1)));
        return result;
    }

    private static string OptionalText(string? value, int max)
    {
        var normalized = value?.Trim() ?? "";
        if (normalized.Length > max || normalized.Contains('\0'))
            throw new PosError(400, "invalid_supplier_field");
        return normalized;
    }

    private static async Task<SupplierCard?> ReadSupplier(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        string code, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT name, tax_id, phone, email, payment_terms, lead_time_days, active, version
            FROM suppliers WHERE (org_id, store_id, code) = ($1, $2, $3)
            """, scope.Organization, scope.Store, code);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? new SupplierCard(code, rows.GetString(0), rows.GetString(1), rows.GetString(2),
            rows.GetString(3), rows.GetString(4), rows.GetInt32(5), rows.GetBoolean(6), rows.GetInt32(7)) : null;
    }

    private async Task ValidateSupplier(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, string supplier,
        CancellationToken ct)
    {
        var code = supplier.ToUpperInvariant();
        await LockOperation(db, tx, $"supplier:{scope.Organization}:{scope.Store}:{code}", ct);
        await using var command = db.Sql(tx, """
            SELECT active FROM suppliers WHERE (org_id, store_id, code) = ($1, $2, $3)
            """, scope.Organization, scope.Store, code);
        var active = await command.ExecuteScalarAsync(ct);
        if (active is false)
            throw new PosError(409, "supplier_inactive");
        if (active is null && configuration.GetValue<bool>("Purchasing:RequireSupplierCard"))
            throw new PosError(409, "supplier_card_required");
    }
}
