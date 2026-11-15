"""T-04: register a weighed label, reprice the lot and verify safe checkout."""
from concurrent.futures import ThreadPoolExecutor
from uuid import uuid4

from smoke import fixture_id as fid, request, sale


def main():
    store, product, lot = (fid(n) for n in (11, 42, 51))
    admin = {"staff": "admin-a"}
    root = f"/v1/stores/{store}"
    price = f"{root}/catalog/products/{product}/price"
    lot_path = f"{root}/stock/lots/{product}/{lot}"
    issue = lot_path + "/weighed-price-labels"
    scan = "/v1/catalog/barcodes/"
    start = request(scan + "002")["lots"][0]["quantity"]

    def set_price(version, amount):
        return request(price, {"expectedVersion": version, "unitPrice": amount, "active": True,
                               "reason": "Переоценка T-04"}, method="PUT", **admin)

    label_request = {"labelId": str(uuid4()), "weight": 0.375}
    request(issue, label_request, expected=403)
    request(issue, label_request, staff="receiver", expected=403)
    request(issue, label_request, mfa=False, expected=403, **admin)
    request(issue, {**label_request, "weight": 0.0001}, expected=400, **admin)
    request(issue, {**label_request, "weight": 100}, expected=409, **admin)
    request(issue.replace(product, fid(41)), label_request, expected=409, **admin)
    request(scan + "WPL-damaged", expected=400)
    request(scan + "WPL-" + uuid4().hex, expected=404)

    set_price(1, 16400)
    old = request(issue, label_request, **admin)
    assert old["printedTotal"] == 6150 and old["weight"] == 0.375
    assert old["barcode"] == "WPL-" + label_request["labelId"].replace("-", "")
    assert request(scan + old["barcode"].lower())["priceLabel"] == old
    request(f"/v1/stores/{fid(12)}/staff",
            {"subject": "cashier-label-foreign", "role": "cashier", "registerId": fid(23)}, staff="admin-foreign")
    request(scan + old["barcode"], staff="cashier-label-foreign", register=23, expected=404)

    shift = str(uuid4())
    request("/v1/shifts", {"shiftId": shift, "openingCash": 50000})
    quote = f"/v1/shifts/{shift}/quote"
    body = sale(shift, (42, 51, 0.375), method="cash", tendered=7000)
    item = {**body["items"][0], "priceVersion": 2, "priceLabelId": old["labelId"]}
    body["items"] = [item]
    assert request(quote, {"items": [item]}) == {"total": 6150, "discountAmount": 0}
    cart_id = str(uuid4())
    cart_path = f"/v1/shifts/{shift}/carts/{cart_id}"
    assert request(cart_path, {"expectedVersion": 0, "items": [item]}, method="PUT")["items"] == [item]
    assert request("/v1/sales", {**body, "cartId": cart_id, "cartVersion": 1,
               "items": [{k: v for k, v in item.items() if k != "priceLabelId"}]}, expected=409
               )["code"] == "parked_cart_items_changed"

    set_price(2, 18000)
    assert request(issue, label_request, **admin) == old
    assert request(issue, {**label_request, "weight": 0.500}, expected=409, **admin
                   )["code"] == "weighed_price_label_id_reused"
    result = request(scan + old["barcode"])
    assert result["priceLabel"] == old and result["unitPrice"] == 18000 and result["lots"][0]["quantity"] == start
    item["priceVersion"] = 3
    assert request(quote, {"items": [item]}, expected=409)["code"] == "weighed_price_label_changed"
    assert request("/v1/sales", body, expected=409)["code"] == "weighed_price_label_changed"
    assert request(scan + "002")["lots"][0]["quantity"] == start
    assert request(cart_path)["lines"][0]["status"] == "priceChanged"

    high = request(issue, {"labelId": str(uuid4()), "weight": 0.375}, **admin)
    assert high["printedTotal"] == 6750
    request(lot_path + "/retail", {"expectedVersion": 0, "unitPrice": 14000, "reason": "Уценка"},
            method="PUT", **admin)
    changed = request(scan + high["barcode"])
    assert changed["priceLabel"]["printedTotal"] == 6750
    assert changed["lots"][0]["markdownUnitPrice"] == 14000
    item.update(priceLabelId=high["labelId"], lotPriceVersion=1)
    assert request(quote, {"items": [item]}, expected=409)["code"] == "weighed_price_label_changed"

    label = request(issue, {"labelId": str(uuid4()), "weight": 0.375}, **admin)
    assert label["printedTotal"] == 5250
    item["priceLabelId"] = label["labelId"]
    for change in ({"quantity": 0.400}, {"priceLabelId": str(uuid4())}, {"productId": fid(43), "lotPriceVersion": None,
                                                                      "priceVersion": 1}):
        assert request(quote, {"items": [{**item, **change}]}, expected=409)["code"] == "weighed_price_label_unavailable"
    assert request(quote, {"items": [item]}) == {"total": 5250, "discountAmount": 0}

    other_shift = str(uuid4())
    other = {"staff": "cashier-b", "register": 22}
    request("/v1/shifts", {"shiftId": other_shift, "openingCash": 50000}, **other)
    second = {**body, "operationId": str(uuid4()), "shiftId": other_shift}
    def try_sale(args):
        payload, access = args
        try:
            return request("/v1/sales", payload, **access)
        except AssertionError as err:
            _, _, status, detail = err.args[0]
            assert status == 409 and b"weighed_price_label_unavailable" in detail, err
            return None

    with ThreadPoolExecutor(2) as executor:
        attempts = list(executor.map(try_sale, [(body, {}), (second, other)]))
    assert sum(attempt is not None for attempt in attempts) == 1
    won = attempts[0] is not None
    sold = attempts[0] if won else attempts[1]
    access = {} if won else other
    assert sold["total"] == 5250 and sold["lines"][0]["priceLabelId"] == label["labelId"]
    assert request("/v1/sales/" + sold["operationId"], **access) == sold
    assert request("/v1/sales", body if won else second, **access) == sold
    assert request("/v1/sales", {**(body if won else second), "items": [{**item, "priceLabelId": high["labelId"]}]},
                   expected=409, **access)["code"] == "operation_id_reused"
    assert request(scan + label["barcode"], expected=409)["code"] == "weighed_price_label_unavailable"
    assert request(scan + "002")["lots"][0]["quantity"] == start - 0.375
    print("PASS: T-04 issue and retry, exact lot and weight, repricing, markdown, role/store isolation, parked-cart binding, concurrent single-use")


if __name__ == "__main__":
    main()
