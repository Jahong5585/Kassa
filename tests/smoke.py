"""Run against an isolated instance initialized with fixtures.sql."""
import base64
import concurrent.futures
import csv
import io
import datetime
import hashlib
import hmac
import json
import os
import time
import urllib.error
import urllib.parse
import urllib.request
import uuid
from zoneinfo import ZoneInfo

BASE = os.environ["POS_TEST_URL"]
KEY = os.environ["POS_TEST_REGISTER_KEY"]
JWT_KEY = os.environ["Auth__DevelopmentKey"].encode()
AUDIENCE = os.environ["Auth__Audience"]
GROQ_TEST_URL = os.environ["GROQ_TEST_URL"]


def fixture_id(n):
    return f"00000000-0000-0000-0000-{n:012d}"


def token(subject, mfa):
    def encode(data):
        return base64.urlsafe_b64encode(data).rstrip(b"=")

    header = encode(b'{"alg":"HS256","typ":"JWT"}')
    claims = {
        "iss": "kasa-local", "aud": AUDIENCE, "sub": subject, "iat": int(time.time()),
        "exp": int(time.time()) + 300,
    }
    if mfa and subject.startswith("admin-"):
        claims["acr"] = os.environ["Auth__AdminAcr"]
    payload = encode(json.dumps(claims).encode())
    message = header + b"." + payload
    return (message + b"." + encode(hmac.digest(JWT_KEY, message, hashlib.sha256))).decode()


def request(path, body=None, staff="cashier-a", register=21, key=KEY, expected=200, mfa=True, method=None,
            raw_body=None, extra_headers=None, raw_response=False):
    headers = {"X-Register-Id": fixture_id(register), "X-Register-Key": key}
    if staff is not None:
        headers["Authorization"] = "Bearer " + token(staff, mfa)
    if body is not None:
        headers["Content-Type"] = "application/json"
    headers.update(extra_headers or {})
    data = json.dumps(body).encode() if body is not None else raw_body
    call = urllib.request.Request(BASE + path, data, headers, method=method)
    try:
        response = urllib.request.urlopen(call, timeout=12)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read()
        assert response.status == expected, (path, expected, response.status, payload)
        if raw_response:
            return payload, response.headers
        return json.loads(payload) if payload else None


def groq_calls():
    with urllib.request.urlopen(GROQ_TEST_URL + "/calls", timeout=2) as response:
        return json.load(response)["calls"]


def sale(shift, *items, method="manualCashless", tendered=None):
    return {
        "operationId": str(uuid.uuid4()), "shiftId": shift, "method": method, "tendered": tendered,
        "items": [{"productId": fixture_id(product), "lotId": fixture_id(lot), "priceVersion": 1,
                   "quantity": quantity} for product, lot, quantity in items],
    }


def main():
    shift_a, shift_b = str(uuid.uuid4()), str(uuid.uuid4())
    opens = {"shiftId": shift_a, "openingCash": 10000}
    request("/v1/shifts", opens, staff=None, expected=401)
    request("/v1/shifts", opens, staff="receiver", expected=403)
    request("/v1/shifts", opens, key="0" * 64, expected=403)
    request("/v1/shifts", opens, register=22, expected=403)
    request("/v1/shifts", {"shiftId": shift_a}, expected=400)

    spec = request("/openapi/v1.json")
    assert "get" in spec["paths"]["/v1/shifts/{id}"]
    checkout = spec["paths"]["/v1/sales"]["post"]
    assert set(checkout["security"][0]) == {"Bearer", "RegisterId", "RegisterKey"}
    assert {"200", "409"}.issubset(checkout["responses"])
    assert set(spec["paths"]["/v1/refunds"]["post"]["security"][0]) == {"Bearer", "RegisterId", "RegisterKey"}
    assert set(spec["paths"]["/v1/stores/{storeId}/receipts/{id}/post"]["post"]["security"][0]) == {"Bearer"}
    upload = spec["paths"]["/v1/stores/{storeId}/receipts/{id}/files/{fileId}"]["put"]
    assert set(upload["requestBody"]["content"]) == {"application/pdf", "image/jpeg", "image/png"}
    assert "X-File-Name" in [parameter["name"] for parameter in upload["parameters"]]
    assert set(spec["paths"]["/v1/expiry/cashier"]["get"]["security"][0]) == {"Bearer", "RegisterId", "RegisterKey"}
    assert set(spec["paths"]["/v1/stores/{storeId}/stock/operations"]["post"]["security"][0]) == {"Bearer"}
    procurement = spec["paths"]["/v1/stores/{storeId}/procurement/orders/{id}/approve"]["post"]
    assert set(procurement["security"][0]) == {"Bearer"}
    assert {"200", "409"}.issubset(procurement["responses"])
    request("/v1/shifts", opens)
    request("/v1/shifts", opens)
    request("/v1/shifts", {"shiftId": str(uuid.uuid4()), "openingCash": 0}, expected=409)
    request("/v1/shifts", {"shiftId": shift_b, "openingCash": 0}, staff="cashier-b", register=22)

    catalog = request("/v1/catalog/barcodes/001")
    assert catalog["unitPrice"] == 17500 and catalog["barcode"] == "001"
    assert len(catalog["lots"]) == 1 and catalog["lots"][0]["quantity"] == 3
    request("/v1/catalog/barcodes/001", staff="receiver", expected=403)

    # Insufficient stock on the second item and insufficient cash both roll back earlier stock changes.
    request("/v1/sales", sale(shift_a, (41, 51, 1), (42, 51, 11)), expected=409)
    request("/v1/sales", sale(shift_a, (41, 51, 1), method="cash", tendered=1), expected=400)
    request("/v1/sales", sale(shift_a, (41, 52, 1)), expected=409)
    assert request("/v1/catalog/barcodes/001")["lots"][0]["quantity"] == 3

    changed_price = sale(shift_a, (41, 51, 1))
    changed_price["items"][0]["priceVersion"] = 2
    request("/v1/sales", changed_price, expected=409)
    bad_cashless = sale(shift_a, (41, 51, 1), tendered=17500)
    request("/v1/sales", bad_cashless, expected=400)
    request("/v1/sales", {**bad_cashless, "total": 1}, expected=400)

    cash = sale(shift_a, (41, 51, 1), method="cash", tendered=20000)
    cash_result = request("/v1/sales", cash)
    assert cash_result["total"] == 17500 and cash_result["changeDue"] == 2500

    cashless = sale(shift_a, (41, 51, 1))
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        duplicates = list(pool.map(lambda _: request("/v1/sales", cashless), range(2)))
    result = duplicates[0]
    assert result == duplicates[1]
    assert result["total"] == 17500 and result["changeDue"] is None and result["tendered"] is None
    assert result["receiptMark"] == "Тестовый • нефискальный"
    assert request("/v1/sales/" + cashless["operationId"]) == result
    request("/v1/sales", {**cashless, "method": "cash", "tendered": 17500}, expected=409)
    request("/v1/sales/" + cashless["operationId"], staff="cashier-b", register=22, expected=404)

    admin_report = f"/v1/reports/stores/{fixture_id(11)}/registers/{fixture_id(21)}/shifts/{shift_a}"
    request(admin_report, staff="receiver", expected=403)
    request(admin_report, expected=403)
    request(admin_report, staff="admin-foreign", expected=404)
    request(admin_report, staff="admin-a", mfa=False, expected=403)
    report = request(admin_report, staff="admin-a")
    assert report["salesCount"] == 2 and report["cashSales"] == 17500
    assert report["manualCashlessSales"] == 17500 and report["expectedCash"] == 27500
    assert request("/v1/shifts/" + shift_a)["expectedCash"] is None

    # Two registers try to sell the last unit, independently and with distinct operation IDs.
    def attempt(subject, register, shift):
        call = sale(shift, (41, 51, 1))
        try:
            request("/v1/sales", call, staff=subject, register=register)
            return 200
        except AssertionError as error:
            assert error.args[0][2] == 409, error
            return 409

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        a = pool.submit(attempt, "cashier-a", 21, shift_a)
        b = pool.submit(attempt, "cashier-b", 22, shift_b)
        assert sorted([a.result(), b.result()]) == [200, 409]
    assert request("/v1/catalog/barcodes/001")["lots"] == []

    weighed = request("/v1/sales", sale(shift_a, (42, 51, 0.685)))
    assert weighed["total"] == 12330 and weighed["lines"][0]["unit"] == "kg"
    assert request("/v1/catalog/barcodes/002")["lots"][0]["quantity"] == 9.315
    close = "/v1/shifts/" + shift_a + "/close"
    closed = request(close, {"countedCash": 27500})
    assert closed["cashDifference"] == 0 and closed["closedAt"] is not None
    assert request(close, {"countedCash": 27500}) == closed
    request(close, {"countedCash": 27501}, expected=409)
    assert request("/v1/sales", cashless) == result
    request("/v1/sales", sale(shift_a, (42, 51, 1)), expected=409)
    print("PASS: register access, tenant isolation, cash, cashless, rollback, retry, concurrent stock and shifts")
    check_receiving()
    check_refunds(shift_b)
    check_partial_order()
    check_supplier_invoice_costs()
    catalog_shift = check_stock_and_inventory()
    check_catalog(catalog_shift)
    check_checkout(catalog_shift)
    check_procurement()
    check_replenishment()
    check_sales_period()
    check_profit_report()
    check_stock_losses()
    check_stock_expiry_report()
    check_stock_movement_report()
    check_supplier_invoice_settlements()
    check_supplier_settlement_report()
    check_opening_stock()
    check_scoped_inventory()
    check_inventory_cost()
    check_register_devices()
    check_store_staff()
    check_store_audit()
    check_receipt_cancellation()
    check_store_exceptions()
    check_stock_snapshots()
    check_background_stock_movement_exports()
    check_register_diagnostics(shift_b)


def receipt_item(lot, quantity, expiry, product=41):
    return {"productId": fixture_id(product), "lotId": fixture_id(lot), "quantity": quantity, "expiryDate": expiry}


def check_receiving():
    prefix = f"/v1/stores/{fixture_id(11)}"
    path = prefix + "/receipts"
    foreign = f"/v1/stores/{fixture_id(12)}/receipts"
    today = datetime.date.today()
    future = (today + datetime.timedelta(days=30)).isoformat()
    later = (today + datetime.timedelta(days=45)).isoformat()
    catalog = prefix + "/receiving/catalog/barcodes/001"
    product = request(catalog, staff="receiver")
    assert product["barcode"] == "001" and product["productId"] == fixture_id(41)
    assert "unitPrice" not in product
    request(catalog, expected=403)
    request(foreign, staff="receiver", expected=404)

    receipt = {
        "receiptId": str(uuid.uuid4()), "supplierRef": "Учебный поставщик", "invoiceNumber": "TEST-12",
        "invoiceDate": today.isoformat(), "items": [receipt_item(70, 7, future), receipt_item(71, 4, later)],
    }
    draft = request(path, receipt, staff="receiver")
    assert draft["status"] == "draft" and draft["version"] == 1 and draft["createdBy"] == fixture_id(33)
    assert request(path, receipt, staff="receiver") == draft
    request(path, {**receipt, "receiptId": str(uuid.uuid4())}, staff="receiver", expected=409)
    row = path + "/" + receipt["receiptId"]
    request(row, staff="admin-foreign", expected=404)
    assert request("/v1/catalog/barcodes/001")["lots"] == []

    submitted = request(row + "/submit", {"expectedVersion": 1}, staff="receiver")
    assert submitted["status"] == "submitted" and submitted["version"] == 2
    assert request(row + "/submit", {"expectedVersion": 1}, staff="receiver") == submitted
    assert request(path + "?status=submitted", staff="admin-a") == [submitted]
    request(row + "/post", {"expectedVersion": 2}, staff="receiver", expected=403)
    request(row + "/post", {"expectedVersion": 2}, expected=403)
    request(row + "/post", {"expectedVersion": 2}, staff="admin-a", mfa=False, expected=403)
    assert request("/v1/catalog/barcodes/001")["lots"] == []

    back = request(row + "/return-to-draft", {"expectedVersion": 2, "reason": "Проверьте количество"}, staff="admin-a")
    assert back["status"] == "draft" and back["version"] == 3 and back["reviewNote"]
    edit = {"expectedVersion": 3, "items": [receipt_item(70, 7, future), receipt_item(71, 5, later)]}
    edited = request(row, edit, staff="receiver", method="PUT")
    assert edited["version"] == 4 and edited["items"][1]["quantity"] == 5
    assert request(row, edit, staff="receiver", method="PUT") == edited
    request(row, {**edit, "expectedVersion": 2}, staff="receiver", method="PUT", expected=409)
    request(row + "/submit", {"expectedVersion": 4}, staff="receiver")
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(lambda _: request(row + "/post", {"expectedVersion": 5}, staff="admin-a"), range(2)))
    assert results[0] == results[1]
    posted = results[0]
    assert posted["status"] == "posted" and posted["version"] == 6 and posted["postedBy"] == fixture_id(34)
    request(row, {**edit, "expectedVersion": 6}, staff="receiver", method="PUT", expected=409)
    lots = request("/v1/catalog/barcodes/001")["lots"]
    assert [(lot["lotId"], lot["quantity"]) for lot in lots] == [(fixture_id(70), 7), (fixture_id(71), 5)]

    bad = {**receipt, "receiptId": str(uuid.uuid4()), "invoiceNumber": "TEST-CONFLICT",
           "items": [receipt_item(70, 2, future), receipt_item(71, 3, future)]}
    request(path, bad, staff="receiver")
    bad_row = path + "/" + bad["receiptId"]
    request(bad_row + "/submit", {"expectedVersion": 1}, staff="receiver")
    error = request(bad_row + "/post", {"expectedVersion": 2}, staff="admin-a", expected=409)
    assert error["code"] == "receipt_lot_conflict"
    assert request("/v1/catalog/barcodes/001")["lots"] == lots
    assert request(bad_row, staff="receiver")["status"] == "submitted"

    expired = {**receipt, "receiptId": str(uuid.uuid4()), "invoiceNumber": "TEST-EXPIRY",
               "items": [receipt_item(72, 1, (today - datetime.timedelta(days=2)).isoformat())]}
    request(path, expired, staff="receiver")
    expired_row = path + "/" + expired["receiptId"]
    request(expired_row + "/submit", {"expectedVersion": 1}, staff="receiver")
    error = request(expired_row + "/post", {"expectedVersion": 2}, staff="admin-a", expected=409)
    assert error["code"] == "receipt_expiry_invalid"
    print("PASS: receiving permissions, draft review, versions, invoice duplication, expiry, concurrent posting and rollback")


def check_refunds(shift_b):
    shift_a = str(uuid.uuid4())
    request("/v1/shifts", {"shiftId": shift_a, "openingCash": 100000})
    original = request("/v1/sales", sale(shift_a, (41, 70, 2), method="cash", tendered=40000))

    def make_refund(sold, shift, product, lot, quantity, destination="stock"):
        return {"operationId": str(uuid.uuid4()), "shiftId": shift, "sourceRegisterId": fixture_id(21),
                "saleId": sold["operationId"], "reason": "Учебный возврат", "items": [{
                    "productId": fixture_id(product), "lotId": fixture_id(lot),
                    "quantity": quantity, "destination": destination,
                }]}

    get_original = f"/v1/sales/{fixture_id(21)}/{original['operationId']}/refund-options"
    assert request(get_original, staff="cashier-b", register=22)["lines"][0]["soldQuantity"] == 2
    admin_original = f"/v1/stores/{fixture_id(11)}" + get_original.removeprefix("/v1")
    assert request(admin_original, staff="admin-a")["lines"][0]["soldAmount"] == 35000
    request(admin_original, staff="receiver", expected=403)
    request(get_original, staff="receiver", expected=403)
    request(f"/v1/sales/{fixture_id(23)}/{original['operationId']}/refund-options", expected=404)

    first = make_refund(original, shift_a, 41, 70, 1)
    first_result = request("/v1/refunds", first)
    assert first_result["total"] == 17500 and first_result["method"] == "cash"
    assert first_result["lines"][0]["destination"] == "stock"
    assert request("/v1/refunds", first) == first_result
    assert request("/v1/refunds/" + first["operationId"]) == first_result
    assert request(get_original)["lines"][0]["returnedQuantity"] == 1
    request("/v1/refunds/" + first["operationId"], staff="cashier-b", register=22, expected=404)

    second = make_refund(original, shift_a, 41, 70, 1, "quarantine")
    error = request("/v1/refunds", second, expected=409)
    assert error["code"] == "refund_approval_required"
    approval_path = f"/v1/stores/{fixture_id(11)}/refund-approvals"
    approval = {"registerId": fixture_id(21), "amount": 17500, "refund": second}
    request(approval_path, approval, staff="receiver", expected=403)
    request(approval_path, approval, staff="admin-foreign", expected=404)
    request(approval_path, approval, staff="admin-a", mfa=False, expected=403)
    request(approval_path, {**approval, "amount": 1}, staff="admin-a", expected=409)
    approved = request(approval_path, approval, staff="admin-a")
    assert request(approval_path, approval, staff="admin-a") == approved
    request("/v1/refunds", {**second, "reason": "Подменённая причина"}, expected=409)
    second_result = request("/v1/refunds", second)
    assert second_result["approvedBy"] == approved["approvedBy"] == fixture_id(34)
    assert second_result["lines"][0]["destination"] == "quarantine"
    assert request("/v1/catalog/barcodes/001")["lots"][0]["quantity"] == 6
    error = request("/v1/refunds", make_refund(original, shift_a, 41, 70, 1), expected=409)
    assert error["code"] == "refund_quantity_exceeded"

    cashless = request("/v1/sales", sale(shift_a, (41, 70, 1)))
    a = make_refund(cashless, shift_a, 41, 70, 1)
    b = make_refund(cashless, shift_b, 41, 70, 1)

    def attempt(body, staff, register):
        try:
            return request("/v1/refunds", body, staff=staff, register=register)
        except AssertionError as error:
            assert error.args[0][2] == 409 and b"refund_quantity_exceeded" in error.args[0][3], error
            return None

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        a_call = pool.submit(attempt, a, "cashier-a", 21)
        b_call = pool.submit(attempt, b, "cashier-b", 22)
        concurrent_results = [r for r in (a_call.result(), b_call.result()) if r is not None]
    assert len(concurrent_results) == 1 and concurrent_results[0]["method"] == "manualCashless"
    assert concurrent_results[0]["total"] == 17500

    weighted = request("/v1/sales", sale(shift_a, (43, 51, 0.003)))
    assert weighted["total"] == 0.04
    parts = [request("/v1/refunds", make_refund(weighted, shift_a, 43, 51, 0.001))["total"] for _ in range(3)]
    assert parts == [0.01, 0.02, 0.01]
    summary = request(f"/v1/reports/stores/{fixture_id(11)}/registers/{fixture_id(21)}/shifts/{shift_a}", staff="admin-a")
    assert summary["cashSales"] == 35000 and summary["cashRefunds"] == 35000
    assert summary["expectedCash"] == 100000
    assert summary["manualCashlessRefunds"] in (0.04, 17500.04)
    assert request("/v1/shifts/" + shift_a)["cashRefunds"] is None
    closed = request("/v1/shifts/" + shift_a + "/close", {"countedCash": 100000})
    assert closed["cashDifference"] == 0 and closed["cashRefunds"] == 35000
    assert request("/v1/refunds", first) == first_result
    print("PASS: partial refunds, rounding, approval binding, quarantine, retry, two registers and shift reconciliation")


def check_partial_order():
    store_path = f"/v1/stores/{fixture_id(11)}"
    order_path = store_path + "/purchase-orders"
    order = {"orderId": str(uuid.uuid4()), "supplierRef": "Учебный поставщик",
             "items": [{"productId": fixture_id(41), "quantity": 24}]}
    request(order_path, order, staff="receiver", expected=403)
    created = request(order_path, order, staff="admin-a")
    assert created["status"] == "awaiting" and created["lines"][0]["remaining"] == 24
    assert request(order_path, order, staff="admin-a") == created
    assert request(order_path, staff="receiver")[0]["orderId"] == order["orderId"]
    single_order = order_path + "/" + order["orderId"]
    request(f"/v1/stores/{fixture_id(12)}/purchase-orders/{order['orderId']}", staff="admin-foreign", expected=404)

    today = datetime.date.today()
    future = (today + datetime.timedelta(days=30)).isoformat()

    def make_delivery(invoice, quantity, lot):
        return {"receiptId": str(uuid.uuid4()), "purchaseOrderId": order["orderId"],
                "supplierRef": order["supplierRef"], "invoiceNumber": invoice,
                "invoiceDate": today.isoformat(), "items": [receipt_item(lot, quantity, future)]}

    receipt_path = store_path + "/receipts"
    first = make_delivery("ORDER-01", 20, 80)
    request(receipt_path, {**first, "supplierRef": "Другой поставщик"}, staff="receiver", expected=409)
    assert request(receipt_path, first, staff="receiver")["purchaseOrderId"] == order["orderId"]
    first_path = receipt_path + "/" + first["receiptId"]
    request(first_path + "/submit", {"expectedVersion": 1}, staff="receiver")
    assert request(single_order, staff="receiver")["lines"][0]["remaining"] == 24
    request(first_path + "/post", {"expectedVersion": 2}, staff="admin-a")
    partial = request(single_order, staff="receiver")
    assert partial["status"] == "partial" and partial["lines"][0]["remaining"] == 4

    excess = make_delivery("ORDER-EXCESS", 5, 81)
    request(receipt_path, excess, staff="receiver")
    excess_path = receipt_path + "/" + excess["receiptId"]
    request(excess_path + "/submit", {"expectedVersion": 1}, staff="receiver")
    error = request(excess_path + "/post", {"expectedVersion": 2}, staff="admin-a", expected=409)
    assert error["code"] == "receipt_order_quantity_exceeded"
    assert request(single_order, staff="receiver") == partial
    assert fixture_id(81) not in [lot["lotId"] for lot in request("/v1/catalog/barcodes/001")["lots"]]

    # Two independent delivery documents compete for the four units remaining on the same order.
    second = make_delivery("ORDER-02", 4, 82)
    competing = make_delivery("ORDER-03", 4, 83)
    for doc in (second, competing):
        request(receipt_path, doc, staff="receiver")
        request(receipt_path + "/" + doc["receiptId"] + "/submit", {"expectedVersion": 1}, staff="receiver")

    def post(doc):
        try:
            request(receipt_path + "/" + doc["receiptId"] + "/post", {"expectedVersion": 2}, staff="admin-a")
            return 200
        except AssertionError as error:
            assert b"receipt_order_quantity_exceeded" in error.args[0][3], error
            return 409

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        assert sorted(pool.map(post, [second, competing])) == [200, 409]
    final = request(single_order, staff="receiver")
    assert final["status"] == "received" and final["lines"][0]["remaining"] == 0
    assert request(order_path, staff="receiver") == []
    assert any(row["orderId"] == order["orderId"] and row["status"] == "received"
               for row in request(order_path + "?openOnly=false", staff="receiver"))
    print("PASS: order-linked receipts, 20 + 4 units, concurrent over-receipt prevention and order completion")


def check_supplier_invoice_costs():
    root = f"/v1/stores/{fixture_id(11)}"
    foreign = f"/v1/stores/{fixture_id(12)}"
    supplier_path = root + "/suppliers/shop-01"
    card = {"expectedVersion": 0, "name": "Стендовый поставщик", "taxId": "11223344",
            "email": "test@example.invalid", "paymentTerms": "30 дней", "leadTimeDays": 5, "active": True}
    request(supplier_path, card, staff="receiver", method="PUT", expected=403)
    saved = request(supplier_path, card, staff="admin-a", method="PUT")
    assert saved["code"] == "SHOP-01" and saved["version"] == 1 and saved["taxId"] == "11223344"
    assert request(supplier_path, card, staff="admin-a", method="PUT") == saved
    request(supplier_path, {**card, "name": "Несогласованное имя"}, staff="admin-a", method="PUT", expected=409)
    request(supplier_path, staff="receiver", expected=403)
    request(supplier_path, staff="admin-foreign", expected=404)
    assert request(root + "/suppliers", staff="receiver") == [{"code": "SHOP-01", "name": saved["name"]}]

    today = datetime.date.today()
    future = (today + datetime.timedelta(days=30)).isoformat()
    draft = {"receiptId": str(uuid.uuid4()), "supplierRef": "SHOP-01", "invoiceNumber": "COST-01",
             "invoiceDate": today.isoformat(), "items": [receipt_item(90, 2, future), receipt_item(91, 1, future)]}
    receipt_base = root + "/receipts"
    request(receipt_base, draft, staff="receiver")
    path = receipt_base + "/" + draft["receiptId"]
    files = path + "/files"
    assert request(files, staff="receiver") == []
    request(path + "/costs", staff="receiver", expected=403)
    request(foreign + "/receipts/" + draft["receiptId"] + "/files", staff="admin-foreign", expected=404)

    def put_file(file_id, data, name="накладная.pdf", media="application/pdf", staff="receiver", expected=200):
        return request(files + "/" + file_id, staff=staff, method="PUT", raw_body=data, expected=expected,
                       extra_headers={"Content-Type": media, "X-File-Name": urllib.parse.quote(name)})

    # The 70 KB PDF-shaped fixture also checks the route's larger upload limit.
    original = b"%PDF-1.4\n% " + b"x" * 70000 + b"\n%%EOF\n"
    good = str(uuid.uuid4())
    request(files + "/" + good, staff=None, expected=401)
    put_file(good, original, name="../invoice.pdf", expected=400)
    put_file(good, b"plain text", expected=415)
    put_file(good, original, name="scan.png", media="image/png", expected=415)
    # Verify early rejection without streaming a body the server will refuse to read.
    oversized = request(files + "/" + good, staff="receiver", method="PUT", expected=413,
                        extra_headers={"Content-Type": "application/pdf", "X-File-Name": "invoice.pdf",
                                       "Content-Length": str(2 * 1024 * 1024 + 1), "Expect": "100-continue"})
    assert oversized["code"] == "invoice_file_too_large"
    clean = put_file(good, original)
    assert clean["scanStatus"] == "clean" and clean["sizeBytes"] == len(original)
    assert put_file(good, original) == clean
    put_file(good, original + b" ", expected=409)
    put_file(str(uuid.uuid4()), original, expected=409)
    request(files + "/" + good, staff="receiver", expected=403)
    request(foreign + "/receipts/" + draft["receiptId"] + "/files/" + good, staff="admin-foreign", expected=404)
    wrong_receipt = receipt_base + "/" + str(uuid.uuid4())
    request(wrong_receipt + "/files/" + good, staff="admin-a", expected=404)
    payload, headers = request(files + "/" + good, staff="admin-a", raw_response=True)
    assert payload == original and headers["Content-Disposition"].startswith("attachment")
    assert headers["X-Content-Type-Options"] == "nosniff" and headers["Cache-Control"] == "no-store"

    pending_id, rejected_id = str(uuid.uuid4()), str(uuid.uuid4())
    pending = put_file(pending_id, b"%PDF-1.4\n% KASA_TEST_NO_VERDICT\n%%EOF")
    rejected = put_file(rejected_id, b"%PDF-1.4\n% KASA_TEST_THREAT\n%%EOF")
    assert pending["scanStatus"] == "pending" and rejected["scanStatus"] == "rejected"
    assert request(files + "/" + pending_id + "/scan", staff="receiver", method="POST") == pending
    for bad in (pending_id, rejected_id):
        request(files + "/" + bad, staff="admin-a", expected=409)
    rejected_submit = request(path + "/submit", {"expectedVersion": 1}, staff="receiver", expected=409)
    assert rejected_submit["code"] == "invoice_files_not_verified"
    put_file(str(uuid.uuid4()), b"%PDF-1.4\n% one too many\n%%EOF", expected=409)
    for bad in (pending_id, rejected_id):
        request(files + "/" + bad, staff="receiver", method="DELETE", expected=204)

    submit = request(path + "/submit", {"expectedVersion": 1}, staff="receiver")
    assert submit["version"] == 2 and request(files, staff="receiver") == [clean]
    put_file(str(uuid.uuid4()), original, expected=409)
    cost_lines = [{"productId": fixture_id(41), "lotId": fixture_id(90), "unitPrice": 12000,
                   "discount": 1000, "includedTax": 200, "allocatedCharges": 300},
                  {"productId": fixture_id(41), "lotId": fixture_id(91), "unitPrice": 900}]
    cost = {"expectedVersion": 2, "items": cost_lines}
    request(path + "/costs", cost, staff="receiver", method="PUT", expected=403)
    request(path + "/costs", {**cost, "items": cost_lines[:1]}, staff="admin-a", method="PUT", expected=400)
    reviewed = request(path + "/costs", cost, staff="admin-a", method="PUT")
    assert reviewed["total"] == 24400 and reviewed["receiptVersion"] == 3
    assert request(path + "/costs", cost, staff="admin-a", method="PUT") == reviewed
    assert "total" not in request(path, staff="receiver")
    request(path + "/costs", staff="admin-foreign", expected=404)

    request(path + "/return-to-draft", {"expectedVersion": 3, "reason": "Уточнить количество"}, staff="admin-a")
    request(path, {"expectedVersion": 4, "items": [receipt_item(90, 3, future), receipt_item(91, 1, future)]},
            staff="receiver", method="PUT")
    request(path + "/submit", {"expectedVersion": 5}, staff="receiver")
    assert request(path + "/costs", staff="admin-a")["status"] == "stale"
    error = request(path + "/post", {"expectedVersion": 6}, staff="admin-a", expected=409)
    assert error["code"] == "receipt_costs_stale"
    reconciled = request(path + "/costs", {**cost, "expectedVersion": 6}, staff="admin-a", method="PUT")
    assert reconciled["total"] == 36400 and reconciled["receiptVersion"] == 7
    posted = request(path + "/post", {"expectedVersion": 7}, staff="admin-a")
    assert posted["version"] == 8 and posted["status"] == "posted"
    assert request(path + "/post", {"expectedVersion": 7}, staff="admin-a") == posted
    assert request(path + "/costs", staff="admin-a") == {**reconciled, "status": "posted"}
    request(path + "/costs", {**cost, "expectedVersion": 8}, staff="admin-a", method="PUT", expected=409)

    inactive = request(supplier_path, {**card, "expectedVersion": 1, "active": False}, staff="admin-a", method="PUT")
    assert inactive["active"] is False and inactive["version"] == 2
    assert request(root + "/suppliers", staff="receiver") == []
    assert request(root + "/suppliers?activeOnly=false", staff="admin-a")[0]["code"] == "SHOP-01"
    blocked = {**draft, "receiptId": str(uuid.uuid4()), "invoiceNumber": "INACTIVE-01"}
    assert request(receipt_base, blocked, staff="receiver", expected=409)["code"] == "supplier_inactive"
    assert request(path, staff="receiver")["supplierRef"] == "SHOP-01"
    print("PASS: supplier cards, invoice scan/access control, and immutable reviewed purchase costs")


def check_stock_and_inventory():
    root = f"/v1/stores/{fixture_id(11)}"
    foreign = f"/v1/stores/{fixture_id(12)}/stock"
    stock = root + "/stock"
    product = fixture_id(41)
    admin = "admin-a"

    def lots():
        return {lot["lotId"]: lot for lot in request(stock + "/lots?productId=" + product, staff=admin)}

    today = datetime.date.today()
    soon = (today + datetime.timedelta(days=2)).isoformat()
    later = (today + datetime.timedelta(days=10)).isoformat()
    doc = str(uuid.uuid4())
    receipt = {"receiptId": doc, "supplierRef": "Учебный поставщик", "invoiceNumber": "EXPIRY-01",
               "invoiceDate": today.isoformat(), "items": [receipt_item(94, 5, soon), receipt_item(95, 5, later)]}
    request(root + "/receipts", receipt, staff="receiver")
    receipt_path = root + "/receipts/" + doc
    request(receipt_path + "/submit", {"expectedVersion": 1}, staff="receiver")
    request(receipt_path + "/post", {"expectedVersion": 2}, staff=admin)

    assert {row["lotId"] for row in request("/v1/expiry/cashier?filter=within3")} == {fixture_id(94), fixture_id(155)}
    assert {row["lotId"] for row in request(stock + "/expiry?filter=expired", staff=admin)} == {fixture_id(52), fixture_id(156)}
    assert {row["lotId"] for row in request("/v1/expiry/cashier?filter=missing")} == {fixture_id(53), fixture_id(157)}
    request("/v1/expiry/cashier", staff="receiver", expected=403)
    request(stock + "/expiry", expected=403)
    request(foreign + "/expiry", staff=admin, expected=404)
    request(stock + "/lots", staff="receiver", expected=403)
    assert lots()[fixture_id(52)]["availableQuantity"] == 0 and lots()[fixture_id(53)]["availableQuantity"] == 0
    catalog = request("/v1/catalog/barcodes/001")
    assert catalog["requiresLotVerification"] is True
    assert fixture_id(52) not in {row["lotId"] for row in catalog["lots"]}

    shelf = {"taskId": str(uuid.uuid4()), "productId": product, "lotId": fixture_id(94), "quantity": 2}
    queue = stock + "/shelf-tasks"
    before = lots()[fixture_id(94)]
    mark = request("/v1/expiry/cashier/shelf-tasks", shelf)
    assert request("/v1/expiry/cashier/shelf-tasks", shelf) == mark
    request("/v1/expiry/cashier/shelf-tasks", {**shelf, "taskId": str(uuid.uuid4())}, expected=409)
    assert request(queue, staff=admin) == [mark]
    assert lots()[fixture_id(94)] == before

    ops = stock + "/operations"
    existing_operations = {row["operationId"] for row in request(ops, staff=admin)}
    quarantine = {"operationId": str(uuid.uuid4()), "kind": "quarantine", "productId": product,
                  "lotId": fixture_id(94), "quantity": 2, "reason": "Требуется проверка упаковки"}
    request(ops, quarantine, expected=403)
    request(ops, quarantine, staff="receiver", expected=403)
    request(foreign + "/operations", quarantine, staff=admin, expected=404)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        result = list(pool.map(lambda _: request(ops, quarantine, staff=admin), range(2)))
    assert result[0] == result[1] and result[0]["lines"][0]["quarantineDelta"] == 2
    assert request(ops + "/" + quarantine["operationId"], staff=admin) == result[0]
    request(ops, {**quarantine, "quantity": 1}, staff=admin, expected=409)
    quarantined = lots()[fixture_id(94)]
    assert quarantined["stockQuantity"] == 3 and quarantined["quarantineQuantity"] == 2
    assert quarantined["accountedQuantity"] == 5 and quarantined["availableQuantity"] == 3
    resolve = queue + "/" + shelf["taskId"] + "/resolve"
    result = request(resolve, {"resolution": "Две упаковки переведены в карантин"}, staff=admin)
    assert result["resolvedBy"] == fixture_id(34) and request(queue, staff=admin) == []
    assert lots()[fixture_id(94)] == quarantined

    writeoff = {**quarantine, "operationId": str(uuid.uuid4()), "kind": "writeOffQuarantine", "quantity": 3}
    assert request(ops, writeoff, staff=admin, expected=409)["code"] == "stock_quantity_conflict"
    disposal = request(ops, {**writeoff, "quantity": 2}, staff=admin)
    assert disposal["lines"][0]["quarantineDelta"] == -2
    assert lots()[fixture_id(94)]["accountedQuantity"] == 3
    expired = {**quarantine, "operationId": str(uuid.uuid4()), "kind": "writeOffStock", "lotId": fixture_id(52),
               "quantity": 1, "reason": "Просрочка"}
    assert request(ops, expired, staff=admin)["lines"][0]["stockDelta"] == -1
    assert lots()[fixture_id(52)]["accountedQuantity"] == 4
    new_operations = {row["operationId"] for row in request(ops, staff=admin)} - existing_operations
    assert new_operations == {quarantine["operationId"], writeoff["operationId"], expired["operationId"]}

    selected = [fixture_id(94), fixture_id(95)]
    inventories = stock + "/inventories"
    start = {"inventoryId": str(uuid.uuid4()), "reason": "Контрольный пересчёт",
             "lots": [{"productId": product, "lotId": lot} for lot in selected]}
    request(inventories, start, staff="receiver", expected=403)
    draft = request(inventories, start, staff=admin)
    assert draft["status"] == "draft" and draft["version"] == 1
    assert request(inventories + "?status=draft", staff=admin)[0]["inventoryId"] == start["inventoryId"]
    assert all(line["expectedStock"] is None and line["countedStock"] is None for line in draft["lines"])
    record = inventories + "/" + start["inventoryId"]
    request(foreign + "/inventories/" + start["inventoryId"], staff=admin, expected=404)

    def count(document, quantities):
        return {"expectedVersion": document["version"], "items": [
            {"productId": product, "lotId": line["lotId"], "lotRevision": line["lotRevision"],
             "stockQuantity": quantities[line["lotId"]], "quarantineQuantity": 0}
            for line in document["lines"]]}

    shift = str(uuid.uuid4())
    request("/v1/shifts", {"shiftId": shift, "openingCash": 0})
    request("/v1/sales", sale(shift, (41, 52, 1)), expected=409)
    request("/v1/sales", sale(shift, (41, 53, 1)), expected=409)
    request("/v1/sales", sale(shift, (41, 95, 1)))
    stale = count(draft, {selected[0]: 2, selected[1]: 4})
    assert request(record + "/count", stale, staff=admin, expected=409)["code"] == "inventory_lot_changed"
    assert request(record, staff=admin)["status"] == "draft"

    fresh = request(record + "?blind=true", staff=admin)
    body = count(fresh, {selected[0]: 2, selected[1]: 4})
    measured = request(record + "/count", body, staff=admin)
    assert measured["lines"][0]["expectedStock"] == 3 and measured["lines"][0]["stockDifference"] == -1
    assert request(record + "/count", body, staff=admin) == measured
    request("/v1/sales", sale(shift, (41, 95, 1)))
    error = request(record + "/post", {"expectedVersion": 2}, staff=admin, expected=409)
    assert error["code"] == "inventory_lot_changed"
    assert lots()[selected[0]]["stockQuantity"] == 3
    request(ops + "/" + start["inventoryId"], staff=admin, expected=404)

    fresh = request(record + "?blind=true", staff=admin)
    assert fresh["lines"][0]["expectedStock"] is None
    measured = request(record + "/count", count(fresh, {selected[0]: 2, selected[1]: 3}), staff=admin)
    assert measured["version"] == 3
    posted = request(record + "/post", {"expectedVersion": 3}, staff=admin)
    assert posted["version"] == 4 and posted["status"] == "posted"
    assert request(inventories + "?status=draft", staff=admin) == []
    assert request(inventories + "?status=posted", staff=admin)[0]["version"] == 4
    assert request(record + "/post", {"expectedVersion": 3}, staff=admin) == posted
    operation = request(ops + "/" + start["inventoryId"], staff=admin)
    assert operation["kind"] == "inventory" and len(operation["lines"]) == 1
    assert operation["lines"][0]["stockDelta"] == -1
    assert lots()[selected[0]]["stockQuantity"] == 2 and lots()[selected[1]]["stockQuantity"] == 3
    print("PASS: expiry filters and tenant access, shelf tasks, idempotent write-offs, concurrent sales and inventory rollback")
    return shift


def check_catalog(shift):
    root = f"/v1/stores/{fixture_id(11)}"
    foreign = f"/v1/stores/{fixture_id(12)}"
    products = root + "/catalog/products"
    admin = "admin-a"
    product = fixture_id(901)
    card_path = products + "/" + product
    card = {"expectedVersion": 0, "name": "Чай листовой", "unit": "piece", "sku": "TEA-01", "category": "Чай",
            "barcodes": [{"code": "000200", "quantityFactor": 1}, {"code": "000201", "quantityFactor": 12}]}
    request(card_path, card, staff="receiver", method="PUT", expected=403)
    created = request(card_path, card, staff=admin, method="PUT")
    assert created["version"] == 1 and created["priceVersion"] == 1 and created["unitPrice"] is None
    assert request(card_path, card, staff=admin, method="PUT") == created
    request(foreign + "/catalog/products/" + product, staff="admin-foreign", expected=404)
    assert request("/v1/catalog/barcodes/000200", expected=409)["code"] == "sale_price_not_set"
    assert request(root + "/receiving/catalog/barcodes/000201", staff="receiver")["quantityFactor"] == 12
    query = "/v1/catalog/search?q=" + urllib.parse.quote("Чай листовой")
    assert request(query)[0]["productId"] == product
    assert request("/v1/catalog/search?q=tea-01")[0]["unitPrice"] is None
    duplicate = request(products + "/" + fixture_id(902), {**card, "sku": "OTHER"}, staff=admin,
                        method="PUT", expected=409)
    assert duplicate["code"] == "product_barcode_in_use"
    request(products + "/" + fixture_id(902), staff=admin, expected=404)
    duplicate_sku = request(products + "/" + fixture_id(902), {**card, "sku": "tea-01", "barcodes": []},
                            staff=admin, method="PUT", expected=409)
    assert duplicate_sku["code"] == "product_sku_in_use"
    request(card_path, {**card, "expectedVersion": 1, "unit": "kg"}, staff=admin, method="PUT", expected=409)
    request(card_path, {**card, "expectedVersion": 1, "name": "Чай листовой сорт 1"}, staff=admin, method="PUT")
    request(card_path, {**card, "expectedVersion": 1, "name": "Устаревшая редакция"}, staff=admin,
            method="PUT", expected=409)

    first_price = {"expectedVersion": 1, "unitPrice": 1200, "active": True, "reason": "Первичная цена"}
    request(card_path + "/price", first_price, staff="receiver", method="PUT", expected=403)
    version2 = request(card_path + "/price", first_price, staff=admin, method="PUT")
    assert version2["version"] == 2 and request(card_path + "/price", first_price, staff=admin, method="PUT") == version2

    lot = fixture_id(903)
    invoice_id = str(uuid.uuid4())
    receipt = {"receiptId": invoice_id, "supplierRef": "Учебный поставщик", "invoiceNumber": "CATALOG-01",
               "invoiceDate": datetime.date.today().isoformat(),
               "items": [{"productId": product, "lotId": lot, "quantity": 24}]}
    request(root + "/receipts", receipt, staff="receiver")
    rp = root + "/receipts/" + invoice_id
    request(rp + "/submit", {"expectedVersion": 1}, staff="receiver")
    request(rp + "/post", {"expectedVersion": 2}, staff=admin)
    by_unit = request("/v1/catalog/barcodes/000200")
    by_pack = request("/v1/catalog/barcodes/000201")
    assert by_unit["productId"] == by_pack["productId"] == product
    assert by_unit["quantityFactor"] == 1 and by_pack["quantityFactor"] == 12
    assert by_pack["barcode"] == "000201" and request("/v1/catalog/products/" + product)["barcode"] == "000200"
    purchase = sale(shift, (901, 903, by_pack["quantityFactor"]))
    purchase["items"][0]["priceVersion"] = 2
    old_sale = request("/v1/sales", purchase)
    assert old_sale["total"] == 14400
    assert request("/v1/catalog/barcodes/000200")["lots"][0]["quantity"] == 12

    reprice = {**first_price, "expectedVersion": 2, "unitPrice": 1500, "reason": "Переоценка"}
    assert request(card_path + "/price", reprice, staff=admin, method="PUT")["version"] == 3
    stale = sale(shift, (901, 903, 1))
    stale["items"][0]["priceVersion"] = 2
    assert request("/v1/sales", stale, expected=409)["code"] == "price_changed"
    assert request("/v1/catalog/barcodes/000201")["lots"][0]["quantity"] == 12
    stale["items"][0]["priceVersion"] = 3
    assert request("/v1/sales", stale)["total"] == 1500
    request(card_path + "/price-history", staff="receiver", expected=403)
    history = request(card_path + "/price-history", staff=admin)
    assert [(price["version"], price["unitPrice"]) for price in history] == [(3, 1500), (2, 1200), (1, None)]
    assert history[0]["reason"] == "Переоценка" and history[0]["changedBy"] == fixture_id(34)
    before_archive = str(uuid.uuid4())
    request(root + "/receipts", {**receipt, "receiptId": before_archive, "invoiceNumber": "CATALOG-OLD"},
            staff="receiver")
    request(card_path + "/price", {**reprice, "expectedVersion": 3, "active": False, "reason": "Снято с продажи"},
            staff=admin, method="PUT")
    archived_rp = root + "/receipts/" + before_archive
    request(archived_rp + "/submit", {"expectedVersion": 1}, staff="receiver")
    assert request(archived_rp + "/post", {"expectedVersion": 2}, staff=admin)["status"] == "posted"
    request(root + "/receipts", {**receipt, "receiptId": str(uuid.uuid4()), "invoiceNumber": "CATALOG-NEW"},
            staff="receiver", expected=409)
    request("/v1/catalog/barcodes/000200", expected=404)
    assert request("/v1/catalog/search?q=TEA-01") == []
    assert request(products + "?q=TEA-01&includeArchived=true", staff=admin)[0]["active"] is False
    assert request("/v1/sales/" + old_sale["operationId"]) == old_sale

    def catalog_csv(*rows):
        buffer = io.StringIO()
        writer = csv.writer(buffer)
        writer.writerow(["productId", "expectedVersion", "name", "unit", "requiresExpiry", "sku", "category",
                         "brand", "supplierRef", "taxProfile", "barcodes", "unitPrice", "expectedPriceVersion"])
        writer.writerows(rows)
        return buffer.getvalue()

    imports = root + "/catalog/import"
    csv_row = [fixture_id(910), 0, "Молоко, 1 л", "piece", False, "MILK-01", "Молоко", "", "", "",
               "000910;000911*6", "2000.25", 1]
    duplicate_row = [fixture_id(911), 0, "Другой товар", "piece", False, "OTHER-01", "", "", "", "",
                     "001", "", ""]
    error = request(imports, {"preview": False, "csv": catalog_csv(csv_row, duplicate_row)}, staff=admin, expected=409)
    assert error["row"] == 3 and error["code"] == "product_barcode_in_use"
    request(products + "/" + fixture_id(910), staff=admin, expected=404)
    csv_text = catalog_csv(csv_row)
    request(imports, {"preview": True, "csv": csv_text}, staff="receiver", expected=403)
    preview = request(imports, {"preview": True, "csv": csv_text}, staff=admin)
    assert preview["preview"] and preview["products"][0]["unitPrice"] == 2000.25
    request("/v1/catalog/barcodes/000910", expected=404)
    confirmed = request(imports, {"preview": False, "csv": csv_text}, staff=admin)
    assert confirmed["products"] == preview["products"]
    assert request(imports, {"preview": False, "csv": csv_text}, staff=admin) == confirmed
    assert request("/v1/catalog/barcodes/000911")["quantityFactor"] == 6
    request(imports, {"preview": False, "csv": "invalid header"}, staff=admin, expected=400)

    proposals = root + "/receiving/catalog/proposals"
    proposed_id = fixture_id(915)
    proposal = {"proposalId": proposed_id, "product": {"name": "Новое молоко", "unit": "piece",
                "barcodes": [{"code": "000915", "quantityFactor": 1}]}}
    pending = request(proposals, proposal, staff="receiver")
    assert pending["status"] == "pending"
    assert request(proposals, proposal, staff="receiver") == pending
    assert request(proposals + "?status=pending", staff=admin) == [pending]
    request(root + "/receiving/catalog/barcodes/000915", staff="receiver", expected=404)
    approve_path = root + "/catalog/proposals/" + proposed_id + "/approve"
    request(approve_path, {}, staff="receiver", expected=403)
    approved = request(approve_path, {"reason": "Проверено"}, staff=admin)
    assert approved["status"] == "approved" and approved["publishedProductId"] == proposed_id
    assert request(approve_path, {"reason": "Проверено"}, staff=admin) == approved
    assert request(root + "/receiving/catalog/barcodes/000915", staff="receiver")["productId"] == proposed_id
    request("/v1/catalog/barcodes/000915", expected=409)
    reject_id = fixture_id(916)
    request(proposals, {**proposal, "proposalId": reject_id}, staff="receiver")
    reject = request(root + "/catalog/proposals/" + reject_id + "/reject", {"reason": "Дубликат"}, staff=admin)
    assert reject["status"] == "rejected" and reject["publishedProductId"] is None
    print("PASS: catalog access, barcode/packaging lookup, versioned prices, atomic CSV and proposal approval")


def check_checkout(previous_shift):
    admin = "admin-a"
    store = f"/v1/stores/{fixture_id(11)}"
    request(f"/v1/shifts/{previous_shift}/close", {"countedCash": 0})
    shift = str(uuid.uuid4())
    request("/v1/shifts", {"shiftId": shift, "openingCash": 100000})
    shift_path = "/v1/shifts/" + shift
    admin_report = f"/v1/reports/stores/{fixture_id(11)}/registers/{fixture_id(21)}/shifts/{shift}"
    movements = shift_path + "/cash-movements"

    withdraw = {"operationId": str(uuid.uuid4()), "direction": "out", "amount": 200000, "reason": "Инкассация"}
    assert request(movements, withdraw, expected=409)["code"] == "insufficient_cash_balance"
    deposit = {"operationId": str(uuid.uuid4()), "direction": "in", "amount": 500000, "reason": "Внесение"}
    request(movements, deposit, staff="receiver", expected=403)
    deposit_saved = request(movements, deposit)
    assert request(movements, deposit) == deposit_saved
    request(movements, {**deposit, "amount": 1}, expected=409)
    request(movements, withdraw)
    assert [x["direction"] for x in request(movements)] == ["out", "in"]
    admin_movements = f"{store}/registers/{fixture_id(21)}/shifts/{shift}/cash-movements"
    assert len(request(admin_movements, staff=admin)) == 2
    request(admin_movements, staff="admin-foreign", expected=404)
    assert request(movements, staff="cashier-b", register=22) == []
    assert request(shift_path)["expectedCash"] is None and request(shift_path)["cashIn"] is None
    totals = request(admin_report, staff=admin)
    assert totals["expectedCash"] == 400000 and totals["cashIn"] == 500000 and totals["cashOut"] == 200000

    cart_id = str(uuid.uuid4())
    cart = shift_path + "/carts/" + cart_id
    cash_sale = sale(shift, (41, 94, 2), (41, 95, 1), method="cash", tendered=60000)
    saved = request(cart, {"expectedVersion": 0, "items": cash_sale["items"]}, method="PUT")
    assert saved["version"] == 1 and saved["status"] == "parked"
    assert request(cart, {"expectedVersion": 0, "items": cash_sale["items"]}, method="PUT") == saved
    assert request(shift_path + "/carts") == [saved]
    request(cart, staff="cashier-b", register=22, expected=404)
    assert request(shift_path + "/close", {"countedCash": 400000}, expected=409)["code"] == "shift_has_parked_carts"
    assert request("/v1/catalog/barcodes/001")["lots"][0]["quantity"] == 2
    price = f"{store}/catalog/products/{fixture_id(41)}/price"
    request(price, {"expectedVersion": 1, "unitPrice": 18000, "active": True, "reason": "Новая цена"},
            method="PUT", staff=admin)
    review = request(cart)
    assert [line["status"] for line in review["lines"]] == ["priceChanged", "priceChanged"]
    cash_sale.update({"cartId": cart_id, "cartVersion": 1})
    assert request("/v1/sales", cash_sale, expected=409)["code"] == "price_changed"
    assert request(cart)["cart"]["status"] == "parked"
    for item in cash_sale["items"]:
        item["priceVersion"] = 2
    cash_sale["discount"] = {"kind": "percent", "value": 11, "reason": "Превышение"}
    assert request("/v1/sales", cash_sale, expected=409)["code"] == "manual_discount_outside_limit"
    assert request("/v1/catalog/barcodes/001")["lots"][0]["quantity"] == 2
    cash_sale["discount"] = {"kind": "amount", "value": 1000.01, "reason": "Скидка покупателю"}
    committed = request("/v1/sales", cash_sale)
    assert committed["discountAmount"] == 1000.01 and committed["discountReason"] == "Скидка покупателю"
    assert committed["total"] == 52999.99 and committed["changeDue"] == 7000.01
    assert [(x["priceVersion"], x["discountAmount"], x["lineTotal"]) for x in committed["lines"]] == [
        (2, 666.67, 35333.33), (2, 333.34, 17666.66)]
    assert request("/v1/sales", cash_sale) == committed
    assert request(cart)["cart"]["saleId"] == committed["operationId"]
    assert request(shift_path + "/carts") == []
    assert request("/v1/sales", {**cash_sale, "operationId": str(uuid.uuid4())}, expected=409)["code"] == "parked_cart_version_conflict"

    sale_id = committed["operationId"]
    lookup = f"/v1/sales/history?receiptId={sale_id}"
    day = datetime.datetime.fromisoformat(committed["createdAt"]).astimezone(ZoneInfo("Asia/Tashkent")).date()
    history = request(lookup + f"&date={day}&amount=52999.99&shiftId={shift}")
    assert len(history) == 1 and history[0]["paymentStatus"] == "recorded" and history[0]["refundStatus"] == "none"
    assert request(lookup, staff="cashier-b", register=22) == []
    admin_lookup = f"{store}/sales/history?cashierId={fixture_id(31)}&registerId={fixture_id(21)}&receiptId={sale_id}"
    assert request(admin_lookup, staff=admin) == history
    request(admin_lookup, staff="admin-foreign", expected=404)
    detail = f"{store}/sales/{fixture_id(21)}/{sale_id}"
    assert request(detail, staff=admin) == committed
    request(detail, staff="receiver", expected=403)
    request(detail, staff="admin-foreign", expected=404)

    def refund(*lots):
        return {"operationId": str(uuid.uuid4()), "shiftId": shift, "sourceRegisterId": fixture_id(21),
                "saleId": sale_id, "reason": "Возврат", "items": [
                    {"productId": fixture_id(41), "lotId": fixture_id(lot), "quantity": 1, "destination": "stock"}
                    for lot in lots]}

    first_refund = request("/v1/refunds", refund(94))
    assert first_refund["total"] == 17666.67
    assert request(lookup)[0]["refundStatus"] == "partial"
    rest = refund(94, 95)
    request(store + "/refund-approvals", {"registerId": fixture_id(21), "amount": 35333.32, "refund": rest}, staff=admin)
    assert request("/v1/refunds", rest)["total"] == 35333.32
    assert request(lookup)[0]["refundStatus"] == "full" and request(lookup)[0]["refundedAmount"] == 52999.99
    percent = sale(shift, (41, 95, 1))
    percent["items"][0]["priceVersion"] = 2
    percent["discount"] = {"kind": "percent", "value": 10, "reason": "Процентная скидка"}
    assert request("/v1/sales", percent)["total"] == 16200

    another = shift_path + "/carts/" + str(uuid.uuid4())
    request(another, {"expectedVersion": 0, "items": cash_sale["items"]}, method="PUT")
    cancelled = request(another + "/cancel", {"expectedVersion": 1, "reason": "Покупатель отказался"})
    assert cancelled["status"] == "cancelled"
    assert request(another + "/cancel", {"expectedVersion": 1, "reason": "Покупатель отказался"}) == cancelled
    closed = request(shift_path + "/close", {"countedCash": 390000})
    assert closed["expectedCash"] == 400000 and closed["cashDifference"] == -10000
    assert closed["cashIn"] == 500000 and closed["cashOut"] == 200000
    request(movements, {**deposit, "operationId": str(uuid.uuid4())}, expected=409)
    assert request(movements, deposit) == deposit_saved
    print("PASS: parked cart repricing and checkout, cash movements, discounted refunds and scoped receipt search")


def check_procurement():
    store = f"/v1/stores/{fixture_id(11)}"
    procurement = store + "/procurement"
    supplier = procurement + "/suppliers/PROC-01"
    prices = supplier + "/prices"
    price = prices + "/BOX-01"
    admin = "admin-a"
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    expiry = (today + datetime.timedelta(days=30)).isoformat()
    delivery = (today + datetime.timedelta(days=3)).isoformat()
    sku = "BOX-01"
    product = fixture_id(41)
    request(store + "/suppliers/PROC-01", {"expectedVersion": 0, "name": "Поставщик закупки", "active": True},
            staff=admin, method="PUT")

    price_body = {"expectedVersion": 0, "productId": product, "packageUnit": "коробка", "unitsPerPackage": 12,
                  "packagePrice": 12000, "validFrom": (today - datetime.timedelta(days=1)).isoformat()}
    active = request(price, price_body, staff=admin, method="PUT")
    assert active["version"] == 1 and active["validThrough"] is None
    assert request(price, price_body, staff=admin, method="PUT") == active
    request(prices, staff="receiver", expected=403)
    request(prices, staff="admin-foreign", expected=404)
    future = (today + datetime.timedelta(days=5)).isoformat()
    scheduled = request(price, {**price_body, "expectedVersion": 1, "packagePrice": 18000, "validFrom": future},
                        staff=admin, method="PUT")
    assert request(prices, staff=admin) == [active]
    assert request(prices + "?asOf=" + future, staff=admin) == [scheduled]
    assert request(price, staff=admin) == [scheduled, active]

    order_id = str(uuid.uuid4())
    order = procurement + "/orders/" + order_id
    receiving_order = store + "/purchase-orders/" + order_id
    offer = {"supplierSku": sku, "priceVersion": 1, "packages": 2}
    draft = {"expectedRevision": 0, "supplierCode": "PROC-01", "expectedDeliveryDate": delivery, "items": [offer]}
    saved = request(order, draft, staff=admin, method="PUT")
    assert saved["revision"] == 1 and saved["stage"] == "draft" and saved["total"] == 24000
    assert request(order, draft, staff=admin, method="PUT") == saved
    request(order, staff="receiver", expected=403)
    request(receiving_order, staff="receiver", expected=404)
    dispatch = {"expectedRevision": 1, "reference": "Письмо 1"}
    assert request(order + "/mark-sent", dispatch, staff=admin, expected=409)["code"] == "purchase_order_approval_required"
    request(order + "/approve", {"expectedRevision": 1}, staff="receiver", expected=403)
    approved = request(order + "/approve", {"expectedRevision": 1}, staff=admin)
    assert approved["stage"] == "approved"
    assert request(order + "/approve", {"expectedRevision": 1}, staff=admin) == approved

    edited = request(order, {**draft, "expectedRevision": 1, "items": [{**offer, "packages": 3}]},
                     staff=admin, method="PUT")
    assert edited["revision"] == 2 and edited["stage"] == "draft" and edited["approvedAt"] is None
    request(order + "/approve", {"expectedRevision": 1}, staff=admin, expected=409)
    dispatch["expectedRevision"] = 2
    request(order + "/mark-sent", dispatch, staff=admin, expected=409)
    request(order + "/approve", {"expectedRevision": 2}, staff=admin)
    sent = request(order + "/mark-sent", dispatch, staff=admin)
    assert sent["stage"] == "sent" and sent["lines"][0]["baseQuantity"] == 36
    assert request(order + "/mark-sent", dispatch, staff=admin) == sent
    request(order + "/mark-sent", {**dispatch, "reference": "Письмо 2"}, staff=admin, expected=409)
    request(order, {**draft, "expectedRevision": 2}, staff=admin, method="PUT", expected=409)
    public = request(receiving_order, staff="receiver")
    assert public["expectedDeliveryDate"] == delivery and public["lines"][0]["remaining"] == 36
    assert "total" not in public and "packagePrice" not in public["lines"][0]
    assert request(procurement + "/orders?stage=sent", staff=admin)[0]["orderId"] == order_id

    stale = procurement + "/orders/" + str(uuid.uuid4())
    request(stale, draft, staff=admin, method="PUT")
    request(price, {**price_body, "expectedVersion": 2, "packagePrice": 14000, "validFrom": today.isoformat()},
            staff=admin, method="PUT")
    assert request(stale + "/approve", {"expectedRevision": 1}, staff=admin, expected=409)["code"] == "supplier_price_changed"
    assert request(order, staff=admin) == sent

    def receive(number, lots, unit_price=None):
        doc = {"receiptId": str(uuid.uuid4()), "purchaseOrderId": order_id, "supplierRef": "PROC-01",
               "invoiceNumber": number, "invoiceDate": today.isoformat(),
               "items": [receipt_item(lot, quantity, expiry) for lot, quantity in lots]}
        receipts = store + "/receipts"
        request(receipts, doc, staff="receiver")
        path = receipts + "/" + doc["receiptId"]
        request(path + "/submit", {"expectedVersion": 1}, staff="receiver")
        version = 2
        if unit_price is not None:
            reviewed = request(path + "/costs", {"expectedVersion": version, "items": [
                {"productId": product, "lotId": fixture_id(lot), "unitPrice": unit_price} for lot, _ in lots]},
                staff=admin, method="PUT")
            version = reviewed["receiptVersion"]
        request(path + "/post", {"expectedVersion": version}, staff=admin)
        return doc["receiptId"]

    receipt = receive("PROC-IN-01", [(960, 18), (962, 2)], unit_price=1000)
    partial = request(receiving_order, staff="receiver")
    assert partial["status"] == "partial" and partial["lines"][0]["remaining"] == 16
    returns = procurement + "/returns"
    item = {"productId": product, "lotId": fixture_id(960), "quantity": 2, "bucket": "stock"}
    command = {"returnId": str(uuid.uuid4()), "receiptId": receipt, "reason": "Возврат брака", "items": [item]}
    request(returns, command, staff="receiver", expected=403)
    wrong_lot = {**command, "items": [{**item, "lotId": fixture_id(51)}]}
    assert request(returns, wrong_lot, staff=admin, expected=409)["code"] == "supplier_return_item_not_in_receipt"
    # The second lot has no quarantine balance. A failure after updating the first lot must roll it back.
    mixed = {**command, "items": [item, {**item, "lotId": fixture_id(962), "bucket": "quarantine"}]}
    assert request(returns, mixed, staff=admin, expected=409)["code"] == "stock_quantity_conflict"
    request(returns + "/" + command["returnId"], staff=admin, expected=404)
    returned = request(returns, command, staff=admin)
    assert returned["creditAmount"] == 2000
    assert request(returns, command, staff=admin) == returned
    return_path = returns + "/" + command["returnId"]
    assert request(return_path, staff=admin) == returned
    request(return_path, staff="receiver", expected=403)
    request(return_path, staff="admin-foreign", expected=404)
    too_much = {**command, "returnId": str(uuid.uuid4()), "items": [{**item, "quantity": 19}]}
    assert request(returns, too_much, staff=admin, expected=409)["code"] == "supplier_return_quantity_exceeded"

    # Each request fits alone. Only one can commit against the original receipt.
    def competing_return(_):
        payload = {**command, "returnId": str(uuid.uuid4()), "items": [{**item, "quantity": 10}]}
        try:
            assert request(returns, payload, staff=admin)["creditAmount"] == 10000
            return 200
        except AssertionError as error:
            assert b"supplier_return_quantity_exceeded" in error.args[0][3], error
            return 409

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        assert sorted(pool.map(competing_return, range(2))) == [200, 409]
    assert request(receiving_order, staff="receiver") == partial

    # An unpriced legacy-style receipt can be returned, with the unknown credit explicitly reported.
    unpriced = receive("PROC-IN-02", [(961, 16)])
    balances = request(store + "/stock/lots?productId=" + product, staff=admin)
    quantities = {lot["lotId"]: lot["stockQuantity"] for lot in balances}
    assert quantities[fixture_id(960)] == 6 and quantities[fixture_id(962)] == 2
    assert request(receiving_order, staff="receiver")["status"] == "received"
    unknown = {**command, "receiptId": unpriced, "returnId": str(uuid.uuid4()), "items": [
        {**item, "lotId": fixture_id(961), "quantity": 1}]}
    assert request(returns, unknown, staff=admin)["creditAmount"] is None

    payments = supplier + "/payments"
    payment = {"operationId": str(uuid.uuid4()), "amount": 5000, "paymentDate": today.isoformat(),
               "reference": "Платёж 001"}
    request(payments, payment, staff="receiver", expected=403)
    recorded = request(payments, payment, staff=admin)
    assert request(payments, payment, staff=admin) == recorded
    assert request(payments, staff=admin) == [recorded]
    duplicate = request(payments, {**payment, "operationId": str(uuid.uuid4())}, staff=admin, expected=409)
    assert duplicate["code"] == "supplier_payment_duplicate_reference"
    request(supplier + "/balance", staff="receiver", expected=403)
    balance = request(supplier + "/balance", staff=admin)
    assert balance == {"supplierCode": "PROC-01", "invoicedAmount": 20000, "returnCredit": 12000, "paidAmount": 5000,
                       "balance": 3000, "unpricedReceipts": 1, "unpricedReturns": 1}
    print("PASS: procurement prices, reapproval, partial delivery, concurrent supplier returns and manual settlements")


def check_replenishment():
    root = f"/v1/stores/{fixture_id(11)}"
    procurement = root + "/procurement"
    product = fixture_id(144)
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    tomorrow = (today + datetime.timedelta(days=1)).isoformat()
    admin = "admin-replenishment"
    forecast = procurement + "/recommendations/REPL-01/COLA-10"
    snapshot = forecast + "/snapshot"

    def wait_snapshot(path, matches):
        deadline = time.monotonic() + 12
        while True:
            value = request(path, staff=admin)
            if matches(value):
                return value
            assert time.monotonic() < deadline, (path, value)
            time.sleep(0.35)

    timer = procurement + "/recommendations/TIMER-01/TIMER-COLA/snapshot"
    expired = wait_snapshot(timer, lambda value: value["status"] == "unavailable")
    assert expired["errorCode"] == "supplier_inactive" and expired["calculatedPolicyVersion"] == 1
    queued_path = procurement + "/recommendations/TIMER-01/TIMER-QUEUED/snapshot"
    queued = wait_snapshot(queued_path, lambda value: value["status"] == "unavailable")
    assert queued["errorCode"] == "supplier_inactive" and queued["calculatedPolicyVersion"] == 1
    stockout = procurement + f"/products/{product}/stockout-days/"
    confirmed_day = (today - datetime.timedelta(days=3)).isoformat()

    request(root + "/suppliers/REPL-01", {"expectedVersion": 0, "name": "Тест пополнения",
            "leadTimeDays": 1, "active": True}, staff=admin, method="PUT")
    request(procurement + "/suppliers/REPL-01/prices/COLA-10", {"expectedVersion": 0, "productId": product,
            "packageUnit": "упаковка", "unitsPerPackage": 10, "packagePrice": 10000,
            "validFrom": today.isoformat()}, staff=admin, method="PUT")
    request(forecast, {}, staff="cashier-a", expected=403)
    request(forecast, {}, staff="receiver", expected=403)
    request(forecast, {}, staff="admin-foreign", expected=404)
    request(forecast, {}, staff=admin, mfa=False, expected=403)
    request(forecast, {"orderWeekdays": []}, staff=admin, expected=400)
    spec = request("/openapi/v1.json")
    route = spec["paths"]["/v1/stores/{storeId}/procurement/recommendations/{code}/{sku}"]["post"]
    assert set(route["security"][0]) == {"Bearer"}
    assert "200" in route["responses"]
    snapshot_route = spec["paths"]["/v1/stores/{storeId}/procurement/recommendations/{code}/{sku}/snapshot"]["get"]
    assert set(snapshot_route["security"][0]) == {"Bearer"}
    request(snapshot, staff=admin, expected=404)
    request(snapshot, staff="receiver", expected=403)
    request(snapshot, staff="admin-foreign", expected=404)
    policy_spec = spec["paths"]["/v1/stores/{storeId}/procurement/replenishment-policies/{code}/{sku}"]
    assert set(policy_spec) >= {"get", "put"} and "409" in policy_spec["put"]["responses"]

    def calculate(**settings):
        return request(forecast, settings, staff=admin)

    initial = calculate()
    assert initial["demand"]["unitsPerDay"] == 18.125 and initial["demand"]["usableDays"] == 8
    confirmation = {"expectedVersion": 0, "confirmed": True, "reason": "Подтверждено по журналу склада"}
    request(stockout + confirmed_day, confirmation, staff="receiver", method="PUT", expected=403)
    request(stockout + today.isoformat(), confirmation, staff=admin, method="PUT", expected=400)
    saved = request(stockout + confirmed_day, confirmation, staff=admin, method="PUT")
    assert request(stockout + confirmed_day, confirmation, staff=admin, method="PUT") == saved
    assert request(stockout + confirmed_day, staff=admin) == saved
    request(stockout + confirmed_day, staff="admin-foreign", expected=404)
    request(stockout + confirmed_day, {**confirmation, "reason": "Конфликт"}, staff=admin, method="PUT", expected=409)

    sparse = calculate(historyDays=4, minimumHistoryDays=4)
    assert sparse["demand"]["insufficientHistory"] and sparse["demand"]["usableDays"] == 3
    assert sparse["projection"] is None and sparse["draft"] is None
    fallback = calculate(historyDays=4, minimumHistoryDays=4, manualDailyDemand=20)
    assert fallback["demand"]["source"] == "manual" and fallback["demand"]["insufficientHistory"]

    baseline = calculate()
    assert baseline["calculationVersion"] == "replenishment-v1"
    assert baseline["demand"]["unitsPerDay"] == 20 and baseline["demand"]["usableDays"] == 7
    assert baseline["sellableStock"] == 25 and baseline["excludedStock"] == 7 and baseline["daysOfStock"] == 1.25
    assert baseline["arrivalDate"] == tomorrow and baseline["projection"]["stockAtArrival"] == 5
    assert baseline["projection"]["risk"] == "orderAtFirstWindow"
    assert baseline["projection"]["targetQuantity"] == 100 and baseline["projection"]["unroundedQuantity"] == 95
    assert baseline["suggestedPackages"] == 10 and baseline["suggestedQuantity"] == 100
    word_example = calculate(safetyStock=5)
    assert word_example["projection"]["targetQuantity"] == 105
    assert word_example["projection"]["unroundedQuantity"] == 100 and word_example["suggestedQuantity"] == 100
    expiry = calculate(manualDailyDemand=5)
    assert expiry["projection"]["expiresBeforeArrival"] == 5 and expiry["projection"]["stockAtArrival"] == 15
    assert expiry["projection"]["unroundedQuantity"] == 10
    assert calculate(manualDailyDemand=5, minimumPackages=3)["suggestedQuantity"] == 30
    future_window = calculate(orderWeekdays=[today.isoweekday() % 7 + 1])
    assert future_window["orderDate"] == tomorrow and future_window["draft"] is None
    assert future_window["projection"]["risk"] == "beforeEarliestDelivery"

    def prepare_order(body):
        order_id = str(uuid.uuid4())
        path = procurement + "/orders/" + order_id
        assert request(path, body, staff=admin, method="PUT")["stage"] == "draft"
        request(path + "/approve", {"expectedRevision": 1}, staff="cashier-a", expected=403)
        assert request(path + "/approve", {"expectedRevision": 1}, staff=admin)["stage"] == "approved"
        return order_id, path

    def send(path):
        dispatch = {"expectedRevision": 1, "reference": "Стендовая отправка"}
        sent = request(path + "/mark-sent", dispatch, staff=admin)
        assert request(path + "/mark-sent", dispatch, staff=admin) == sent

    policy_path = procurement + "/replenishment-policies/REPL-01/COLA-10"
    weekdays = list(range(1, 8))
    policy_request = {"expectedVersion": 0, "settings": {"orderCutoffTime": "00:00:00", "orderWeekdays": weekdays}}
    request(forecast, staff=admin, expected=404)
    request(policy_path, policy_request, staff="receiver", method="PUT", expected=403)
    request(policy_path, policy_request, staff="admin-foreign", method="PUT", expected=404)
    request(policy_path, {"expectedVersion": 0, "settings": None}, staff=admin, method="PUT", expected=400)
    policy = request(policy_path, policy_request, staff=admin, method="PUT")
    assert policy["version"] == 1 and policy["productId"] == product
    replay = {**policy_request, "settings": {**policy_request["settings"], "orderWeekdays": weekdays[::-1]}}
    assert request(policy_path, replay, staff=admin, method="PUT") == policy
    assert request(policy_path, staff=admin) == policy
    request(forecast, staff="cashier-a", expected=403)
    stored_forecast = request(forecast, staff=admin)
    assert stored_forecast["orderDate"] == tomorrow and stored_forecast["draft"] is None
    assert stored_forecast["policyVersion"] == 1
    explain = forecast + "/explain"
    for staff, status in ((None, 401), ("cashier-a", 403), ("receiver", 403), ("admin-foreign", 404)):
        request(explain, staff=staff, expected=status, method="POST")
    assert request(explain, staff="admin-a", expected=403, method="POST")["code"] == "procurement_ai_access_denied"
    calls_before_other_store = groq_calls()
    other_store = explain.replace(fixture_id(11), fixture_id(13))
    assert request(other_store, staff="admin-inventory", expected=403,
                   method="POST")["code"] == "procurement_ai_access_denied"
    assert groq_calls() == calls_before_other_store
    assert request(explain, staff=admin, mfa=False, expected=403, method="POST")["code"] == "admin_mfa_required"
    explanation = request(explain, staff=admin, method="POST")
    assert explanation["explanation"].startswith("Запас может закончиться")
    assert datetime.datetime.fromisoformat(explanation["calculatedAt"]) >= \
        datetime.datetime.fromisoformat(stored_forecast["calculatedAt"])
    assert explanation["policyVersion"] == 1
    assert explanation["orderDate"] == stored_forecast["orderDate"]
    assert explanation["suggestedQuantity"] == stored_forecast["suggestedQuantity"]
    assert groq_calls() == calls_before_other_store + 1
    assert request(explain, staff="admin-inventory", method="POST")["policyVersion"] == 1
    assert request(explain, staff="admin-inventory", method="POST")["policyVersion"] == 1
    request(explain, staff="admin-inventory", expected=429, method="POST")
    saved_snapshot = wait_snapshot(snapshot, lambda value: value["status"] == "ready")
    assert saved_snapshot["calculatedPolicyVersion"] == 1
    assert saved_snapshot["recommendation"]["orderDate"] == tomorrow
    request(snapshot, staff="cashier-a", expected=403)
    request(policy_path, {"expectedVersion": 0, "settings": {}}, staff=admin, method="PUT", expected=409)

    policy = request(policy_path, {"expectedVersion": 1, "settings": {}}, staff=admin, method="PUT")
    assert policy["version"] == 2
    first_body = request(forecast, staff=admin)["draft"]
    assert first_body["replenishment"]["policyVersion"] == 2
    order_id = str(uuid.uuid4())
    linked_order = procurement + "/orders/" + order_id
    linked_draft = request(linked_order, first_body, staff=admin, method="PUT")
    assert request(linked_order, first_body, staff=admin, method="PUT") == linked_draft
    assert linked_draft["replenishment"] == first_body["replenishment"]
    omitted_origin = {key: value for key, value in first_body.items() if key != "replenishment"}
    error = request(linked_order, {**omitted_origin, "expectedRevision": 1}, staff=admin, method="PUT", expected=409)
    assert error["code"] == "purchase_replenishment_origin_required"
    changed_amount = {**first_body, "expectedRevision": 1, "items": [{**first_body["items"][0], "packages": 11}]}
    error = request(linked_order, changed_amount, staff=admin, method="PUT", expected=409)
    assert error["code"] == "purchase_replenishment_changed"
    request(policy_path, {"expectedVersion": 2, "settings": {"safetyStock": 5}}, staff=admin, method="PUT")
    error = request(linked_order + "/approve", {"expectedRevision": 1}, staff=admin, expected=409)
    assert error["code"] == "replenishment_policy_changed" and request(linked_order, staff=admin) == linked_draft
    updated_body = request(forecast, staff=admin)["draft"]
    assert updated_body["replenishment"]["policyVersion"] == 3
    updated = request(linked_order, {**updated_body, "expectedRevision": 1}, staff=admin, method="PUT")
    assert updated["revision"] == 2
    waiting = procurement + "/orders/" + str(uuid.uuid4())
    pending = request(waiting, updated_body, staff=admin, method="PUT")

    # A separate receipt changes the live stock before either recommended order is approved.
    receipts = root + "/receipts"
    extra_id = str(uuid.uuid4())
    extra = {"receiptId": extra_id, "supplierRef": "REPL-01", "invoiceNumber": "REPL-STOCK-CHANGE",
             "invoiceDate": today.isoformat(), "items": [
                 receipt_item(159, 20, (today + datetime.timedelta(days=20)).isoformat(), product=144)]}
    request(receipts, extra, staff="receiver")
    request(receipts + "/" + extra_id + "/submit", {"expectedVersion": 1}, staff="receiver")
    request(receipts + "/" + extra_id + "/post", {"expectedVersion": 2}, staff=admin)
    assert request(forecast, staff=admin)["suggestedQuantity"] == 80
    after_stock = wait_snapshot(snapshot, lambda value: value["status"] == "ready"
        and value["recommendation"]["sellableStock"] == 45)
    assert after_stock["calculatedPolicyVersion"] == 3
    assert after_stock["recommendation"]["suggestedQuantity"] == 80
    error = request(waiting + "/approve", {"expectedRevision": 1}, staff=admin, expected=409)
    assert error["code"] == "purchase_replenishment_changed"
    request(root + "/stock/operations", {"operationId": str(uuid.uuid4()), "kind": "writeOffStock", "productId": product,
            "lotId": fixture_id(159), "quantity": 20, "reason": "Списать стендовую партию"}, staff=admin)

    late_day = (today + datetime.timedelta(days=3)).isoformat()
    late_id, late_order = prepare_order({**omitted_origin, "expectedDeliveryDate": late_day})
    assert calculate()["deliveries"] == []
    send(late_order)
    after_late = calculate()
    late_snapshot = wait_snapshot(snapshot, lambda value: value["status"] == "ready"
        and any(row["orderId"] == late_id for row in value["recommendation"]["deliveries"]))
    assert late_snapshot["recommendation"]["suggestedQuantity"] == 100
    assert after_late["deliveries"] == [{"orderId": late_id, "supplierRef": "REPL-01", "expectedDate": late_day,
                                         "remainingQuantity": 100, "timing": "late"}]
    assert after_late["projection"]["firstShortageDate"] == tomorrow
    assert after_late["projection"]["risk"] == "orderAtFirstWindow" and after_late["suggestedQuantity"] == 100

    timely_id = order_id
    approval = {"expectedRevision": 2}
    assert request(linked_order + "/approve", approval, staff=admin)["stage"] == "approved"
    dispatch = {"expectedRevision": 2, "reference": "Стендовая отправка после пересчёта"}
    sent = request(linked_order + "/mark-sent", dispatch, staff=admin)
    assert request(linked_order + "/mark-sent", dispatch, staff=admin) == sent
    error = request(waiting + "/approve", {"expectedRevision": 1}, staff=admin, expected=409)
    assert error["code"] == "purchase_replenishment_changed" and request(waiting, staff=admin) == pending
    assert request(linked_order + "/approve", approval, staff=admin) == sent
    after_timely = calculate()
    assert after_timely["deliveries"][0]["orderId"] == timely_id and after_timely["deliveries"][0]["timing"] == "timely"
    assert after_timely["suggestedQuantity"] == 0 and after_timely["draft"] is None

    receipt_id = str(uuid.uuid4())
    receipts = root + "/receipts"
    receipt = {"receiptId": receipt_id, "purchaseOrderId": timely_id, "supplierRef": "REPL-01",
               "invoiceNumber": "REPL-PARTIAL", "invoiceDate": today.isoformat(),
               "items": [receipt_item(158, 10, (today + datetime.timedelta(days=20)).isoformat(), product=144)]}
    request(receipts, receipt, staff="receiver")
    request(receipts + "/" + receipt_id + "/submit", {"expectedVersion": 1}, staff="receiver")
    request(receipts + "/" + receipt_id + "/post", {"expectedVersion": 2}, staff=admin)
    after_receipt = calculate()
    assert after_receipt["sellableStock"] == 35 and after_receipt["deliveries"][0]["remainingQuantity"] == 90
    assert after_receipt["projection"]["stockAtArrival"] == after_timely["projection"]["stockAtArrival"]

    correction = {**confirmation, "expectedVersion": 1, "confirmed": False, "reason": "Исправление проверки"}
    assert request(stockout + confirmed_day, correction, staff=admin, method="PUT")["version"] == 2
    assert calculate()["demand"]["unitsPerDay"] == 18.125
    refreshed = wait_snapshot(snapshot, lambda value: value["status"] == "ready"
        and value["recommendation"]["demand"]["unitsPerDay"] == 18.125)
    assert refreshed["recommendation"]["sellableStock"] == 35
    supplier_path = root + "/suppliers/REPL-01"
    supplier = {"name": "Тест пополнения", "leadTimeDays": 1}
    request(supplier_path, {**supplier, "expectedVersion": 1, "active": False}, staff=admin, method="PUT")
    unavailable = wait_snapshot(snapshot, lambda value: value["status"] == "unavailable")
    assert unavailable["errorCode"] == "supplier_inactive" and unavailable["recommendation"] is None
    request(supplier_path, {**supplier, "expectedVersion": 2, "active": True}, staff=admin, method="PUT")
    recovered = wait_snapshot(snapshot, lambda value: value["status"] == "ready")
    assert recovered["errorCode"] is None and recovered["recommendation"]["sellableStock"] == 35
    print("PASS: forecast, recheck at approval, daily and inventory refresh, scoped snapshots and recovery")


def check_sales_period():
    admin = "admin-replenishment"
    scope = f"/v1/reports/stores/{fixture_id(11)}/sales"
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    first = today - datetime.timedelta(days=15)
    second = first + datetime.timedelta(days=1)
    third = second + datetime.timedelta(days=1)
    period = f"?from={first}&to={third}"
    filter_b = period + f"&registerId={fixture_id(22)}&cashierId={fixture_id(32)}"
    spec = request("/openapi/v1.json")
    paths = spec["paths"]
    route = paths["/v1/reports/stores/{storeId}/sales"]["get"]
    assert set(route["security"][0]) == {"Bearer"}
    csv_route = paths["/v1/reports/stores/{storeId}/sales/csv"]["get"]
    assert set(csv_route["security"][0]) == {"Bearer"} and "text/csv" in csv_route["responses"]["200"]["content"]

    request(scope + period, staff=None, expected=401)
    request(scope + period, staff="receiver", expected=403)
    request(scope + period, staff=admin, mfa=False, expected=403)
    request(scope + period, staff="admin-foreign", expected=404)
    request(scope + "/csv" + filter_b, staff="cashier-a", expected=403)
    request(scope + "/csv" + filter_b, staff="admin-foreign", expected=404)
    request(scope + "/csv" + period + f"&registerId={fixture_id(23)}", staff=admin, expected=404)
    request(scope + period + f"&cashierId={fixture_id(35)}", staff=admin, expected=404)
    request(scope, staff=admin, expected=400)
    request(scope + f"?from={second}&to={first}", staff=admin, expected=400)
    request(scope + f"?from={first}&to={first + datetime.timedelta(days=93)}", staff=admin, expected=400)
    request(scope + period + f"&registerId={fixture_id(0)}", staff=admin, expected=400)

    report = request(scope + period, staff=admin)
    assert report["storeId"] == fixture_id(11) and report["timeZone"] == "Asia/Tashkent"
    assert report["from"] == str(first) and report["to"] == str(third)
    assert [row["date"] for row in report["days"]] == [str(first), str(second), str(third)]
    earlier, later, empty = [row["amounts"] for row in report["days"]]
    assert earlier["grossSales"] == 150 and earlier["discounts"] == 30 and earlier["cashSales"] == 120
    assert earlier["saleCount"] == 1 and earlier["refundCount"] == 0 and earlier["averageCheck"] == 120
    assert later["manualCashlessSales"] == 50 and later["cashRefunds"] == 120 and later["netSales"] == -70
    assert later["netCash"] == -120 and later["netManualCashless"] == 50
    assert later["cashIn"] == 500 and later["cashOut"] == 75 and later["averageCheck"] == 50
    assert empty["netSales"] == 0 and empty["averageCheck"] is None
    total = report["total"]
    assert total["saleCount"] == 2 and total["refundCount"] == 1 and total["averageCheck"] == 85
    assert total["grossSales"] == 200 and total["sales"] == 170 and total["netSales"] == 50
    assert total["refunds"] == 120 and total["netCash"] == 0
    one_day = request(scope + f"?from={first}&to={first}", staff=admin)
    assert one_day["total"] == earlier  # Midnight of the next day is excluded from a one-day window.
    foreign = request(scope.replace(fixture_id(11), fixture_id(12)) + period, staff="admin-foreign")
    assert foreign["total"]["saleCount"] == foreign["total"]["refundCount"] == 0

    register_b = request(scope + filter_b, staff=admin)
    assert register_b["registerId"] == fixture_id(22) and register_b["cashierId"] == fixture_id(32)
    assert register_b["total"] == later
    opposite = request(scope + period + f"&registerId={fixture_id(22)}&cashierId={fixture_id(31)}", staff=admin)
    assert opposite["total"] == empty

    payload, headers = request(scope + "/csv" + filter_b, staff=admin, raw_response=True)
    assert payload.startswith(b"\xef\xbb\xbf")
    assert headers.get_content_type() == "text/csv" and "attachment" in headers["Content-Disposition"]
    assert headers["Cache-Control"] == "no-store" and headers["X-Content-Type-Options"] == "nosniff"
    assert headers["X-Request-Id"]
    rows = list(csv.DictReader(io.StringIO(payload.decode("utf-8-sig"))))
    assert len(rows) == 4 and rows[-1]["row_type"] == "total"
    assert rows[1]["date"] == str(second) and rows[1]["time_zone"] == "Asia/Tashkent"
    assert rows[1]["register_id"] == fixture_id(22) and rows[1]["cashier_id"] == fixture_id(32)
    assert rows[1]["cash_refunds"] == "120.00" and rows[-1]["net_sales"] == "-70.00"
    assert rows[-1]["cash_in"] == "500.00" and rows[-1]["average_check"] == "50.00"
    print("PASS: sales period, local midnight, discounts, cross-register refunds, scope and CSV")


def check_profit_report():
    admin = "admin-replenishment"
    route = f"/v1/reports/stores/{fixture_id(11)}/profit"
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    first, second, third, fourth = (today - datetime.timedelta(days=n) for n in (65, 64, 63, 62))
    period = f"?from={first}&to={fourth}"
    spec = request("/openapi/v1.json")["paths"]
    api_path = "/v1/reports/stores/{storeId}/profit"
    assert set(spec[api_path]["get"]["security"][0]) == {"Bearer"}
    assert "text/csv" in spec[api_path + "/csv"]["get"]["responses"]["200"]["content"]
    request(route + period, staff=None, expected=401)
    request(route + period, staff="receiver", expected=403)
    request(route + period, staff=admin, mfa=False, expected=403)
    request(route + period, staff="admin-foreign", expected=404)
    request(route + "/csv" + period, staff="cashier-a", expected=403)
    request(route + "/csv" + period, staff="admin-foreign", expected=404)
    request(route, staff=admin, expected=400)
    request(route + f"?from={second}&to={first}", staff=admin, expected=400)
    request(route + f"?from={first}&to={first + datetime.timedelta(days=93)}", staff=admin, expected=400)

    report = request(route + period, staff=admin)
    assert report["storeId"] == fixture_id(11) and report["timeZone"] == "Asia/Tashkent"
    assert report["costMethod"] == "movingWeightedAverageByLot" and report["generatedAt"]
    assert [row["date"] for row in report["days"]] == [str(day) for day in (first, second, third, fourth)]
    earlier, later, unknown_loss, empty = (row["amounts"] for row in report["days"])
    assert earlier["sales"] == 200 and earlier["costOfGoodsSold"] == 100 and earlier["grossMarginPercent"] == 50
    assert earlier["grossProfit"] == 100 and earlier["inventoryExpenses"] == 9.50
    assert later["sales"] == 40 and later["refunds"] == 50 and later["netRevenue"] == -10
    assert later["knownRefundCost"] == 25 and later["costOfGoodsSold"] == -13
    assert later["grossProfit"] == 3 and later["grossMarginPercent"] is None
    assert later["inventoryExpenses"] == 2.50
    assert unknown_loss["inventoryExpenses"] is None and unknown_loss["unvaluedExpenseLineCount"] == 1
    assert empty["costOfGoodsSold"] == empty["grossProfit"] == empty["inventoryExpenses"] == 0
    assert empty["grossMarginPercent"] is None
    total = report["total"]
    assert total["netRevenue"] == 190 and total["costOfGoodsSold"] == 87
    assert total["grossProfit"] == 103 and total["grossMarginPercent"] == 54.21
    assert total["knownWriteOffCost"] == 9.50 and total["knownInventoryShortageCost"] == 2.50
    assert total["inventoryExpenses"] is None and total["unvaluedExpenseLineCount"] == 1

    legacy_from = today - datetime.timedelta(days=15)
    legacy_to = legacy_from + datetime.timedelta(days=1)
    legacy = request(route + f"?from={legacy_from}&to={legacy_to}", staff=admin)["total"]
    assert legacy["netRevenue"] == 50
    assert legacy["unvaluedSaleLineCount"] == 3 and legacy["unvaluedRefundLineCount"] == 2
    assert legacy["costOfGoodsSold"] is None and legacy["grossProfit"] is None and legacy["grossMarginPercent"] is None
    foreign = request(route.replace(fixture_id(11), fixture_id(12)) + period, staff="admin-foreign")
    assert foreign["total"] == empty

    payload, headers = request(route + "/csv" + period, staff=admin, raw_response=True)
    assert payload.startswith(b"\xef\xbb\xbf") and headers.get_content_type() == "text/csv"
    assert headers["Cache-Control"] == "no-store" and headers["X-Content-Type-Options"] == "nosniff"
    rows = list(csv.DictReader(io.StringIO(payload.decode("utf-8-sig"))))
    assert len(rows) == 5 and rows[-1]["row_type"] == "total"
    assert rows[-1]["gross_margin_percent"] == "54.21" and rows[1]["gross_margin_percent"] == ""
    assert rows[-1]["time_zone"] == "Asia/Tashkent" and rows[-1]["access_role"] == "admin"
    assert rows[-1]["inventory_expenses"] == "" and rows[-1]["unvalued_expense_line_count"] == "1"
    print("PASS: profit report, mixed cost coverage, local day boundaries, stock expenses, access and CSV")


def check_stock_expiry_report():
    admin = "admin-replenishment"
    route = f"/v1/reports/stores/{fixture_id(11)}/stock-expiry"
    csv_route = route + "/csv"
    spec = request("/openapi/v1.json")["paths"]
    assert "text/csv" in spec["/v1/reports/stores/{storeId}/stock-expiry/csv"]["get"]["responses"]["200"]["content"]
    for staff, status in ((None, 401), ("cashier-a", 403), ("receiver", 403), ("admin-foreign", 404)):
        request(route, staff=staff, expected=status)
        request(csv_route, staff=staff, expected=status)
    request(csv_route, staff=admin, mfa=False, expected=403)
    request(csv_route + "?filter=bad", staff=admin, expected=400)
    assert request(route, staff=admin)["filter"] == "within7"
    assert all(row["daysLeft"] == 0 for row in request(route + "?filter=today", staff=admin)["items"])
    expired = request(route + "?filter=expired", staff=admin)["items"]
    assert expired and all(row["daysLeft"] < 0 for row in expired)
    missing = request(route + "?filter=missing", staff=admin)["items"]
    assert missing and all(row["expiryDate"] is None and row["daysLeft"] is None for row in missing)
    full = request(route + "?filter=all", staff=admin)
    assert full["timeZone"] == "Asia/Tashkent" and full["filter"] == "all"
    assert any(row["inventoryValue"] is not None for row in full["items"])
    assert any(row["inventoryValue"] is None for row in full["items"])
    assert all(row["stockQuantity"] + row["quarantineQuantity"] > 0 for row in full["items"])
    assert all("inventoryValue" not in row for row in request("/v1/expiry/cashier?filter=all"))
    payload, headers = request(csv_route + "?filter=all", staff=admin, raw_response=True)
    assert payload.startswith(b"\xef\xbb\xbf") and headers.get_content_type() == "text/csv"
    assert headers["X-Content-Type-Options"] == "nosniff" and headers["Cache-Control"] == "no-store"
    rows = list(csv.DictReader(io.StringIO(payload.decode("utf-8-sig"))))
    assert len(rows) == len(full["items"])
    for row, entry in zip(rows, full["items"]):
        assert row["lot_id"] == entry["lotId"] and row["filter"] == "all"
        assert row["time_zone"] == full["timeZone"]
        assert (row["expiry_date"] or None) == entry["expiryDate"]
        assert (int(row["days_left"]) if row["days_left"] else None) == entry["daysLeft"]
        assert float(row["stock_quantity"]) == entry["stockQuantity"]
        assert float(row["quarantine_quantity"]) == entry["quarantineQuantity"]
        assert (float(row["inventory_value"]) if row["inventory_value"] else None) == entry["inventoryValue"]
    foreign = route.replace(fixture_id(11), fixture_id(12))
    assert request(foreign + "?filter=all", staff="admin-foreign")["nextOffset"] == 50
    assert request(foreign + "/csv?filter=all", staff="admin-foreign", expected=409)["code"] == "stock_expiry_export_limit"
    print("PASS: expiry report filters, inventory values, cashier privacy, CSV and export limit")


def check_stock_losses():
    admin = "admin-replenishment"
    scope = f"/v1/reports/stores/{fixture_id(11)}/stock-losses"
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    first, second, third = (today - datetime.timedelta(days=n) for n in (65, 64, 63))
    period = f"?from={first}&to={third}"
    product = fixture_id(41)
    api_path = "/v1/reports/stores/{storeId}/stock-losses"
    spec = request("/openapi/v1.json")["paths"]
    assert set(spec[api_path]["get"]["security"][0]) == {"Bearer"}
    assert "text/csv" in spec[api_path + "/csv"]["get"]["responses"]["200"]["content"]
    request(scope + period, staff=None, expected=401)
    request(scope + period, staff="receiver", expected=403)
    request(scope + period, staff=admin, mfa=False, expected=403)
    request(scope + period, staff="admin-foreign", expected=404)
    for staff, status in (("cashier-a", 403), ("receiver", 403), ("admin-foreign", 404)):
        request(scope + "/csv" + period, staff=staff, expected=status)
    request(scope + period + "&after=bad", staff=admin, expected=400)
    request(scope + period + "&limit=101", staff=admin, expected=400)
    request(scope + period + "&productId=" + fixture_id(242), staff=admin, expected=404)

    report = request(scope + period, staff=admin)
    assert report["timeZone"] == "Asia/Tashkent"
    entries = report["items"]
    assert [(row["documentId"], row["kind"], row["quantity"], row["cost"], row["localDate"]) for row in entries] == [
        (fixture_id(986), "writeOffStock", 1, None, str(third)),
        (fixture_id(985), "inventory", 0.1, 2.5, str(second)),
        (fixture_id(984), "writeOffQuarantine", 1, 9.5, str(first)),
    ]
    assert request(scope + f"?from={first}&to={first}", staff=admin)["items"] == entries[-1:]
    first_page = request(scope + period + "&limit=1", staff=admin)
    assert first_page["items"] == entries[:1] and first_page["nextCursor"]
    second_page = request(scope + period + "&after=" + first_page["nextCursor"], staff=admin)
    assert second_page["items"] == entries[1:] and second_page["nextCursor"] is None
    assert request(scope + period + "&productId=" + product, staff=admin)["items"] == [entries[0], entries[2]]
    payload, headers = request(scope + "/csv" + period, staff=admin, raw_response=True)
    assert payload.startswith(b"\xef\xbb\xbf") and headers.get_content_type() == "text/csv"
    assert headers["Cache-Control"] == "no-store" and headers["X-Content-Type-Options"] == "nosniff"
    rows = list(csv.DictReader(io.StringIO(payload.decode("utf-8-sig"))))
    assert len(rows) == len(entries)
    for row, entry in zip(rows, entries):
        assert row["document_id"] == entry["documentId"] and row["lot_id"] == entry["lotId"]
        assert row["reason"] == entry["reason"] and row["access_role"] == "admin"
        assert float(row["quantity"]) == entry["quantity"]
        assert (float(row["cost"]) if row["cost"] else None) == entry["cost"]
    foreign_date = today - datetime.timedelta(days=35)
    foreign_scope = scope.replace(fixture_id(11), fixture_id(12))
    error = request(foreign_scope + f"/csv?from={foreign_date}&to={foreign_date}", staff="admin-foreign", expected=409)
    assert error["code"] == "stock_loss_export_limit"
    print("PASS: stock loss filters, net shortages, local day, cursor, permissions, CSV and export limit")


def check_stock_movement_report():
    admin = "admin-replenishment"
    store = fixture_id(11)
    scope = f"/v1/reports/stores/{store}/stock-movements"
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    current = f"?from={today}&to={today}"
    first = today - datetime.timedelta(days=15)
    second = first + datetime.timedelta(days=1)
    past = f"?from={first}&to={second}"
    product = fixture_id(41)
    spec = request("/openapi/v1.json")["paths"]
    api_path = "/v1/reports/stores/{storeId}/stock-movements"
    assert set(spec[api_path]["get"]["security"][0]) == {"Bearer"}
    assert "text/csv" in spec[api_path + "/csv"]["get"]["responses"]["200"]["content"]
    request(scope + past, staff=None, expected=401)
    request(scope + past, staff="receiver", expected=403)
    request(scope + past, staff=admin, mfa=False, expected=403)
    request(scope + past, staff="admin-foreign", expected=404)
    request(scope + "/csv" + past, staff="cashier-a", expected=403)
    request(scope + "/csv" + past, staff="admin-foreign", expected=404)
    request(scope + past + "&limit=101", staff=admin, expected=400)
    request(scope + past + "&after=invalid", staff=admin, expected=400)
    request(scope + past + "&kind=madeUp", staff=admin, expected=400)
    request(scope + past + f"&lotId={fixture_id(51)}", staff=admin, expected=400)
    request(scope + past + f"&productId={fixture_id(242)}", staff=admin, expected=404)

    historical = request(scope + past, staff=admin)
    assert historical["timeZone"] == "Asia/Tashkent" and historical["generatedAt"]
    expected = historical["items"]
    assert len(expected) == 5  # Two sales and a refund, with five movement lines across two local days.
    sale_first = [row for row in expected if row["documentId"] == fixture_id(942)]
    assert len(sale_first) == 2 and all(row["localDate"] == str(first) for row in sale_first)
    assert sorted(row["stockDelta"] for row in sale_first) == [-0.6, -0.3]
    returned = [row for row in expected if row["kind"] == "customerRefund"]
    assert len(returned) == 2 and all(row["localDate"] == str(second) for row in returned)
    assert all(row["registerId"] == fixture_id(22) and row["sourceRegisterId"] == fixture_id(21)
               and row["sourceDocumentId"] == fixture_id(942) for row in returned)
    pages = []
    cursor = None
    for _ in range(len(expected) + 1):
        query = past + "&limit=1" + ("&after=" + cursor if cursor else "")
        page = request(scope + query, staff=admin)
        pages.extend(page["items"])
        cursor = page["nextCursor"]
        if cursor is None:
            break
    assert cursor is None and pages == expected

    def lot_report(lot, kind=None):
        query = current + f"&productId={product}&lotId={fixture_id(lot)}" + ("&kind=" + kind if kind else "")
        return request(scope + query, staff=admin)["items"]

    priced = lot_report(960)
    received = next(row for row in priced if row["kind"] == "receipt")
    assert received["stockDelta"] == 18 and received["receivingCost"] == 18000
    supplier_returns = [row for row in priced if row["kind"] == "supplierReturn"]
    assert sorted(row["supplierCredit"] for row in supplier_returns) == [2000, 10000]
    assert sorted(row["stockDelta"] for row in supplier_returns) == [-10, -2]
    assert all(row["sourceDocumentId"] == received["documentId"] for row in supplier_returns)
    unpriced = lot_report(961)
    assert len(unpriced) == 2 and all(row["supplierCredit"] is None and row["receivingCost"] is None for row in unpriced)

    quarantine = lot_report(94, "quarantine")
    assert len(quarantine) == 1 and quarantine[0]["stockDelta"] == -2
    assert quarantine[0]["quarantineDelta"] == 2 and quarantine[0]["totalDelta"] == 0
    corrections = lot_report(94, "inventory")
    assert len(corrections) == 1 and corrections[0]["totalDelta"] == -1
    disposal = lot_report(94, "writeOff")
    assert len(disposal) == 1 and disposal[0]["kind"] == "writeOffQuarantine"
    assert disposal[0]["quarantineDelta"] == -2 and disposal[0]["receivingCost"] is None

    reason = '=HYPERLINK("https://example.invalid","проверка")'
    command = {"operationId": str(uuid.uuid4()), "kind": "writeOffStock", "productId": product,
               "lotId": fixture_id(52), "quantity": 1, "reason": reason}
    ops = f"/v1/stores/{store}/stock/operations"
    writeoff = request(ops, command, staff=admin)
    assert request(ops, command, staff=admin) == writeoff
    expiry_rows = lot_report(52, "writeOff")
    assert len(expiry_rows) == 2 and sum(row["documentId"] == command["operationId"] for row in expiry_rows) == 1
    export_filter = current + f"&productId={product}&lotId={fixture_id(52)}&kind=writeOff"
    payload, headers = request(scope + "/csv" + export_filter, staff=admin, raw_response=True)
    assert headers.get_content_type() == "text/csv" and "attachment" in headers["Content-Disposition"]
    assert headers["Cache-Control"] == "no-store" and headers["X-Content-Type-Options"] == "nosniff"
    csv_rows = list(csv.DictReader(io.StringIO(payload.decode("utf-8-sig"))))
    assert len(csv_rows) == 2
    exported = next(row for row in csv_rows if row["document_id"] == command["operationId"])
    assert exported["reason"] == "'" + reason and exported["stock_delta"] == "-1.000"
    assert exported["filter_kind"] == "writeOff" and exported["filter_lot_id"] == fixture_id(52)

    foreign = scope.replace(store, fixture_id(12))
    bulk_day = today - datetime.timedelta(days=35)
    bulk_period = f"?from={bulk_day}&to={bulk_day}"
    assert request(foreign + "/csv" + bulk_period, staff="admin-foreign", expected=409)["code"] == "stock_movement_export_limit"
    bulk = request(foreign + bulk_period + "&limit=100", staff="admin-foreign")
    assert len(bulk["items"]) == 100 and bulk["nextCursor"]
    assert request(scope + bulk_period, staff=admin)["items"] == []
    print("PASS: stock movement report, cross-register refund, cursor ties, costs, bounded and safe CSV")


def check_supplier_invoice_settlements():
    admin = "admin-settlements"
    store = f"/v1/stores/{fixture_id(11)}"
    base = store + "/procurement/suppliers/PROC-01"
    invoices = base + "/invoices"
    allocations = base + "/payment-allocations"
    payments = base + "/payments"
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    yesterday = today - datetime.timedelta(days=1)
    invoice_day = today - datetime.timedelta(days=2)
    expiry = (today + datetime.timedelta(days=30)).isoformat()
    product = fixture_id(41)
    spec = request("/openapi/v1.json")["paths"]
    operation = spec["/v1/stores/{storeId}/procurement/suppliers/{code}/payment-allocations"]["post"]
    assert set(operation["security"][0]) == {"Bearer"} and "409" in operation["responses"]

    def receive(number, lot, quantity, priced=True):
        receipt_id = str(uuid.uuid4())
        body = {"receiptId": receipt_id, "supplierRef": "PROC-01", "invoiceNumber": number,
                "invoiceDate": str(invoice_day), "items": [receipt_item(lot, quantity, expiry)]}
        path = store + "/receipts/" + receipt_id
        request(store + "/receipts", body, staff="receiver")
        request(path + "/submit", {"expectedVersion": 1}, staff="receiver")
        if priced:
            request(path + "/costs", {"expectedVersion": 2, "items": [
                {"productId": product, "lotId": fixture_id(lot), "unitPrice": 10}]}, staff=admin, method="PUT")
        request(path + "/post", {"expectedVersion": 3 if priced else 2}, staff=admin)
        return receipt_id

    invoice_a = receive("ALLOC-A", 971, 10)
    invoice_b = receive("ALLOC-B", 972, 8)
    unknown = receive("ALLOC-UNKNOWN", 973, 1, priced=False)
    path_a = invoices + "/" + invoice_a
    path_b = invoices + "/" + invoice_b
    terms_path = path_a + "/due-date"
    assert request(path_a, staff=admin)["terms"] == {"dueDate": None, "version": 0}
    due = {"expectedVersion": 0, "dueDate": str(yesterday)}
    request(terms_path, due, staff="receiver", method="PUT", expected=403)
    request(terms_path, {**due, "dueDate": str(invoice_day - datetime.timedelta(days=1))},
            staff=admin, method="PUT", expected=400)
    saved_due = request(terms_path, due, staff=admin, method="PUT")
    assert request(terms_path, due, staff=admin, method="PUT") == saved_due
    request(terms_path, {**due, "dueDate": str(today)}, staff=admin, method="PUT", expected=409)
    request(path_b + "/due-date", {"expectedVersion": 0, "dueDate": str(today)}, staff=admin, method="PUT")
    assert request(path_a, staff=admin)["isOverdue"] and not request(path_b, staff=admin)["isOverdue"]
    assert request(invoices + "/" + unknown, staff=admin)["status"] == "unpriced"
    assert {row["receiptId"] for row in request(invoices, staff=admin)} >= {invoice_a, invoice_b, unknown}
    request(invoices, staff=None, expected=401)
    request(path_a, staff="cashier-a", expected=403)
    request(path_a, staff="admin-foreign", expected=404)
    request(path_a, staff=admin, mfa=False, expected=403)

    def payment(reference, amount):
        body = {"operationId": str(uuid.uuid4()), "amount": amount, "paymentDate": str(today),
                "reference": reference}
        return request(payments, body, staff=admin)["operationId"]

    def available(payment_id):
        return request(payments + "/" + payment_id + "/allocation-balance", staff=admin)["unallocatedAmount"]

    def allocation(payment_id, *lines):
        return {"operationId": str(uuid.uuid4()), "paymentId": payment_id,
                "items": [{"receiptId": receipt, "amount": amount} for receipt, amount in lines]}

    prior = next(row for row in request(payments, staff=admin) if row["reference"] == "Платёж 001")
    assert available(prior["operationId"]) == 5000
    advance = payment("Предоплата для двух накладных", 130)
    other = payment("Вторая оплата", 100)
    assert available(advance) == 130
    supplier_balance = request(base + "/balance", staff=admin)
    command = allocation(advance, (invoice_a, 60), (invoice_b, 40))
    request(allocations, command, staff="receiver", expected=403)
    initial = request(allocations, command, staff=admin)
    assert request(allocations, {**command, "items": command["items"][::-1]}, staff=admin) == initial
    allocation_path = allocations + "/" + initial["operationId"]
    assert request(allocation_path, staff=admin) == initial
    request(allocation_path, staff="admin-foreign", expected=404)
    changed = {**command, "items": [{**command["items"][0], "amount": 59}, command["items"][1]]}
    assert request(allocations, changed, staff=admin, expected=409)["code"] == "supplier_allocation_id_reused"
    assert available(advance) == 30
    assert request(path_a, staff=admin)["status"] == "partiallyPaid"
    assert request(path_a, staff=admin)["outstandingAmount"] == 40
    assert request(base + "/balance", staff=admin) == supplier_balance

    def failed(body, error, status=409):
        assert request(allocations, body, staff=admin, expected=status)["code"] == error

    failed(allocation(advance, (invoice_a, 31)), "supplier_payment_allocation_exceeded")
    failed(allocation(other, (invoice_a, 20), (invoice_b, 60)), "supplier_invoice_allocation_exceeded")
    failed(allocation(other, (unknown, 1)), "supplier_invoice_unpriced")
    foreign_invoice = request(base.replace("PROC-01", "REPL-01") + "/invoices", staff=admin)[0]["receiptId"]
    failed(allocation(other, (foreign_invoice, 1)), "supplier_invoice_not_found", 404)
    assert available(other) == 100 and request(path_a, staff=admin)["allocatedAmount"] == 60

    def race(body, conflict):
        try:
            request(allocations, body, staff=admin)
            return 200
        except AssertionError as error:
            assert error.args[0][2] == 409 and conflict.encode() in error.args[0][3], error
            return 409

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        statuses = list(pool.map(lambda receipt: race(allocation(advance, (receipt, 20)),
                                                     "supplier_payment_allocation_exceeded"), [invoice_a, invoice_b]))
    assert sorted(statuses) == [200, 409] and available(advance) == 10

    reverse = {"operationId": str(uuid.uuid4()), "reason": "Исправление распределения"}
    reversal = request(allocation_path + "/reverse", reverse, staff=admin)
    assert request(allocation_path + "/reverse", reverse, staff=admin) == reversal
    assert request(allocation_path + "/reverse", {**reverse, "operationId": str(uuid.uuid4())},
                   staff=admin, expected=409)["code"] == "supplier_allocation_already_reversed"
    assert available(advance) == 110
    history = request(path_a + "/allocations", staff=admin)
    assert any(row["operationId"] == initial["operationId"] and row["reversalId"] == reversal["operationId"]
               and row["amount"] == 60 and row["reversalReason"] == reverse["reason"] for row in history)

    amount = request(path_b, staff=admin)["outstandingAmount"] - 1
    third_payment = payment("Третья оплата", 100)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        statuses = list(pool.map(lambda payment_id: race(allocation(payment_id, (invoice_b, amount)),
            "supplier_invoice_allocation_exceeded"), [other, third_payment]))
    assert sorted(statuses) == [200, 409]
    assert request(path_b, staff=admin)["outstandingAmount"] == 1
    assert sorted([available(other), available(third_payment)]) == [100 - amount, 100]

    current = request(path_a, staff=admin)["outstandingAmount"]
    backing = request(allocations, allocation(advance, (invoice_a, current)), staff=admin)
    settled = request(path_a, staff=admin)
    assert settled["status"] == "settled" and not settled["isOverdue"] and settled["balance"] == 0
    request(store + "/procurement/returns", {"returnId": str(uuid.uuid4()), "receiptId": invoice_a,
            "reason": "Возврат после оплаты", "items": [
                {"productId": product, "lotId": fixture_id(971), "quantity": 1, "bucket": "stock"}]}, staff=admin)
    credited = request(path_a, staff=admin)
    assert credited["status"] == "overpaid" and credited["overpaidAmount"] == 10 and credited["returnCredit"] == 10
    assert request(terms_path, {"expectedVersion": 1, "dueDate": None}, staff=admin, method="PUT") == {
        "version": 2, "dueDate": None}
    print("PASS: supplier invoice deadlines, advances, atomic allocations, races, reversal and credit")

    credit_transfers = base + "/credit-transfers"
    credit_route = spec["/v1/stores/{storeId}/procurement/suppliers/{code}/credit-transfers"]["post"]
    assert set(credit_route["security"][0]) == {"Bearer"} and "409" in credit_route["responses"]
    invoice_c = receive("CREDIT-C", 974, 3)
    invoice_d = receive("CREDIT-D", 975, 4)

    def read_invoice(invoice):
        return request(invoices + "/" + invoice, staff=admin)

    def transfer(source, target, amount):
        return {"operationId": str(uuid.uuid4()), "sourceReceiptId": source, "targetReceiptId": target,
                "amount": amount, "reason": "Зачёт переплаты по возврату"}

    request(credit_transfers, transfer(invoice_a, invoice_c, 3), staff="receiver", expected=403)
    request(credit_transfers, transfer(invoice_a, invoice_c, 3), staff="admin-foreign", expected=404)
    request(credit_transfers, transfer(invoice_a, invoice_a, 1), staff=admin, expected=400)
    request(credit_transfers, transfer(invoice_a, invoice_c, 1.001), staff=admin, expected=400)
    request(credit_transfers, transfer(invoice_a, foreign_invoice, 1), staff=admin, expected=404)
    assert request(credit_transfers, transfer(invoice_a, unknown, 1), staff=admin, expected=409)["code"] == "supplier_invoice_unpriced"
    assert request(credit_transfers, transfer(invoice_a, invoice_c, 11), staff=admin, expected=409)["code"] == "supplier_credit_transfer_exceeded"
    assert request(credit_transfers, transfer(invoice_a, invoice_b, 2), staff=admin, expected=409)["code"] == "supplier_credit_transfer_exceeds_target_debt"
    assert read_invoice(invoice_a)["overpaidAmount"] == 10 and read_invoice(invoice_c)["outstandingAmount"] == 30
    supplier_balance = request(base + "/balance", staff=admin)
    payment_balance = available(advance)
    command = transfer(invoice_a, invoice_c, 3)
    applied = request(credit_transfers, command, staff=admin)
    assert request(credit_transfers, command, staff=admin) == applied
    transfer_path = credit_transfers + "/" + applied["operationId"]
    assert request(transfer_path, staff=admin) == applied
    request(transfer_path, staff="admin-foreign", expected=404)
    request(transfer_path, staff=admin, mfa=False, expected=403)
    assert request(credit_transfers, {**command, "reason": "Изменённая причина"},
                   staff=admin, expected=409)["code"] == "supplier_credit_transfer_id_reused"
    assert read_invoice(invoice_a)["creditSentAmount"] == 3 and read_invoice(invoice_a)["overpaidAmount"] == 7
    target = read_invoice(invoice_c)
    assert target["creditReceivedAmount"] == 3 and target["outstandingAmount"] == 27
    assert target["status"] == "partiallyPaid" and target["allocatedAmount"] == 0
    assert request(base + "/balance", staff=admin) == supplier_balance and available(advance) == payment_balance

    def compete(target):
        try:
            request(credit_transfers, transfer(invoice_a, target, 5), staff=admin)
            return 200
        except AssertionError as error:
            assert error.args[0][2] == 409 and b"supplier_credit_transfer_exceeded" in error.args[0][3], error
            return 409

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        assert sorted(pool.map(compete, [invoice_c, invoice_d])) == [200, 409]
    assert read_invoice(invoice_a)["overpaidAmount"] == 2
    reverse_backing = {"operationId": str(uuid.uuid4()), "reason": "Уточнение привязки"}
    blocked = request(allocations + "/" + backing["operationId"] + "/reverse",
                      reverse_backing, staff=admin, expected=409)
    assert blocked["code"] == "supplier_credit_transfer_depends_on_settlement"
    assert available(advance) == payment_balance

    def reverse_credit(credit, body=None, expected=200):
        body = body or {"operationId": str(uuid.uuid4()), "reason": "Коррекция зачёта"}
        return request(credit_transfers + "/" + credit["operationId"] + "/reverse", body,
                       staff=admin, expected=expected)

    reverse = {"operationId": str(uuid.uuid4()), "reason": "Коррекция первого зачёта"}
    reversed_credit = reverse_credit(applied, reverse)
    assert reverse_credit(applied, reverse) == reversed_credit
    assert reverse_credit(applied, expected=409)["code"] == "supplier_credit_transfer_already_reversed"
    assert read_invoice(invoice_a)["overpaidAmount"] == 5
    for invoice in (invoice_a, invoice_c):
        log = request(invoices + "/" + invoice + "/credit-transfers", staff=admin)
        assert any(row["transfer"] == applied and row["reversal"] == reversed_credit for row in log)

    # A received credit can become the basis of another transfer after a return.
    invoice_e = receive("CREDIT-E", 976, 2)
    invoice_f = receive("CREDIT-F", 977, 1)
    inbound = request(credit_transfers, transfer(invoice_a, invoice_e, 5), staff=admin)
    request(allocations, allocation(prior["operationId"], (invoice_e, 15)), staff=admin)
    request(store + "/procurement/returns", {"returnId": str(uuid.uuid4()), "receiptId": invoice_e,
            "reason": "Возврат после зачёта", "items": [
                {"productId": product, "lotId": fixture_id(976), "quantity": 1, "bucket": "stock"}]}, staff=admin)
    outbound = request(credit_transfers, transfer(invoice_e, invoice_f, 10), staff=admin)
    assert read_invoice(invoice_f)["status"] == "settled"
    reverse_inbound = {"operationId": str(uuid.uuid4()), "reason": "Исправление цепочки"}
    assert reverse_credit(inbound, reverse_inbound, expected=409)["code"] == "supplier_credit_transfer_depends_on_settlement"
    reverse_credit(outbound)
    reverse_credit(inbound, reverse_inbound)
    assert read_invoice(invoice_a)["overpaidAmount"] == 5 and read_invoice(invoice_e)["overpaidAmount"] == 5
    assert read_invoice(invoice_f)["outstandingAmount"] == 10
    print("PASS: supplier credit transfer, invoice limits, concurrent spend, dependent reversal and audit history")


def check_supplier_settlement_report():
    admin = "admin-inventory"
    store = fixture_id(11)
    route = f"/v1/reports/stores/{store}/supplier-settlements"
    source = f"/v1/stores/{store}/procurement/suppliers/PROC-01"
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    spec = request("/openapi/v1.json")["paths"]
    assert set(spec["/v1/reports/stores/{storeId}/supplier-settlements"]["get"]["security"][0]) == {"Bearer"}
    for staff, status in ((None, 401), ("cashier-b", 403), ("receiver", 403), ("admin-foreign", 404)):
        request(route, staff=staff, expected=status)
    request(route, staff=admin, mfa=False, expected=403)
    request(route + "?offset=-1", staff=admin, expected=400)
    request(route + "?supplierCode=bad%20code", staff=admin, expected=400)

    def collect(path):
        result = []
        while True:
            page = request(path + f"?offset={len(result)}", staff=admin)
            result += page
            if len(page) < 25:
                return result

    invoices = collect(source + "/invoices")
    for number, delta in (("ALLOC-B", 1), ("CREDIT-C", -1)):
        row = next(invoice for invoice in invoices if invoice["invoiceNumber"] == number)
        assert row["outstandingAmount"] > 0
        terms = {"expectedVersion": row["terms"]["version"], "dueDate": str(today + datetime.timedelta(days=delta))}
        request(source + "/invoices/" + row["receiptId"] + "/due-date", terms, staff=admin, method="PUT")
    invoices = collect(source + "/invoices")
    current = request(source + "/balance", staff=admin)
    payments = collect(source + "/payments")
    advances = [request(source + "/payments/" + p["operationId"] + "/allocation-balance", staff=admin)["unallocatedAmount"]
                for p in payments]
    summary = request(route + "?supplierCode=proc-01", staff=admin)
    assert summary["storeId"] == store and summary["timeZone"] == "Asia/Tashkent"
    assert summary["asOf"] == str(today) and summary["supplierCode"] == "PROC-01" and summary["nextOffset"] is None
    row = summary["items"][0]
    for field in ("invoicedAmount", "returnCredit", "paidAmount"):
        assert row[field] == current[field]
    assert row["knownLedgerBalance"] == current["balance"]
    assert row["unpricedInvoiceCount"] == current["unpricedReceipts"] > 0
    assert row["unpricedReturnCount"] == current["unpricedReturns"] and row["ledgerBalance"] is None
    assert row["unallocatedAdvance"] == round(sum(advances), 2)
    assert row["knownOutstandingAmount"] == round(sum(i["outstandingAmount"] or 0 for i in invoices), 2)
    assert row["knownOverdueAmount"] == round(sum(i["outstandingAmount"] for i in invoices if i["isOverdue"]), 2) > 0
    assert row["unpricedSettlementCount"] == sum(i["outstandingAmount"] is None for i in invoices)
    assert row["outstandingAmount"] is None
    assert row in request(route, staff=admin)["items"]
    assert request(route + "?supplierCode=PROC-01&offset=1", staff=admin)["items"] == []
    assert request(route.replace(store, fixture_id(12)) + "?supplierCode=PROC-01", staff="admin-foreign")["items"] == []
    print("PASS: supplier settlement summary, existing ledger parity, advances, unknown amounts, deadlines and scope")


def check_opening_stock():
    admin = "admin-settlements"
    store_id = fixture_id(11)
    store = f"/v1/stores/{store_id}"
    stock = store + "/stock"
    opening = stock + "/opening-balances"
    product, weighed, untouched, receiving_product = (fixture_id(n) for n in (2401, 2402, 2403, 2404))
    lot, quarantine_lot, weighed_lot = (fixture_id(n) for n in (2411, 2412, 2413))
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    future = str(today + datetime.timedelta(days=30))
    expired = str(today - datetime.timedelta(days=1))

    spec = request("/openapi/v1.json")["paths"]
    api_path = "/v1/stores/{storeId}/stock/opening-balances"
    assert set(spec[api_path + "/preview"]["post"]["security"][0]) == {"Bearer"}
    assert "text/csv" in spec[api_path + "/{id}/csv"]["get"]["responses"]["200"]["content"]
    cards = ((product, "piece"), (weighed, "kg"), (untouched, "piece"), (receiving_product, "piece"))
    for number, (pid, unit) in enumerate(cards):
        request(store + "/catalog/products/" + pid, {"expectedVersion": 0, "name": f"Стартовый товар {number}",
                "unit": unit, "requiresExpiry": pid == product, "barcodes": []}, staff=admin, method="PUT")

    def item(pid, lid, quantity, expiry=None, quarantine=0, cost=None):
        return {"productId": pid, "lotId": lid, "stockQuantity": quantity, "expiryDate": expiry,
                "quarantineQuantity": quarantine, "totalCost": cost}

    def command(**input):
        return {"operationId": str(uuid.uuid4()), "sourceReference": "=Сверка",
                "reason": "Перенос остатков", **input}

    def lots(pid):
        return request(stock + "/lots?productId=" + pid, staff=admin)

    header = "productId,lotId,expiryDate,stockQuantity,quarantineQuantity,totalCost\n"
    body = command(csv=header + f"{product},{lot},{future},3,0,123.45\n"
                   f"{product},{quarantine_lot},{expired},0,2,0\n{weighed},{weighed_lot},,1.250,0,\n")
    preview_path = opening + "/preview"
    path = opening + "/" + body["operationId"]
    request(preview_path, body, staff=None, expected=401)
    request(preview_path, body, staff="receiver", expected=403)
    request(opening, body, staff="cashier-a", expected=403)
    request(opening, body, staff=admin, mfa=False, expected=403)
    request(opening, body, staff="admin-foreign", expected=404)

    preview = request(preview_path, body, staff=admin)
    assert preview["postedAt"] is None and preview["openingDate"] == str(today)
    assert preview["knownCost"] == 123.45 and preview["unpricedLineCount"] == 1
    assert len(preview["items"]) == 3 and preview["items"][1]["totalCost"] == 0
    assert lots(product) == [] and lots(weighed) == []
    request(path, staff=admin, expected=404)

    err = request(preview_path, command(csv=header + f"{product},{lot},{expired},1,0,\n"),
                  staff=admin, expected=409)
    assert err["code"] == "opening_stock_expiry_invalid" and err["row"] == 2
    err = request(preview_path, command(items=[item(product, lot, 0.5, future)]), staff=admin, expected=400)
    assert err["row"] == 1
    err = request(preview_path, command(items=[item(product, lot, 1, future)] * 2), staff=admin, expected=400)
    assert err["code"] == "duplicate_opening_stock_lot" and err["row"] == 2
    missing = command(items=[item(untouched, lot, 1), item(fixture_id(2499), lot, 1)])
    assert request(opening, missing, staff=admin, expected=404)["row"] == 2
    assert lots(untouched) == []
    request(opening + "/" + missing["operationId"], staff=admin, expected=404)
    request(stock + "/operations/" + missing["operationId"], staff=admin, expected=404)

    posted = request(opening, body, staff=admin)
    assert posted["postedAt"] and {**posted, "postedAt": None} == preview
    assert request(opening, body, staff=admin) == posted
    retry = {**body, "csv": None, "items": [item(weighed, weighed_lot, 1.25),
             item(product, quarantine_lot, 0, expired, 2, 0), item(product, lot, 3, future, cost=123.45)]}
    assert request(opening, retry, staff=admin) == posted
    assert request(opening, {**body, "reason": "Изменение"}, staff=admin, expected=409)["code"] == "opening_stock_id_reused"
    assert request(opening, command(items=[item(product, lot, 1, future)]), staff=admin,
                   expected=409)["code"] == "opening_stock_product_has_lots"
    assert request(path, staff=admin) == posted
    request(path, staff="admin-foreign", expected=404)
    request(path + "/csv", staff="receiver", expected=403)
    payload, headers = request(path + "/csv", staff=admin, raw_response=True)
    assert headers.get_content_type() == "text/csv" and headers["X-Content-Type-Options"] == "nosniff"
    rows = list(csv.DictReader(io.StringIO(payload.decode("utf-8-sig"))))
    assert len(rows) == 3 and rows[0]["source_reference"] == "'=Сверка"
    assert [row["total_cost"] for row in rows] == ["123.45", "0.00", ""]

    balances = {row["lotId"]: row for row in lots(product)}
    assert balances[lot]["stockQuantity"] == 3 and balances[quarantine_lot]["quarantineQuantity"] == 2
    assert balances[quarantine_lot]["availableQuantity"] == 0 and lots(weighed)[0]["stockQuantity"] == 1.25
    report = f"/v1/reports/stores/{store_id}/stock-movements?from={today}&to={today}&kind=opening"
    movements = {row["lotId"]: row for row in request(report, staff=admin)["items"]}
    assert movements[lot]["openingCost"] == 123.45 and movements[lot]["receivingCost"] is None
    assert movements[quarantine_lot]["quarantineDelta"] == 2 and "openingCost" not in movements[weighed_lot]

    def compete(cmd):
        try:
            request(opening, cmd, staff=admin)
            return 200
        except AssertionError as error:
            assert error.args[0][2] == 409 and b"opening_stock_product_has_lots" in error.args[0][3], error
            return 409

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        contenders = [command(items=[item(untouched, fixture_id(n), 1)]) for n in (2414, 2415)]
        assert sorted(pool.map(compete, contenders)) == [200, 409]
    assert len(lots(untouched)) == 1

    def submit_receipt(number, pid, lid):
        receipt = str(uuid.uuid4())
        request(store + "/receipts", {"receiptId": receipt, "supplierRef": "Проверка остатков",
                "invoiceNumber": number, "invoiceDate": str(today),
                "items": [receipt_item(lid, 2, future, product=pid)]}, staff="receiver")
        request(store + f"/receipts/{receipt}/submit", {"expectedVersion": 1}, staff="receiver")
        return receipt

    def post_receipt(receipt):
        return request(store + f"/receipts/{receipt}/post", {"expectedVersion": 2}, staff=admin)

    post_receipt(submit_receipt("OPENING-01", 2401, 2411))
    assert next(row for row in lots(product) if row["lotId"] == lot)["stockQuantity"] == 5
    assert request(path, staff=admin) == posted

    receipt = submit_receipt("OPENING-02", 2404, 2416)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        result = pool.submit(compete, command(items=[item(receiving_product, fixture_id(2416), 1, future)]))
        posting = pool.submit(post_receipt, receipt)
        assert posting.result()["status"] == "posted"
        expected_quantity = 3 if result.result() == 200 else 2
    assert [row["stockQuantity"] for row in lots(receiving_product)] == [expected_quantity]
    print("PASS: opening balances preview, reconciliation, atomic validation, receipt race, replay and access")


def check_scoped_inventory():
    admin = "admin-inventory"
    store = f"/v1/stores/{fixture_id(11)}"
    stock = store + "/stock"
    inventories = stock + "/inventories"
    piece, weighed, bulk = (fixture_id(n) for n in (2501, 2502, 2503))
    piece_lot, weighed_lot, new_lot = (fixture_id(n) for n in (2511, 2512, 2513))
    category = "Пересчёт тестовый"
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()

    def create(selection):
        return {"inventoryId": str(uuid.uuid4()), "reason": "Пересчёт", "scope": selection}

    def count_body(document, counts):
        return {"expectedVersion": document["version"], "items": [
            {"productId": line["productId"], "lotId": line["lotId"], "lotRevision": line["lotRevision"],
             "stockQuantity": counts[(line["productId"], line["lotId"])][0],
             "quarantineQuantity": counts[(line["productId"], line["lotId"])][1]}
            for line in document["lines"] if (line["productId"], line["lotId"]) in counts]}

    def post(path, document, expected=200):
        return request(path + "/post", {"expectedVersion": document["version"]}, staff=admin, expected=expected)

    def submit_receipt(number, lid):
        receipt = str(uuid.uuid4())
        receipt_path = store + "/receipts/" + receipt
        request(store + "/receipts", {"receiptId": receipt, "supplierRef": "Инвентаризация",
                "invoiceNumber": number, "invoiceDate": str(today), "items": [
                    receipt_item(lid, 2, str(today + datetime.timedelta(days=3)), product=2501)]}, staff="receiver")
        request(receipt_path + "/submit", {"expectedVersion": 1}, staff="receiver")
        return receipt_path

    spec = request("/openapi/v1.json")["paths"]
    api_path = "/v1/stores/{storeId}/stock/inventories/{id}/refresh"
    assert set(spec[api_path]["post"]["security"][0]) == {"Bearer"}
    for pid, unit, group in ((piece, "piece", category), (weighed, "kg", category), (bulk, "piece", "Другая")):
        request(store + "/catalog/products/" + pid, {"expectedVersion": 0, "name": "Ревизия " + pid[-4:],
                "unit": unit, "category": group, "barcodes": []}, staff=admin, method="PUT")

    def open_stock(pid, quantities):
        request(stock + "/opening-balances", {"operationId": str(uuid.uuid4()), "sourceReference": "Инвентаризация",
                "reason": "Начальный ввод", "items": [
                    {"productId": pid, "lotId": lid, "stockQuantity": quantity, "quarantineQuantity": 0}
                    for lid, quantity in quantities]}, staff=admin)

    open_stock(piece, [(piece_lot, 5)])
    open_stock(weighed, [(weighed_lot, 2.5)])
    open_stock(bulk, [(fixture_id(n), 1) for n in range(2600, 2700)])

    body = create({"kind": "category", "category": category})
    path = inventories + "/" + body["inventoryId"]
    request(inventories, body, staff=None, expected=401)
    request(inventories, body, staff="receiver", expected=403)
    request(inventories, body, staff=admin, mfa=False, expected=403)
    request(inventories, {**body, "lots": []}, staff=admin, expected=400)
    request(inventories, create({"kind": "full", "productId": piece}), staff=admin, expected=400)
    request(inventories, create({"kind": "product", "productId": fixture_id(2599)}), staff=admin, expected=404)
    assert request(inventories, create({"kind": "category", "category": "Без партий"}),
                   staff=admin, expected=409)["code"] == "inventory_scope_empty"
    draft = request(inventories, body, staff=admin)
    assert draft["scope"]["kind"] == "category" and draft["scope"]["category"] == category
    assert {row["productId"] for row in draft["lines"]} == {piece, weighed}
    assert request(inventories, body, staff=admin) == draft
    assert request(inventories, {**body, "scope": {"kind": "full"}}, staff=admin,
                   expected=409)["code"] == "inventory_id_reused"
    assert request(inventories + "?status=draft", staff=admin)[0]["scope"] == draft["scope"]
    request(path, staff="admin-foreign", expected=404)
    request(path + "/refresh", staff="receiver", method="POST", expected=403)

    first = count_body(draft, {(piece, piece_lot): (4, 0)})
    partial = request(path + "/count", first, staff=admin)
    assert partial["status"] == "draft" and partial["version"] == 2
    assert request(path + "/count", first, staff=admin) == partial
    post(path, partial, expected=409)
    second = count_body(partial, {(weighed, weighed_lot): (2.125, 0)})
    counted = request(path + "/count", second, staff=admin)
    assert counted["status"] == "counted" and counted["lines"][1]["stockDifference"] == -0.375
    assert request(path + "/count", second, staff=admin) == counted
    wrong_retry = {**first, "expectedVersion": partial["version"]}
    assert request(path + "/count", wrong_retry, staff=admin, expected=409)["code"] == "inventory_version_conflict"

    receipt_path = submit_receipt("INV-TEST", 2513)
    request(receipt_path + "/post", {"expectedVersion": 2}, staff=admin)
    assert post(path, counted, expected=409)["code"] == "inventory_scope_changed"
    request(stock + "/operations/" + draft["inventoryId"], staff=admin, expected=404)
    refreshed = request(path + "/refresh", staff=admin, method="POST")
    assert refreshed["status"] == "draft" and refreshed["version"] == counted["version"] + 1
    assert {row["lotId"] for row in refreshed["lines"]} == {piece_lot, weighed_lot, new_lot}
    assert all(row["countedStock"] is None and row["expectedStock"] is None for row in refreshed["lines"])
    assert {row["lotId"]: row["countStatus"] for row in refreshed["lines"]} == {
        piece_lot: "counted", weighed_lot: "counted", new_lot: "pending"}
    assert request(path + "/refresh", staff=admin, method="POST") == refreshed
    counted = request(path + "/count", count_body(refreshed, {(piece, new_lot): (2, 0)}), staff=admin)
    assert counted["status"] == "counted"

    request(stock + "/operations", {"operationId": str(uuid.uuid4()), "productId": piece, "lotId": piece_lot,
            "kind": "writeOffStock", "quantity": 2, "reason": "Движение во время пересчёта"}, staff=admin)
    assert post(path, counted, expected=409)["code"] == "inventory_lot_changed"
    request(stock + "/operations/" + draft["inventoryId"], staff=admin, expected=404)
    blind = request(path + "?blind=true", staff=admin)
    assert all(row["stockDifference"] is None and row["countedRevision"] is None for row in blind["lines"])
    assert [row["lotId"] for row in blind["lines"] if row["countStatus"] == "stale"] == [piece_lot]
    recounted = request(path + "/count", count_body(blind, {(piece, piece_lot): (4, 0)}), staff=admin)
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        posted, retry = pool.map(lambda _: post(path, recounted), range(2))
    assert posted["status"] == "posted" and post(path, recounted) == posted
    assert retry == posted
    request(path + "/refresh", staff=admin, method="POST", expected=409)
    operation = request(stock + "/operations/" + posted["inventoryId"], staff=admin)
    assert {row["lotId"]: row["stockDelta"] for row in operation["lines"]} == {piece_lot: 1, weighed_lot: -0.375}

    product_doc = request(inventories, create({"kind": "product", "productId": piece}), staff=admin)
    assert {row["lotId"] for row in product_doc["lines"]} == {piece_lot, new_lot}

    full = request(inventories, create({"kind": "full"}), staff=admin)
    full_path = inventories + "/" + full["inventoryId"]
    current = {}
    for offset in range(0, 10000, 50):
        page = request(stock + f"/lots?offset={offset}", staff=admin)
        current.update({(row["productId"], row["lotId"]):
                       (row["stockQuantity"], row["quarantineQuantity"]) for row in page})
        if len(page) < 50:
            break
    assert len(current) > 100 and len(full["lines"]) == len(current)
    keys = list(current)
    for offset in range(0, len(keys), 100):
        batch = {key: current[key] for key in keys[offset:offset + 100]}
        full = request(full_path + "/count", count_body(full, batch), staff=admin)
        assert full["status"] == ("draft" if offset + 100 < len(keys) else "counted")
    assert post(full_path, full)["status"] == "posted"
    assert request(stock + "/operations/" + full["inventoryId"], staff=admin)["lines"] == []
    print("PASS: full, category and product inventory, batch retries, blind recount, new lots, conflicts and access")


def check_inventory_cost():
    admin = "admin-inventory"
    store_id = fixture_id(11)
    store = f"/v1/stores/{store_id}"
    stock = store + "/stock"
    product, lot = fixture_id(2801), fixture_id(2811)
    valuation_path = f"/v1/reports/stores/{store_id}/stock-valuation"
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    movements_path = f"/v1/reports/stores/{store_id}/stock-movements?from={today}&to={today}&productId={product}"
    profit_path = f"/v1/reports/stores/{store_id}/profit?from={today}&to={today}"
    profit_before = request(profit_path, staff=admin)["total"]
    card = store + "/catalog/products/" + product
    request(card, {"expectedVersion": 0, "name": "=Товар для оценки", "unit": "piece", "barcodes": []},
            staff=admin, method="PUT")
    request(card + "/price", {"expectedVersion": 1, "unitPrice": 10, "active": True, "reason": "Розничная цена"},
            staff=admin, method="PUT")
    price_version = request(card, staff=admin)["priceVersion"]
    request(stock + "/opening-balances", {"operationId": str(uuid.uuid4()), "sourceReference": "Сверка",
            "reason": "Начальный ввод", "items": [{"productId": product, "lotId": lot, "stockQuantity": 2,
                                                   "quarantineQuantity": 0, "totalCost": 0.17}]}, staff=admin)

    receipt = str(uuid.uuid4())
    receipt_path = store + "/receipts/" + receipt
    request(store + "/receipts", {"receiptId": receipt, "supplierRef": "PROC-01",
            "invoiceNumber": "VALUE-01", "invoiceDate": str(today),
            "items": [receipt_item(2811, 4, None, product=2801)]}, staff="receiver")
    request(receipt_path + "/submit", {"expectedVersion": 1}, staff="receiver")
    request(receipt_path + "/costs", {"expectedVersion": 2,
            "items": [{"productId": product, "lotId": lot, "unitPrice": 0.25, "discount": 0.18}]},
            staff=admin, method="PUT")
    request(receipt_path + "/post", {"expectedVersion": 3}, staff=admin)

    spec = request("/openapi/v1.json")["paths"]
    assert set(spec["/v1/reports/stores/{storeId}/stock-valuation"]["get"]["security"][0]) == {"Bearer"}
    for staff, status in ((None, 401), ("cashier-a", 403), ("receiver", 403), ("admin-foreign", 404)):
        request(valuation_path, staff=staff, expected=status)
    request(valuation_path, staff=admin, mfa=False, expected=403)
    request(valuation_path + "?offset=-1", staff=admin, expected=400)
    request(valuation_path + "?productId=" + fixture_id(2899), staff=admin, expected=404)

    def valuation():
        return request(valuation_path + "?productId=" + product, staff=admin)

    def value():
        report = valuation()
        assert report["unvaluedLotCount"] == 0 and report["method"] == "movingWeightedAverageByLot"
        assert len(report["items"]) == 1
        row = report["items"][0]
        return row["stockQuantity"], row["quarantineQuantity"], row["stockValue"], row["quarantineValue"], report["totalValue"]

    assert value() == (6, 0, 0.99, 0, 0.99)
    legacy = request(valuation_path + "?productId=" + fixture_id(41), staff=admin)
    assert legacy["unvaluedLotCount"] > 0 and legacy["totalValue"] is None
    store_value = request(valuation_path, staff=admin)
    next_offset = store_value["nextOffset"]
    assert next_offset == 50
    page_two = request(valuation_path + f"?offset={next_offset}", staff=admin)
    assert page_two["knownValue"] == store_value["knownValue"]
    assert store_value["items"][-1]["lotId"] != page_two["items"][0]["lotId"]

    shift = str(uuid.uuid4())
    request("/v1/shifts", {"shiftId": shift, "openingCash": 0})
    checkout = sale(shift, (2801, 2811, 3))
    checkout["items"][0]["priceVersion"] = price_version
    checkout["discount"] = {"kind": "percent", "value": 10, "reason": "Проверка"}
    sold = request("/v1/sales", checkout)
    assert sold["total"] == 27 and "inventoryCost" not in sold["lines"][0]
    assert request("/v1/sales", checkout) == sold
    assert value() == (3, 0, 0.49, 0, 0.49)

    refunds = []
    for destination in ("stock", "quarantine", "stock"):
        refund = {"operationId": str(uuid.uuid4()), "shiftId": shift, "sourceRegisterId": fixture_id(21),
                  "saleId": checkout["operationId"], "reason": "Проверка восстановления",
                  "items": [{"productId": product, "lotId": lot, "quantity": 1, "destination": destination}]}
        restored = request("/v1/refunds", refund)
        assert restored["total"] == 9 and "inventoryCost" not in restored["lines"][0]
        assert request("/v1/refunds", refund) == restored
        refunds.append(refund["operationId"])
    costs = {row["documentId"]: row["inventoryCostDelta"]
             for row in request(movements_path, staff=admin)["items"]}
    assert costs[checkout["operationId"]] == -0.50 and costs[receipt] == 0.82
    assert [costs[refund] for refund in refunds] == [0.17, 0.16, 0.17]
    assert value() == (5, 1, 0.83, 0.16, 0.99)
    check_stock_valuation_csv(valuation_path, product, valuation(), admin)

    def writeoff(kind, quantity):
        return {"operationId": str(uuid.uuid4()), "productId": product, "lotId": lot,
                "quantity": quantity, "kind": kind, "reason": "Проверка себестоимости"}

    command = writeoff("writeOffQuarantine", 1)
    written_off = request(stock + "/operations", command, staff=admin)
    assert written_off["lines"][0]["inventoryCostDelta"] == -0.17
    assert request(stock + "/operations", command, staff=admin) == written_off
    assert value() == (5, 0, 0.82, 0, 0.82)

    inventory = str(uuid.uuid4())
    inventories = stock + "/inventories"
    draft = request(inventories, {"inventoryId": inventory, "reason": "Излишек",
                    "lots": [{"productId": product, "lotId": lot}]}, staff=admin)
    inv_path = inventories + "/" + inventory
    counted = request(inv_path + "/count", {"expectedVersion": 1, "items": [{"productId": product, "lotId": lot,
            "lotRevision": draft["lines"][0]["lotRevision"], "stockQuantity": 6, "quarantineQuantity": 0}]}, staff=admin)
    assert counted["lines"][0]["inventoryCostDelta"] == 0.16
    assert "inventoryCostDelta" not in request(inv_path + "?blind=true", staff=admin)["lines"][0]
    posted = request(inv_path + "/post", {"expectedVersion": 2}, staff=admin)
    assert posted["lines"][0]["inventoryCostDelta"] == 0.16
    assert request(stock + "/operations/" + inventory, staff=admin)["lines"][0]["inventoryCostDelta"] == 0.16
    assert value() == (6, 0, 0.98, 0, 0.98)

    supplier_return = str(uuid.uuid4())
    request(store + "/procurement/returns", {"returnId": supplier_return, "receiptId": receipt, "reason": "Проверка",
            "items": [{"productId": product, "lotId": lot, "quantity": 1, "bucket": "stock"}]}, staff=admin)
    returns = request(movements_path + "&kind=supplierReturn", staff=admin)["items"]
    assert returns[0]["supplierCredit"] == 0.21 and returns[0]["inventoryCostDelta"] == -0.16
    assert value() == (5, 0, 0.82, 0, 0.82)

    def compete(command):
        try:
            return request(stock + "/operations", command, staff=admin)["lines"][0]["inventoryCostDelta"]
        except AssertionError as error:
            assert error.args[0][2] == 409, error
            return None

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        assert set(pool.map(compete, [writeoff("writeOffStock", 4), writeoff("writeOffStock", 4)])) == {-0.66, None}
    assert value() == (1, 0, 0.16, 0, 0.16)
    rejected = sale(shift, (2801, 2811, 1), (2801, 2899, 1))
    for item in rejected["items"]:
        item["priceVersion"] = price_version
    request("/v1/sales", rejected, expected=409)
    assert value() == (1, 0, 0.16, 0, 0.16)
    last = request(stock + "/operations", writeoff("writeOffStock", 1), staff=admin)
    assert last["lines"][0]["inventoryCostDelta"] == -0.16
    assert valuation()["totalValue"] == 0 and valuation()["items"] == []
    assert {row["documentId"]: row["inventoryCostDelta"] for row in request(movements_path, staff=admin)["items"]
            if row["documentId"] == checkout["operationId"]} == {checkout["operationId"]: -0.50}
    request("/v1/shifts/" + shift + "/close", {"countedCash": 0})
    profit_after = request(profit_path, staff=admin)["total"]
    expected_changes = {"sales": 27, "refunds": 27, "knownSaleCost": 0.50, "knownRefundCost": 0.50,
                        "knownWriteOffCost": 0.99, "knownInventoryShortageCost": 0}
    for field, change in expected_changes.items():
        assert round(profit_after[field] - profit_before[field], 2) == change, field
    losses_path = f"/v1/reports/stores/{store_id}/stock-losses?from={today}&to={today}&productId={product}"
    loss_items = request(losses_path, staff=admin)["items"]
    assert sorted((row["kind"], row["quantity"], row["cost"]) for row in loss_items) == [
        ("writeOffQuarantine", 1, 0.17), ("writeOffStock", 1, 0.16), ("writeOffStock", 4, 0.66)]
    print("PASS: lot valuation, receipt discounts, sale privacy, refund cents, adjustments, races, rollback and profit totals")


def check_stock_valuation_csv(path, product, valuation, admin):
    def download(route, staff):
        payload, headers = request(route, staff=staff, raw_response=True)
        assert payload.startswith(b"\xef\xbb\xbf")
        assert headers.get_content_type() == "text/csv" and "attachment" in headers["Content-Disposition"]
        assert headers["Cache-Control"] == "no-store" and headers["X-Content-Type-Options"] == "nosniff"
        rows = list(csv.DictReader(io.StringIO(payload.decode("utf-8-sig"))))
        assert all(None not in row and None not in row.values() for row in rows)
        return rows

    csv_path = path + "/csv"
    filtered = csv_path + "?productId=" + product
    spec = request("/openapi/v1.json")["paths"]
    operation = spec["/v1/reports/stores/{storeId}/stock-valuation/csv"]["get"]
    assert set(operation["security"][0]) == {"Bearer"} and "text/csv" in operation["responses"]["200"]["content"]
    request(filtered, staff=None, expected=401)
    request(filtered, staff="cashier-a", expected=403)
    request(filtered, staff="receiver", expected=403)
    request(filtered, staff=admin, mfa=False, expected=403)
    request(filtered, staff="admin-foreign", expected=404)
    request(csv_path + "?productId=" + fixture_id(0), staff=admin, expected=400)
    request(csv_path + "?productId=" + fixture_id(2899), staff=admin, expected=404)

    row, total = download(filtered, admin)
    expected = valuation["items"][0]
    assert row["row_type"] == "lot" and total["row_type"] == "total"
    assert row["product_id"] == expected["productId"] and row["lot_id"] == expected["lotId"]
    assert row["product_name"] == "'" + expected["productName"] and row["unit"] == expected["unit"]
    assert row["store_id"] == valuation["storeId"] and row["filter_product_id"] == product
    assert row["time_zone"] == "Asia/Tashkent" and row["access_role"] == "admin" and row["generated_at"]
    assert row["cost_method"] == valuation["method"]
    fields = {"stock_quantity": "stockQuantity", "quarantine_quantity": "quarantineQuantity",
              "stock_value": "stockValue", "quarantine_value": "quarantineValue", "total_value": "totalValue"}
    assert all(float(row[field]) == expected[key] for field, key in fields.items())
    assert float(total["total_value"]) == valuation["totalValue"]
    assert float(total["known_value"]) == valuation["knownValue"]

    unknown_product = fixture_id(41)
    query = "?productId=" + unknown_product
    unknown = request(path + query, staff=admin)
    rows = download(csv_path + query, admin)
    assert rows[-1]["total_value"] == "" and int(rows[-1]["unvalued_lot_count"]) == unknown["unvaluedLotCount"] > 0
    assert len(rows) == len(unknown["items"]) + 1
    for row, expected in zip(rows[:-1], unknown["items"]):
        assert row["lot_id"] == expected["lotId"]
        for field, key in fields.items():
            assert (None if row[field] == "" else float(row[field])) == expected[key]

    foreign_path = csv_path.replace(fixture_id(11), fixture_id(12))
    error = request(foreign_path, staff="admin-foreign", expected=409)
    assert error["code"] == "stock_valuation_export_limit"
    rows = download(foreign_path + "?productId=" + fixture_id(242), "admin-foreign")
    assert len(rows) == 1001 and rows[-1]["total_value"] == "1230.00"
    assert rows[0]["lot_id"] == fixture_id(5000) and rows[-2]["lot_id"] == fixture_id(5999)
    print("PASS: stock valuation CSV parity, unknown values, formula escaping, access and export limit")


def check_register_devices():
    admin = "admin-inventory"
    register = 29
    store = fixture_id(11)
    base = f"/v1/stores/{store}/registers"
    path = base + "/" + fixture_id(register)
    key = uuid.uuid4().hex + uuid.uuid4().hex
    new_key = uuid.uuid4().hex + uuid.uuid4().hex
    body = {"registerId": fixture_id(register), "name": "Касса стенда", "cashierId": fixture_id(31), "registerKey": key}
    rotate = {"expectedVersion": 1, "registerKey": new_key}
    revoke = {"expectedVersion": 2}
    paths = request("/openapi/v1.json")["paths"]
    assert set(paths["/v1/stores/{storeId}/registers/{id}/rotate-key"]["post"]["security"][0]) == {"Bearer"}
    for staff, status in ((None, 401), ("cashier-a", 403), ("receiver", 403), ("admin-foreign", 404)):
        request(base, staff=staff, expected=status)
        request(base, body, staff=staff, expected=status)
        request(path + "/rotate-key", rotate, staff=staff, expected=status)
        request(path + "/revoke", revoke, staff=staff, expected=status)
    request(base, body, staff=admin, mfa=False, expected=403)
    request(base, {**body, "cashierId": fixture_id(35)}, staff=admin, expected=404)
    request(path, staff=admin, expected=404)
    request(base, {**body, "registerKey": "invalid"}, staff=admin, expected=400)
    request(base, {**body, "registerId": fixture_id(23)}, staff=admin, expected=409)

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        created, retry = pool.map(lambda _: request(base, body, staff=admin), range(2))
    assert created == retry == {"registerId": fixture_id(register), "name": "Касса стенда",
                               "active": True, "version": 1, "cashierIds": [fixture_id(31)]}
    assert request(path, staff=admin) == created and created in request(base, staff=admin)
    request(base, {**body, "cashierId": fixture_id(32)}, staff=admin, expected=409)
    request("/v1/catalog/barcodes/002", register=register, key=key, staff="cashier-b", expected=403)

    shift = str(uuid.uuid4())
    request("/v1/shifts", {"shiftId": shift, "openingCash": 0}, register=register, key=key)
    order = sale(shift, (42, 51, 0.001))
    sold = request("/v1/sales", order, register=register, key=key)
    request(path + "/rotate-key", {**rotate, "registerKey": key}, staff=admin, expected=409)
    request(path + "/rotate-key", rotate, staff=admin, mfa=False, expected=403)
    rotated = request(path + "/rotate-key", rotate, staff=admin)
    assert rotated == {**created, "version": 2}
    assert request(path + "/rotate-key", rotate, staff=admin) == rotated
    request("/v1/sales", order, register=register, key=key, expected=403)
    assert request("/v1/sales", order, register=register, key=new_key) == sold
    request("/v1/shifts/" + shift + "/close", {"countedCash": 0}, register=register, key=new_key)

    request(path + "/revoke", revoke, staff=admin, mfa=False, expected=403)
    removed = request(path + "/revoke", revoke, staff=admin)
    assert removed == {**created, "version": 3, "active": False}
    assert request(path + "/revoke", revoke, staff=admin) == removed
    assert request(path, staff=admin) == removed
    request(path + "/rotate-key", {**rotate, "expectedVersion": 3}, staff=admin, expected=409)
    request("/v1/shifts", {"shiftId": str(uuid.uuid4()), "openingCash": 0},
            register=register, key=new_key, expected=403)
    request("/v1/sales/" + sold["operationId"], register=register, key=new_key, expected=403)
    receipt = request(f"/v1/stores/{store}/sales/{fixture_id(register)}/{sold['operationId']}", staff=admin)
    assert receipt == sold
    history = request(f"/v1/reports/stores/{store}/registers/{fixture_id(register)}/shifts/{shift}", staff=admin)
    assert history["closedAt"] and history["salesCount"] == 1
    print("PASS: register enrolment, cashier scope, atomic retries, key rotation, revocation and preserved history")


def check_store_staff():
    admin = "admin-inventory"
    store, second = fixture_id(11), fixture_id(13)
    root = f"/v1/stores/{store}/staff"
    other = f"/v1/stores/{second}/staff"
    employee = {"subject": "new-cashier", "role": "cashier", "registerId": fixture_id(21)}
    delegate = {"subject": "admin-enrolled", "role": "admin"}
    headers = {"Authorization": "Bearer " + token(delegate["subject"], True)}
    cashier_headers = {"Authorization": "Bearer " + token(employee["subject"], False)}
    for staff, status in ((None, 401), ("cashier-a", 403), ("receiver", 403), ("admin-foreign", 404)):
        request(root, staff=staff, expected=status)
        request(root, employee, staff=staff, expected=status)
    request(root, employee, staff=admin, mfa=False, expected=403)
    request(root, {**employee, "registerId": fixture_id(23)}, staff=admin, expected=404)
    request(root, {"subject": "cashier-a", "role": "admin"}, staff=admin, expected=409)
    request(root, {"subject": "admin-foreign", "role": "admin"}, staff=admin, expected=409)
    request(root + "/" + fixture_id(38) + "/revoke", method="POST", staff=admin, expected=409)

    assigned = request(root, employee, staff=admin)
    assert assigned["active"] and assigned["role"] == "cashier" and assigned["registerIds"] == [fixture_id(21)]
    assert request(root, employee, staff=admin) == assigned
    assert assigned in request(root, staff=admin)
    assert assigned not in request(other, staff=admin)
    request("/v1/catalog/barcodes/002", extra_headers=cashier_headers)
    request("/v1/catalog/barcodes/002", register=22, extra_headers=cashier_headers, expected=403)
    shift = str(uuid.uuid4())
    request("/v1/shifts", {"shiftId": shift, "openingCash": 0}, extra_headers=cashier_headers)
    order = sale(shift, (42, 51, 0.001))
    sold = request("/v1/sales", order, extra_headers=cashier_headers)
    request("/v1/shifts/" + shift + "/close", {"countedCash": 0}, extra_headers=cashier_headers)
    revoke = root + "/" + assigned["staffId"] + "/revoke"
    request(revoke, method="POST", staff="receiver", expected=403)
    removed = request(revoke, method="POST", staff=admin)
    assert removed == {**assigned, "active": False} and request(revoke, method="POST", staff=admin) == removed
    request(root, employee, staff=admin, expected=409)
    request("/v1/sales", order, extra_headers=cashier_headers, expected=403)
    assert request(f"/v1/stores/{store}/sales/{fixture_id(21)}/{sold['operationId']}", staff=admin) == sold

    first = request(root, delegate, staff=admin)
    assert request(other, delegate, staff=admin) == first
    assert request(root, extra_headers=headers) and request(other, extra_headers=headers)
    revoke = root + "/" + first["staffId"] + "/revoke"
    request(revoke, method="POST", staff=admin, mfa=False, expected=403)
    request(revoke, method="POST", staff=admin)
    request(root, extra_headers=headers, expected=404)
    request(f"/v1/reports/stores/{store}/stock-expiry/csv", extra_headers=headers, expected=404)
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    request(f"/v1/stores/{store}/audit?from={today}&to={today}", extra_headers=headers, expected=404)
    assert request(other, extra_headers=headers)
    print("PASS: staff enrolment, historical records, scoped token revocation and unaffected second store")


def check_store_audit():
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    day = today - datetime.timedelta(days=2)
    store = fixture_id(11)
    path = f"/v1/stores/{store}/audit"
    admin = "admin-inventory"
    params = f"?from={day}&to={day}&action=fixture.audit_tie"
    spec = request("/openapi/v1.json", staff="cashier-b")["paths"]
    assert set(spec["/v1/stores/{storeId}/audit"]["get"]["security"][0]) == {"Bearer"}
    for staff, status in ((None, 401), ("cashier-b", 403), ("receiver", 403), ("admin-foreign", 404)):
        request(path + params, staff=staff, expected=status)
    request(path + params, staff=admin, mfa=False, expected=403)
    request(path + params + "&limit=101", staff=admin, expected=400)
    request(path + params + "&after=invalid", staff=admin, expected=400)
    request(path + f"?from={today}&to={day}", staff=admin, expected=400)
    request(path + f"?from={day}&to={day}&action=bad%20filter", staff=admin, expected=400)
    report = request(path + params, staff=admin)
    assert report["timeZone"] == "Asia/Tashkent" and report["storeId"] == store
    entries = report["items"]
    assert len(entries) == 3 and len({row["occurredAt"] for row in entries}) == 1
    assert all(set(row) == {"id", "occurredAt", "action", "staffId", "objectId", "registerId", "requestId"} for row in entries)
    assert all(row["registerId"] is None and row["action"] == "fixture.audit_tie" for row in entries)
    assert [row["id"] for row in entries] == sorted([row["id"] for row in entries], reverse=True)
    cursor = None
    paged = []
    for _ in entries:
        page = request(path + params + "&limit=1" + ("&after=" + cursor if cursor else ""), staff=admin)
        paged += page["items"]
        cursor = page["nextCursor"]
    assert paged == entries and cursor is None
    other = request(path.replace(store, fixture_id(12)) + params, staff="admin-foreign")
    assert other["items"] == []

    real = request(path + f"?from={today}&to={today}&action=register.key_rotated", staff=admin)["items"]
    assert len(real) == 1
    assert real[0]["staffId"] == fixture_id(38) and real[0]["registerId"] == real[0]["objectId"] == fixture_id(29)
    assert real[0]["requestId"]
    print("PASS: audit scope, safe fields, real event, filters and tied timestamp pagination")


def check_receipt_cancellation():
    store = f"/v1/stores/{fixture_id(11)}"
    receipts = store + "/receipts"
    admin = "admin-inventory"
    order_id = str(uuid.uuid4())
    supplier = "Учебный поставщик"
    product = fixture_id(42)
    order_path = store + "/purchase-orders/" + order_id
    order = request(store + "/purchase-orders", {"orderId": order_id, "supplierRef": supplier,
                    "items": [{"productId": product, "quantity": 5}]}, staff=admin)

    def receipt(number, lot, staff="receiver"):
        body = {"receiptId": str(uuid.uuid4()), "purchaseOrderId": order_id, "supplierRef": supplier,
                "invoiceNumber": number, "invoiceDate": str(datetime.date.today()),
                "items": [receipt_item(lot, 1, None, product=42)]}
        request(receipts, body, staff=staff)
        return receipts + "/" + body["receiptId"], body

    path, body = receipt("CANCEL-01", 2920)
    file_path = path + "/files/" + str(uuid.uuid4())
    original = b"%PDF-1.4\n% retained attachment\n%%EOF"
    file = request(file_path, staff="receiver", method="PUT", raw_body=original,
                   extra_headers={"Content-Type": "application/pdf", "X-File-Name": "invoice.pdf"})
    assert file["scanStatus"] == "clean"
    command = {"expectedVersion": 1, "reason": "Ошибочный черновик"}
    request(path + "/cancel", command, staff=None, expected=401)
    request(path + "/cancel", command, staff="cashier-b", expected=403)
    request(path + "/cancel", command, staff=admin, mfa=False, expected=403)
    request(path + "/cancel", command, staff="admin-foreign", expected=404)
    request(path + "/cancel", {**command, "reason": ""}, staff=admin, expected=400)
    request(path + "/cancel", {**command, "expectedVersion": 2}, staff="receiver", expected=409)

    cancelled = request(path + "/cancel", command, staff="receiver")
    assert cancelled["status"] == "cancelled" and cancelled["version"] == 2 and cancelled["postedAt"] is None
    assert cancelled["cancellation"]["cancelledBy"] == fixture_id(33)
    assert cancelled["cancellation"]["cancelledAt"] and cancelled["cancellation"]["reason"] == command["reason"]
    assert request(path + "/cancel", command, staff="receiver") == cancelled
    request(path + "/cancel", {**command, "reason": "Другая причина"}, staff="receiver", expected=409)
    assert request(path, staff=admin) == cancelled
    assert cancelled in request(receipts + "?status=cancelled", staff=admin)
    request(path, {"expectedVersion": 2, "items": body["items"]}, method="PUT", staff="receiver", expected=409)
    request(path + "/submit", {"expectedVersion": 2}, staff="receiver", expected=409)
    request(path + "/post", {"expectedVersion": 2}, staff=admin, expected=409)
    assert request(path + "/files", staff=admin) == [file]
    assert request(file_path, staff=admin, raw_response=True)[0] == original
    assert request(order_path, staff=admin) == order
    assert all(row["lotId"] != fixture_id(2920) for row in request(store + "/stock/lots?productId=" + product, staff=admin))

    replacement, replacement_body = receipt("CANCEL-01", 2920, staff=admin)
    request(replacement + "/cancel", command, staff="receiver", expected=403)
    request(receipts, {**replacement_body, "receiptId": str(uuid.uuid4())}, staff=admin, expected=409)
    request(replacement + "/cancel", command, staff=admin)

    racing, _ = receipt("CANCEL-RACE", 2921)
    request(racing + "/submit", {"expectedVersion": 1}, staff="receiver")
    request(racing + "/cancel", {**command, "expectedVersion": 2}, staff="receiver", expected=403)
    request(racing + "/costs", {"expectedVersion": 2, "items": [
        {"productId": product, "lotId": fixture_id(2921), "unitPrice": 10}]}, staff=admin, method="PUT")

    def try_transition(action):
        data = {"expectedVersion": 3}
        if action == "cancel":
            data["reason"] = "Отменить до проведения"
        try:
            return request(racing + "/" + action, data, staff=admin)
        except AssertionError as error:
            assert error.args[0][2] == 409, error
            return None

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(try_transition, ("post", "cancel")))
    assert sum(result is not None for result in results) == 1
    final = request(racing, staff=admin)
    posted = final["status"] == "posted"
    assert final["status"] in ("posted", "cancelled") and final["version"] == 4
    request(racing + ("/cancel" if posted else "/post"), {**command, "expectedVersion": 4}
            if posted else {"expectedVersion": 4}, staff=admin, expected=409)
    assert request(order_path, staff=admin)["lines"][0]["remaining"] == (4 if posted else 5)
    lots = request(store + "/stock/lots?productId=" + product, staff=admin)
    assert any(row["lotId"] == fixture_id(2921) for row in lots) == posted
    print("PASS: receipt cancellation permissions, files, idempotency, invoice reuse and posting race")


def check_store_exceptions():
    admin = "admin-settlements"
    receiver = "receiver-exceptions"
    store = fixture_id(11)
    path = f"/v1/reports/stores/{store}/exceptions"
    spec = request("/openapi/v1.json", staff="cashier-b")["paths"]
    assert set(spec["/v1/reports/stores/{storeId}/exceptions"]["get"]["security"][0]) == {"Bearer"}
    for staff, status in ((None, 401), ("cashier-b", 403), (receiver, 403), ("admin-foreign", 404)):
        request(path, staff=staff, expected=status)
    request(path, staff=admin, mfa=False, expected=403)
    request(path + "?code=wrong", staff=admin, expected=400)
    request(path + "?limit=101", staff=admin, expected=400)
    request(path + "?offset=-1", staff=admin, expected=400)

    def items(code):
        return request(path + "?code=" + code, staff=admin)["items"]

    missing = items("missingRequiredExpiry")
    assert any(row["objectId"] == fixture_id(53) and row["productId"] == fixture_id(41) for row in missing)
    assert all(row["objectId"] != fixture_id(52) for row in missing)
    discrepancies = items("cashDifference")
    difference = next(row for row in discrepancies if row["objectId"] == fixture_id(2980))
    shift_path = f"/v1/reports/stores/{store}/registers/{fixture_id(21)}/shifts/{fixture_id(2980)}"
    assert difference["cashDifference"] == request(shift_path, staff=admin)["cashDifference"] == -5
    assert difference["registerId"] == fixture_id(21) and difference["occurredAt"]
    assert all(row["objectId"] != fixture_id(940) for row in discrepancies)

    receipt = str(uuid.uuid4())
    receipts = f"/v1/stores/{store}/receipts"
    receipt_path = receipts + "/" + receipt
    request(receipts, {"receiptId": receipt, "supplierRef": "Учебный поставщик", "invoiceNumber": "EXCEPTION-01",
                       "invoiceDate": str(datetime.date.today()), "items": [receipt_item(2990, 1, None, product=42)]},
            staff=receiver)
    for code, marker in (("invoiceScanPending", b"KASA_TEST_NO_VERDICT"),
                         ("invoiceScanRejected", b"KASA_TEST_THREAT"), (None, b"clean")):
        file_id = str(uuid.uuid4())
        request(receipt_path + "/files/" + file_id, staff=receiver, method="PUT",
                raw_body=b"%PDF-1.4\n% " + marker + b"\n%%EOF",
                extra_headers={"Content-Type": "application/pdf", "X-File-Name": "invoice.pdf"})
        if code is None:
            assert all(row["objectId"] != file_id for row in request(path, staff=admin)["items"])
        else:
            row = next(row for row in items(code) if row["objectId"] == file_id)
            assert row["receiptId"] == receipt and "contents" not in row and "fileName" not in row

    all_rows = request(path, staff=admin)["items"]
    first = request(path + "?limit=1", staff=admin)
    second = request(path + "?limit=1&offset=" + str(first["nextOffset"]), staff=admin)
    assert first["items"] + second["items"] == all_rows[:2]
    request(receipt_path + "/cancel", {"expectedVersion": 1, "reason": "Проверка исключений"}, staff=receiver)
    assert all(row["receiptId"] != receipt for row in request(path, staff=admin)["items"])
    other = request(path.replace(store, fixture_id(12)), staff="admin-foreign")
    assert other["items"] == []
    print("PASS: store exception scope, cash reconciliation, expiry, current scan state and pagination")


def check_stock_snapshots():
    admin = "admin-replenishment"
    store = fixture_id(11)
    product, valued, unvalued = (fixture_id(n) for n in (3031, 3041, 3042))
    prefix = f"/v1/stores/{store}"
    valuation = f"/v1/reports/stores/{store}/stock-valuation"
    path = valuation + "/snapshots"
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    yesterday = today - datetime.timedelta(days=1)
    command = {"operationId": str(uuid.uuid4())}
    query = path + f"?date={today}&productId={product}"
    spec = request("/openapi/v1.json", staff="cashier-b")["paths"]
    for method in ("get", "post"):
        assert set(spec["/v1/reports/stores/{storeId}/stock-valuation/snapshots"][method]["security"][0]) == {"Bearer"}
    for staff, status in ((None, 401), ("cashier-b", 403), ("receiver-exceptions", 403), ("admin-foreign", 404)):
        request(path, command, staff=staff, expected=status)
        request(query, staff=staff, expected=status)
    request(path, command, staff=admin, mfa=False, expected=403)
    request(query, staff=admin, mfa=False, expected=403)
    assert request(path + f"?date={yesterday}", staff=admin, expected=404)["code"] == "stock_snapshot_not_found"
    request(query + "&offset=-1", staff=admin, expected=400)
    request(prefix + "/catalog/products/" + product,
            {"expectedVersion": 0, "name": "Товар для снимка", "unit": "piece", "barcodes": []},
            staff=admin, method="PUT")
    request(prefix + "/stock/opening-balances", {"operationId": str(uuid.uuid4()), "sourceReference": "Сверка",
            "reason": "Проверка снимка", "items": [
                {"productId": product, "lotId": valued, "stockQuantity": 3, "quarantineQuantity": 1, "totalCost": 40},
                {"productId": product, "lotId": unvalued, "stockQuantity": 1, "quarantineQuantity": 0}]}, staff=admin)

    def capture(_):
        return request(path, command, staff=admin)

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        first, second = pool.map(capture, range(2))
    assert first == second and first["date"] == str(today) and first["timeZone"] == "Asia/Tashkent"
    assert first["capturedAt"] and first["capturedBy"] == fixture_id(36)
    other = request(path, {"operationId": str(uuid.uuid4())}, staff=admin, expected=409)
    assert other["code"] == "stock_snapshot_date_exists"
    before = request(query, staff=admin)
    assert before["snapshot"] == first and before["nextOffset"] is None
    assert [(row["lotId"], row["stockQuantity"], row["quarantineQuantity"], row["inventoryValue"])
            for row in before["items"]] == [(valued, 3, 1, 40), (unvalued, 1, 0, None)]
    page = request(path + f"?date={today}", staff=admin)
    assert len(page["items"]) == page["nextOffset"] == 50
    more = request(path + f"?date={today}&offset=50", staff=admin)
    assert more["items"] and page["items"][-1]["lotId"] != more["items"][0]["lotId"]
    request(prefix + "/stock/operations", {"operationId": str(uuid.uuid4()), "kind": "writeOffStock",
            "productId": product, "lotId": valued, "quantity": 1, "reason": "Проверка неизменности снимка"}, staff=admin)
    current = request(valuation + "?productId=" + product, staff=admin)
    assert next(row for row in current["items"] if row["lotId"] == valued)["stockQuantity"] == 2
    assert request(query, staff=admin) == before
    request(query.replace(store, fixture_id(12)), staff="admin-foreign", expected=404)
    print("PASS: stock snapshots preserve original quantities and unknown costs, enforce store scope and deduplicate capture")


def check_background_stock_movement_exports():
    store = fixture_id(11)
    staff_path = f"/v1/stores/{store}/staff"
    scope = f"/v1/reports/stores/{store}/stock-movements"
    route = scope + "/exports"
    spec = request("/openapi/v1.json", staff="cashier-b")["paths"]
    spec_path = "/v1/reports/stores/{storeId}/stock-movements/exports"
    assert "202" in spec[spec_path]["post"]["responses"]
    assert set(spec[spec_path + "/{id}"]["get"]["security"][0]) == {"Bearer"}
    assert "text/csv" in spec[spec_path + "/{id}/file"]["get"]["responses"]["200"]["content"]

    author = "admin-export-smoke"
    owner = request(staff_path, {"subject": author, "role": "admin"}, staff="admin-replenishment")
    today = datetime.datetime.now(ZoneInfo("Asia/Tashkent")).date()
    day = today - datetime.timedelta(days=15)
    data = {"operationId": str(uuid.uuid4()), "from": str(day), "to": str(day),
            "productId": fixture_id(42), "lotId": fixture_id(51), "kind": "sale"}
    path = route + "/" + data["operationId"]
    for staff, status in ((None, 401), ("cashier-b", 403), ("receiver-exceptions", 403), ("admin-foreign", 404)):
        request(route, data, staff=staff, expected=status)
        request(path, staff=staff, expected=status)
        request(path + "/file", staff=staff, expected=status)
    request(route, data, staff=author, mfa=False, expected=403)
    request(route, {**data, "to": str(day - datetime.timedelta(days=1))}, staff=author, expected=400)
    request(route, {**data, "productId": None}, staff=author, expected=400)

    def enqueue(_):
        content, headers = request(route, data, staff=author, expected=202, raw_response=True)
        assert headers["Location"] == path
        return json.loads(content)

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        results = list(pool.map(enqueue, range(2)))
    assert any(row["status"] == "queued" for row in results)
    assert results[0]["queuedAt"] == results[1]["queuedAt"]
    conflict = request(route, {**data, "kind": "customerRefund"}, staff=author, expected=409)
    assert conflict["code"] == "stock_movement_export_operation_conflict"
    request(path, staff="admin-replenishment", expected=404)
    request(path + "/file", staff="admin-replenishment", expected=404)
    request(path, staff=author, mfa=False, expected=403)
    request(path + "/file", staff=author, mfa=False, expected=403)

    def ready(url, staff):
        deadline = time.monotonic() + 20
        while time.monotonic() < deadline:
            state = request(url, staff=staff)
            if state["status"] != "queued":
                assert state["status"] == "ready", state
                assert state["finishedAt"] and state["expiresAt"] and state["errorCode"] is None
                return state
            time.sleep(0.3)
        raise AssertionError(f"Export did not finish: {url}")

    status = ready(path, author)
    payload, headers = request(path + "/file", staff=author, raw_response=True)
    assert headers.get_content_type() == "text/csv" and "attachment" in headers["Content-Disposition"]
    assert headers["Cache-Control"] == "no-store" and headers["X-Content-Type-Options"] == "nosniff"
    assert request(path + "/file", staff=author, raw_response=True)[0] == payload
    query = urllib.parse.urlencode({key: val for key, val in data.items() if key != "operationId"})
    reference, _ = request(scope + "/csv?" + query, staff=author, raw_response=True)

    def csv_entries(content):
        rows = list(csv.DictReader(io.StringIO(content.decode("utf-8-sig"))))
        for row in rows:
            del row["generated_at"]
        return rows

    entries = csv_entries(payload)
    assert len(entries) == status["rowCount"] > 0 and entries == csv_entries(reference)
    assert enqueue(None) == status
    request(staff_path + "/" + owner["staffId"] + "/revoke", method="POST", staff="admin-replenishment")
    request(path, staff=author, expected=404)
    request(path + "/file", staff=author, expected=404)

    foreign = route.replace(store, fixture_id(12))
    bulk = foreign + "/" + fixture_id(3197)
    assert ready(bulk, "admin-foreign")["rowCount"] == 1001
    content, _ = request(bulk + "/file", staff="admin-foreign", raw_response=True)
    assert len(csv_entries(content)) == 1001
    expired = foreign + "/" + fixture_id(3198)
    assert request(expired, staff="admin-foreign")["status"] == "expired"
    assert request(expired + "/file", staff="admin-foreign", expected=410)["code"] == "stock_movement_export_expired"

    bulk_day = today - datetime.timedelta(days=35)
    too_large = {"operationId": str(uuid.uuid4()), "from": str(bulk_day - datetime.timedelta(days=1)),
                 "to": str(bulk_day)}
    failed_path = foreign + "/" + too_large["operationId"]
    assert request(foreign, too_large, staff="admin-foreign", expected=202)["status"] == "queued"
    deadline = time.monotonic() + 20
    while time.monotonic() < deadline:
        failure = request(failed_path, staff="admin-foreign")
        if failure["status"] != "queued":
            break
        time.sleep(0.3)
    assert failure["status"] == "failed" and failure["errorCode"] == "stock_movement_background_export_limit"
    assert failure["rowCount"] is None and failure["expiresAt"] is None
    assert request(failed_path + "/file", staff="admin-foreign", expected=409)["code"] == failure["errorCode"]
    print("PASS: background movement CSV, durable queue, retries, filters, 1001 rows, size limit, expiry and revoked access")


def check_register_diagnostics(previous_shift_b):
    route = "/v1/register/diagnostics"
    cashier = "cashier-diagnostics"
    peer = "cashier-diagnostics-peer"
    admin = "admin-settlements"
    store = fixture_id(11)
    report = f"/v1/reports/stores/{store}/exceptions"
    shift = str(uuid.uuid4())
    purchase = sale(shift, (42, 51, 0.1))
    operation = purchase["operationId"]
    purchase_path = "/v1/sales/" + operation
    spec = request("/openapi/v1.json", staff=cashier)["paths"]
    assert set(spec[route]["post"]["security"][0]) == {"Bearer", "RegisterId", "RegisterKey"}
    peer_shift = str(uuid.uuid4())
    request("/v1/shifts", {"shiftId": peer_shift, "openingCash": 0}, staff=peer)
    request(f"/v1/shifts/{peer_shift}/close", {"countedCash": 0}, staff=peer)
    request("/v1/shifts", {"shiftId": shift, "openingCash": 0}, staff=cashier)

    def diagnostic(code):
        return {"eventId": str(uuid.uuid4()), "shiftId": shift, "code": code, "operationId": operation}

    def exception_rows(code):
        return request(report + "?code=" + code, staff=admin)["items"]

    def quantity():
        item = request("/v1/catalog/barcodes/002", staff=cashier)
        return next(lot["quantity"] for lot in item["lots"] if lot["lotId"] == fixture_id(51))

    unknown = diagnostic("saleOutcomeUnknown")
    print_error = diagnostic("printFailed")
    request(route, unknown, staff=None, expected=401)
    request(route, unknown, staff=admin, expected=403)
    request(route, unknown, staff=cashier, key="0" * 64, expected=403)
    request(route, unknown, staff=cashier, register=22, expected=403)
    request(route, unknown, staff=peer, expected=404)
    request(route, {**unknown, "message": "unstructured"}, staff=cashier, expected=400)
    request(route, {**unknown, "code": "custom"}, staff=cashier, expected=400)
    request(route, {**unknown, "operationId": None}, staff=cashier, expected=400)
    request(route, print_error, staff=cashier, expected=404)
    request(purchase_path, staff=cashier, expected=404)
    before = quantity()

    def submit(_):
        return request(route, unknown, staff=cashier)

    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        saved, replayed = pool.map(submit, range(2))
    assert saved == replayed and saved["receivedAt"]
    conflict = request(route, {**unknown, "operationId": str(uuid.uuid4())}, staff=cashier, expected=409)
    assert conflict["code"] == "register_diagnostic_event_conflict"
    rows = exception_rows("saleOutcomeUnknown")
    assert len(rows) == 1 and rows[0]["objectId"] == unknown["eventId"]
    assert rows[0]["registerId"] == fixture_id(21) and rows[0]["shiftId"] == shift
    assert rows[0]["operationId"] == operation and rows[0]["occurredAt"] == saved["receivedAt"]
    request(report + "?code=saleOutcomeUnknown", staff="admin-foreign", expected=404)
    request(report + "?code=saleOutcomeUnknown", staff=admin, mfa=False, expected=403)
    foreign = report.replace(store, fixture_id(12))
    assert request(foreign + "?code=saleOutcomeUnknown", staff="admin-foreign")["items"] == []
    assert quantity() == before

    # Simulate a committed sale whose response was lost by ignoring the response.
    request("/v1/sales", purchase, staff=cashier)
    request(route, diagnostic("saleOutcomeUnknown"), staff=cashier)
    reconciled = request(purchase_path, staff=cashier)
    assert request("/v1/sales", purchase, staff=cashier) == reconciled
    assert exception_rows("saleOutcomeUnknown") == []
    assert request(route, unknown, staff=cashier) == saved
    request(route, {**print_error, "shiftId": peer_shift}, staff=peer, expected=404)
    request(route, {**print_error, "shiftId": previous_shift_b}, staff="cashier-b", register=22, expected=404)
    request(f"/v1/shifts/{shift}/close", {"countedCash": 0}, staff=cashier)
    printed = request(route, print_error, staff=cashier)
    sync = {**diagnostic("syncFailed"), "operationId": None}
    synced = request(route, sync, staff=cashier)
    assert request(route, sync, staff=cashier) == synced
    assert exception_rows("printFailed")[0]["objectId"] == printed["eventId"]
    sync_rows = exception_rows("syncFailed")
    assert len(sync_rows) == 1 and sync_rows[0]["objectId"] == synced["eventId"] and "operationId" not in sync_rows[0]
    assert abs(quantity() - (before - 0.1)) < 0.0001
    print("PASS: register diagnostics, closed shift, scoped sale reconciliation, repeat and a single stock debit")


if __name__ == "__main__":
    main()
