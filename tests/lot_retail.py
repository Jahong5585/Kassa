"""Lot markdown and exact label acceptance on a disposable fixtures.sql database."""
from uuid import uuid4
from smoke import fixture_id as fid, request, sale


def main():
    store, product, marked, normal = (fid(n) for n in (11, 144, 155, 154))
    prefix = f"/v1/stores/{store}/stock/lots/"
    admin = {"staff": "admin-a"}
    setting = f"{prefix}{product}/{marked}/retail"
    change = {"expectedVersion": 0, "unitPrice": 6500, "reason": "Учебная уценка партии"}
    scan = "/v1/catalog/barcodes/"
    assert request(setting, **admin)["version"] == 0
    request(setting, change, method="PUT", expected=403)
    request(setting, change, method="PUT", staff="receiver", expected=403)
    request(setting, change, method="PUT", mfa=False, expected=403, **admin)
    request(setting, change, method="PUT", staff="admin-foreign", expected=404)
    request(setting.replace(store, fid(13)), change, method="PUT", staff="admin-inventory", expected=404)
    assert request(setting, {**change, "unitPrice": 10000}, method="PUT", expected=409, **admin)["code"] == "lot_markdown_unavailable"
    saved = request(setting, change, method="PUT", **admin)
    assert saved["version"] == 1 and saved["unitPrice"] == 6500 and saved["labelCode"].startswith("LOT-")
    assert request(setting, change, method="PUT", **admin) == saved
    assert request(setting, {**change, "unitPrice": 6400}, method="PUT", expected=409, **admin)["code"] == "lot_retail_version_conflict"

    expired_path = f"{prefix}{fid(41)}/{fid(52)}/retail"
    assert request(expired_path, change, method="PUT", expected=409, **admin)["code"] == "lot_markdown_unavailable"
    expired = request(expired_path, {**change, "unitPrice": None}, method="PUT", **admin)
    assert request(scan + expired["labelCode"], expected=409)["code"] == "lot_barcode_expired"
    missing = request(f"{prefix}{fid(41)}/{fid(53)}/retail", {**change, "unitPrice": None}, method="PUT", **admin)
    assert request(scan + missing["labelCode"], expected=409)["code"] == "lot_barcode_unavailable"
    request(scan + "LOT-damaged", expected=400)
    request(scan + "LOT-" + uuid4().hex, expected=404)
    assert request(scan + "001")["requiresLotVerification"] is True
    exact = request(scan + saved["labelCode"].lower())
    assert exact["productId"] == product and exact["requiresLotVerification"] is False
    assert len(exact["lots"]) == 1 and exact["lots"][0]["lotId"] == marked
    assert exact["lots"][0]["markdownUnitPrice"] == 6500 and exact["lots"][0]["lotPriceVersion"] == 1
    request(f"/v1/stores/{fid(12)}/staff",
            {"subject": "cashier-lot-foreign", "role": "cashier", "registerId": fid(23)}, staff="admin-foreign")
    request(scan + saved["labelCode"], staff="cashier-lot-foreign", register=23, expected=404)
    common = request(scan + "00999")
    lots_before = {row["lotId"]: row for row in common["lots"]}
    assert common["requiresLotVerification"] and set(lots_before) == {marked, normal}
    assert common["unitPrice"] == 10000 and "markdownUnitPrice" not in lots_before[normal]

    promotion = f"/v1/stores/{store}/catalog/products/{product}/promotion"
    request(promotion, {"expectedVersion": 0, "enabled": True, "reason": "Учебное совмещение"}, method="PUT", **admin)
    shift = str(uuid4())
    request("/v1/shifts", {"shiftId": shift, "openingCash": 50000})
    quote = f"/v1/shifts/{shift}/quote"
    body = sale(shift, (144, 155, 1), method="cash", tendered=6500)
    assert request(quote, {"items": body["items"]}, expected=409)["code"] == "lot_price_changed"
    body["items"][0]["lotPriceVersion"] = 1
    mixed = sale(shift, (144, 154, 3))["items"][0]
    mixed["promotionVersion"] = 1
    assert request(quote, {"items": body["items"] + [mixed]}) == {"total": 26500, "discountAmount": 10000}
    assert request(quote, {"items": body["items"] + [{**mixed, "quantity": 2}]}) == {"total": 26500, "discountAmount": 0}
    sold = request("/v1/sales", body)
    assert sold["total"] == 6500 and sold["lines"][0]["lotPriceVersion"] == 1
    lots_after = {row["lotId"]: row for row in request(scan + "00999")["lots"]}
    assert lots_after[marked]["quantity"] == lots_before[marked]["quantity"] - 1
    assert lots_after[normal] == lots_before[normal]

    cart_path = f"/v1/shifts/{shift}/carts/{uuid4()}"
    request(cart_path, {"expectedVersion": 0, "items": body["items"]}, method="PUT")
    repriced = request(setting, {"expectedVersion": 1, "unitPrice": 5000, "reason": "Вторая цена партии"},
                       method="PUT", **admin)
    assert repriced["labelCode"] == saved["labelCode"]
    reviewed = request(cart_path)["lines"][0]
    assert reviewed["status"] == "priceChanged" and reviewed["currentUnitPrice"] == 5000
    assert reviewed["currentLotPriceVersion"] == 2
    assert request(quote, {"items": body["items"]}, expected=409)["code"] == "lot_price_changed"
    assert request("/v1/sales", {**body, "operationId": str(uuid4())}, expected=409)["code"] == "lot_price_changed"
    assert request("/v1/sales", body) == sold
    assert request("/v1/sales", {**body, "items": [{**body["items"][0], "lotPriceVersion": 2}]},
                   expected=409)["code"] == "operation_id_reused"
    refund = {"operationId": str(uuid4()), "shiftId": shift, "sourceRegisterId": fid(21),
              "saleId": sold["operationId"], "reason": "Учебный возврат",
              "items": [{"productId": product, "lotId": marked, "quantity": 1, "destination": "stock"}]}
    assert request("/v1/refunds", refund)["total"] == 6500
    assert request(scan + saved["labelCode"])["lots"][0]["quantity"] == lots_before[marked]["quantity"]

    price_path = f"/v1/stores/{store}/catalog/products/{product}/price"
    request(price_path, {"expectedVersion": 1, "unitPrice": 4000, "active": True, "reason": "Цена ниже уценки"},
            method="PUT", **admin)
    new = request(scan + saved["labelCode"])
    assert new["unitPrice"] == 4000 and "lotPriceVersion" not in new["lots"][0]
    assert request(quote, {"items": [{**mixed, "lotId": marked, "priceVersion": 2, "quantity": 1}]}) == {
        "total": 4000, "discountAmount": 0}
    print("PASS: lot markdown and label scope, expired/missing expiry, exact scan, quote and sale versions, 2+1, refund, lower base price")


if __name__ == "__main__":
    main()
