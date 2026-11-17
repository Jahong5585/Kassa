using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private const int FinancePageSize = 25;
    private static readonly string[] ExpenseCategories =
        ["rent", "salary", "utilities", "delivery", "repair", "advertising", "tax", "other"];
    private static readonly string[] ExpensePaymentMethods = ["cash", "manualCashless", "bankTransfer", "other"];

    public async Task<OperatingExpense> CreateOperatingExpense(HttpContext context, Guid store,
        CreateOperatingExpense request, CancellationToken ct)
    {
        if (request.OperationId == Guid.Empty || !IsBusinessDate(request.IncurredOn) ||
            !ExpenseCategories.Contains(request.Category, StringComparer.Ordinal) ||
            !ExpensePaymentMethods.Contains(request.PaymentMethod, StringComparer.Ordinal) ||
            !IsMoney(request.Amount) || request.Amount <= 0 || !IsText(request.Description, 500))
            throw new PosError(400, "invalid_operating_expense");
        var description = request.Description.Trim();

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"operating-expense:{scope.Organization}:{store}:{request.OperationId}", ct);
        var existing = await ReadOperatingExpense(db, tx, scope, request.OperationId, ct);
        if (existing is not null)
        {
            if (existing.IncurredOn != request.IncurredOn || existing.Category != request.Category ||
                existing.Amount != request.Amount || existing.PaymentMethod != request.PaymentMethod ||
                existing.Description != description)
                throw new PosError(409, "operating_expense_id_reused");
            return existing;
        }

        await db.Run(tx, ct, """
            INSERT INTO operating_expenses
                (org_id, store_id, id, incurred_on, category, amount, payment_method, description, recorded_by)
            VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)
            """, scope.Organization, store, request.OperationId, request.IncurredOn, request.Category,
            request.Amount, request.PaymentMethod, description, scope.Staff);
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            "finance.expense_recorded", request.OperationId, ct, request.Category);
        var saved = await ReadOperatingExpense(db, tx, scope, request.OperationId, ct)
            ?? throw new InvalidOperationException("Saved operating expense is missing.");
        await tx.CommitAsync(ct);
        return saved;
    }

    public async Task<OperatingExpense> ReverseOperatingExpense(HttpContext context, Guid store, Guid id,
        ReverseOperatingExpense request, CancellationToken ct)
    {
        if (id == Guid.Empty || request.OperationId == Guid.Empty || !IsText(request.Reason, 500))
            throw new PosError(400, "invalid_operating_expense_reversal");
        var reason = request.Reason.Trim();

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockOperation(db, tx, $"operating-expense:{scope.Organization}:{store}:{id}", ct);
        var current = await ReadOperatingExpense(db, tx, scope, id, ct)
            ?? throw new PosError(404, "operating_expense_not_found");
        if (current.ReversalId is { } reversal)
        {
            if (reversal != request.OperationId || current.ReversalReason != reason)
                throw new PosError(409, "operating_expense_already_reversed");
            return current;
        }
        try
        {
            var changed = await db.Run(tx, ct, """
                UPDATE operating_expenses
                SET reversal_id = $4, reversal_reason = $5, reversed_by = $6, reversed_at = statement_timestamp()
                WHERE (org_id, store_id, id) = ($1, $2, $3) AND reversed_at IS NULL
                """, scope.Organization, store, id, request.OperationId, reason, scope.Staff);
            if (changed != 1) throw new PosError(409, "operating_expense_already_reversed");
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            throw new PosError(409, "operating_expense_reversal_id_reused");
        }
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff,
            "finance.expense_reversed", id, ct, request.OperationId.ToString());
        var saved = await ReadOperatingExpense(db, tx, scope, id, ct)
            ?? throw new InvalidOperationException("Saved operating expense is missing.");
        await tx.CommitAsync(ct);
        return saved;
    }

    public async Task<OperatingExpense> GetOperatingExpense(HttpContext context, Guid store, Guid id,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await ReadOperatingExpense(db, tx, scope, id, ct)
            ?? throw new PosError(404, "operating_expense_not_found");
    }

    public async Task<OperatingExpensePage> ListOperatingExpenses(HttpContext context, Guid store, DateOnly from,
        DateOnly to, string? category, bool? includeReversed, int? offset, CancellationToken ct)
    {
        if (!IsValidReportPeriod(from, to) || offset is < 0 or > 1_000_000 ||
            category is not null && !ExpenseCategories.Contains(category, StringComparer.Ordinal))
            throw new PosError(400, "invalid_operating_expense_filter");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, """
            SELECT id, incurred_on, category, amount, payment_method, description, recorded_by, created_at,
                   reversal_id, reversal_reason, reversed_by, reversed_at
            FROM operating_expenses
            WHERE (org_id, store_id) = ($1, $2) AND incurred_on BETWEEN $3 AND $4
              AND ($5 = '' OR category = $5) AND ($6 OR reversed_at IS NULL)
            ORDER BY incurred_on DESC, created_at DESC, id
            LIMIT $7 OFFSET $8
            """, scope.Organization, store, from, to, category ?? "", includeReversed == true,
            FinancePageSize + 1, offset ?? 0);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<OperatingExpense> items = [];
        while (await rows.ReadAsync(ct))
            items.Add(ReadOperatingExpenseRow(rows));
        int? next = items.Count > FinancePageSize ? (offset ?? 0) + FinancePageSize : null;
        if (next.HasValue) items.RemoveAt(items.Count - 1);
        return new OperatingExpensePage(store, scope.TimeZone, from, to, category, includeReversed == true,
            items, next);
    }

    private static async Task<OperatingExpense?> ReadOperatingExpense(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid id, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT id, incurred_on, category, amount, payment_method, description, recorded_by, created_at,
                   reversal_id, reversal_reason, reversed_by, reversed_at
            FROM operating_expenses WHERE (org_id, store_id, id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, id);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? ReadOperatingExpenseRow(rows) : null;
    }

    private static OperatingExpense ReadOperatingExpenseRow(NpgsqlDataReader rows) =>
        new(rows.GetGuid(0), rows.GetFieldValue<DateOnly>(1), rows.GetString(2), rows.GetDecimal(3), rows.GetString(4),
            rows.GetString(5), rows.GetGuid(6), rows.GetDateTime(7), rows.IsDBNull(8) ? null : rows.GetGuid(8),
            rows.IsDBNull(9) ? null : rows.GetString(9), rows.IsDBNull(10) ? null : rows.GetGuid(10), rows.TimeOrNull(11));

}
