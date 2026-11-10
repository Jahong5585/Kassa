using System.Text.Json.Serialization;

namespace Kasa.Api;

public sealed record RegisterDevice(Guid RegisterId, string Name, bool Active, int Version, IReadOnlyList<Guid> CashierIds);
public sealed record RegisterNewDevice(Guid RegisterId, string Name, Guid CashierId, string RegisterKey);
public sealed record RotateDeviceKey(int ExpectedVersion, string RegisterKey);
public sealed record RevokeDevice(int ExpectedVersion);
public sealed record StoreStaffAccess(Guid StaffId, string Subject, string Role, bool Active, IReadOnlyList<Guid> RegisterIds);
public sealed record ConnectStoreStaff(string Subject, string Role, Guid? RegisterId = null);
public sealed record StoreAuditEvent(long Id, DateTime OccurredAt, string Action, Guid StaffId, Guid ObjectId,
    Guid? RegisterId, string RequestId);
public sealed record StoreAuditPage(Guid StoreId, string TimeZone, DateOnly From, DateOnly To, string? Action,
    IReadOnlyList<StoreAuditEvent> Items, string? NextCursor);

public sealed record OpenShift(Guid ShiftId, [property: JsonRequired] decimal OpeningCash);
public sealed record CloseShift([property: JsonRequired] decimal CountedCash);
public sealed record SaleItem(Guid ProductId, Guid LotId, int PriceVersion, decimal Quantity);
public sealed record CreateSale(Guid OperationId, Guid ShiftId, string Method, decimal? Tendered, SaleItem[] Items,
    ManualSaleDiscount? Discount = null, Guid? CartId = null, int? CartVersion = null);
public sealed record SaleLine(Guid ProductId, Guid LotId, string Name, string Unit, decimal Quantity,
    decimal UnitPrice, decimal LineTotal)
{
    public decimal DiscountAmount { get; init; }
    public int? PriceVersion { get; init; }
    internal decimal? InventoryCost { get; init; }
}
public sealed record SaleResult(Guid OperationId, Guid ShiftId, string Method, decimal Total, decimal? Tendered,
    decimal? ChangeDue, DateTime CreatedAt, IReadOnlyList<SaleLine> Lines, string ReceiptMark)
{
    public string? DiscountReason { get; init; }
    public decimal DiscountAmount => Lines.Sum(line => line.DiscountAmount);
}
public sealed record ShiftSummary(Guid ShiftId, DateTime OpenedAt, DateTime? ClosedAt, decimal OpeningCash,
    decimal? CashSales, decimal ManualCashlessSales, decimal? ExpectedCash, decimal? CountedCash,
    decimal? CashDifference, long SalesCount)
{
    public decimal? CashRefunds { get; init; }
    public decimal ManualCashlessRefunds { get; init; }
    public long RefundCount { get; init; }
    public decimal? CashIn { get; init; }
    public decimal? CashOut { get; init; }
    public long OpenCartCount { get; init; }
}
public sealed record CatalogLot(Guid LotId, DateOnly? ExpiryDate, decimal Quantity);
public sealed record CatalogProduct(Guid ProductId, string Name, string Barcode, string Unit, decimal UnitPrice,
    int PriceVersion, bool RequiresExpiry, IReadOnlyList<CatalogLot> Lots)
{
    public bool RequiresLotVerification { get; init; }
    public decimal QuantityFactor { get; init; } = 1;
}

internal sealed class PosError(int status, string code, int? importRow = null) : Exception(code)
{
    public int Status { get; } = status;
    public string Code { get; } = code;
    public int? ImportRow { get; } = importRow;
}

public sealed record RefundItem(Guid ProductId, Guid LotId, decimal Quantity, string Destination);
public sealed record CreateRefund(Guid OperationId, Guid ShiftId, Guid SourceRegisterId, Guid SaleId, string Reason,
    RefundItem[] Items);
public sealed record ApproveRefund(Guid RegisterId, [property: JsonRequired] decimal Amount, CreateRefund Refund);
public sealed record RefundApproval(Guid OperationId, Guid RegisterId, decimal Amount, Guid ApprovedBy);
public sealed record RefundLine(Guid ProductId, Guid LotId, decimal Quantity, decimal Amount, string Destination);
public sealed record RefundResult(Guid OperationId, Guid ShiftId, Guid SourceRegisterId, Guid SaleId, string Method,
    decimal Total, string Reason, DateTime CreatedAt, Guid? ApprovedBy, IReadOnlyList<RefundLine> Lines, string ReceiptMark);
public sealed record RefundableLine(Guid ProductId, Guid LotId, string Name, string Unit, decimal SoldQuantity,
    decimal SoldAmount, decimal ReturnedQuantity, decimal ReturnedAmount);
public sealed record RefundOptions(Guid SourceRegisterId, Guid SaleId, string Method, IReadOnlyList<RefundableLine> Lines);

public sealed record ReceiptItem(Guid ProductId, Guid LotId, DateOnly? ExpiryDate, decimal Quantity);
public sealed record CreateReceipt(Guid ReceiptId, string SupplierRef, string InvoiceNumber, DateOnly InvoiceDate,
    ReceiptItem[] Items, Guid? PurchaseOrderId = null);
public sealed record EditReceipt(int ExpectedVersion, ReceiptItem[] Items);
public sealed record ReceiptTransition(int ExpectedVersion);
public sealed record ReturnReceipt(int ExpectedVersion, string Reason);
public sealed record CancelReceipt(int ExpectedVersion, string Reason);
public sealed record ReceiptCancellation(Guid CancelledBy, DateTime CancelledAt, string Reason);
public sealed record ReceivingDocument(Guid ReceiptId, string SupplierRef, string InvoiceNumber, DateOnly InvoiceDate,
    string Status, int Version, Guid CreatedBy, Guid? PostedBy, DateTime? PostedAt, string? ReviewNote, ReceiptItem[] Items, Guid? PurchaseOrderId)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ReceiptCancellation? Cancellation { get; init; }
}
public sealed record ReceivingProduct(Guid ProductId, string Name, string Barcode, string Unit, bool RequiresExpiry)
{
    public decimal QuantityFactor { get; init; } = 1;
}

public sealed record OrderItem(Guid ProductId, decimal Quantity);
public sealed record CreatePurchaseOrder(Guid OrderId, string SupplierRef, OrderItem[] Items);
public sealed record OrderLine(Guid ProductId, decimal Ordered, decimal Received, decimal Remaining);
public sealed record PurchaseOrder(Guid OrderId, string SupplierRef, Guid CreatedBy, string Status,
    IReadOnlyList<OrderLine> Lines)
{
    public DateOnly? ExpectedDeliveryDate { get; init; }
}
public sealed record PurchaseOrderSummary(Guid OrderId, string SupplierRef, string Status)
{
    public DateOnly? ExpectedDeliveryDate { get; init; }
}

public sealed record WriteSupplier(int ExpectedVersion, string Name, string? TaxId, string? Phone, string? Email,
    string? PaymentTerms, int LeadTimeDays, [property: JsonRequired] bool Active);
public sealed record SupplierCard(string Code, string Name, string TaxId, string Phone, string Email, string PaymentTerms,
    int LeadTimeDays, bool Active, int Version);
public sealed record ReceivingSupplier(string Code, string Name);

public sealed record InvoiceFile(Guid FileId, string FileName, string ContentType, int SizeBytes, string ScanStatus);
public sealed record ReceiptCostItem(Guid ProductId, Guid LotId, [property: JsonRequired] decimal UnitPrice,
    decimal Discount, decimal IncludedTax, decimal AllocatedCharges);
public sealed record WriteReceiptCosts(int ExpectedVersion, ReceiptCostItem[] Items);
public sealed record ReceiptCostLine(Guid ProductId, Guid LotId, decimal Quantity, decimal UnitPrice, decimal Discount,
    decimal IncludedTax, decimal AllocatedCharges, decimal Total);
public sealed record ReceiptCosts(Guid ReceiptId, int ReceiptVersion, string Status, decimal Total,
    IReadOnlyList<ReceiptCostLine> Lines);

public sealed record StockLotKey(Guid ProductId, Guid LotId);
public sealed record StockLot(Guid ProductId, Guid LotId, string Name, string Barcode, string Unit,
    DateOnly? ExpiryDate, decimal StockQuantity, decimal QuarantineQuantity, decimal AvailableQuantity, long Revision,
    bool RequiresExpiry)
{
    public decimal AccountedQuantity => StockQuantity + QuarantineQuantity;
}
public sealed record ExpiringLot(Guid ProductId, Guid LotId, string Name, string Barcode, string Unit,
    DateOnly? ExpiryDate, int? DaysRemaining, decimal AccountedQuantity, bool RequiresExpiry);

public sealed record CreateShelfTask(Guid TaskId, Guid ProductId, Guid LotId, decimal Quantity);
public sealed record ResolveShelfTask(string Resolution);
public sealed record ShelfTask(Guid TaskId, Guid ProductId, Guid LotId, decimal Quantity, Guid CreatedBy,
    DateTime CreatedAt, Guid? ResolvedBy, DateTime? ResolvedAt, string? Resolution);

public sealed record CreateStockOperation(Guid OperationId, Guid ProductId, Guid LotId, string Kind, decimal Quantity,
    string Reason);
public sealed record StockOperationLine(Guid ProductId, Guid LotId, decimal StockBefore, decimal StockAfter,
    decimal QuarantineBefore, decimal QuarantineAfter)
{
    public decimal StockDelta => StockAfter - StockBefore;
    public decimal QuarantineDelta => QuarantineAfter - QuarantineBefore;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? InventoryCostDelta { get; init; }
}
public sealed record StockOperation(Guid OperationId, string Kind, string Reason, Guid ActorId,
    DateTime CreatedAt, IReadOnlyList<StockOperationLine> Lines);

public sealed record InventoryScope(string Kind, Guid? ProductId = null, string? Category = null);
public sealed record CreateInventory(Guid InventoryId, string Reason, StockLotKey[]? Lots = null,
    InventoryScope? Scope = null);
public sealed record CountInventoryLine(Guid ProductId, Guid LotId, long LotRevision,
    [property: JsonRequired] decimal StockQuantity, [property: JsonRequired] decimal QuarantineQuantity);
public sealed record CountInventory(int ExpectedVersion, CountInventoryLine[] Items);
public sealed record InventoryLine(Guid ProductId, Guid LotId, string Name, string Unit, long LotRevision,
    long? CountedRevision, decimal? ExpectedStock, decimal? ExpectedQuarantine, decimal? CountedStock,
    decimal? CountedQuarantine)
{
    public decimal? StockDifference => CountedStock - ExpectedStock;
    public decimal? QuarantineDifference => CountedQuarantine - ExpectedQuarantine;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? CountStatus { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? InventoryCostDelta { get; init; }
}
public sealed record InventoryDocument(Guid InventoryId, string Reason, string Status, int Version, Guid CreatedBy,
    DateTime CreatedAt, IReadOnlyList<InventoryLine> Lines)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InventoryScope? Scope { get; init; }
    internal byte[]? LastCountDigest { get; init; }
}
public sealed record InventorySummary(Guid InventoryId, string Reason, string Status, int Version, Guid CreatedBy,
    DateTime CreatedAt)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public InventoryScope? Scope { get; init; }
}

public sealed record ProductBarcode(string Code, decimal QuantityFactor);
public sealed record WriteProductCard([property: JsonRequired] int ExpectedVersion, string Name, string Unit,
    bool RequiresExpiry, string? Sku, string? Category, string? Brand, string? SupplierRef, string? TaxProfile,
    ProductBarcode[] Barcodes);
public sealed record ProductCard(Guid ProductId, string Name, string Unit, bool RequiresExpiry, string Sku,
    string Category, string Brand, string SupplierRef, string TaxProfile, int Version,
    IReadOnlyList<ProductBarcode> Barcodes, decimal? UnitPrice, int PriceVersion, bool Active);
public sealed record ProductSearchResult(Guid ProductId, string Name, string Sku, string Barcode, string Unit,
    decimal? UnitPrice, int PriceVersion, bool Active);
public sealed record WriteProductPrice(int ExpectedVersion, [property: JsonRequired] decimal? UnitPrice,
    [property: JsonRequired] bool Active, string Reason);
public sealed record ProductPrice(int Version, decimal? UnitPrice, bool Active, string Reason, Guid? ChangedBy,
    DateTime RecordedAt);
public sealed record ImportCatalog([property: JsonRequired] bool Preview, string Csv);
public sealed record CatalogImportResult(bool Preview, IReadOnlyList<ProductCard> Products);
public sealed record ProductProposalBody(string Name, string Unit, bool RequiresExpiry, string? Sku,
    string? Category, ProductBarcode[] Barcodes);
public sealed record ProposeProduct(Guid ProposalId, ProductProposalBody Product);
public sealed record ReviewProductProposal(string? Reason);
public sealed record ProductProposal(Guid ProposalId, Guid ProposedBy, string Status, ProductProposalBody Product,
    DateTime CreatedAt, string? ReviewReason, Guid? ReviewedBy, DateTime? ReviewedAt, Guid? PublishedProductId);

public sealed record ManualSaleDiscount(string Kind, decimal Value, string Reason);
public sealed record CreateCashMovement(Guid OperationId, string Direction, decimal Amount, string Reason);
public sealed record CashMovement(Guid OperationId, Guid ShiftId, Guid CashierId, string Direction,
    decimal Amount, string Reason, DateTime CreatedAt);
public sealed record SaveParkedCart([property: JsonRequired] int ExpectedVersion, SaleItem[] Items);
public sealed record CancelParkedCart(int ExpectedVersion, string Reason);
public sealed record ParkedCart(Guid CartId, Guid ShiftId, int Version, string Status, DateTime CreatedAt,
    DateTime ExpiresAt, Guid? SaleId, string? CancelReason, SaleItem[] Items);
public sealed record ParkedCartLineReview(Guid ProductId, Guid LotId, int SavedPriceVersion, int? CurrentPriceVersion,
    decimal? CurrentUnitPrice, decimal AvailableQuantity, string Status);
public sealed record ParkedCartReview(ParkedCart Cart, IReadOnlyList<ParkedCartLineReview> Lines);
public sealed record SaleHistoryEntry(Guid ReceiptId, Guid RegisterId, Guid ShiftId, Guid CashierId, DateTime CreatedAt,
    string Method, decimal Total, decimal RefundedAmount, string RefundStatus)
{
    public string PaymentStatus => "recorded";
}

public sealed record WriteSupplierPrice([property: JsonRequired] int ExpectedVersion, Guid ProductId,
    string PackageUnit, decimal UnitsPerPackage, decimal PackagePrice, DateOnly ValidFrom, DateOnly? ValidThrough);
public sealed record SupplierPrice(string SupplierSku, int Version, Guid ProductId, string PackageUnit,
    decimal UnitsPerPackage, decimal PackagePrice, DateOnly ValidFrom, DateOnly? ValidThrough, Guid CreatedBy, DateTime CreatedAt);
public sealed record OrderOffer(string SupplierSku, int PriceVersion, int Packages);
public sealed record SavePurchaseDraft([property: JsonRequired] int ExpectedRevision, string SupplierCode,
    DateOnly ExpectedDeliveryDate, OrderOffer[] Items)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PurchaseReplenishment? Replenishment { get; init; }
}
public sealed record ApprovePurchaseOrder(int ExpectedRevision);
public sealed record DispatchPurchaseOrder(int ExpectedRevision, string Reference);
public sealed record PurchaseDraftLine(string SupplierSku, int PriceVersion, Guid ProductId, string PackageUnit,
    decimal UnitsPerPackage, int Packages, decimal BaseQuantity, decimal PackagePrice, decimal Total);
public sealed record PurchaseDraft(Guid OrderId, string SupplierCode, string Stage, int Revision,
    DateOnly ExpectedDeliveryDate, decimal Total, Guid CreatedBy, DateTime RevisedAt, Guid? ApprovedBy,
    DateTime? ApprovedAt, Guid? SentBy, DateTime? SentAt, string? DispatchReference, IReadOnlyList<PurchaseDraftLine> Lines)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public PurchaseReplenishment? Replenishment { get; init; }
}
public sealed record PurchaseDraftSummary(Guid OrderId, string SupplierCode, string Stage, int Revision,
    DateOnly ExpectedDeliveryDate, decimal Total);
public sealed record SupplierReturnItem(Guid ProductId, Guid LotId, decimal Quantity, string Bucket);
public sealed record CreateSupplierReturn(Guid ReturnId, Guid ReceiptId, string Reason, SupplierReturnItem[] Items);
public sealed record SupplierReturnLine(Guid ProductId, Guid LotId, decimal Quantity, string Bucket, decimal? CreditAmount);
public sealed record SupplierReturn(Guid ReturnId, Guid ReceiptId, string SupplierRef, string Reason, Guid CreatedBy,
    DateTime CreatedAt, decimal? CreditAmount, IReadOnlyList<SupplierReturnLine> Lines);
public sealed record RecordSupplierPayment(Guid OperationId, decimal Amount, DateOnly PaymentDate, string Reference);
public sealed record SupplierPayment(Guid OperationId, decimal Amount, DateOnly PaymentDate, string Reference,
    Guid RecordedBy, DateTime CreatedAt);
public sealed record SupplierBalance(string SupplierCode, decimal InvoicedAmount, decimal ReturnCredit,
    decimal PaidAmount, decimal Balance, long UnpricedReceipts, long UnpricedReturns);

public sealed record ReplenishmentRequest(int HistoryDays = 28, int MinimumHistoryDays = 7, int CoverageDays = 5,
    decimal SafetyStock = 0, int MinimumPackages = 1, int? LeadTimeDays = null, int[]? OrderWeekdays = null,
    decimal? ManualDailyDemand = null, TimeOnly? OrderCutoffTime = null);

public sealed record WriteReplenishmentPolicy([property: JsonRequired] int ExpectedVersion, ReplenishmentRequest Settings);
public sealed record ReplenishmentPolicy(string SupplierCode, string SupplierSku, Guid ProductId, int Version,
    ReplenishmentRequest Settings, Guid UpdatedBy, DateTime UpdatedAt);
public sealed record PurchaseReplenishment(ReplenishmentRequest Settings, int? PolicyVersion = null);

public sealed record WriteStockoutDay([property: JsonRequired] int ExpectedVersion,
    [property: JsonRequired] bool Confirmed, string Reason);
public sealed record StockoutDay(Guid ProductId, DateOnly Day, bool Confirmed, string Reason, int Version,
    Guid RecordedBy, DateTime RecordedAt);

public sealed record DemandSample(DateOnly Day, decimal Sold, bool ConfirmedStockout);
public sealed record ReplenishmentDemand(string Source, bool InsufficientHistory, decimal? UnitsPerDay, decimal SoldToday,
    int UsableDays, IReadOnlyList<DemandSample> Samples);
public sealed record ReplenishmentDelivery(Guid OrderId, string SupplierRef, DateOnly? ExpectedDate,
    decimal RemainingQuantity, string Timing);
public sealed record ReplenishmentProjection(decimal StockAtArrival, decimal ExpiresBeforeArrival,
    decimal ExpiresDuringCoverage, DateOnly? FirstShortageDate, DateOnly? FirstBelowSafetyDate,
    string Risk, decimal TargetQuantity, decimal UnroundedQuantity);
public sealed record ReplenishmentRecommendation(string CalculationVersion, DateTime CalculatedAt, DateOnly StoreDate,
    string TimeZone, Guid ProductId, string ProductName, string Unit, string SupplierCode, string SupplierSku,
    DateOnly OrderDate, DateOnly ArrivalDate, DateOnly NextOrderArrivalDate, int LeadTimeDays,
    int CoverageDays, decimal SafetyStock, int MinimumPackages, decimal SellableStock, decimal ExcludedStock,
    decimal? DaysOfStock, ReplenishmentDemand Demand, IReadOnlyList<ReplenishmentDelivery> Deliveries, ReplenishmentProjection? Projection,
    SupplierPrice? Quote, int? SuggestedPackages, decimal? SuggestedQuantity, SavePurchaseDraft? Draft)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? PolicyVersion { get; init; }
}

public sealed record ReplenishmentSnapshot(string Status, int PolicyVersion, int? CalculatedPolicyVersion,
    DateTime? CalculatedAt, string? ErrorCode, ReplenishmentRecommendation? Recommendation);

public sealed record ReplenishmentExplanation(string Explanation, DateTime CalculatedAt, int? PolicyVersion,
    DateOnly OrderDate, decimal? SuggestedQuantity);

public sealed record SalesPeriodAmounts(long SaleCount, long RefundCount, decimal CashSales,
    decimal ManualCashlessSales, decimal CashRefunds, decimal ManualCashlessRefunds,
    decimal Discounts, decimal CashIn, decimal CashOut)
{
    public decimal Sales => CashSales + ManualCashlessSales;
    public decimal GrossSales => Sales + Discounts;
    public decimal Refunds => CashRefunds + ManualCashlessRefunds;
    public decimal NetSales => Sales - Refunds;
    public decimal NetCash => CashSales - CashRefunds;
    public decimal NetManualCashless => ManualCashlessSales - ManualCashlessRefunds;
    public decimal? AverageCheck => SaleCount == 0 ? null
        : decimal.Round(Sales / SaleCount, 2, MidpointRounding.AwayFromZero);
}

public sealed record SalesPeriodDay(DateOnly Date, SalesPeriodAmounts Amounts);

public sealed record SalesPeriodReport(Guid StoreId, Guid? RegisterId, Guid? CashierId, string TimeZone,
    DateOnly From, DateOnly To, DateTime GeneratedAt, SalesPeriodAmounts Total, IReadOnlyList<SalesPeriodDay> Days);

public sealed record ProfitAmounts(decimal Sales, decimal Refunds, decimal KnownSaleCost, decimal KnownRefundCost,
    long UnvaluedSaleLineCount, long UnvaluedRefundLineCount, decimal KnownWriteOffCost,
    decimal KnownInventoryShortageCost, long UnvaluedExpenseLineCount)
{
    public decimal NetRevenue => Sales - Refunds;
    public decimal? CostOfGoodsSold => UnvaluedSaleLineCount == 0 && UnvaluedRefundLineCount == 0
        ? KnownSaleCost - KnownRefundCost : null;
    public decimal? GrossProfit => NetRevenue - CostOfGoodsSold;
    public decimal? GrossMarginPercent => NetRevenue > 0 && GrossProfit is { } profit
        ? decimal.Round(profit / NetRevenue * 100, 2, MidpointRounding.AwayFromZero) : null;
    public decimal? InventoryExpenses => UnvaluedExpenseLineCount == 0
        ? KnownWriteOffCost + KnownInventoryShortageCost : null;
}

public sealed record ProfitDay(DateOnly Date, ProfitAmounts Amounts);

public sealed record ProfitReport(Guid StoreId, string TimeZone, DateOnly From, DateOnly To, DateTime GeneratedAt,
    string CostMethod, ProfitAmounts Total, IReadOnlyList<ProfitDay> Days);

public sealed record StockMovementEntry(string Kind, Guid DocumentId, Guid? RegisterId, Guid ActorId,
    DateTime OccurredAt, DateOnly LocalDate, Guid ProductId, string ProductName, string Unit,
    Guid LotId, DateOnly? ExpiryDate, decimal StockDelta, decimal QuarantineDelta, string? Reason,
    Guid? SourceDocumentId, Guid? SourceRegisterId, decimal? ReceivingCost, decimal? SupplierCredit)
{
    public decimal TotalDelta => StockDelta + QuarantineDelta;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? OpeningCost { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public decimal? InventoryCostDelta { get; init; }
}

public sealed record StockValuationLine(Guid ProductId, Guid LotId, string ProductName, string Unit,
    decimal StockQuantity, decimal QuarantineQuantity, decimal? StockValue, decimal? QuarantineValue)
{
    public decimal? TotalValue => StockValue + QuarantineValue;
}

public sealed record StockValuation(Guid StoreId, Guid? ProductId, string Method, DateTime GeneratedAt,
    decimal KnownStockValue, decimal KnownQuarantineValue, long UnvaluedLotCount,
    IReadOnlyList<StockValuationLine> Items, int? NextOffset)
{
    internal string TimeZone { get; init; } = "";
    public decimal KnownValue => KnownStockValue + KnownQuarantineValue;
    public decimal? TotalValue => UnvaluedLotCount == 0 ? KnownValue : null;
}

public sealed record CaptureStockSnapshot(Guid OperationId);
public sealed record StockSnapshotHeader(Guid OperationId, Guid StoreId, DateOnly Date, DateTime CapturedAt,
    string TimeZone, Guid CapturedBy);
public sealed record StockSnapshotLine(Guid ProductId, Guid LotId, string ProductName, string Unit,
    decimal StockQuantity, decimal QuarantineQuantity, decimal? InventoryValue);
public sealed record StockSnapshotPage(StockSnapshotHeader Snapshot, Guid? ProductId,
    IReadOnlyList<StockSnapshotLine> Items, int? NextOffset);

public sealed record StockMovementReport(Guid StoreId, string TimeZone, DateOnly From, DateOnly To,
    Guid? ProductId, Guid? LotId, string? Kind, DateTime GeneratedAt, IReadOnlyList<StockMovementEntry> Items, string? NextCursor);

public sealed record CreateStockMovementExport(Guid OperationId, DateOnly From, DateOnly To,
    Guid? ProductId, Guid? LotId, string? Kind);
public sealed record StockMovementExport(Guid OperationId, string Status, DateTime QueuedAt, DateTime? FinishedAt,
    DateTime? ExpiresAt, int? RowCount, string? ErrorCode);

public sealed record StockLossEntry(string Kind, Guid DocumentId, DateTime OccurredAt, DateOnly LocalDate,
    Guid ProductId, string ProductName, string Unit, Guid LotId, decimal Quantity, string? Reason, decimal? Cost);

public sealed record StockLossReport(Guid StoreId, string TimeZone, DateOnly From, DateOnly To,
    Guid? ProductId, DateTime GeneratedAt, IReadOnlyList<StockLossEntry> Items, string? NextCursor);

public sealed record StockExpiryEntry(Guid ProductId, Guid LotId, string ProductName, string Unit,
    DateOnly? ExpiryDate, int? DaysLeft, decimal StockQuantity, decimal QuarantineQuantity, decimal? InventoryValue);

public sealed record StockExpiryReport(Guid StoreId, string TimeZone, string Filter,
    IReadOnlyList<StockExpiryEntry> Items, int? NextOffset);

public sealed record StoreException(string Code, Guid ObjectId, Guid? ProductId, Guid? ReceiptId, Guid? RegisterId,
    DateTime? OccurredAt, decimal? CashDifference)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? ShiftId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? OperationId { get; init; }
}
public sealed record StoreExceptionReport(Guid StoreId, string TimeZone, string? Code,
    IReadOnlyList<StoreException> Items, int? NextOffset);

public sealed record ReportRegisterDiagnostic(Guid EventId, Guid ShiftId, string Code, Guid? OperationId);
public sealed record RegisterDiagnostic(Guid EventId, Guid ShiftId, string Code, Guid? OperationId, DateTime ReceivedAt);

public sealed record SupplierPaymentAllocationLine(Guid ReceiptId, decimal Amount);
public sealed record AllocateSupplierPayment(Guid OperationId, Guid PaymentId, SupplierPaymentAllocationLine[] Items);
public sealed record SupplierPaymentAllocation(Guid OperationId, Guid PaymentId, Guid AllocatedBy,
    DateTime AllocatedAt, IReadOnlyList<SupplierPaymentAllocationLine> Items);
public sealed record ReverseSupplierPaymentAllocation(Guid OperationId, string Reason);
public sealed record SupplierPaymentAllocationReversal(Guid OperationId, Guid AllocationId, string Reason,
    Guid ReversedBy, DateTime ReversedAt);
public sealed record SupplierInvoiceAllocation(Guid OperationId, Guid PaymentId, decimal Amount,
    DateOnly PaymentDate, string PaymentReference, Guid AllocatedBy, DateTime AllocatedAt,
    Guid? ReversalId, DateTime? ReversedAt, Guid? ReversedBy, string? ReversalReason);
public sealed record SupplierPaymentAllocationBalance(SupplierPayment Payment, decimal AllocatedAmount)
{
    public decimal UnallocatedAmount => Payment.Amount - AllocatedAmount;
}

public sealed record SetSupplierInvoiceDueDate([property: JsonRequired] int ExpectedVersion, [property: JsonRequired] DateOnly? DueDate);
public sealed record SupplierInvoiceTerms(DateOnly? DueDate, int Version);
public sealed record SupplierSettlementSummary(string SupplierCode, string SupplierName, bool Active,
    decimal InvoicedAmount, decimal ReturnCredit, decimal PaidAmount, decimal UnallocatedAdvance,
    decimal KnownOutstandingAmount, decimal KnownOverdueAmount, long UnpricedInvoiceCount,
    long UnpricedReturnCount, long UnpricedSettlementCount, long UnpricedOverdueInvoiceCount)
{
    public decimal KnownLedgerBalance => InvoicedAmount - ReturnCredit - PaidAmount;
    public decimal? LedgerBalance => UnpricedInvoiceCount == 0 && UnpricedReturnCount == 0 ? KnownLedgerBalance : null;
    public decimal? OutstandingAmount => UnpricedSettlementCount == 0 ? KnownOutstandingAmount : null;
    public decimal? OverdueAmount => UnpricedOverdueInvoiceCount == 0 ? KnownOverdueAmount : null;
}

public sealed record SupplierSettlementReport(Guid StoreId, string TimeZone, DateOnly AsOf, string? SupplierCode,
    IReadOnlyList<SupplierSettlementSummary> Items, int? NextOffset);

public sealed record SupplierInvoiceSettlement(Guid ReceiptId, string InvoiceNumber, DateOnly InvoiceDate, DateTime PostedAt,
    DateOnly AsOf, SupplierInvoiceTerms Terms, decimal? InvoicedAmount, decimal? ReturnCredit, decimal AllocatedAmount)
{
    public decimal CreditSentAmount { get; init; }
    public decimal CreditReceivedAmount { get; init; }
    public decimal? Balance => InvoicedAmount - ReturnCredit - AllocatedAmount + CreditSentAmount - CreditReceivedAmount;
    public decimal? OutstandingAmount => Balance is { } balance ? Math.Max(0, balance) : null;
    public decimal? OverpaidAmount => Balance is { } balance ? Math.Max(0, -balance) : null;
    public bool IsOverdue => Terms.DueDate < AsOf && OutstandingAmount > 0;
    public string Status => Balance switch
    {
        null => "unpriced",
        < 0 => "overpaid",
        0 => "settled",
        _ => AllocatedAmount > 0 || CreditReceivedAmount > 0 ? "partiallyPaid" : "unpaid"
    };
}

public sealed record TransferSupplierCredit(Guid OperationId, Guid SourceReceiptId, Guid TargetReceiptId,
    decimal Amount, string Reason);
public sealed record SupplierCreditTransfer(Guid OperationId, Guid SourceReceiptId, Guid TargetReceiptId,
    decimal Amount, string Reason, Guid RecordedBy, DateTime RecordedAt);
public sealed record ReverseSupplierCreditTransfer(Guid OperationId, string Reason);
public sealed record SupplierCreditTransferReversal(Guid OperationId, Guid TransferId, string Reason,
    Guid ReversedBy, DateTime ReversedAt);
public sealed record SupplierInvoiceCreditTransfer(SupplierCreditTransfer Transfer, SupplierCreditTransferReversal? Reversal);

public sealed record OpeningStockItem(Guid ProductId, Guid LotId, DateOnly? ExpiryDate,
    [property: JsonRequired] decimal StockQuantity, [property: JsonRequired] decimal QuarantineQuantity, decimal? TotalCost);
public sealed record ImportOpeningStock(Guid OperationId, string SourceReference, string Reason,
    OpeningStockItem[]? Items = null, string? Csv = null);
public sealed record OpeningStockLine(Guid ProductId, Guid LotId, DateOnly? ExpiryDate, string Unit,
    decimal StockQuantity, decimal QuarantineQuantity, decimal? TotalCost);
public sealed record OpeningStockReport(Guid OperationId, string SourceReference, string Reason, DateOnly OpeningDate,
    Guid ActorId, DateTime? PostedAt, IReadOnlyList<OpeningStockLine> Items)
{
    public decimal KnownCost => Items.Sum(item => item.TotalCost ?? 0);
    public int UnpricedLineCount => Items.Count(item => item.TotalCost is null);
}
