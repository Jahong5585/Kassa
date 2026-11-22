using Kasa.Api;
using Xunit;

namespace Kasa.Api.Tests;

public sealed class QuickReceivingTests
{
    private static QuickReceivingItem Line(string unit = "piece", decimal quantity = 2m) => new(
        Guid.NewGuid(), Guid.NewGuid(), 0, 0,
        new ReceivingProductInput("Non", unit, false, null, null, null, []), quantity,
        2000m, 3000m, null, null);
    private static QuickReceivingRequest Request(params QuickReceivingItem[] items) => new(Guid.NewGuid(), items);
    [Fact] public void AcceptsNewNoBarcodeAndWeightedItems() =>
        PosService.ValidateQuickReceiving(Request(Line(), Line("kg", 1.125m)));
    [Fact] public void UnknownCostAndRetailAreNotInvented() =>
        PosService.ValidateQuickReceiving(Request(Line() with { UnitCost = null, SalePrice = null }));
    [Fact] public void RejectsEmptyDuplicateAndOversizedBatch()
    {
        Reject(Request());
        var one = Line(); Reject(Request(one, one with { LotId = Guid.NewGuid() }));
        Reject(Request(Enumerable.Range(0,101).Select(_=>Line()).ToArray()));
    }
    [Fact] public void RejectsInvalidQuantitiesMoneyAndVersions()
    {
        Reject(Request(Line(quantity:0))); Reject(Request(Line(quantity:1.5m)));
        Reject(Request(Line("kg",1.0001m))); Reject(Request(Line() with { UnitCost = -1 }));
        Reject(Request(Line() with { SalePrice = 0 })); Reject(Request(Line() with { SalePrice = 0.001m }));
        Reject(Request(Line() with { ExpectedVersion = 2, ExpectedPriceVersion = 0 }));
        Reject(Request(Line() with { ExpectedVersion = int.MaxValue, ExpectedPriceVersion = 1 }));
        Reject(Request(Line("kg",99999999999m) with { UnitCost = 999999999999.99m }));
    }
    [Fact] public void ValidatesNewProductMetadataAndBarcodes()
    {
        var one=Line(); Reject(Request(one with { Product = one.Product with { Name = "" } }));
        Reject(Request(one with { Product = one.Product with { Barcodes = [new ProductBarcode("bad",1)] } }));
    }
    private static void Reject(QuickReceivingRequest request) => Assert.Throws<PosError>(()=>PosService.ValidateQuickReceiving(request));
}
