namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<RegisterDiagnostic> ReportRegisterDiagnostic(HttpContext context, ReportRegisterDiagnostic request,
        CancellationToken ct)
    {
        if (request.EventId == Guid.Empty || request.ShiftId == Guid.Empty ||
            request.Code is not ("saleOutcomeUnknown" or "printFailed" or "syncFailed") ||
            request.OperationId == Guid.Empty || request.Code != "syncFailed" && request.OperationId is null)
            throw new PosError(400, "invalid_register_diagnostic");

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireCashier(db, tx, context, true, ct);
        await using (var shift = db.Sql(tx, """
            SELECT 1 FROM shifts WHERE (org_id, store_id, register_id, id, cashier_id) = ($1, $2, $3, $4, $5)
            """, scope.Organization, scope.Store, scope.Register, request.ShiftId, scope.Staff))
        {
            if (await shift.ExecuteScalarAsync(ct) is null)
                throw new PosError(404, "shift_not_found");
        }

        await using (var existing = db.Sql(tx, """
            SELECT shift_id, cashier_id, code, operation_id, received_at
            FROM register_diagnostics WHERE (org_id, store_id, register_id, id) = ($1, $2, $3, $4)
            """, scope.Organization, scope.Store, scope.Register, request.EventId))
        {
            await using var rows = await existing.ExecuteReaderAsync(ct);
            if (await rows.ReadAsync(ct))
            {
                Guid? operation = rows.IsDBNull(3) ? null : rows.GetGuid(3);
                if (rows.GetGuid(0) != request.ShiftId || rows.GetGuid(1) != scope.Staff ||
                    rows.GetString(2) != request.Code || operation != request.OperationId)
                    throw new PosError(409, "register_diagnostic_event_conflict");
                return new RegisterDiagnostic(request.EventId, request.ShiftId, request.Code, operation, rows.GetDateTime(4));
            }
        }

        if (request.Code != "syncFailed")
        {
            await using var sale = db.Sql(tx, """
                SELECT (shift_id, cashier_id) = ($5, $6)
                FROM sales WHERE (org_id, store_id, register_id, id) = ($1, $2, $3, $4)
                """, scope.Organization, scope.Store, scope.Register, request.OperationId!.Value, request.ShiftId, scope.Staff);
            var owned = await sale.ExecuteScalarAsync(ct);
            if (owned is false || request.Code == "printFailed" && owned is not true)
                throw new PosError(404, "sale_not_found");
        }

        await using var insert = db.Sql(tx, """
            INSERT INTO register_diagnostics (org_id, store_id, register_id, id, shift_id, cashier_id, code, operation_id)
            VALUES ($1, $2, $3, $4, $5, $6, $7, NULLIF($8, $9))
            RETURNING received_at
            """, scope.Organization, scope.Store, scope.Register, request.EventId, request.ShiftId, scope.Staff,
            request.Code, request.OperationId ?? Guid.Empty, Guid.Empty);
        var receivedAt = (DateTime)(await insert.ExecuteScalarAsync(ct))!;
        await Audit(db, tx, context, scope.Organization, scope.Store, scope.Register, scope.Staff,
            "register.diagnostic_recorded", request.EventId, ct, request.Code);
        await tx.CommitAsync(ct);
        return new RegisterDiagnostic(request.EventId, request.ShiftId, request.Code, request.OperationId, receivedAt);
    }
}
