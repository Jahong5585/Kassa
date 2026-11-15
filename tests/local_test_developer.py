"""Acceptance checks for the loopback-only Development platform login."""
import base64
import hashlib
import hmac
import json
import os
import socket
import time
import urllib.error
import urllib.request
import uuid

BASE = os.environ["POS_TEST_URL"]
PASSWORD = os.environ.get("POS_TEST_LOCAL_PASSWORD") or os.environ["LOCAL_TEST_DEVELOPER_PASSWORD"]
AUDIENCE = os.environ["Auth__Audience"]
DEVELOPMENT_KEY = os.environ["Auth__DevelopmentKey"].encode()


def call(path, body=None, token=None, expected=200, method=None, base=BASE, include_headers=False):
    headers = {}
    data = None
    if body is not None:
        headers["Content-Type"] = "application/json"
        data = json.dumps(body).encode()
    if token:
        headers["Authorization"] = "Bearer " + token
    request = urllib.request.Request(base + path, data=data, headers=headers, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=5)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read()
        assert response.status == expected, (path, expected, response.status, payload)
        result = json.loads(payload) if payload else None
        return (result, response.headers) if include_headers else result


def ordinary_token(subject, mfa=False, auth_time=None):
    encode = lambda value: base64.urlsafe_b64encode(value).rstrip(b"=")
    header = encode(b'{"alg":"HS256","typ":"JWT","kid":"development"}')
    now = int(time.time())
    claims = {"iss": "kasa-local", "aud": AUDIENCE, "sub": subject, "iat": now, "exp": now + 300}
    if mfa:
        claims["acr"] = os.environ["Auth__AdminAcr"]
        claims["auth_time"] = now if auth_time is None else auth_time
    payload = encode(json.dumps(claims).encode())
    content = header + b"." + payload
    return (content + b"." + encode(hmac.digest(DEVELOPMENT_KEY, content, hashlib.sha256))).decode()


def payload(token):
    raw = token.split(".")[1]
    return json.loads(base64.urlsafe_b64decode(raw + "=" * (-len(raw) % 4)))


def main():
    status = call("/development/platform/status")
    assert status == {"enabled": True, "environment": "Development", "loopbackOnly": True}

    login = call("/development/platform/login", {"login": "developer-test", "password": PASSWORD})
    assert set(login) == {"accessToken", "tokenType", "expiresIn", "expiresAt"}
    assert login["tokenType"] == "Bearer" and 0 < login["expiresIn"] <= 3600
    claims = payload(login["accessToken"])
    assert claims["iss"] == "kasa-local" and claims["sub"] == "developer-test"
    assert claims["aud"] == AUDIENCE and claims["local_test_developer"] is True
    assert claims["exp"] - claims["iat"] <= 3600 and "refresh_token" not in login

    call("/development/platform/login", {"login": "developer-test", "password": PASSWORD + "x"}, expected=401)

    token = login["accessToken"]
    created = call("/v1/platform/test-stores", {"name": "Автотест " + uuid.uuid4().hex[:8]}, token, method="POST")
    assert created["isTest"] is True and created["storeId"] and created["organizationId"]
    renamed = call("/v1/platform/test-stores/" + created["storeId"], {"name": "Автотест переименован"},
                   token, method="PATCH")
    assert renamed["isTest"] is True and renamed["name"] == "Автотест переименован"
    listed = call("/v1/platform/test-stores", token=token)
    assert any(row["storeId"] == created["storeId"] and row["isTest"] is True for row in listed)

    identity = call("/v1/platform/me", token=token)
    assert identity["canRead"] is True and identity["canWrite"] is True
    organization_id = str(uuid.uuid4())
    store_id = str(uuid.uuid4())
    platform_created = call("/v1/platform/stores", {
        "organizationId": organization_id,
        "organizationName": "Тестовая организация",
        "storeId": store_id,
        "storeName": "Тестовый магазин",
        "timeZone": "Asia/Tashkent",
        "ownerSubject": None,
        "reason": "Автоматическая проверка контракта Windows-клиента",
    }, token, method="POST")
    assert platform_created["storeId"] == store_id and platform_created["version"] == 1
    assert platform_created["ownerSubject"] is None
    page = call("/v1/platform/stores?offset=0", token=token)
    row = next(item for item in page["items"] if item["storeId"] == store_id)
    assert row["organizationName"] == "Тестовая организация" and row["isTest"] is True
    assert row["ownerSubject"] is None
    renamed_platform = call(
        "/v1/platform/organizations/" + organization_id + "/stores/" + store_id + "/name",
        {"expectedVersion": 1, "name": "Тестовый магазин 2", "reason": "Проверка переименования"},
        token, method="PUT")
    assert renamed_platform["version"] == 2 and renamed_platform["storeName"] == "Тестовый магазин 2"
    owner, owner_headers = call(
        "/v1/platform/organizations/" + organization_id + "/stores/" + store_id + "/owner-account",
        {"username": "owner." + uuid.uuid4().hex[:12], "firstName": "Test", "lastName": "Owner",
         "email": None, "replaceSubject": None, "reason": "Создание тестового владельца"},
        token, method="POST", include_headers=True)
    assert owner["subject"] and owner["username"].startswith("owner.")
    password = owner["temporaryPassword"]
    assert len(password) >= 16 and any(c.isupper() for c in password) and any(c.islower() for c in password)
    assert any(c.isdigit() for c in password) and any(not c.isalnum() for c in password)
    assert owner["passwordTemporary"] is True
    assert owner["requiredActions"] == ["UPDATE_PASSWORD"]
    assert "no-store" in owner_headers.get("Cache-Control", "")
    owner_roles = call("/v1/me/roles?storeId=" + store_id, token=ordinary_token(owner["subject"]))
    assert owner_roles["owner"] is True
    assert owner_roles["roles"] == ["admin", "cashier", "receiver"]
    page_after_owner = call("/v1/platform/stores?offset=0", token=token)
    owner_row = next(item for item in page_after_owner["items"] if item["storeId"] == store_id)
    assert owner_row["ownerSubject"] == owner["subject"]
    audit = call("/v1/platform/audit?offset=0", token=token)
    assert any(event["storeId"] == store_id and event["reason"] == "Проверка переименования"
               for event in audit["items"])
    assert any(event["storeId"] == store_id and event["action"] == "assign_owner"
               for event in audit["items"])

    developer = "10000000-0000-0000-0000-000000000001"
    call("/v1/platform/test-stores", token=ordinary_token(developer), expected=403)
    temporary = call("/v1/platform/test-stores", token=ordinary_token(developer, mfa=True))
    assert any(row["storeId"] == created["storeId"] for row in temporary)
    call("/v1/platform/test-stores",
         token=ordinary_token(developer, mfa=True, auth_time=int(time.time()) - 601), expected=403)
    call("/v1/platform/test-stores", token=ordinary_token("cashier-a"), expected=403)

    port = BASE.rsplit(":", 1)[1]
    addresses = [address for address in socket.gethostbyname_ex(socket.gethostname())[2]
                 if not address.startswith("127.")]
    assert addresses, "No non-loopback address available for the remote-access check"
    try:
        call("/development/platform/status", base="http://" + addresses[0] + ":" + port)
    except (urllib.error.URLError, TimeoutError, OSError):
        pass
    else:
        raise AssertionError("The Development API accepted a non-loopback connection")

    print("PASS: local login, bounded temporary MFA-only developer, denial paths and test-store isolation")


if __name__ == "__main__":
    main()
