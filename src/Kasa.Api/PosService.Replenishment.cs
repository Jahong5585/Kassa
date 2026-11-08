using System.Data;
using Npgsql;

namespace Kasa.Api;

internal sealed partial class PosService
{
    private sealed record ReplenishmentProduct(string Name, string Unit, bool RequiresExpiry);
    private sealed record ForecastLot(decimal Quantity, DateOnly? Expiry, bool Quarantined, decimal QuarantineQuantity);

    public async Task<ReplenishmentRecommendation> RecommendPurchase(HttpContext context, Guid store, string code,
        string sku, ReplenishmentRequest request, CancellationToken ct)
    {
        code = SupplierCode(code);
        sku = SupplierCode(sku);
        request = NormalizeReplenishmentSettings(request);
        await using var db = await source.OpenConnectionAsync(ct);
        await using var tx = await db.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var scope = await Access.RequireStore(db, tx, context, configuration, store, true, ct);
        return await CalculateReplenishment(db, tx, scope, code, sku, request, null, ct);
    }

    private static ReplenishmentRequest NormalizeReplenishmentSettings(ReplenishmentRequest request)
    {
        var weekdays = request.OrderWeekdays ?? [1, 2, 3, 4, 5, 6, 7];
        if (request.HistoryDays is < 1 or > 90 || request.MinimumHistoryDays < 1 ||
            request.MinimumHistoryDays > request.HistoryDays || request.CoverageDays is < 1 or > 90 ||
            request.MinimumPackages <= 0 || !IsStockQuantity(request.SafetyStock) ||
            request.LeadTimeDays is < 0 or > 365 || weekdays.Length is < 1 or > 7 ||
            weekdays.Any(day => day is < 1 or > 7) || weekdays.Distinct().Count() != weekdays.Length ||
            request.ManualDailyDemand is { } manual && !IsStockQuantity(manual))
            throw new PosError(400, "invalid_replenishment_settings");
        return request with { OrderWeekdays = weekdays.Order().ToArray() };
    }

    private static async Task<ReplenishmentRecommendation> CalculateReplenishment(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, string code, string sku, ReplenishmentRequest request, int? policyVersion,
        CancellationToken ct)
    {
        var supplier = await ReadSupplier(db, tx, scope, code, ct) ?? throw new PosError(404, "supplier_not_found");
        if (!supplier.Active)
            throw new PosError(409, "supplier_inactive");

        await using var time = db.Sql(tx, "SELECT transaction_timestamp(), transaction_timestamp() AT TIME ZONE $1",
            scope.TimeZone);
        await using var clock = await time.ExecuteReaderAsync(ct);
        await clock.ReadAsync(ct);
        var calculatedAt = clock.GetDateTime(0);
        var localTime = clock.GetDateTime(1);
        var today = DateOnly.FromDateTime(localTime);
        await clock.CloseAsync();

        var lead = request.LeadTimeDays ?? supplier.LeadTimeDays;
        var firstOrderDay = request.OrderCutoffTime is { } cutoff && TimeOnly.FromDateTime(localTime) >= cutoff
            ? today.AddDays(1)
            : today;
        var orderDate = NextOrderDay(firstOrderDay, request.OrderWeekdays!);
        var arrival = orderDate.AddDays(lead);
        var nextArrival = NextOrderDay(orderDate.AddDays(1), request.OrderWeekdays!).AddDays(lead);
        var quote = (await ReadEffectivePrices(db, tx, scope, code, orderDate, sku, 0, ct)).SingleOrDefault();
        var productId = quote?.ProductId ??
            (await ReadSupplierPriceHistory(db, tx, scope, code, sku, 1, 0, ct)).SingleOrDefault()?.ProductId ??
            throw new PosError(404, "supplier_sku_not_found");
        var product = await ReadReplenishmentProduct(db, tx, scope, productId, ct);
        ValidateLotQuantity(product.Unit, request.SafetyStock);
        var (demand, demandTotal, demandDays) = await ReadReplenishmentDemand(db, tx, scope, productId, today, request, ct);
        var allLots = await ReadForecastLots(db, tx, scope, productId, ct);
        var sellable = allLots.Where(lot => lot.Quantity > 0 && !lot.Quarantined && (lot.Expiry is { } expiry
            ? expiry >= today
            : !product.RequiresExpiry)).ToArray();
        var stock = sellable.Sum(lot => lot.Quantity);
        var deliveries = await ReadForecastDeliveries(db, tx, scope, productId, today, arrival, ct);
        var projection = demand.UnitsPerDay.HasValue
            ? ProjectReplenishment(today, arrival, nextArrival, request.CoverageDays, request.SafetyStock, demandTotal,
                demandDays, demand.SoldToday, sellable, deliveries)
            : null;

        int? packages = null;
        decimal? quantity = null;
        SavePurchaseDraft? draft = null;
        if (projection is not null && quote is not null)
        {
            var packs = projection.UnroundedQuantity > 0
                ? Math.Max(request.MinimumPackages, decimal.Ceiling(projection.UnroundedQuantity / quote.UnitsPerPackage))
                : 0;
            if (packs > int.MaxValue || !IsStockQuantity(packs * quote.UnitsPerPackage) ||
                !IsMoney(packs * quote.PackagePrice))
                throw new PosError(409, "replenishment_quantity_out_of_range");
            packages = (int)packs;
            quantity = packs * quote.UnitsPerPackage;
            if (packages > 0 && orderDate == today)
                draft = new SavePurchaseDraft(0, code, arrival, [new OrderOffer(sku, quote.Version, packages.Value)])
                    { Replenishment = new PurchaseReplenishment(request, policyVersion) };
        }
        return new ReplenishmentRecommendation("replenishment-v1", calculatedAt, today, scope.TimeZone, productId,
            product.Name, product.Unit, code, sku, orderDate, arrival, nextArrival, lead, request.CoverageDays,
            request.SafetyStock, request.MinimumPackages, stock, allLots.Sum(lot => lot.Quantity + lot.QuarantineQuantity) - stock,
            demandTotal > 0 && demand.UnitsPerDay.HasValue ? stock * demandDays / demandTotal : null, demand,
            deliveries, projection, quote, packages, quantity, draft) { PolicyVersion = policyVersion };
    }

    private static DateOnly NextOrderDay(DateOnly from, int[] weekdays)
    {
        while (!weekdays.Contains(((int)from.DayOfWeek + 6) % 7 + 1))
            from = from.AddDays(1);
        return from;
    }

    private static ReplenishmentProjection ProjectReplenishment(DateOnly today, DateOnly arrival, DateOnly nextArrival,
        int coverage, decimal safetyStock, decimal demandTotal, int demandDays, decimal soldToday, ForecastLot[] lots,
        IReadOnlyList<ReplenishmentDelivery> deliveries)
    {
        // Keep quantities in multiples of 1 / demandDays until the end to avoid rounding a repeating daily average.
        var stock = new PriorityQueue<decimal, DateOnly>(
            lots.Select(lot => (lot.Quantity * demandDays, lot.Expiry ?? DateOnly.MaxValue)));
        var safety = safetyStock * demandDays;
        var incoming = deliveries.Where(delivery => delivery.Timing is "timely" or "late")
            .GroupBy(delivery => delivery.ExpectedDate!.Value)
            .ToDictionary(group => group.Key, group => group.Sum(delivery => delivery.RemainingQuantity));
        var todayRemaining = Math.Max(0, demandTotal - soldToday * demandDays);
        var coverageEnd = arrival.AddDays(coverage);
        var projectionEnd = coverageEnd > nextArrival ? coverageEnd : nextArrival;
        var expiredBefore = 0m;
        DateOnly? shortage = null;
        DateOnly? belowSafety = null;
        PriorityQueue<decimal, DateOnly>? arrivalStock = null;

        for (var day = today; day < projectionEnd; day = day.AddDays(1))
        {
            var expired = RemoveExpired(stock, day);
            if (day <= arrival)
                expiredBefore += expired;
            if (incoming.TryGetValue(day, out var due))
                stock.Enqueue(due * demandDays, DateOnly.MaxValue);
            if (day == arrival)
                arrivalStock = new PriorityQueue<decimal, DateOnly>(stock.UnorderedItems);
            if (ConsumeStock(stock, day == today ? todayRemaining : demandTotal) > 0)
                shortage ??= day;
            if (safety > 0 && AvailableStock(stock) < safety)
                belowSafety ??= day;
        }

        // Size the new order from stock that will actually be available by its arrival.
        // Later deliveries are visible in the risk projection, but do not reduce this target.
        var coverageStock = arrivalStock!;
        var atArrival = AvailableStock(coverageStock);
        var expiredDuring = 0m;
        var missing = 0m;
        for (var day = arrival; day < coverageEnd; day = day.AddDays(1))
        {
            expiredDuring += RemoveExpired(coverageStock, day);
            missing += ConsumeStock(coverageStock, day == today ? todayRemaining : demandTotal);
        }
        if (safety > 0)
            expiredDuring += RemoveExpired(coverageStock, coverageEnd);
        var target = demandTotal * coverage + safety - (arrival == today ? demandTotal - todayRemaining : 0);
        missing += Math.Max(0, safety - AvailableStock(coverageStock));

        var risk = shortage < arrival ? "beforeEarliestDelivery"
            : shortage < nextArrival || belowSafety < nextArrival ? "orderAtFirstWindow"
            : "coveredUntilNextWindow";
        return new ReplenishmentProjection(atArrival / demandDays, expiredBefore / demandDays, expiredDuring / demandDays,
            shortage, belowSafety, risk, target / demandDays, missing / demandDays);
    }

    private static decimal AvailableStock(PriorityQueue<decimal, DateOnly> stock) =>
        stock.UnorderedItems.Sum(lot => lot.Element);

    private static decimal RemoveExpired(PriorityQueue<decimal, DateOnly> stock, DateOnly day)
    {
        var expired = 0m;
        while (stock.TryPeek(out var quantity, out var expiry) && expiry < day)
        {
            expired += quantity;
            stock.Dequeue();
        }
        return expired;
    }

    private static decimal ConsumeStock(PriorityQueue<decimal, DateOnly> stock, decimal demand)
    {
        while (demand > 0 && stock.TryDequeue(out var quantity, out var expiry))
        {
            if (quantity > demand)
            {
                stock.Enqueue(quantity - demand, expiry);
                return 0;
            }
            demand -= quantity;
        }
        return demand;
    }

    private static async Task<ReplenishmentProduct> ReadReplenishmentProduct(NpgsqlConnection db, NpgsqlTransaction tx,
        StoreScope scope, Guid product, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT p.name, p.unit, p.requires_expiry, sp.active
            FROM store_prices sp JOIN products p ON (sp.org_id, sp.product_id) = (p.org_id, p.id)
            WHERE (sp.org_id, sp.store_id, sp.product_id) = ($1, $2, $3)
            """, scope.Organization, scope.Store, product);
        await using var rows = await command.ExecuteReaderAsync(ct);
        if (!await rows.ReadAsync(ct) || !rows.GetBoolean(3))
            throw new PosError(409, "replenishment_product_inactive");
        return new ReplenishmentProduct(rows.GetString(0), rows.GetString(1), rows.GetBoolean(2));
    }

    private static async Task<ForecastLot[]> ReadForecastLots(NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope,
        Guid product, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT quantity, expiry_date, quarantined, quarantine_quantity FROM stock_lots
            WHERE (org_id, store_id, product_id) = ($1, $2, $3) AND (quantity > 0 OR quarantine_quantity > 0)
            """, scope.Organization, scope.Store, product);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ForecastLot> result = [];
        while (await rows.ReadAsync(ct))
            result.Add(new ForecastLot(rows.GetDecimal(0), ReadDate(rows, 1), rows.GetBoolean(2), rows.GetDecimal(3)));
        return result.ToArray();
    }

    private static async Task<(ReplenishmentDemand Demand, decimal DemandTotal, int DemandDays)> ReadReplenishmentDemand(
        NpgsqlConnection db, NpgsqlTransaction tx, StoreScope scope, Guid product, DateOnly today,
        ReplenishmentRequest request, CancellationToken ct)
    {
        var start = today.AddDays(-request.HistoryDays);
        await using var command = db.Sql(tx, """
            WITH sold AS (
                SELECT (s.created_at AT TIME ZONE $4)::date AS day, sum(l.quantity) AS quantity
                FROM sales s JOIN sale_lines l ON (s.org_id, s.store_id, s.register_id, s.id) =
                    (l.org_id, l.store_id, l.register_id, l.sale_id)
                WHERE (s.org_id, s.store_id, l.product_id) = ($1, $2, $3)
                    AND s.created_at >= ($5::timestamp AT TIME ZONE $4)
                    AND s.created_at < (($6::date + 1)::timestamp AT TIME ZONE $4)
                GROUP BY (s.created_at AT TIME ZONE $4)::date
            ), excluded AS (
                SELECT day FROM confirmed_stockout_days
                WHERE (org_id, store_id, product_id) = ($1, $2, $3) AND confirmed AND day >= $5 AND day < $6
            )
            SELECT coalesce(s.day, x.day), coalesce(s.quantity, 0), x.day IS NOT NULL
            FROM sold s FULL JOIN excluded x ON s.day = x.day ORDER BY 1
            """, scope.Organization, scope.Store, product, scope.TimeZone, start, today);
        await using var rows = await command.ExecuteReaderAsync(ct);
        var known = new Dictionary<DateOnly, DemandSample>();
        DateOnly? firstSale = null;
        var soldToday = 0m;
        while (await rows.ReadAsync(ct))
        {
            var day = rows.GetFieldValue<DateOnly>(0);
            var sold = rows.GetDecimal(1);
            if (day == today)
            {
                soldToday = sold;
                continue;
            }
            known.Add(day, new DemandSample(day, sold, rows.GetBoolean(2)));
            if (sold > 0)
                firstSale ??= day;
        }
        // Zero-sales days before the first observed sale do not establish a trading history for a new SKU.
        List<DemandSample> samples = [];
        for (var day = start; day < today; day = day.AddDays(1))
        {
            if (known.TryGetValue(day, out var sample))
                samples.Add(sample);
            else if (day >= firstSale)
                samples.Add(new DemandSample(day, 0, false));
        }
        var usable = samples.Where(sample => !sample.ConfirmedStockout).ToArray();
        var insufficient = usable.Length < request.MinimumHistoryDays;
        var total = request.ManualDailyDemand ?? usable.Sum(sample => sample.Sold);
        var days = request.ManualDailyDemand.HasValue ? 1 : usable.Length;
        decimal? rate = request.ManualDailyDemand.HasValue || !insufficient ? total / days : null;
        var source = request.ManualDailyDemand.HasValue ? "manual" : insufficient ? "insufficientHistory" : "sales";
        return (new ReplenishmentDemand(source, insufficient, rate, soldToday, usable.Length, samples), total, days);
    }

    private static async Task<IReadOnlyList<ReplenishmentDelivery>> ReadForecastDeliveries(NpgsqlConnection db,
        NpgsqlTransaction tx, StoreScope scope, Guid product, DateOnly today, DateOnly arrival, CancellationToken ct)
    {
        await using var command = db.Sql(tx, """
            SELECT p.id, p.supplier_ref, p.expected_delivery_date, l.ordered - l.received
            FROM purchase_order_lines l JOIN purchase_orders p
                ON (p.org_id, p.store_id, p.id) = (l.org_id, l.store_id, l.order_id)
            WHERE (l.org_id, l.store_id, l.product_id) = ($1, $2, $3)
                AND p.stage IN ('registered', 'sent') AND l.received < l.ordered
            ORDER BY p.expected_delivery_date, p.id
            """, scope.Organization, scope.Store, product);
        await using var rows = await command.ExecuteReaderAsync(ct);
        List<ReplenishmentDelivery> result = [];
        while (await rows.ReadAsync(ct))
        {
            var date = ReadDate(rows, 2);
            var timing = date is null ? "undated" : date < today ? "overdue" : date <= arrival ? "timely" : "late";
            result.Add(new ReplenishmentDelivery(rows.GetGuid(0), rows.GetString(1), date, rows.GetDecimal(3), timing));
        }
        return result;
    }
}
