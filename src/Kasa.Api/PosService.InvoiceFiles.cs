using System.Security.Cryptography;
using Microsoft.AspNetCore.Http.Features;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    internal const int MaxInvoiceFileBytes = 2 * 1024 * 1024;

    public async Task<InvoiceFile> UploadInvoiceFile(HttpContext context, Guid store, Guid receipt, Guid file,
        string fileName, CancellationToken ct)
    {
        if (file == Guid.Empty)
            throw new PosError(400, "invalid_invoice_file_id");
        await CheckInvoiceEditor(context, store, receipt, ct);
        fileName = Uri.UnescapeDataString(fileName).Trim();
        if (fileName.Length is < 1 or > 120 || fileName[0] == '.' || fileName.Any(char.IsControl) ||
            fileName.IndexOfAny(['/', '\\', ':']) >= 0)
            throw new PosError(400, "invalid_invoice_file_name");
        var mediaType = context.Request.ContentType;
        if (mediaType is not ("application/pdf" or "image/jpeg" or "image/png"))
            throw new PosError(415, "invalid_invoice_media_type");
        var contents = await ReadInvoiceBody(context, ct);
        ValidateInvoiceFormat(fileName, mediaType, contents);
        var digest = SHA256.HashData(contents);

        var pending = await SaveInvoiceFile(context, store, receipt, file, fileName, mediaType, contents, digest, ct);
        return pending.ScanStatus == "pending" ? await ScanInvoiceFile(context, store, receipt, file, ct) : pending;
    }

    private async Task CheckInvoiceEditor(HttpContext context, Guid store, Guid receipt, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await RequireInvoiceEditor(db, tx, scope, receipt, ct);
    }

    private async Task<InvoiceFile> SaveInvoiceFile(HttpContext context, Guid store, Guid receipt, Guid file,
        string fileName, string mediaType, byte[] contents, byte[] digest, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await LockReceipt(db, tx, scope, receipt, ct);
        await RequireInvoiceEditor(db, tx, scope, receipt, ct);
        var existing = await ReadInvoiceFile(db, tx, scope, receipt, file, withContents: false, ct);
        if (existing is { } saved)
        {
            if (saved.Metadata.FileName != fileName || saved.Metadata.ContentType != mediaType ||
                !CryptographicOperations.FixedTimeEquals(saved.Digest, digest))
                throw new PosError(409, "invoice_file_id_reused");
            return saved.Metadata;
        }

        await using var count = db.Sql(tx, """
            SELECT count(*) FROM invoice_files WHERE (org_id, store_id, receipt_id) = ($1, $2, $3)
            """, scope.Organization, store, receipt);
        if (await count.ExecuteScalarAsync(ct) is long total && total >= 3)
            throw new PosError(409, "invoice_file_limit");
        var inserted = await db.Run(tx, ct, """
            INSERT INTO invoice_files (org_id, store_id, receipt_id, id, file_name, content_type, size_bytes,
                digest, contents, created_by) VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10) ON CONFLICT DO NOTHING
            """, scope.Organization, store, receipt, file, fileName, mediaType, contents.Length, digest, contents, scope.Staff);
        if (inserted != 1)
            throw new PosError(409, "invoice_file_duplicate");
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "invoice_file.uploaded", file, ct);
        await tx.CommitAsync(ct);
        return new InvoiceFile(file, fileName, mediaType, contents.Length, "pending");
    }

    public async Task<InvoiceFile> ScanInvoiceFile(HttpContext context, Guid store, Guid receipt, Guid file, CancellationToken ct)
    {
        byte[] digest;
        byte[] contents;
        await using (var db = await source.OpenConnectionAsync(ct))
        await using (var tx = await db.BeginTransactionAsync(ct))
        {
            var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
            await RequireInvoiceEditor(db, tx, scope, receipt, ct);
            var saved = await ReadInvoiceFile(db, tx, scope, receipt, file, withContents: true, ct)
                ?? throw new PosError(404, "invoice_file_not_found");
            if (saved.Metadata.ScanStatus != "pending")
                return saved.Metadata;
            digest = saved.Digest;
            contents = saved.Contents ?? throw new InvalidOperationException("Pending invoice file has no contents.");
        }

        var status = await invoiceScanner.Scan(contents, ct);
        await using var finishDb = await source.OpenConnectionAsync(ct);
        await using var finishTx = await finishDb.BeginTransactionAsync(ct);
        var finishScope = await Access.RequireStore(finishDb, finishTx, context, configuration, store, false, ct);
        await LockReceipt(finishDb, finishTx, finishScope, receipt, ct);
        var current = await ReadInvoiceFile(finishDb, finishTx, finishScope, receipt, file, withContents: false, ct)
            ?? throw new PosError(404, "invoice_file_not_found");
        if (!CryptographicOperations.FixedTimeEquals(current.Digest, digest))
            throw new PosError(409, "invoice_file_changed");
        if (status == "pending" || current.Metadata.ScanStatus != "pending")
            return current.Metadata;
        await RequireInvoiceEditor(finishDb, finishTx, finishScope, receipt, ct);
        await finishDb.Run(finishTx, ct, """
            UPDATE invoice_files SET scan_status = $5, contents = CASE WHEN $5 = 'rejected' THEN NULL ELSE contents END
            WHERE (org_id, store_id, receipt_id, id) = ($1, $2, $3, $4)
            """, finishScope.Organization, store, receipt, file, status);
        await Audit(finishDb, finishTx, context, finishScope.Organization, store, Guid.Empty, finishScope.Staff,
            "invoice_file." + status, file, ct);
        await finishTx.CommitAsync(ct);
        return current.Metadata with { ScanStatus = status };
    }

    public async Task<IReadOnlyList<InvoiceFile>> ListInvoiceFiles(HttpContext context, Guid store, Guid receipt,
        CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        _ = await ReadReceipt(db, tx, scope, receipt, ct) ?? throw new PosError(404, "receipt_not_found");
        await using var command = db.Sql(tx, """
            SELECT id, file_name, content_type, size_bytes, scan_status FROM invoice_files
            WHERE (org_id, store_id, receipt_id) = ($1, $2, $3) ORDER BY created_at, id
            """, scope.Organization, store, receipt);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<InvoiceFile> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(ReadFileMetadata(rows));
        return result;
    }

    public async Task<(InvoiceFile Metadata, byte[] Contents)> DownloadInvoiceFile(HttpContext context, Guid store,
        Guid receipt, Guid file, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await using var command = db.Sql(tx, """
            SELECT id, file_name, content_type, size_bytes, scan_status,
                CASE WHEN scan_status = 'clean' THEN contents END
            FROM invoice_files WHERE (org_id, store_id, receipt_id, id) = ($1, $2, $3, $4)
            """, scope.Organization, store, receipt, file);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new PosError(404, "invoice_file_not_found");
        if (rows.IsDBNull(5))
            throw new PosError(409, "invoice_file_not_clean");
        var data = (ReadFileMetadata(rows), rows.GetFieldValue<byte[]>(5));
        await rows.CloseAsync();
        await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "invoice_file.downloaded", file, ct);
        await tx.CommitAsync(ct);
        return data;
    }

    public async Task DeleteInvoiceFile(HttpContext context, Guid store, Guid receipt, Guid file, CancellationToken ct)
    {
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, false, ct);
        await LockReceipt(db, tx, scope, receipt, ct);
        await RequireInvoiceEditor(db, tx, scope, receipt, ct);
        var changed = await db.Run(tx, ct, """
            DELETE FROM invoice_files WHERE (org_id, store_id, receipt_id, id) = ($1, $2, $3, $4)
            """, scope.Organization, store, receipt, file);
        if (changed != 0)
            await Audit(db, tx, context, scope.Organization, store, Guid.Empty, scope.Staff, "invoice_file.deleted", file, ct);
        await tx.CommitAsync(ct);
    }

    private async Task RequireVerifiedInvoiceFiles(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid receipt, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT count(*), count(*) FILTER (WHERE scan_status <> 'clean') FROM invoice_files
            WHERE (org_id, store_id, receipt_id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, receipt);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct))
            throw new InvalidOperationException("Invoice file count missing.");
        if (rows.GetInt64(1) != 0)
            throw new PosError(409, "invoice_files_not_verified");
        if (rows.GetInt64(0) == 0 && configuration.GetValue<bool>("Purchasing:RequireReceiptAttachment"))
            throw new PosError(409, "invoice_file_required");
    }

    private static async Task RequireInvoiceEditor(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid receipt, CancellationToken ct)
    {
        var document = await ReadReceipt(db, tx, scope, receipt, ct) ?? throw new PosError(404, "receipt_not_found");
        RequireReceiptEditor(scope, document);
        if (document.Status != "draft")
            throw new PosError(409, "receipt_not_editable");
    }

    private static async Task<(InvoiceFile Metadata, byte[] Digest, byte[]? Contents)?> ReadInvoiceFile(
        NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid receipt, Guid file, bool withContents,
        CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT id, file_name, content_type, size_bytes, scan_status, digest,
                CASE WHEN $5 AND scan_status = 'pending' THEN contents END
            FROM invoice_files WHERE (org_id, store_id, receipt_id, id) = ($1, $2, $3, $4)
            """, scope.Organization, scope.Store, receipt, file, withContents);
        await using var rows = await command.ExecuteReaderAsync(ct);
        return await rows.ReadAsync(ct) ? (ReadFileMetadata(rows), rows.GetFieldValue<byte[]>(5),
            rows.IsDBNull(6) ? null : rows.GetFieldValue<byte[]>(6)) : null;
    }

    private static InvoiceFile ReadFileMetadata(NpgsqlDataReader rows) =>
        new(rows.GetGuid(0), rows.GetString(1), rows.GetString(2), rows.GetInt32(3), rows.GetString(4));

    private static async Task<byte[]> ReadInvoiceBody(HttpContext context, CancellationToken ct)
    {
        if (context.Request.ContentLength > MaxInvoiceFileBytes)
            throw new PosError(413, "invoice_file_too_large");
        if (context.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
            limit.MaxRequestBodySize = MaxInvoiceFileBytes;

        using var body = new MemoryStream();
        var buffer = new byte[32 * 1024];
        int read;
        while ((read = await context.Request.Body.ReadAsync(buffer, ct)) != 0)
        {
            if (body.Length + read > MaxInvoiceFileBytes)
                throw new PosError(413, "invoice_file_too_large");
            body.Write(buffer, 0, read);
        }
        return body.ToArray();
    }

    private static void ValidateInvoiceFormat(string name, string mediaType, ReadOnlySpan<byte> contents)
    {
        var extension = Path.GetExtension(name).ToLowerInvariant();
        var valid = mediaType switch
        {
            "application/pdf" => extension == ".pdf" && contents.StartsWith("%PDF-"u8) &&
                contents[^Math.Min(contents.Length, 1024)..].IndexOf("%%EOF"u8) >= 0,
            "image/jpeg" => extension is ".jpg" or ".jpeg" && contents.StartsWith(new byte[] { 255, 216, 255 }) &&
                contents.EndsWith(new byte[] { 255, 217 }),
            "image/png" => extension == ".png" && contents.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) &&
                contents.EndsWith(new byte[] { 0, 0, 0, 0, 73, 69, 78, 68, 174, 66, 96, 130 }),
            _ => false
        };
        if (!valid)
            throw new PosError(415, "invoice_file_format_mismatch");
    }
}
