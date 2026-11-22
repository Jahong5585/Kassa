using System.Text.Json;
using Microsoft.AspNetCore.WebUtilities;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private sealed record AuditCursor(DateTime OccurredAt, long Id);

    public async Task<StoreAuditPage> GetStoreAudit(HttpContext context, Guid store, DateOnly from, DateOnly to,
        string? action, string? after, int limit, CancellationToken ct)
    {
        if (!IsValidReportPeriod(from, to) || limit is < 1 or > 100 ||
            (action is not null && (action.Length is < 1 or > 100 ||
             action.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('.' or '_' or '-')))))
            throw new PosError(400, "invalid_audit_filter");
        var cursor = DecodeAuditCursor(after);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, """
            SELECT a.id, a.occurred_at, a.action, a.staff_id, a.object_id, a.register_id, a.request_id,
                   s.first_name, s.last_name, s.display_name, s.subject,
                   EXISTS(SELECT 1 FROM staff_store_roles r
                      WHERE (r.org_id,r.store_id,r.staff_id,r.role) = ($10,$2,a.staff_id,'owner'))
            FROM public.read_store_audit($1, $2, $3, $4, $5, $6, $7, $8, $9)
            AS a
            LEFT JOIN staff s ON (s.org_id,s.id) = ($10,a.staff_id)
            ORDER BY a.occurred_at DESC, a.id DESC
            """, scope.Organization, store, from, to, scope.TimeZone, action ?? "",
            cursor?.OccurredAt ?? DateTime.UnixEpoch, cursor?.Id ?? 0, limit + 1, scope.Organization);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<StoreAuditEvent> items = [];
        while (await rows.ReadAsync(ct))
        {
            items.Add(new StoreAuditEvent(rows.GetInt64(0), rows.GetDateTime(1), rows.GetString(2), rows.GetGuid(3),
                rows.GetGuid(4), rows.IsDBNull(5) ? null : rows.GetGuid(5), rows.GetString(6))
            {
                StaffName = AuditStaffName(rows.IsDBNull(7) ? null : rows.GetString(7),
                    rows.IsDBNull(8) ? null : rows.GetString(8), rows.IsDBNull(9) ? null : rows.GetString(9),
                    rows.IsDBNull(10) ? null : rows.GetString(10)),
                StaffIsOwner = rows.GetBoolean(11)
            });
        }
        string? next = null;
        if (items.Count > limit)
        {
            items.RemoveAt(limit);
            var last = items[^1];
            next = WebEncoders.Base64UrlEncode(JsonSerializer.SerializeToUtf8Bytes(new AuditCursor(last.OccurredAt, last.Id)));
        }
        return new StoreAuditPage(store, scope.TimeZone, from, to, action, items, next);
    }

    internal static string? AuditStaffName(string? firstName, string? lastName, string? displayName, string? subject)
    {
        foreach (var name in new[] { string.Join(" ", new[] { firstName, lastName }.Where(n => !string.IsNullOrWhiteSpace(n))), displayName, subject })
            if (!string.IsNullOrWhiteSpace(name) && name.Trim().Length <= 256 && !Guid.TryParse(name.Trim(), out _)) return name.Trim();
        return null;
    }

    private static AuditCursor? DecodeAuditCursor(string? cursor)
    {
        if (cursor is null)
            return null;
        if (cursor.Length is > 0 and <= 256)
        {
            try
            {
                var value = JsonSerializer.Deserialize<AuditCursor>(WebEncoders.Base64UrlDecode(cursor));
                if (value is { Id: > 0 } && value.OccurredAt.Kind == DateTimeKind.Utc)
                    return value;
            }
            catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
            {
                // The server still applies the authorized store and period on every page.
            }
        }
        throw new PosError(400, "invalid_audit_cursor");
    }
}
