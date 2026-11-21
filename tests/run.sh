#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
restore_check=false
promotion_check=false
lot_retail_check=false
label_check=false
if [[ "${1:-}" == "--restore" && "$#" == 1 ]]; then
    restore_check=true
elif [[ "${1:-}" == "--promotions" && "$#" == 1 ]]; then
    promotion_check=true
elif [[ "${1:-}" == "--price-labels" && "$#" == 1 ]]; then
    label_check=true
elif [[ "${1:-}" == "--lot-retail" && "$#" == 1 ]]; then
    lot_retail_check=true
elif [[ "$#" != 0 ]]; then
    echo "Usage: bash tests/run.sh [--restore | --promotions | --lot-retail | --price-labels]" >&2
    exit 2
fi
dotnet_command=dotnet
if [[ -x /d/CodexTools/dotnet/dotnet.exe ]]; then
    dotnet_command=/d/CodexTools/dotnet/dotnet.exe
fi
command -v "$dotnet_command" >/dev/null || { echo "Install the .NET 10 SDK" >&2; exit 127; }
"$dotnet_command" --list-sdks | grep -q '^10\.' || { echo "Install the .NET 10 SDK" >&2; exit 127; }
command -v docker >/dev/null || { echo "Install Docker with the Compose plugin" >&2; exit 127; }
if command -v python3 >/dev/null; then python_command=python3
elif command -v python >/dev/null; then python_command=python
else echo "Install Python 3" >&2; exit 127
fi
docker compose version >/dev/null
export COMPOSE_PROJECT_NAME="kasa-test-$$-${RANDOM}"
compose=(docker compose --file compose.yaml --project-name "$COMPOSE_PROJECT_NAME")
export POS_DB_PORT="${POS_TEST_DB_PORT:-55432}"
api_port="${POS_TEST_API_PORT:-5189}"
hex() { "$python_command" -c 'import secrets; print(secrets.token_hex(32))'; }
export POS_DB_OWNER_PASSWORD="$(hex)"
export POS_DB_APP_PASSWORD="$(hex)"
export POS_TEST_REGISTER_KEY="$(hex)"
export Auth__DevelopmentKey="$(hex)"
export Auth__Authority=kasa-local
export Auth__AllowLocalTestDeveloper=true
export Auth__LocalTestDeveloperSigningKey="$(hex)$(hex)"
export Auth__LocalTestDeveloperTokenMinutes=30
export Auth__Audience=kasa-smoke
export Auth__AdminAcr=kasa-test-webauthn
export Auth__AllowTemporaryPlatformDeveloperMfaOnly=true
export Auth__TemporaryPlatformDeveloperIssuer=kasa-local
export Auth__TemporaryPlatformDeveloperSubject=10000000-0000-0000-0000-000000000001
export Auth__TemporaryPlatformDeveloperExpiresAt="$($python_command -c 'import datetime; print((datetime.datetime.now(datetime.timezone.utc) + datetime.timedelta(hours=1)).isoformat())')"
export Refunds__CashierLimit=20000
export Sales__MaxManualDiscountPercent=10
export Purchasing__ReplenishmentRefreshSeconds=1
export Ai__ProcurementEnabled=true
export GROQ_API_KEY=test-groq-key
export RateLimiting__PermitLimit=2000
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://127.0.0.1:$api_port"
export POS_TEST_LOCAL_PASSWORD="A7!$(hex | cut -c1-24)"
export POS_TEST_LOCAL_HASH="$("$python_command" tests/password_hash.py)"
export POS_TEST_OPERATOR_PASSWORD="Op9!$(hex | cut -c1-24)"
export POS_TEST_DB_CONTAINER="${COMPOSE_PROJECT_NAME}-db-1"
export POS_TEST_URL="$ASPNETCORE_URLS"
export ConnectionStrings__Pos="Host=127.0.0.1;Port=$POS_DB_PORT;Database=kasa;Username=kasa_api;Password=$POS_DB_APP_PASSWORD"
api_pid=
scanner_pid=
groq_pid=
keycloak_pid=
scanner_port_file="$(mktemp)"
groq_port_file="$(mktemp)"
keycloak_port_file="$(mktemp)"
logs="$(mktemp)"
restore_dir=
cleanup() {
    if [[ -n "$api_pid" ]]; then kill "$api_pid" 2>/dev/null || true; fi
    "${compose[@]}" down --volumes >/dev/null 2>&1 || true
    if [[ -n "$scanner_pid" ]]; then kill "$scanner_pid" 2>/dev/null || true; fi
    if [[ -n "$groq_pid" ]]; then kill "$groq_pid" 2>/dev/null || true; fi
    if [[ -n "$keycloak_pid" ]]; then kill "$keycloak_pid" 2>/dev/null || true; fi
    rm -f "$logs" "$scanner_port_file" "$groq_port_file" "$keycloak_port_file"
    if [[ -n "$restore_dir" ]]; then rm -rf "$restore_dir"; fi
}
trap cleanup EXIT
"$python_command" tests/clamd_stub.py "$scanner_port_file" &
scanner_pid=$!
for ((i=0; i<100; i++)); do
    if [[ -s "$scanner_port_file" ]]; then break; fi
    sleep 0.05
done
export InvoiceScanner__Host=127.0.0.1
export InvoiceScanner__Port="$(cat "$scanner_port_file")"
[[ "$InvoiceScanner__Port" =~ ^[0-9]+$ ]] || { echo "Scanner fixture failed to start" >&2; exit 1; }
"$python_command" tests/groq_stub.py "$groq_port_file" &
groq_pid=$!
for ((i=0; i<100; i++)); do
    if [[ -s "$groq_port_file" ]]; then break; fi
    sleep 0.05
done
groq_port="$(cat "$groq_port_file")"
[[ "$groq_port" =~ ^[0-9]+$ ]] || { echo "Groq fixture failed to start" >&2; exit 1; }
export Ai__Endpoint="http://127.0.0.1:$groq_port/openai/v1/"
export GROQ_TEST_URL="http://127.0.0.1:$groq_port"
"$python_command" tests/keycloak_admin_stub.py "$keycloak_port_file" &
keycloak_pid=$!
for ((i=0; i<100; i++)); do
    if [[ -s "$keycloak_port_file" ]]; then break; fi
    sleep 0.05
done
keycloak_port="$(cat "$keycloak_port_file")"
[[ "$keycloak_port" =~ ^[0-9]+$ ]] || { echo "Keycloak fixture failed to start" >&2; exit 1; }
export IdentityProvisioning__Enabled=true
export IdentityProvisioning__BaseUrl="http://127.0.0.1:$keycloak_port/auth/"
export IdentityProvisioning__Realm=kasa
export IdentityProvisioning__ClientId=kasa-platform-provisioner
export IdentityProvisioning__ClientSecret="$(hex)$(hex)"
"${compose[@]}" up -d --wait
if "$restore_check"; then
    pg_version=$("${compose[@]}" exec -T db psql -XAt -U kasa_owner -d kasa -v ON_ERROR_STOP=1 -c 'SHOW server_version_num')
    [[ "$pg_version" =~ ^17[0-9]{4}$ ]] || { echo "Expected PostgreSQL 17, found $pg_version" >&2; exit 1; }
    printf 'PASS: PostgreSQL 17 server (%s)\n' "$pg_version"
fi
"${compose[@]}" exec -T -e POS_TEST_REGISTER_KEY db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/fixtures.sql
"${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/operator_auth_setup.sql
"${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/replenishment_schedule.sql
"${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/sales_period_fixtures.sql
"${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/stock_movement_report_fixtures.sql
"${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/finance_expenses_fixtures.sql
"${compose[@]}" exec -T -e POS_TEST_LOCAL_HASH db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/local_test_developer_fixture.sql
if "$restore_check"; then
    business_counts_before=$("${compose[@]}" exec -T db psql -XAt -U kasa_owner -d kasa -v ON_ERROR_STOP=1 \
        -c "SELECT json_build_array((SELECT count(*) FROM organizations),(SELECT count(*) FROM stores),(SELECT count(*) FROM staff),(SELECT count(*) FROM products),(SELECT count(*) FROM sales));")
    "${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < db/rollback/042_operating_expenses.down.sql
    [[ "$("${compose[@]}" exec -T db psql -XAt -U kasa_owner -d kasa -c "SELECT to_regclass('public.operating_expenses') IS NULL;")" == "t" ]] || \
        { echo "Migration 042 rollback left operating_expenses behind" >&2; exit 1; }
    "${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < db/042_operating_expenses.sql
    business_counts_after=$("${compose[@]}" exec -T db psql -XAt -U kasa_owner -d kasa -v ON_ERROR_STOP=1 \
        -c "SELECT json_build_array((SELECT count(*) FROM organizations),(SELECT count(*) FROM stores),(SELECT count(*) FROM staff),(SELECT count(*) FROM products),(SELECT count(*) FROM sales));")
    [[ "$business_counts_before" == "$business_counts_after" ]] || \
        { echo "Migration 042 changed existing business data" >&2; exit 1; }
    [[ "$("${compose[@]}" exec -T db psql -XAt -U kasa_owner -d kasa -c "SELECT has_table_privilege('kasa_api','operating_expenses','SELECT,INSERT') AND has_column_privilege('kasa_api','operating_expenses','reversal_id','UPDATE');")" == "t" ]] || \
        { echo "Migration 042 runtime grants are incomplete" >&2; exit 1; }
    echo "PASS: migration 042 upgrade and rollback preserve existing organizations, stores, staff, products and sales"
fi
"$dotnet_command" build src/Kasa.Api/Kasa.Api.csproj -c Release -warnaserror -p:RestoreLockedMode=true
"$dotnet_command" test tests/Kasa.Api.Tests/Kasa.Api.Tests.csproj -c Release -warnaserror -p:RestoreLockedMode=true
guard_log="$(mktemp)"
if env -u Auth__DevelopmentKey ASPNETCORE_ENVIRONMENT=Production ASPNETCORE_URLS="http://127.0.0.1:$((api_port + 1))" \
    Auth__AllowLocalTestDeveloper=true "$dotnet_command" src/Kasa.Api/bin/Release/net10.0/Kasa.Api.dll >"$guard_log" 2>&1; then
    echo "Local test mode unexpectedly started outside Development" >&2; exit 1
fi
grep -q "permitted only in Development" "$guard_log" || { cat "$guard_log" >&2; exit 1; }
if env ASPNETCORE_ENVIRONMENT=Development ASPNETCORE_URLS="http://0.0.0.0:$((api_port + 1))" \
    Auth__AllowLocalTestDeveloper=true "$dotnet_command" src/Kasa.Api/bin/Release/net10.0/Kasa.Api.dll >"$guard_log" 2>&1; then
    echo "Local test mode unexpectedly started on a non-loopback address" >&2; exit 1
fi
grep -q "only on an explicit 127.0.0.1" "$guard_log" || { cat "$guard_log" >&2; exit 1; }
rm -f "$guard_log"
env 'Ai__ProcurementAccess__00000000-0000-0000-0000-000000000001__00000000-0000-0000-0000-000000000011__00000000-0000-0000-0000-000000000036=true' \
    'Ai__ProcurementAccess__00000000-0000-0000-0000-000000000001__00000000-0000-0000-0000-000000000011__00000000-0000-0000-0000-000000003046=true' \
    'Ai__ProcurementAccess__00000000-0000-0000-0000-000000000001__00000000-0000-0000-0000-000000000011__00000000-0000-0000-0000-000000003047=true' \
    'Ai__ProcurementAccess__00000000-0000-0000-0000-000000000001__00000000-0000-0000-0000-000000000013__00000000-0000-0000-0000-000000003048=true' \
    "$dotnet_command" src/Kasa.Api/bin/Release/net10.0/Kasa.Api.dll > "$logs" 2>&1 &
api_pid=$!
for ((i=0; i<60; i++)); do
    if curl -fsS "$POS_TEST_URL/health/live" >/dev/null 2>&1; then break; fi
    if ! kill -0 "$api_pid" 2>/dev/null || ((i==59)); then
        cat "$logs" >&2
        exit 1
    fi
    sleep 1
done
if "$promotion_check"; then
    "$python_command" tests/quantity_promotions.py
elif "$lot_retail_check"; then
    "$python_command" tests/lot_retail.py
elif "$label_check"; then
    "$python_command" tests/weighed_price_labels.py
else
    if ! "$python_command" tests/smoke.py; then
        cat "$logs" >&2
        exit 1
    fi
    if ! "$python_command" tests/release_compatibility.py; then
        cat "$logs" >&2
        exit 1
    fi
    "${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/sales_period_audit.sql
fi
if ! "$python_command" tests/local_test_developer.py; then
    cat "$logs" >&2
    exit 1
fi
if ! "$python_command" tests/operator_auth.py; then
    cat "$logs" >&2
    exit 1
fi
"${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/operator_auth_security.sql
if ! "$promotion_check" && ! "$lot_retail_check" && ! "$label_check"; then
    if ! "$python_command" tests/receiving_api.py; then
        cat "$logs" >&2
        exit 1
    fi
fi
if grep -Fq -- "$POS_TEST_OPERATOR_PASSWORD" "$logs"; then
    echo "Operator password leaked into application logs" >&2
    exit 1
fi
if "$restore_check"; then
    kill "$api_pid"
    wait "$api_pid" || true
    api_pid=
    restore_dir="$(mktemp -d)"
    "${compose[@]}" exec -T db psql -X --csv -U kasa_owner -d kasa -v ON_ERROR_STOP=1 \
        < tests/backup_control.sql > "$restore_dir/before.csv"
    source_expenses=$("${compose[@]}" exec -T db psql -XAt -U kasa_owner -d kasa -v ON_ERROR_STOP=1 \
        -c "SELECT count(*) || ':' || coalesce(sum(amount),0) || ':' || count(*) FILTER (WHERE reversed_at IS NOT NULL) FROM operating_expenses;")
    "${compose[@]}" exec -T db pg_dump -U kasa_owner -d kasa --format=custom > "$restore_dir/kasa.dump"
    "${compose[@]}" exec -T db createdb -U kasa_owner --template=template0 kasa_restore
    "${compose[@]}" exec -T db pg_restore -U kasa_owner -d kasa_restore --exit-on-error \
        < "$restore_dir/kasa.dump"
    "${compose[@]}" exec -T db psql -X --csv -U kasa_owner -d kasa_restore -v ON_ERROR_STOP=1 \
        < tests/backup_control.sql > "$restore_dir/after.csv"
    restored_expenses=$("${compose[@]}" exec -T db psql -XAt -U kasa_owner -d kasa_restore -v ON_ERROR_STOP=1 \
        -c "SELECT count(*) || ':' || coalesce(sum(amount),0) || ':' || count(*) FILTER (WHERE reversed_at IS NOT NULL) FROM operating_expenses;")
    [[ "$source_expenses" == "$restored_expenses" ]] || \
        { echo "Restored operating expenses differ from source" >&2; exit 1; }
    "$python_command" - "$restore_dir/before.csv" "$restore_dir/after.csv" <<'PY'
import csv
import sys
from decimal import Decimal


def read_control(path):
    with open(path, newline="", encoding="utf-8") as file:
        rows = list(csv.DictReader(file))
    assert len(rows) == 1, "Expected one checkout from the smoke register"
    row = rows[0]
    assert row["sale_id"] and row["shift_id"] and row["closed_at"] and row["audit_id"]
    assert row["method"] == "manualCashless"
    assert row["register_active"] == "f"
    assert Decimal(row["total"]) == Decimal("18.00")
    assert Decimal(row["movement_quantity"]) == Decimal("-0.001")
    assert Decimal(row["remaining_quantity"]) >= 0
    return row


assert read_control(sys.argv[1]) == read_control(sys.argv[2]), "Restored checkout or stock differs from source"
print("PASS: PostgreSQL 17 backup restored into a separate database; sale, shift, movement, stock and audit match")
PY
fi
