using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<MyRoleSelection> ReadMyRoles(HttpContext context, Guid store, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var selection = await Access.ReadRolesForStore(db, tx, context, store, ct);
        return new MyRoleSelection(selection.Organization, selection.Staff, selection.Store, selection.TimeZone,
            selection.Roles, selection.Owner);
    }
}
