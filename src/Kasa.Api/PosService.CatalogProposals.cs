using System.Text.Json;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    public async Task<ProductProposal> ProposeProduct(HttpContext context, Guid store, ProposeProduct request,
        CancellationToken ct)
    {
        var input = request.Product ?? throw new PosError(400, "invalid_product_proposal");
        var card = ProposalCard(request.ProposalId, input);
        var body = new ProductProposalBody(card.Name, card.Unit, card.RequiresExpiry, card.Sku, card.Category,
            card.Barcodes.ToArray());

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await LockProposal(db, tx, scope, request.ProposalId, ct);
        var existing = (await ReadProposals(db, tx, scope, request.ProposalId, null, 0, ct)).SingleOrDefault();
        if (existing is not null)
        {
            if (existing.ProposedBy != scope.Staff ||
                !body.Barcodes.SequenceEqual(existing.Product.Barcodes) ||
                body with { Barcodes = existing.Product.Barcodes } != existing.Product)
                throw new PosError(409, "product_proposal_id_reused");
            return existing;
        }

        await using var command = db.Sql(tx, """
            INSERT INTO product_proposals (org_id, store_id, id, proposed_by, body) VALUES ($1, $2, $3, $4, $5::jsonb)
            ON CONFLICT DO NOTHING RETURNING created_at
            """, scope.Organization, store, request.ProposalId, scope.Staff, JsonSerializer.Serialize(body));
        if (await command.ExecuteScalarAsync(ct) is not DateTime created)
            throw new PosError(409, "product_proposal_id_reused");
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "product.proposed",
            request.ProposalId, ct);
        await tx.CommitAsync(ct);
        return new ProductProposal(request.ProposalId, scope.Staff, "pending", body, created, null, null, null, null);
    }

    public async Task<IReadOnlyList<ProductProposal>> ListProductProposals(HttpContext context, Guid store, string? status,
        int? offset, CancellationToken ct)
    {
        ValidateStockOffset(offset);
        if (status is not (null or "pending" or "approved" or "rejected"))
            throw new PosError(400, "invalid_product_proposal_status");
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        return await ReadProposals(db, tx, scope, null, status, offset ?? 0, ct);
    }

    public async Task<ProductProposal> ReviewProductProposal(HttpContext context, Guid store, Guid id,
        ReviewProductProposal request, bool approve, CancellationToken ct)
    {
        if (!approve && !IsText(request.Reason, 500))
            throw new PosError(400, "product_proposal_rejection_reason_required");
        var reason = OptionalText(request.Reason, 500);
        var outcome = approve ? "approved" : "rejected";

        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockProposal(db, tx, scope, id, ct);
        var proposal = (await ReadProposals(db, tx, scope, id, null, 0, ct)).SingleOrDefault()
            ?? throw new PosError(404, "product_proposal_not_found");
        if (proposal.Status != "pending")
        {
            if (proposal.Status != outcome || proposal.ReviewReason != reason)
                throw new PosError(409, "product_proposal_already_reviewed");
            return proposal;
        }

        if (approve)
        {
            await LockProduct(db, tx, scope.Organization, id, true, ct);
            if (await ReadProductCard(db, tx, scope, id, ct) is not null)
                throw new PosError(409, "product_id_reused");
            await WriteCard(db, tx, context, scope, ProposalCard(id, proposal.Product), 0, ct);
        }
        await using var update = db.Sql(tx, """
            UPDATE product_proposals SET status = $4, review_reason = $5, reviewed_by = $6,
                reviewed_at = statement_timestamp(), published_product_id = CASE WHEN $4 = 'approved' THEN id END
            WHERE (org_id, store_id, id) = ($1, $2, $3) RETURNING reviewed_at
            """, scope.Organization, store, id, outcome, reason, scope.Staff);
        var reviewed = (DateTime)(await update.ExecuteScalarAsync(ct)
            ?? throw new InvalidOperationException("Updated product proposal missing."));
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "product.proposal_" + outcome,
            id, ct);
        await tx.CommitAsync(ct);
        return proposal with { Status = outcome, ReviewReason = reason, ReviewedBy = scope.Staff, ReviewedAt = reviewed,
            PublishedProductId = approve ? id : null };
    }

    private static ProductCard ProposalCard(Guid id, ProductProposalBody body) =>
        ValidateProductCard(id, new WriteProductCard(0, body.Name, body.Unit, body.RequiresExpiry, body.Sku, body.Category,
            null, null, null, body.Barcodes));

    private static Task LockProposal(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid id,
        CancellationToken ct) =>
        LockOperation(db, tx, $"product_proposal:{scope.Organization}:{scope.Store}:{id}", ct);

    private static async Task<IReadOnlyList<ProductProposal>> ReadProposals(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid? id, string? status, int offset, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT id, proposed_by, status, body, created_at, review_reason, reviewed_by, reviewed_at, published_product_id
            FROM product_proposals WHERE (org_id, store_id) = ($1, $2) AND ($3 OR proposed_by = $4)
                AND ($5 OR id = $6) AND ($7 = '' OR status = $7)
            ORDER BY created_at DESC, id LIMIT 25 OFFSET $8
            """, scope.Organization, scope.Store, scope.Role == "admin", scope.Staff, id is null, id ?? Guid.Empty,
            status ?? "", offset);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ProductProposal> result = [];
        while (await rows.ReadAsync(ct))
        {
            var body = JsonSerializer.Deserialize<ProductProposalBody>(rows.GetString(3))
                ?? throw new InvalidOperationException("Product proposal body missing.");
            result.Add(new ProductProposal(rows.GetGuid(0), rows.GetGuid(1), rows.GetString(2), body,
                rows.GetDateTime(4), rows.IsDBNull(5) ? null : rows.GetString(5), rows.IsDBNull(6) ? null : rows.GetGuid(6),
                rows.TimeOrNull(7), rows.IsDBNull(8) ? null : rows.GetGuid(8)));
        }
        return result;
    }
}
