"""Release gates for Android 0.2.100 and Desktop 0.8.18 server contracts."""

import csv
import datetime
import io
import urllib.parse
import uuid
from decimal import Decimal

from smoke import ZoneInfo, fixture_id, request


def money(value):
    return Decimal(str(value))


def check_operating_expenses_and_finance():
    store = fixture_id(12)
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    previous = today - datetime.timedelta(days=1)
    period = f"from={today}&to={today}"
    expenses = f"/v1/stores/{store}/finance/expenses"
    listing = expenses + "?" + period + "&offset=0&includeReversed=false"
    finance = f"/v1/reports/stores/{store}/finance?{period}"

    paths = request("/openapi/v1.json")["paths"]
    assert "/v1/stores/{storeId}/finance/expenses" in paths
    assert "/v1/stores/{storeId}/finance/expenses/{id}/reverse" in paths
    assert "/v1/reports/stores/{storeId}/finance" in paths
    assert "/v1/reports/stores/{storeId}/finance/csv" in paths
    request(listing, staff=None, expected=401)
    request(listing, staff="receiver", expected=404)
    request(listing, staff="admin-a", expected=404)
    assert request(listing, staff="admin-foreign")["items"] == []

    before = request(finance, staff="admin-foreign")
    assert money(before["profit"]["netRevenue"]) == 8000
    assert money(before["profit"]["costOfGoodsSold"]) == 5500
    assert money(before["profit"]["grossProfit"]) == 2500
    assert money(before["profit"]["inventoryExpenses"]) == 0
    assert money(before["operatingExpenses"]) == 0
    assert money(before["netProfit"]) == 2500
    empty = request(f"/v1/reports/stores/{store}/finance?from={previous}&to={previous}", staff="admin-foreign")
    assert money(empty["profit"]["netRevenue"]) == 0

    expense_id = str(uuid.uuid4())
    body = {
        "operationId": expense_id,
        "incurredOn": str(today),
        "category": "delivery",
        "amount": 1000,
        "paymentMethod": "cash",
        "description": "Контрольный расход",
    }
    saved = request(expenses, body, staff="admin-foreign")
    assert saved["operationId"] == expense_id and money(saved["amount"]) == 1000 and not saved["reversed"]
    assert request(expenses, body, staff="admin-foreign") == saved
    conflict = request(expenses, {**body, "amount": 1001}, staff="admin-foreign", expected=409)
    assert conflict["code"] == "operating_expense_id_reused"
    assert request(expenses + "/" + expense_id, staff="admin-foreign") == saved
    assert request(listing, staff="admin-foreign")["items"] == [saved]

    after = request(finance, staff="admin-foreign")
    assert money(after["profit"]["grossProfit"]) == 2500
    assert money(after["operatingExpenses"]) == 1000
    assert money(after["netProfit"]) == 1500

    other_store = fixture_id(11)
    other_expense = {
        **body,
        "operationId": str(uuid.uuid4()),
        "amount": 777,
        "description": "Расход другого магазина",
    }
    request(f"/v1/stores/{other_store}/finance/expenses", other_expense, staff="admin-a")
    isolated = request(finance, staff="admin-foreign")
    assert money(isolated["operatingExpenses"]) == 1000 and money(isolated["netProfit"]) == 1500

    payload, headers = request(f"/v1/reports/stores/{store}/finance/csv?{period}",
                               staff="admin-foreign", raw_response=True)
    assert payload.startswith(b"\xef\xbb\xbf") and headers.get_content_type() == "text/csv"
    rows = list(csv.DictReader(io.StringIO(payload.decode("utf-8-sig"))))
    assert len(rows) == 1
    row = rows[0]
    assert money(row["net_revenue"]) == money(after["profit"]["netRevenue"])
    assert money(row["cost_of_goods_sold"]) == money(after["profit"]["costOfGoodsSold"])
    assert money(row["gross_profit"]) == money(after["profit"]["grossProfit"])
    assert money(row["inventory_expenses"]) == money(after["profit"]["inventoryExpenses"])
    assert money(row["operating_expenses"]) == money(after["operatingExpenses"])
    assert money(row["net_profit"]) == money(after["netProfit"])

    reversal = {"operationId": str(uuid.uuid4()), "reason": "Контрольное сторнирование"}
    reverse_path = expenses + "/" + expense_id + "/reverse"
    reversed_expense = request(reverse_path, reversal, staff="admin-foreign")
    assert reversed_expense["reversed"] is True
    assert request(reverse_path, reversal, staff="admin-foreign") == reversed_expense
    different = request(reverse_path, {**reversal, "reason": "Другая причина"},
                        staff="admin-foreign", expected=409)
    assert different["code"] == "operating_expense_already_reversed"
    assert request(listing, staff="admin-foreign")["items"] == []
    with_reversed = request(listing.replace("includeReversed=false", "includeReversed=true"), staff="admin-foreign")
    assert with_reversed["items"] == [reversed_expense]
    restored = request(finance, staff="admin-foreign")
    assert money(restored["operatingExpenses"]) == 0 and money(restored["netProfit"]) == 2500
    print("PASS: expense CRUD, idempotency, reversal, store isolation, local period, finance totals and CSV")


def check_catalog_snapshot():
    route = "/v1/catalog/snapshot"
    request(route, staff=None, expected=401)
    request(route, staff="receiver", expected=403)
    snapshot = request(route, staff="cashier-a")
    assert snapshot["storeId"] == fixture_id(11) and snapshot["generatedAt"]
    by_id = {item["productId"]: item for item in snapshot["items"]}
    item = by_id[fixture_id(41)]
    current = request("/v1/catalog/search?q=001", staff="cashier-a")[0]
    assert item["name"] == "Учебная кола" and item["barcode"] == "001"
    assert item["unitPrice"] == current["unitPrice"] and item["priceVersion"] == current["priceVersion"]
    assert item["active"] is True
    assert item["barcodes"] == [{"code": "001", "quantityFactor": 1}]
    assert all(row["active"] and row["unitPrice"] > 0 for row in snapshot["items"])
    print("PASS: Desktop catalogue snapshot is protected, store-scoped and preserves barcode lookup metadata")


def check_expiry_category():
    store = fixture_id(11)
    category = urllib.parse.urlencode({"filter": "all", "category": "напитки"})
    cashier = request("/v1/expiry/cashier?" + category, staff="cashier-a")
    assert cashier and {row["productId"] for row in cashier} == {fixture_id(41)}
    admin = request(f"/v1/stores/{store}/stock/expiry?" + category, staff="admin-a")
    assert admin and {row["productId"] for row in admin} == {fixture_id(41)}
    report_path = f"/v1/reports/stores/{store}/stock-expiry?" + category
    report = request(report_path, staff="admin-replenishment")
    assert report["category"] == "напитки"
    assert report["items"] and {row["productId"] for row in report["items"]} == {fixture_id(41)}

    filtered_payload, _ = request(f"/v1/reports/stores/{store}/stock-expiry/csv?" + category,
                                  staff="admin-replenishment", raw_response=True)
    filtered_rows = list(csv.DictReader(io.StringIO(filtered_payload.decode("utf-8-sig"))))
    expected_header = ["store_id", "time_zone", "filter", "access_role", "product_id", "lot_id",
                       "product_name", "unit", "expiry_date", "days_left", "stock_quantity",
                       "quarantine_quantity", "inventory_value"]
    assert list(filtered_rows[0]) == expected_header
    assert {row["product_id"] for row in filtered_rows} == {fixture_id(41)}
    invalid = request(f"/v1/reports/stores/{store}/stock-expiry?filter=all&category=%20",
                      staff="admin-replenishment", expected=400)
    assert invalid["code"] == "invalid_expiry_category"
    print("PASS: expiry category applies to cashier, admin, report and CSV without changing the legacy CSV schema")


if __name__ == "__main__":
    check_operating_expenses_and_finance()
    check_catalog_snapshot()
    check_expiry_category()
