"""Integration/security checks for native Android authentication."""
import base64
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
OWNER_PASSWORD = os.environ["POS_TEST_MOBILE_OWNER_PASSWORD"]
EMPLOYEE_PASSWORD = os.environ["POS_TEST_MOBILE_EMPLOYEE_PASSWORD"]
OWNER_LOGIN = "android.owner"
EMPLOYEE_A_LOGIN = "android.cashier.a"
EMPLOYEE_B_LOGIN = "android.cashier.b"
STORE = "00000000-0000-0000-0000-000000000011"
FOREIGN_STORE = "00000000-0000-0000-0000-000000000012"
REGISTER = "00000000-0000-0000-0000-000000000021"
DEVICE = "00000000-0000-0000-0000-000000009102"
EMPLOYEE_A = "00000000-0000-0000-0000-000000009101"
EMPLOYEE_B = "00000000-0000-0000-0000-000000009103"


def oidc_token(subject="admin-owner"):
    def enc(value):
        return base64.urlsafe_b64encode(value).rstrip(b"=")
    now = int(time.time())
    header = enc(b'{"alg":"HS256","typ":"JWT"}')
    payload = enc(json.dumps({"iss": "kasa-local", "aud": AUDIENCE, "sub": subject,
                              "iat": now, "exp": now + 300}).encode())
    message = header + b"." + payload
    return (message + b"." + enc(hmac.digest(JWT_KEY, message, hashlib.sha256))).decode()


def call(path, body=None, expected=200, method=None, bearer="oidc", session=None, role=None, headers=None):
    request_headers = {}
    if bearer == "oidc":
        request_headers["Authorization"] = "Bearer " + oidc_token()
    elif bearer:
        request_headers["Authorization"] = "Bearer " + bearer
    if body is not None:
        request_headers["Content-Type"] = "application/json"
    if session:
        request_headers["X-Operator-Session"] = session
    if role:
        request_headers["X-Active-Role"] = role
    request_headers.update(headers or {})
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(BASE + path, data=data, headers=request_headers, method=method)
    try:
        response = urllib.request.urlopen(request, timeout=45)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read()
        assert response.status == expected, (path, expected, response.status, payload)
        parsed = json.loads(payload) if payload else None
        return parsed, response.headers


def sql(statement):
    container = os.environ["POS_TEST_DB_CONTAINER"]
    completed = subprocess.run(
        ["docker", "exec", container, "psql", "-X", "-U", "kasa_owner", "-d", "kasa",
         "-v", "ON_ERROR_STOP=1", "-Atc", statement],
        capture_output=True, check=False)
    assert completed.returncode == 0, completed.stderr.decode(errors="replace")
    return completed.stdout.decode().strip()


def owner_headers(access, operator):
    return {"bearer": access, "session": operator, "role": "admin"}


def cashier_headers(access, operator, device_token):
    return {"bearer": access, "session": operator, "role": "cashier",
            "headers": {"X-Register-Id": REGISTER, "X-Register-Device-Token": device_token}}


def main():
    spec, _ = call("/openapi/v1.json")
    for path in ("/v1/mobile-auth/login", "/v1/mobile-auth/refresh",
                 "/v1/mobile-auth/current", "/v1/mobile-auth/switch",
                 "/v1/mobile-auth/owner/reauthenticate", "/v1/mobile-auth/operator-sessions"):
        assert path in spec["paths"]
    assert spec["paths"]["/v1/mobile-auth/login"]["post"].get("security") == []
    assert spec["paths"]["/v1/mobile-auth/refresh"]["post"].get("security") == []

    # A mobile password for an existing OIDC owner can only be established after OIDC confirmation.
    denied, _ = call("/v1/mobile-auth/credentials",
                     {"login": OWNER_LOGIN, "password": OWNER_PASSWORD, "deviceId": DEVICE},
                     expected=401, bearer=None)
    assert denied["code"] in {"oidc_session_expired", "mobile_session_invalid"}
    configured, _ = call("/v1/mobile-auth/credentials",
                         {"login": OWNER_LOGIN, "password": OWNER_PASSWORD, "deviceId": DEVICE})
    assert configured["login"] == OWNER_LOGIN and configured["mobileEnabled"] is True

    bad, _ = call("/v1/mobile-auth/login",
                  {"login": OWNER_LOGIN, "password": "wrong-pass-123", "deviceId": DEVICE},
                  expected=401, bearer=None)
    assert bad["code"] == "mobile_credentials_invalid"

    owner, _ = call("/v1/mobile-auth/login",
                    {"login": OWNER_LOGIN, "password": OWNER_PASSWORD, "deviceId": DEVICE},
                    bearer=None)
    owner_access = owner["accessToken"]
    owner_refresh = owner["refreshToken"]
    assert owner["tokenType"] == "Bearer" and owner["operator"]["isOwner"] is True
    assert owner["ownerRoleExpiresAt"] is not None

    stores, _ = call("/v1/me/stores?offset=0", bearer=owner_access)
    assert {item["storeId"] for item in stores["items"]} == {
        STORE, "00000000-0000-0000-0000-000000000013"}
    roles, _ = call(f"/v1/me/roles?storeId={STORE}", bearer=owner_access)
    assert roles["owner"] is True and set(roles["roles"]) == {"admin", "cashier", "receiver"}
    foreign, _ = call(f"/v1/me/roles?storeId={FOREIGN_STORE}", bearer=owner_access, expected=404)
    assert foreign["code"] == "store_not_found"

    owner_operator, _ = call("/v1/mobile-auth/operator-sessions", {"storeId": STORE}, bearer=owner_access)
    owner_operator_token = owner_operator["operatorSessionToken"]
    admin = owner_headers(owner_access, owner_operator_token)

    employees = []
    for employee_id, first_name, login, roles_to_assign in (
            (EMPLOYEE_A, "MobileA", EMPLOYEE_A_LOGIN, ["cashier", "receiver"]),
            (EMPLOYEE_B, "MobileB", EMPLOYEE_B_LOGIN, ["cashier"])):
        employee, _ = call(f"/v1/stores/{STORE}/employees",
                           {"employeeId": employee_id, "firstName": first_name, "lastName": "Test",
                            "roles": roles_to_assign, "password": EMPLOYEE_PASSWORD,
                            "reason": "mobile integration employee"},
                           headers={"Idempotency-Key": str(uuid.uuid4())}, **admin)
        credential, _ = call(f"/v1/stores/{STORE}/employees/{employee_id}/mobile-credentials",
                             {"expectedVersion": employee["version"], "login": login,
                              "newPassword": None, "reason": "enable native Android login"},
                             method="PUT", **admin)
        assert credential["mobileEnabled"] is True and credential["staffId"] == employee_id
        employees.append((employee, credential))

    registers, _ = call(f"/v1/stores/{STORE}/registers?offset=0", **admin)
    register = next(item for item in registers if item["registerId"] == REGISTER)
    assigned, _ = call(f"/v1/stores/{STORE}/registers/{REGISTER}/cashiers",
                       {"expectedVersion": register["version"],
                        "cashierIds": [EMPLOYEE_A, EMPLOYEE_B],
                        "reason": "Android device cashiers"}, method="PUT", **admin)
    assert set(assigned["cashierIds"]) == {EMPLOYEE_A, EMPLOYEE_B}
    binding, _ = call(f"/v1/stores/{STORE}/registers/{REGISTER}/device-bindings",
                      {"deviceName": "Android integration phone", "reason": "owner approved Android device"},
                      **admin)
    device_token = binding["registerDeviceToken"]
    assert binding["deviceId"] == DEVICE and binding["registerId"] == REGISTER

    # Switching revokes the previous mobile/operator sessions but preserves the device binding.
    employee_a, _ = call("/v1/mobile-auth/switch",
                         {"login": EMPLOYEE_A_LOGIN, "password": EMPLOYEE_PASSWORD}, bearer=owner_access)
    call("/v1/mobile-auth/current", bearer=owner_access, expected=401)
    call("/v1/operator-sessions/current", bearer=employee_a["accessToken"],
         session=owner_operator_token, role="admin", expected=401)
    employee_a_operator, _ = call("/v1/mobile-auth/operator-sessions", {"storeId": STORE},
                                  bearer=employee_a["accessToken"])
    assert employee_a_operator["registerId"] == REGISTER
    assert set(employee_a_operator["operator"]["roles"]) == {"cashier", "receiver"}

    # Refresh simulates restoring authentication after an application restart.
    refreshed, _ = call("/v1/mobile-auth/refresh", {"refreshToken": employee_a["refreshToken"]}, bearer=None)
    call("/v1/mobile-auth/current", bearer=employee_a["accessToken"], expected=401)
    current, _ = call("/v1/mobile-auth/current", bearer=refreshed["accessToken"])
    assert current["operator"]["staffId"] == EMPLOYEE_A
    call("/v1/mobile-auth/refresh", {"refreshToken": employee_a["refreshToken"]},
         bearer=None, expected=401)

    shift_a = str(uuid.uuid4())
    cashier_a = cashier_headers(refreshed["accessToken"], employee_a_operator["operatorSessionToken"], device_token)
    opened, _ = call("/v1/shifts", {"shiftId": shift_a, "openingCash": 1000}, **cashier_a)
    assert opened["shiftId"] == shift_a
    assert sql(f"SELECT cashier_id FROM shifts WHERE id='{shift_a}'::uuid") == EMPLOYEE_A
    call(f"/v1/shifts/{shift_a}/close", {"countedCash": 1000}, **cashier_a)

    employee_b, _ = call("/v1/mobile-auth/switch",
                         {"login": EMPLOYEE_B_LOGIN, "password": EMPLOYEE_PASSWORD},
                         bearer=refreshed["accessToken"])
    employee_b_operator, _ = call("/v1/mobile-auth/operator-sessions", {"storeId": STORE},
                                  bearer=employee_b["accessToken"])
    assert employee_b_operator["registerId"] == REGISTER
    assert employee_b_operator["operator"]["roles"] == ["cashier"]
    shift_b = str(uuid.uuid4())
    cashier_b = cashier_headers(employee_b["accessToken"], employee_b_operator["operatorSessionToken"], device_token)
    opened_b, _ = call("/v1/shifts", {"shiftId": shift_b, "openingCash": 1500}, **cashier_b)
    assert opened_b["shiftId"] == shift_b
    assert sql(f"SELECT cashier_id FROM shifts WHERE id='{shift_b}'::uuid") == EMPLOYEE_B
    call(f"/v1/shifts/{shift_b}/close", {"countedCash": 1500}, **cashier_b)

    owner_again, _ = call("/v1/mobile-auth/switch",
                          {"login": OWNER_LOGIN, "password": OWNER_PASSWORD}, bearer=employee_b["accessToken"])
    owner_again_operator, _ = call("/v1/mobile-auth/operator-sessions", {"storeId": STORE},
                                   bearer=owner_again["accessToken"])
    owner_again_admin = owner_headers(owner_again["accessToken"], owner_again_operator["operatorSessionToken"])
    sql("UPDATE mobile_sessions SET owner_verified_at=now()-interval '32 minutes', "
        "owner_role_expires_at=now()-interval '2 minutes' "
        f"WHERE id='{owner_again['sessionId']}'::uuid")
    expired, _ = call(f"/v1/stores/{STORE}/employees?offset=0&limit=50", expected=403, **owner_again_admin)
    assert expired["code"] == "owner_reauthentication_required"
    background_refresh, _ = call("/v1/mobile-auth/refresh",
                                 {"refreshToken": owner_again["refreshToken"]}, bearer=None)
    still_expired_admin = owner_headers(background_refresh["accessToken"],
                                        owner_again_operator["operatorSessionToken"])
    still_expired, _ = call(f"/v1/stores/{STORE}/employees?offset=0&limit=50",
                            expected=403, **still_expired_admin)
    assert still_expired["code"] == "owner_reauthentication_required"
    reauthenticated, _ = call("/v1/mobile-auth/owner/reauthenticate",
                              {"storeId": STORE, "login": OWNER_LOGIN, "password": OWNER_PASSWORD},
                              bearer=background_refresh["accessToken"])
    assert reauthenticated["ownerRoleExpiresAt"] is not None
    call(f"/v1/stores/{STORE}/employees?offset=0&limit=50", **still_expired_admin)

    # Disabling an employee revokes both mobile and operator sessions immediately.
    employee_rows, _ = call(f"/v1/stores/{STORE}/employees?offset=0&limit=50", **still_expired_admin)
    employee_b_row = next(item for item in employee_rows["items"] if item["employeeId"] == EMPLOYEE_B)
    call(f"/v1/stores/{STORE}/employees/{EMPLOYEE_B}/disable",
         {"expectedVersion": employee_b_row["version"], "reason": "mobile revocation test"},
         **still_expired_admin)
    disabled, _ = call("/v1/mobile-auth/login",
                       {"login": EMPLOYEE_B_LOGIN, "password": EMPLOYEE_PASSWORD, "deviceId": DEVICE},
                       bearer=None, expected=401)
    assert disabled["code"] == "mobile_credentials_invalid"

    call("/v1/mobile-auth/current", method="DELETE", bearer=background_refresh["accessToken"], expected=204)
    call("/v1/mobile-auth/current", bearer=background_refresh["accessToken"], expected=401)
    call("/v1/mobile-auth/refresh", {"refreshToken": background_refresh["refreshToken"]},
         bearer=None, expected=401)

    # Only digests/hashes are persisted; plaintext credentials and bearer tokens are absent.
    leaked = sql("SELECT count(*) FROM employee_credentials WHERE password_hash IN ("
                 f"'{OWNER_PASSWORD.replace("'", "''")}', '{EMPLOYEE_PASSWORD.replace("'", "''")}')")
    assert leaked == "0"
    raw_tokens = sql("SELECT count(*) FROM mobile_sessions WHERE octet_length(access_token_digest) <> 32 "
                     "OR octet_length(refresh_token_digest) <> 32")
    assert raw_tokens == "0"
    audit_secrets = sql("SELECT count(*) FROM mobile_auth_audit_events WHERE reason LIKE '%" +
                        EMPLOYEE_PASSWORD.replace("'", "''") + "%'")
    assert audit_secrets == "0"
    print("PASS: native Android login, refresh, switch, register binding, owner reauthentication and revocation")


if __name__ == "__main__":
    main()
