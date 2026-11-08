namespace Kasa.Api;

internal sealed partial class PosService
{
    private static ManualSaleDiscount? ValidateManualDiscount(ManualSaleDiscount? discount)
    {
        if (discount is null)
            return null;
        if (!IsMoney(discount.Value) || discount.Value <= 0 ||
            discount.Kind is not ("amount" or "percent") ||
            (discount.Kind == "percent" && discount.Value >= 100) ||
            !IsText(discount.Reason, 500))
            throw new PosError(400, "invalid_manual_discount");
        return discount with { Reason = discount.Reason.Trim() };
    }

    private void ApplyManualDiscount(List<SaleLine> lines, ManualSaleDiscount discount)
    {
        var gross = lines.Sum(line => line.LineTotal);
        if (!IsMoney(gross))
            throw new PosError(400, "invalid_sale_total");
        var limit = configuration.GetValue<decimal>("Sales:MaxManualDiscountPercent");
        if (limit <= 0 || limit >= 100)
            throw new PosError(409, "manual_discounts_disabled");
        var totalDiscount = discount.Kind == "amount" ? discount.Value :
            decimal.Round(gross * discount.Value / 100m, 2, MidpointRounding.AwayFromZero);
        // Enforce the configured percentage on the actual rounded monetary amount.
        if (totalDiscount <= 0 || totalDiscount >= gross || totalDiscount > gross * limit / 100m)
            throw new PosError(409, "manual_discount_outside_limit");

        decimal grossToLine = 0;
        decimal allocated = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            grossToLine += line.LineTotal;
            var cumulative = decimal.Round(totalDiscount * (grossToLine / gross), 2, MidpointRounding.AwayFromZero);
            var amount = cumulative - allocated;
            allocated = cumulative;
            lines[i] = line with { LineTotal = line.LineTotal - amount, DiscountAmount = amount };
        }
    }
}
