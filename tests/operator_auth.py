"""Focused integration/security checks for store operator authentication."""
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
PASSWORD = os.environ["POS_TEST_OPERATOR_PASSWORD"]
STORE = "00000000-0000-0000-0000-000000000011"
SECOND_STORE = "00000000-0000-0000-0000-000000000013"
FOREIGN_STORE = "00000000-0000-0000-0000-000000000012"
EMPLOYEE = "00000000-0000-0000-0000-000000009001"
DEVICE = "00000000-0000-0000-0000-000000009002"
SECOND_EMPLOYEE = "00000000-0000-0000-0000-000000009003"
REGISTER = "00000000-0000-0000-0000-000000000021"


def token(subject="admin-owner"):
    def enc(value):
        return base64.urlsafe_b64encode(value).rstrip(b"=")
    now = int(time.time())
    header = enc(b'{"alg":"HS256","typ":"JWT"}')
    payload = enc(json.dumps({"iss": "kasa-local", "aud": AUDIENCE, "sub": subject,
                              "iat": now, "exp": now + 300}).encode())
    message = header + b"." + payload
    return (message + b"." + enc(hmac.digest(JWT_KEY, message, hashlib.sha256))).decode()


def call(path, body=None, expected=200, method=None, session=None, role=None, headers=None):
    request_headers = {"Authorization": "Bearer " + token()}
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
        response = urllib.request.urlopen(request, timeout=20)
    except urllib.error.HTTPError as error:
        response = error
    with response:
        payload = response.read()
        assert response.status == expected, (path, expected, response.status, payload)
        parsed = json.loads(payload) if payload else None
        return parsed, response.headers


def expire_session(session_id):
    session = str(uuid.UUID(session_id))
    container = os.environ["POS_TEST_DB_CONTAINER"]
    sql = ("UPDATE operator_sessions SET idle_expires_at=now()-interval '1 minute', "
           "absolute_expires_at=now()-interval '1 minute' WHERE id='" + session + "'::uuid")
    completed = subprocess.run(["docker", "exec", container, "psql", "-X", "-U", "kasa_owner", "-d", "kasa",
                                "-v", "ON_ERROR_STOP=1", "-c", sql], capture_output=True, check=False)
    assert completed.returncode == 0, completed.stderr.decode(errors="replace")


def main():
    spec, _ = call("/openapi/v1.json")
    bootstrap = spec["paths"]["/v1/me/stores"]["get"]["security"][0]
    business = spec["paths"]["/v1/stores/{storeId}/employees"]["get"]["security"][0]
    assert set(bootstrap) == {"Bearer"}
    assert set(business) == {"Bearer", "OperatorSession"}
    employee_schema = next(schema for schema in spec["components"]["schemas"].values()
                           if "employeeId" in schema.get("properties", {}) and
                           "password" in schema.get("properties", {}))
    assert employee_schema["properties"]["password"]["minLength"] == 8
    assert employee_schema["properties"]["password"]["maxLength"] == 128
    assert set(employee_schema["properties"]["roles"]["items"]["enum"]) == {"admin", "cashier", "receiver"}
    if os.environ.get("Auth__RequireOperatorSession", "false").lower() == "true":
        required, _ = call(f"/v1/me/roles?storeId={STORE}", expected=403)
        assert required["code"] == "operator_session_required"

    stores, _ = call("/v1/me/stores")
    assert {item["storeId"] for item in stores["items"]} == {STORE, SECOND_STORE}
    assert all(item["isOwner"] is True for item in stores["items"])
    operators, _ = call(f"/v1/stores/{STORE}/operators")
    assert len(operators["items"]) == 1 and operators["items"][0]["isOwner"] is True
    empty_store, _ = call(f"/v1/stores/{SECOND_STORE}/operators")
    assert len(empty_store["items"]) == 1 and empty_store["items"][0]["isOwner"] is True

    owner_id = operators["items"][0]["operatorId"]
    owner_session, _ = call(f"/v1/stores/{STORE}/operator-sessions",
                            {"operatorId": owner_id, "password": None, "deviceId": DEVICE})
    owner_token = owner_session["operatorSessionToken"]
    assert owner_session["operator"]["roles"] == ["admin", "cashier", "receiver"]
    call("/v1/operator-sessions/current", session=owner_token, role="admin")
    call(f"/v1/stores/{FOREIGN_STORE}/employees", expected=409, session=owner_token, role="admin")

    create = {"employeeId": EMPLOYEE, "firstName": "Саида", "lastName": "Каримова",
              "roles": ["cashier", "receiver"], "password": PASSWORD,
              "reason": "integration security test"}
    idempotency = str(uuid.uuid4())
    employee, _ = call(f"/v1/stores/{STORE}/employees", create, session=owner_token, role="admin",
                       headers={"Idempotency-Key": idempotency})
    assert employee["roles"] == ["cashier", "receiver"] and "password" not in employee
    replay, _ = call(f"/v1/stores/{STORE}/employees", create, session=owner_token, role="admin",
                     headers={"Idempotency-Key": idempotency})
    assert replay == employee
    conflict = dict(create, lastName="Другая")
    problem, _ = call(f"/v1/stores/{STORE}/employees", conflict, expected=409,
                      session=owner_token, role="admin", headers={"Idempotency-Key": idempotency})
    assert problem["code"] == "idempotency_conflict"
    single, _ = call(f"/v1/stores/{STORE}/employees",
                     {"employeeId": SECOND_EMPLOYEE, "firstName": "Азиз", "lastName": "Тестов",
                      "roles": ["cashier"], "password": PASSWORD, "reason": "single role test"},
                     session=owner_token, role="admin", headers={"Idempotency-Key": str(uuid.uuid4())})
    assert single["roles"] == ["cashier"]
    owner_denied, _ = call(f"/v1/stores/{STORE}/employees",
                           {"employeeId": str(uuid.uuid4()), "firstName": "Owner", "lastName": "Forbidden",
                            "roles": ["owner"], "password": PASSWORD, "reason": "owner role test"},
                           expected=400, session=owner_token, role="admin",
                           headers={"Idempotency-Key": str(uuid.uuid4())})
    assert owner_denied["code"] == "invalid_employee_roles"

    invalid, _ = call(f"/v1/stores/{STORE}/operator-sessions",
                      {"operatorId": EMPLOYEE, "password": PASSWORD + "x", "deviceId": DEVICE}, expected=401)
    assert invalid["code"] == "operator_credentials_invalid"
    employee_session, _ = call(f"/v1/stores/{STORE}/operator-sessions",
                                {"operatorId": EMPLOYEE, "password": PASSWORD, "deviceId": DEVICE})
    employee_token = employee_session["operatorSessionToken"]
    assert employee_session["operator"]["roles"] == ["cashier", "receiver"]
    denied, _ = call(f"/v1/stores/{STORE}/registers", expected=403, session=employee_token, role="admin")
    assert denied["code"] == "active_role_denied"

    updated, _ = call(f"/v1/stores/{STORE}/employees/{EMPLOYEE}/roles",
                      {"expectedVersion": employee["version"], "roles": ["cashier"],
                       "reason": "integration role change"}, method="PUT", session=owner_token, role="admin")
    expired, _ = call("/v1/operator-sessions/current", expected=401, session=employee_token, role="cashier")
    assert expired["code"] in ("operator_session_expired", "operator_session_invalid")

    before_reset, _ = call(f"/v1/stores/{STORE}/operator-sessions",
                           {"operatorId": EMPLOYEE, "password": PASSWORD, "deviceId": DEVICE})

    new_password = PASSWORD + "N"
    reset, _ = call(f"/v1/stores/{STORE}/employees/{EMPLOYEE}/reset-password",
                    {"expectedVersion": updated["version"], "newPassword": new_password,
                     "reason": "integration reset"}, session=owner_token, role="admin")
    call("/v1/operator-sessions/current", expected=401,
         session=before_reset["operatorSessionToken"], role="cashier")
    fresh, _ = call(f"/v1/stores/{STORE}/operator-sessions",
                    {"operatorId": EMPLOYEE, "password": new_password, "deviceId": DEVICE})
    fresh_token = fresh["operatorSessionToken"]
    disabled, _ = call(f"/v1/stores/{STORE}/employees/{EMPLOYEE}/disable",
                       {"expectedVersion": reset["version"], "reason": "integration disable"},
                       session=owner_token, role="admin")
    call("/v1/operator-sessions/current", expected=401, session=fresh_token, role="cashier")
    disabled_login, _ = call(f"/v1/stores/{STORE}/operator-sessions",
                             {"operatorId": EMPLOYEE, "password": new_password, "deviceId": DEVICE}, expected=401)
    assert disabled_login["code"] == "operator_credentials_invalid"
    enabled, _ = call(f"/v1/stores/{STORE}/employees/{EMPLOYEE}/enable",
                      {"expectedVersion": disabled["version"], "reason": "integration enable"},
                      session=owner_token, role="admin")
    assert enabled["active"] is True

    expiring, _ = call(f"/v1/stores/{STORE}/operator-sessions",
                       {"operatorId": EMPLOYEE, "password": new_password, "deviceId": DEVICE})
    expire_session(expiring["sessionId"])
    expired_problem, _ = call("/v1/operator-sessions/current", expected=401,
                              session=expiring["operatorSessionToken"], role="cashier")
    assert expired_problem["code"] == "operator_session_expired"

    working, _ = call(f"/v1/stores/{STORE}/operator-sessions",
                      {"operatorId": EMPLOYEE, "password": new_password, "deviceId": DEVICE})
    working_token = working["operatorSessionToken"]
    register, _ = call(f"/v1/stores/{STORE}/registers/{REGISTER}/cashiers",
                       {"expectedVersion": 1, "cashierIds": [EMPLOYEE, owner_id],
                        "reason": "integration cashier assignment"}, method="PUT",
                       session=owner_token, role="admin")
    assert set(register["cashierIds"]) == {EMPLOYEE, owner_id}
    call("/v1/shifts", {"shiftId": str(uuid.uuid4()), "openingCash": 1000},
         session=working_token, role="cashier",
         headers={"X-Register-Id": REGISTER, "X-Register-Key": os.environ["POS_TEST_REGISTER_KEY"]})
    changed_password = new_password + "C"
    call("/v1/operator-sessions/current/change-password",
         {"currentPassword": new_password, "newPassword": changed_password},
         expected=204, session=working_token, role="cashier")
    call("/v1/operator-sessions/current", session=working_token, role="cashier")

    rate_limited = False
    for attempt in range(4):
        problem, response_headers = call(f"/v1/stores/{STORE}/operator-sessions",
                                         {"operatorId": EMPLOYEE, "password": "wrong-password",
                                          "deviceId": DEVICE}, expected=429 if attempt == 3 else 401)
        if problem["code"] == "operator_login_rate_limited":
            assert "Retry-After" in response_headers
            rate_limited = True
            break
    assert rate_limited
    print("PASS: operator bootstrap, owner/employee sessions, idempotency, revocation, isolation and rate limiting")


if __name__ == "__main__":
    main()
