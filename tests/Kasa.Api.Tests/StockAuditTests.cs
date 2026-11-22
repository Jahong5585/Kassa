using Kasa.Api;
using Xunit;
namespace Kasa.Api.Tests;
public sealed class StockAuditTests
{
    [Fact] public void AuditPrefersActualNamesOverOpaqueSubject()
    {
        var opaque=Guid.NewGuid().ToString();
        Assert.Equal("Jahongir Orifjonov",PosService.AuditStaffName("Jahongir","Orifjonov",opaque,opaque));
        Assert.Equal("Davronbek",PosService.AuditStaffName(null,null,"Davronbek",opaque));
        Assert.Null(PosService.AuditStaffName(null,null,opaque,opaque));
    }
    [Fact] public void LotCostIncludesQuarantineAndPreservesUnknownValue()
    {
        var row=new StockLot(Guid.NewGuid(),Guid.NewGuid(),"Sut","","piece",null,4,1,4,1,false){InventoryValue=40000};
        Assert.Equal(8000,row.UnitCost);
        Assert.Equal(9000,(row with {InventoryValue=45000}).UnitCost);
        Assert.Null((row with {InventoryValue=null}).UnitCost);
        Assert.Null((row with {StockQuantity=0,QuarantineQuantity=0,InventoryValue=0}).UnitCost);
    }
}
