"""Server-side integration checks for Desktop 0.8.16 receiving routes."""
import base64
from datetime import date, timedelta
import hashlib
import hmac
import json
import os
import subprocess
import time
import urllib.error
import urllib.request
import uuid


BASE = os.environ["POS_TEST_URL"]
JWT_KEY = os.environ["Auth__DevelopmentKey"].encode()
AUDIENCE = os.environ["Auth__Audience"]
CONTAINER = os.environ["POS_TEST_DB_CONTAINER"]
STORE = "00000000-0000-0000-0000-000000000011"
FOREIGN_STORE = "00000000-0000-0000-0000-000000000012"
DEVICE = "00000000-0000-0000-0000-000000009202"
SHARED_ORG = "00000000-0000-0000-0000-000000000203"
SHARED_STORE = "00000000-0000-0000-0000-000000000213"
SHARED_PRODUCT = "00000000-0000-0000-0000-000000000223"
TEST_PRODUCT = "00000000-0000-0000-0000-000000000224"
BARCODE = "4780022620139"


def token():
    def enc(value):
        return base64.urlsafe_b64encode(value).rstrip(b"=")
    now = int(time.time())
    header = enc(b'{"alg":"HS256","typ":"JWT"}')
    payload = enc(json.dumps({"iss": "kasa-local", "aud": AUDIENCE, "sub": "admin-owner",
                              "iat": now, "exp": now + 600}).encode())
    message = header + b"." + payload
    return (message + b"." + enc(hmac.digest(JWT_KEY, message, hashlib.sha256))).decode()


def call(path, body=None, expected=200, method=None, session=None, role=None, authenticated=True):
    headers = {}
    if authenticated:
        headers["Authorization"] = "Bearer " + token()
    if body is not None:
        headers["Content-Type"] = "application/json"
    if session:
        headers["X-Operator-Session"] = session
    if role:
        headers["X-Active-Role"] = role
    request = urllib.request.Request(BASE + path,
        data=json.dumps(body).encode() if body is not None else None, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=30)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read()
        assert response.status == expected, (path, expected, response.status, payload)
        return json.loads(payload) if payload else None


def sql(statement):
    completed = subprocess.run(["docker", "exec", CONTAINER, "psql", "-X", "-U", "kasa_owner", "-d", "kasa",
                                "-v", "ON_ERROR_STOP=1", "-Atc", statement], capture_output=True, check=False)
    assert completed.returncode == 0, completed.stderr.decode(errors="replace")
    return completed.stdout.decode().strip()


def main():
    sql(f"""
        UPDATE organizations SET is_test=true WHERE id='00000000-0000-0000-0000-000000000002'::uuid;
        UPDATE stores SET is_test=true WHERE org_id='00000000-0000-0000-0000-000000000002'::uuid;
        INSERT INTO products (org_id,id,name,unit,requires_expiry,barcode,brand,category,shelf_life_days,revision)
        VALUES ('00000000-0000-0000-0000-000000000002','{TEST_PRODUCT}',
                'TEST SECRET PRODUCT','piece',true,'{BARCODE}','Hidden','Hidden',30,99);
        INSERT INTO product_barcodes (org_id,code,product_id,quantity_factor)
        VALUES ('00000000-0000-0000-0000-000000000002','{BARCODE}','{TEST_PRODUCT}',1);
        INSERT INTO store_prices (org_id,store_id,product_id,unit_price,version)
        VALUES ('00000000-0000-0000-0000-000000000002','{FOREIGN_STORE}','{TEST_PRODUCT}',777777,1);
        INSERT INTO organizations (id,name,is_test) VALUES ('{SHARED_ORG}','Shared production catalogue',false);
        INSERT INTO stores (org_id,id,name,is_test) VALUES ('{SHARED_ORG}','{SHARED_STORE}','Shared store',false);
        INSERT INTO products (org_id,id,name,unit,requires_expiry,barcode,brand,category,shelf_life_days,revision)
        VALUES ('{SHARED_ORG}','{SHARED_PRODUCT}','Pepsi 0.5l','piece',true,'{BARCODE}','Pepsi','Drinks',180,2);
        INSERT INTO product_barcodes (org_id,code,product_id,quantity_factor)
        VALUES ('{SHARED_ORG}','{BARCODE}','{SHARED_PRODUCT}',1);
        INSERT INTO store_prices (org_id,store_id,product_id,unit_price,version)
        VALUES ('{SHARED_ORG}','{SHARED_STORE}','{SHARED_PRODUCT}',12345,1);
        INSERT INTO suppliers (org_id,store_id,code,name,tax_id,phone,email,payment_terms,lead_time_days,active,version)
        VALUES ('00000000-0000-0000-0000-000000000001','{STORE}','DEMO','Test supplier','','','','',0,true,1);
    """)

    protected = f"/v1/stores/{STORE}/receiving/catalog/shared-barcodes/{BARCODE}"
    problem = call(protected, expected=401, authenticated=False)
    assert problem["status"] == 401

    operators = call(f"/v1/stores/{STORE}/operators")
    owner = next(item for item in operators["items"] if item["isOwner"])
    operator = call(f"/v1/stores/{STORE}/operator-sessions",
                    {"operatorId": owner["operatorId"], "password": None, "deviceId": DEVICE})
    session = operator["operatorSessionToken"]

    shared = call(protected, session=session, role="receiver")
    assert shared == {"barcode": BARCODE, "name": "Pepsi 0.5l", "unit": "piece",
                      "requiresExpiry": True, "category": "Drinks", "brand": "Pepsi",
                      "shelfLifeDays": 180}
    assert not ({"productId", "storeId", "organizationId", "unitPrice", "stock"} & set(shared))

    product_id = str(uuid.uuid4())
    product = {"name": "Pepsi 0.5l", "unit": "piece", "requiresExpiry": True,
               "category": "Drinks", "brand": "Pepsi", "shelfLifeDays": 180,
               "barcodes": [{"code": BARCODE, "quantityFactor": 1}]}
    create = {"productId": product_id, "initialSalePrice": 8000, "product": product}
    created = call(f"/v1/stores/{STORE}/receiving/catalog/priced-products", create,
                   session=session, role="receiver")
    assert created["productId"] == product_id and created["initialSalePrice"] == 8000
    replay = call(f"/v1/stores/{STORE}/receiving/catalog/priced-products", create,
                  session=session, role="receiver")
    assert replay == created
    changed = json.loads(json.dumps(create))
    changed["product"]["name"] = "Different body"
    conflict = call(f"/v1/stores/{STORE}/receiving/catalog/priced-products", changed,
                    expected=409, session=session, role="receiver")
    assert conflict["code"] == "product_id_reused"
    assert sql(f"SELECT count(*) FROM receiving_product_creations WHERE request_id='{product_id}'::uuid") == "1"
    assert sql(f"SELECT unit_price FROM store_prices WHERE store_id='{STORE}'::uuid "
               f"AND product_id='{product_id}'::uuid") == "8000.00"

    reopened_product = call(f"/v1/stores/{STORE}/receiving/catalog/products/{product_id}",
                            session=session, role="receiver")
    assert reopened_product["shelfLifeDays"] == 180 and reopened_product["brand"] == "Pepsi"

    receipt_id = str(uuid.uuid4())
    lot_id = str(uuid.uuid4())
    made = date.today() - timedelta(days=10)
    expires = made + timedelta(days=180)
    receipt = {"receiptId": receipt_id, "supplierRef": "DEMO", "invoiceNumber": "DESKTOP-0816",
               "invoiceDate": date.today().isoformat(), "items": [{"productId": product_id,
               "lotId": lot_id, "expiryDate": expires.isoformat(), "quantity": 10,
               "manufacturedOn": made.isoformat(), "proposedUnitCost": 5500}]}
    draft = call(f"/v1/stores/{STORE}/receipts/with-proposed-costs", receipt,
                 session=session, role="receiver")
    assert draft["items"][0]["proposedUnitCost"] == 5500
    reopened = call(f"/v1/stores/{STORE}/receipts/{receipt_id}", session=session, role="receiver")
    assert reopened["items"][0]["manufacturedOn"] == made.isoformat()
    assert reopened["items"][0]["proposedUnitCost"] == 5500

    invalid_item = dict(receipt["items"][0])
    invalid_item["expiryDate"] = (expires + timedelta(days=1)).isoformat()
    invalid = call(f"/v1/stores/{STORE}/receipts/{receipt_id}/with-proposed-costs",
                   {"expectedVersion": 1, "items": [invalid_item]}, method="PUT", expected=400,
                   session=session, role="receiver")
    assert invalid["code"] == "invalid_manufacture_date"

    submitted = call(f"/v1/stores/{STORE}/receipts/{receipt_id}/submit", {"expectedVersion": 1},
                     session=session, role="receiver")
    missing_costs = call(f"/v1/stores/{STORE}/receipts/{receipt_id}/post",
                         {"expectedVersion": submitted["version"]}, expected=409,
                         session=session, role="admin")
    assert missing_costs["code"] == "receipt_costs_required"
    costs = call(f"/v1/stores/{STORE}/receipts/{receipt_id}/costs",
                 {"expectedVersion": submitted["version"], "items": [{"productId": product_id,
                  "lotId": lot_id, "unitPrice": 5500, "discount": 0, "includedTax": 0,
                  "allocatedCharges": 0}]}, method="PUT", session=session, role="admin")
    assert costs["total"] == 55000 and costs["status"] == "reviewed"
    posted = call(f"/v1/stores/{STORE}/receipts/{receipt_id}/post",
                  {"expectedVersion": costs["receiptVersion"]}, session=session, role="admin")
    assert posted["status"] == "posted"

    zero_receipt_id = str(uuid.uuid4())
    zero_lot_id = str(uuid.uuid4())
    zero_item = dict(receipt["items"][0])
    zero_item["lotId"] = zero_lot_id
    zero_item["proposedUnitCost"] = 0
    zero_receipt = dict(receipt)
    zero_receipt.update(receiptId=zero_receipt_id, invoiceNumber="DESKTOP-0816-ZERO", items=[zero_item])
    zero_draft = call(f"/v1/stores/{STORE}/receipts/with-proposed-costs", zero_receipt,
                      session=session, role="receiver")
    assert zero_draft["items"][0]["proposedUnitCost"] == 0
    zero_item["quantity"] = 11
    zero_edited = call(f"/v1/stores/{STORE}/receipts/{zero_receipt_id}/with-proposed-costs",
                       {"expectedVersion": 1, "items": [zero_item]}, method="PUT",
                       session=session, role="receiver")
    assert zero_edited["version"] == 2 and zero_edited["items"][0]["proposedUnitCost"] == 0
    print("PASS: Desktop 0.8.16 receiving catalogue, idempotency, costs, dates and access control")


if __name__ == "__main__":
    main()
