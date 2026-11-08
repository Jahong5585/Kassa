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
            SELECT id, occurred_at, action, staff_id, object_id, register_id, request_id
            FROM public.read_store_audit($1, $2, $3, $4, $5, $6, $7, $8, $9)
            ORDER BY occurred_at DESC, id DESC
            """, scope.Organization, store, from, to, scope.TimeZone, action ?? "",
            cursor?.OccurredAt ?? DateTime.UnixEpoch, cursor?.Id ?? 0, limit + 1);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<StoreAuditEvent> items = [];
        while (await rows.ReadAsync(ct))
        {
            items.Add(new StoreAuditEvent(rows.GetInt64(0), rows.GetDateTime(1), rows.GetString(2), rows.GetGuid(3),
                rows.GetGuid(4), rows.IsDBNull(5) ? null : rows.GetGuid(5), rows.GetString(6)));
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
