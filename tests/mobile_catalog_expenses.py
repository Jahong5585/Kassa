"""Check the requests used by Android against the isolated API."""

from uuid import uuid4

from smoke import fixture_id, request


def check_receiving_card(root):
    proposals = root + "/receiving/catalog/proposals"
    proposal_id = str(uuid4())
    product = {
        "name": "Напиток",
        "unit": "piece",
        "requiresExpiry": False,
        "category": "Напитки",
        "brand": "Напиток",
        "barcodes": [{"code": "190000019", "quantityFactor": 1}],
    }
    body = {"proposalId": proposal_id, "product": product}
    pending = request(proposals, body, staff="receiver")
    assert pending["status"] == "pending" and pending["product"]["brand"] == "Напиток"
    assert request(proposals, body, staff="receiver") == pending
    request(root + f"/catalog/proposals/{proposal_id}/approve", {}, staff="admin-a")
    assert request(root + f"/catalog/products/{proposal_id}", staff="admin-a")["brand"] == "Напиток"

    dated_id = str(uuid4())
    dated = {
        **product,
        "requiresExpiry": True,
        "shelfLifeDays": 180,
        "barcodes": [{"code": "190000020", "quantityFactor": 1}],
    }
    pending = request(proposals, {"proposalId": dated_id, "product": dated}, staff="receiver")
    assert pending["product"]["shelfLifeDays"] == 180
    request(root + f"/catalog/proposals/{dated_id}/approve", {}, staff="admin-a")
    card_path = root + f"/catalog/products/{dated_id}"
    card = request(card_path, staff="admin-a")
    assert card["shelfLifeDays"] == 180
    legacy_edit = {key: dated[key] for key in ("name", "unit", "category", "brand", "barcodes")}
    legacy_edit["expectedVersion"] = card["version"]
    legacy_edit["name"] = "Напиток 0,5 л"
    updated = request(card_path, legacy_edit, method="PUT", staff="admin-a")
    assert updated["requiresExpiry"] is True and updated["shelfLifeDays"] == 180

    invalid = {**product, "shelfLifeDays": 180}
    bad = request(proposals, {"proposalId": str(uuid4()), "product": invalid}, staff="receiver", expected=400)
    assert bad["code"] == "invalid_product_card"
    unknown = {**product, "unknownField": True}
    request(proposals, {"proposalId": str(uuid4()), "product": unknown}, staff="receiver", expected=400)


def check_expenses(root):
    expenses = root + "/finance/expenses"
    query = "?from=2026-11-17&to=2026-11-17&offset=0"
    path = expenses + query + "&includeReversed=false"
    request(path, staff=None, expected=401)
    request(path, staff="receiver", expected=403)
    assert request(path, staff="admin-a")["items"] == []

    expense_id = str(uuid4())
    body = {
        "operationId": expense_id,
        "incurredOn": "2026-11-17",
        "category": "delivery",
        "amount": 24500,
        "paymentMethod": "cash",
        "description": "Доставка товара",
    }
    request(expenses, body, staff="receiver", expected=403)
    recorded = request(expenses, body, staff="admin-a")
    assert recorded["operationId"] == expense_id and recorded["amount"] == 24500
    assert request(expenses, body, staff="admin-a") == recorded
    changed = request(expenses, {**body, "amount": 100}, staff="admin-a", expected=409)
    assert changed["code"] == "operating_expense_id_reused"
    assert request(path, staff="admin-a")["items"] == [recorded]

    reverse_path = expenses + f"/{expense_id}/reverse"
    reversal = {"operationId": str(uuid4()), "reason": "Ошибочная запись"}
    reversed_expense = request(reverse_path, reversal, staff="admin-a")
    assert reversed_expense["reversed"] is True
    assert request(reverse_path, reversal, staff="admin-a") == reversed_expense
    assert request(path, staff="admin-a")["items"] == []
    assert request(expenses + query + "&includeReversed=true", staff="admin-a")["items"] == [reversed_expense]


if __name__ == "__main__":
    store = f"/v1/stores/{fixture_id(11)}"
    check_receiving_card(store)
    check_expenses(store)
    print("PASS: Android proposals retain brand and shelf life, expenses list, record and reverse")
