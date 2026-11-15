using System.Numerics;

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

        AllocateDiscount(lines, Enumerable.Range(0, lines.Count), totalDiscount, gross);
    }

    private static void AllocateDiscount(List<SaleLine> lines, IEnumerable<int> indexes, decimal totalDiscount, decimal gross)
    {
        decimal grossToLine = 0;
        decimal allocated = 0;
        foreach (var i in indexes)
        {
            var line = lines[i];
            grossToLine += line.LineTotal;
            var cumulative = RoundedShare(totalDiscount, grossToLine, gross);
            var amount = cumulative - allocated;
            allocated = cumulative;
            lines[i] = line with { LineTotal = line.LineTotal - amount, DiscountAmount = amount };
        }
    }

    // Amounts have two decimal places; quantities and monetary weights have at most three.
    // Divide only after forming the exact numerator to retain half-cent ties.
    private static decimal RoundedShare(decimal amount, decimal share, decimal whole)
    {
        var minor = new BigInteger(amount * 100);
        var part = new BigInteger(share * 1000);
        var divisor = new BigInteger(whole * 1000);
        return (decimal)((minor * part * 2 + divisor) / (divisor * 2)) / 100;
    }
}
