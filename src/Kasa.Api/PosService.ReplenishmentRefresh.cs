using System.Data;
using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    internal sealed record ReplenishmentWork(Guid Organization, Guid Store, Guid Product);

    public async Task<ReplenishmentSnapshot> GetReplenishmentSnapshot(HttpContext context, Guid store,
        string code, string sku, CancellationToken ct)
    {
        code = SupplierCode(code);
        sku = SupplierCode(sku);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, """
            SELECT p.version, s.policy_version, s.calculated_at, s.error_code, s.recommendation,
                   s.policy_version <> p.version OR s.refresh_at <= statement_timestamp() OR EXISTS (
                       SELECT 1 FROM replenishment_refresh_events e
                       WHERE (e.org_id, e.store_id, e.product_id) = (p.org_id, p.store_id, p.product_id))
            FROM replenishment_policies p LEFT JOIN replenishment_snapshots s
                USING (org_id, store_id, supplier_code, supplier_sku)
            WHERE (p.org_id, p.store_id, p.supplier_code, p.supplier_sku) = ($1, $2, $3, $4)
            """, scope.Organization, store, code, sku);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(404, "replenishment_policy_not_found");
        var version = rows.GetInt32(0);
        if (rows.IsDBNull(1))
            return new ReplenishmentSnapshot("pending", version, null, null, null, null);
        var stale = rows.GetBoolean(5);
        var error = rows.IsDBNull(3) ? null : rows.GetString(3);
        var recommendation = rows.IsDBNull(4) ? null : JsonSerializer.Deserialize<ReplenishmentRecommendation>(rows.GetString(4));
        if (stale && recommendation is not null)
            recommendation = recommendation with { Draft = null };
        var status = stale ? "stale" : error is null ? "ready" : "unavailable";
        return new ReplenishmentSnapshot(status, version, rows.GetInt32(1), rows.GetDateTime(2), error, recommendation);
    }

    internal async Task<IReadOnlyList<ReplenishmentWork>> ListReplenishmentWork(CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        await using var command = db.Sql(tx, """
            WITH recent_events AS (
                SELECT org_id, store_id, product_id, id FROM replenishment_refresh_events ORDER BY id LIMIT 256
            ), queued AS (
                SELECT org_id, store_id, product_id FROM recent_events
                GROUP BY org_id, store_id, product_id ORDER BY min(id) LIMIT 16
            ), scheduled AS (
                SELECT p.org_id, p.store_id, p.product_id
                FROM replenishment_policies p LEFT JOIN replenishment_snapshots s
                    USING (org_id, store_id, supplier_code, supplier_sku)
                WHERE s.policy_version IS DISTINCT FROM p.version OR s.refresh_at <= statement_timestamp()
                GROUP BY p.org_id, p.store_id, p.product_id
                ORDER BY min(coalesce(s.refresh_at, '-infinity'::timestamptz)), p.org_id, p.store_id, p.product_id LIMIT 8
            )
            SELECT org_id, store_id, product_id FROM queued
            UNION SELECT org_id, store_id, product_id FROM scheduled
            """);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ReplenishmentWork> work = [];
        while (await rows.ReadAsync(ct))
            work.Add(new ReplenishmentWork(rows.GetGuid(0), rows.GetGuid(1), rows.GetGuid(2)));
        return work;
    }

    internal async Task RefreshReplenishment(ReplenishmentWork work, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        await using var claim = db.Sql(tx, "SELECT pg_try_advisory_xact_lock(hashtextextended($1, 0))",
            $"replenishment:{work.Organization}:{work.Store}:{work.Product}");
        if (await claim.ExecuteScalarAsync(ct) is not true)
            return;

        await using var command = db.Sql(tx, """
            WITH events AS (
                SELECT EXISTS (
                    SELECT 1 FROM replenishment_refresh_events
                    WHERE (org_id, store_id, product_id) = ($1, $2, $3)
                ) AS dirty
            )
            SELECT p.supplier_code, p.supplier_sku, p.version, p.settings, stores.time_zone
            FROM replenishment_policies p
            JOIN stores ON (stores.org_id, stores.id) = (p.org_id, p.store_id)
            LEFT JOIN replenishment_snapshots s
              ON (s.org_id, s.store_id, s.supplier_code, s.supplier_sku) =
                 (p.org_id, p.store_id, p.supplier_code, p.supplier_sku)
            WHERE (p.org_id, p.store_id, p.product_id) = ($1, $2, $3)
              AND ((SELECT dirty FROM events) OR s.policy_version IS DISTINCT FROM p.version
                   OR s.refresh_at <= transaction_timestamp())
            ORDER BY p.supplier_code, p.supplier_sku
            """, work.Organization, work.Store, work.Product);
        List<(string Code, string Sku, int Version, ReplenishmentRequest Settings, string Zone)> policies = [];
        await using (var rows = await command.ExecuteReaderAsync(ct))
        {
            while (await rows.ReadAsync(ct))
            {
                var settings = JsonSerializer.Deserialize<ReplenishmentRequest>(rows.GetString(3))
                    ?? throw new InvalidOperationException("Replenishment settings missing.");
                policies.Add((rows.GetString(0), rows.GetString(1), rows.GetInt32(2), settings, rows.GetString(4)));
            }
        }

        foreach (var policy in policies)
        {
            var scope = new StoreScope(work.Organization, work.Store, Guid.Empty, policy.Zone, "");
            ReplenishmentRecommendation? recommendation = null;
            var error = "";
            try
            {
                recommendation = await CalculateReplenishment(db, tx, scope, policy.Code, policy.Sku, policy.Settings,
                    policy.Version, ct);
            }
            catch (PosError ex)
            {
                error = ex.Code;
            }
            await db.Run(tx, ct, """
                INSERT INTO replenishment_snapshots (org_id, store_id, supplier_code, supplier_sku,
                    policy_version, calculated_at, refresh_at, recommendation, error_code)
                VALUES ($1, $2, $3, $4, $5, transaction_timestamp(),
                    replenishment_next_refresh_at(transaction_timestamp(), $8, CASE WHEN $9 THEN $10::time END),
                    NULLIF($6, '')::jsonb, NULLIF($7, ''))
                ON CONFLICT (org_id, store_id, supplier_code, supplier_sku) DO UPDATE SET
                    policy_version = EXCLUDED.policy_version, calculated_at = EXCLUDED.calculated_at,
                    refresh_at = EXCLUDED.refresh_at, recommendation = EXCLUDED.recommendation, error_code = EXCLUDED.error_code
                """, work.Organization, work.Store, policy.Code, policy.Sku, policy.Version,
                recommendation is null ? "" : JsonSerializer.Serialize(recommendation), error,
                policy.Zone, policy.Settings.OrderCutoffTime.HasValue, policy.Settings.OrderCutoffTime ?? TimeOnly.MinValue);
        }

        // In this snapshot DELETE sees only events whose business transactions contributed to the calculation.
        await db.Run(tx, ct, """
            DELETE FROM replenishment_refresh_events WHERE (org_id, store_id, product_id) = ($1, $2, $3)
            """, work.Organization, work.Store, work.Product);
        await tx.CommitAsync(ct);
    }
}
