"""Promotion and refund acceptance on a fresh, disposable fixtures.sql database."""
from decimal import Decimal
from uuid import uuid4
from smoke import fixture_id, request, sale


def check_refunds(shift, sold, product, lot, parts):
    for quantity, amount in parts:
        refund = {"operationId": str(uuid4()), "shiftId": shift, "sourceRegisterId": fixture_id(21),
                  "saleId": sold["operationId"], "reason": "Учебный возврат", "items": [
                      {"productId": product, "lotId": lot, "quantity": quantity, "destination": "stock"}]}
        returned = request("/v1/refunds", refund)
        assert Decimal(str(returned["total"])) == Decimal(amount)
        assert request("/v1/refunds", refund) == returned


def main():
    store = fixture_id(11)
    product = fixture_id(41)
    lot = fixture_id(51)
    promotion = f"/v1/stores/{store}/catalog/products/{product}/promotion"
    price = f"/v1/stores/{store}/catalog/products/{product}/price"
    admin = {"staff": "admin-a"}
    assert request(promotion, **admin) == {"version": 0, "enabled": False}
    enable = {"expectedVersion": 0, "enabled": True, "reason": "Учебная акция 2+1"}
    request(promotion, enable, method="PUT", expected=403)
    request(promotion, enable, method="PUT", staff="receiver", expected=403)
    request(promotion, enable, method="PUT", staff="admin-foreign", expected=404)
    request(promotion, enable, method="PUT", mfa=False, expected=403, **admin)
    weighted = promotion.replace(product, fixture_id(42))
    assert request(weighted, enable, method="PUT", expected=409, **admin)["code"] == "quantity_promotion_unavailable"
    new_price = request(price, {"expectedVersion": 1, "unitPrice": 10000, "active": True, "reason": "Учебная цена"},
                        method="PUT", **admin)
    active = request(promotion, enable, method="PUT", **admin)
    assert active == {"version": 1, "enabled": True}
    assert request(promotion, enable, method="PUT", **admin) == active
    assert request(promotion, {**enable, "reason": "Подмена"}, method="PUT", expected=409, **admin)["code"] == \
        "quantity_promotion_version_conflict"
    item = request("/v1/catalog/barcodes/001")
    assert item["promotionVersion"] == 1 and item["unitPrice"] == 10000
    shift = str(uuid4())
    request("/v1/shifts", {"shiftId": shift, "openingCash": 50000})
    quote = f"/v1/shifts/{shift}/quote"
    checkout = sale(shift, (41, 51, 3), method="cash", tendered=20000)
    checkout["items"][0].update(priceVersion=new_price["version"])
    assert request(quote, {"items": checkout["items"]}, expected=409)["code"] == "quantity_promotion_changed"
    checkout["items"][0]["promotionVersion"] = 1
    assert request(quote, {"items": checkout["items"]}) == {"total": 20000, "discountAmount": 10000}
    assert request(quote, {"items": [{**checkout["items"][0], "quantity": 2}]}) == {"total": 20000, "discountAmount": 0}
    combined = {**checkout, "discount": {"kind": "amount", "value": 100, "reason": "Совмещение"}}
    assert request("/v1/sales", combined, expected=409)["code"] == "quantity_promotion_discount_conflict"
    assert request("/v1/catalog/barcodes/001")["lots"][0]["quantity"] == 3
    original = request("/v1/sales", checkout)
    assert original["total"] == 20000 and original["discountReason"] == "2+1" and original["changeDue"] == 0
    assert original["lines"][0]["discountAmount"] == 10000 and original["lines"][0]["promotionVersion"] == 1
    assert request("/v1/sales/" + checkout["operationId"]) == original
    assert request("/v1/sales", checkout) == original
    changed = {**checkout, "items": [{**checkout["items"][0], "promotionVersion": 2}]}
    assert request("/v1/sales", changed, expected=409)["code"] == "operation_id_reused"

    current_price = request(price, {"expectedVersion": new_price["version"], "unitPrice": 12000, "active": True,
                                    "reason": "Учебная переоценка"}, method="PUT", **admin)
    check_refunds(shift, original, product, lot, [(1, "6666.67"), (2, "13333.33")])
    assert request("/v1/catalog/barcodes/001")["lots"][0]["quantity"] == 3

    cart_id = str(uuid4())
    cart_path = f"/v1/shifts/{shift}/carts/{cart_id}"
    cart_item = {**checkout["items"][0], "priceVersion": current_price["version"]}
    saved_cart = request(cart_path, {"expectedVersion": 0, "items": [cart_item]}, method="PUT")
    assert saved_cart["items"][0]["promotionVersion"] == 1
    assert request(cart_path)["lines"][0]["currentPromotionVersion"] == 1
    disabled = request(promotion, {"expectedVersion": 1, "enabled": False, "reason": "Окончание акции"},
                       method="PUT", **admin)
    assert disabled == {"version": 2, "enabled": False}
    assert request("/v1/sales", checkout) == original
    review = request(cart_path)
    assert review["cart"]["items"][0]["promotionVersion"] == 1
    assert "currentPromotionVersion" not in review["lines"][0]
    assert request(quote, {"items": [cart_item]}, expected=409)["code"] == "quantity_promotion_changed"
    del cart_item["promotionVersion"]
    assert request(quote, {"items": [cart_item]}) == {"total": 36000, "discountAmount": 0}
    recovered = request("/v1/sales", {**checkout, "operationId": str(uuid4()), "tendered": 36000,
                                     "items": [cart_item], "cartId": cart_id, "cartVersion": saved_cart["version"]})
    assert recovered["total"] == 36000 and recovered["discountAmount"] == 0
    assert request(cart_path)["cart"]["status"] == "sold"
    # The first lot has 1.59 paid for six pieces. Five pieces are exactly 1.325.
    # Keep the half-cent tie and use the remainder for the final returned piece.
    small_product = fixture_id(144)
    small_price = request(price.replace(product, small_product),
                          {"expectedVersion": 1, "unitPrice": 0.37, "active": True, "reason": "Проверка округления"},
                          method="PUT", **admin)
    request(promotion.replace(product, small_product), enable, method="PUT", **admin)
    small = sale(shift, (144, 154, 6), (144, 155, 1), method="cash", tendered=1.85)
    for item in small["items"]:
        item.update(priceVersion=small_price["version"], promotionVersion=1)
    assert request(quote, {"items": small["items"]}) == {"total": 1.85, "discountAmount": 0.74}
    small_sale = request("/v1/sales", small)
    assert small_sale["lines"][0]["lineTotal"] == 1.59 and small_sale["lines"][0]["discountAmount"] == 0.63
    assert small_sale["lines"][1]["lineTotal"] == 0.26 and small_sale["lines"][1]["discountAmount"] == 0.11
    check_refunds(shift, small_sale, small_product, fixture_id(154), [(5, "1.33"), (1, "0.26")])
    print("PASS: 2+1 scope, versions, quote, no discount stacking, 20,000 paid, 6,666.67 + 13,333.33 refunds, expired promotion in parked cart and replay")


if __name__ == "__main__":
    main()
