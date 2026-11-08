using System.Globalization;
using Microsoft.VisualBasic.FileIO;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private static readonly string[] CatalogCsvHeader =
    [
        "productId", "expectedVersion", "name", "unit", "requiresExpiry", "sku", "category", "brand",
        "supplierRef", "taxProfile", "barcodes", "unitPrice", "expectedPriceVersion"
    ];

    public async Task<CatalogImportResult> ImportStoreCatalog(HttpContext context, Guid store, ImportCatalog request,
        CancellationToken ct)
    {
        var entries = ParseCatalogCsv(request.Csv);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        await LockInventoryMembership(db, tx, scope.Organization, ct, shared: true);
        var result = new Dictionary<Guid, ProductCard>();
        // Use the sale's product order while holding multiple transaction locks.
        foreach (var entry in entries.OrderBy(entry => entry.Card.ProductId))
        {
            try
            {
                var saved = await WriteCard(db, tx, context, scope, entry.Card, entry.Card.Version - 1, ct);
                if (entry.Price is { } requestedPrice)
                {
                    var price = await WritePrice(db, tx, context, scope, saved.ProductId,
                        requestedPrice with { Active = saved.Active }, ct);
                    saved = saved with { UnitPrice = price.UnitPrice, PriceVersion = price.Version, Active = price.Active };
                }
                result.Add(saved.ProductId, saved);
            }
            catch (PosError ex)
            {
                throw new PosError(ex.Status, ex.Code, entry.Row);
            }
        }

        if (!request.Preview)
            await tx.CommitAsync(ct);
        return new CatalogImportResult(request.Preview, entries.Select(entry => result[entry.Card.ProductId]).ToArray());
    }

    private static List<CatalogCsvEntry> ParseCatalogCsv(string csv)
    {
        if (!IsText(csv, 48_000))
            throw new PosError(400, "invalid_catalog_csv");
        using var input = new StringReader(csv.TrimStart('\uFEFF'));
        using var parser = new TextFieldParser(input)
        {
            HasFieldsEnclosedInQuotes = true
        };
        parser.SetDelimiters(",");
        List<CatalogCsvEntry> entries = [];
        var ids = new HashSet<Guid>();
        var barcodes = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            if (parser.ReadFields() is not { } header || !header.SequenceEqual(CatalogCsvHeader))
                throw new PosError(400, "invalid_catalog_csv_header", 1);

            while (!parser.EndOfData)
            {
                var row = checked((int)parser.LineNumber);
                var fields = parser.ReadFields();
                if (fields is null)
                    break;
                if (entries.Count >= 100 || fields.Length != CatalogCsvHeader.Length ||
                    !Guid.TryParse(fields[0], out var id) || !ids.Add(id) ||
                    !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var version) ||
                    !bool.TryParse(fields[4], out var requiresExpiry))
                    throw new PosError(400, "invalid_catalog_csv_row", row);
                try
                {
                    var codes = ParseCsvBarcodes(fields[10]);
                    if (codes.Any(code => !barcodes.Add(code.Code)))
                        throw new PosError(409, "product_barcode_in_use");
                    var request = new WriteProductCard(version, fields[2], fields[3], requiresExpiry, fields[5],
                        fields[6], fields[7], fields[8], fields[9], codes);
                    var product = ValidateProductCard(id, request);
                    WriteProductPrice? price = null;
                    if (fields[11] != "" || fields[12] != "")
                    {
                        if (!decimal.TryParse(fields[11], NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture,
                                out var amount) ||
                            !int.TryParse(fields[12], NumberStyles.None, CultureInfo.InvariantCulture, out var priceVersion))
                            throw new PosError(400, "invalid_product_price");
                        price = new WriteProductPrice(priceVersion, amount, true, "catalog_import");
                        ValidateProductPrice(price);
                    }
                    entries.Add(new CatalogCsvEntry(row, product, price));
                }
                catch (PosError ex)
                {
                    throw new PosError(ex.Status, ex.Code, row);
                }
            }
        }
        catch (MalformedLineException)
        {
            throw new PosError(400, "invalid_catalog_csv_row", checked((int)parser.ErrorLineNumber));
        }
        if (entries.Count == 0)
            throw new PosError(400, "catalog_csv_empty");
        return entries;
    }

    private static ProductBarcode[] ParseCsvBarcodes(string field)
    {
        if (field == "")
            return [];
        List<ProductBarcode> result = [];
        foreach (var entry in field.Split(';'))
        {
            var parts = entry.Split('*', 2);
            decimal factor = 1;
            if (parts.Length == 2 && !decimal.TryParse(parts[1], NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture, out factor))
                throw new PosError(400, "invalid_product_barcode");
            result.Add(new ProductBarcode(parts[0], factor));
        }
        return result.ToArray();
    }

    private sealed record CatalogCsvEntry(int Row, ProductCard Card, WriteProductPrice? Price);
}
