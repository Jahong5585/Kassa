#!/usr/bin/env bash
set -euo pipefail
cd "$(dirname "$0")/.."
restore_check=false
if [[ "${1:-}" == "--restore" && "$#" == 1 ]]; then
    restore_check=true
elif [[ "$#" != 0 ]]; then
    echo "Usage: bash tests/run.sh [--restore]" >&2
    exit 2
fi
command -v dotnet >/dev/null || { echo "Install the .NET 10 SDK" >&2; exit 127; }
command -v docker >/dev/null || { echo "Install Docker with the Compose plugin" >&2; exit 127; }
docker compose version >/dev/null
export COMPOSE_PROJECT_NAME="kasa-test-$$-${RANDOM}"
compose=(docker compose --file compose.yaml --project-name "$COMPOSE_PROJECT_NAME")
export POS_DB_PORT="${POS_TEST_DB_PORT:-55432}"
api_port="${POS_TEST_API_PORT:-5189}"
hex() { python3 -c 'import secrets; print(secrets.token_hex(32))'; }
export POS_DB_OWNER_PASSWORD="$(hex)"
export POS_DB_APP_PASSWORD="$(hex)"
export POS_TEST_REGISTER_KEY="$(hex)"
export Auth__DevelopmentKey="$(hex)"
export Auth__Audience=kasa-smoke
export Auth__AdminAcr=kasa-test-webauthn
export Refunds__CashierLimit=20000
export Sales__MaxManualDiscountPercent=10
export Purchasing__ReplenishmentRefreshSeconds=1
export Ai__ProcurementEnabled=true
export GROQ_API_KEY=test-groq-key
export ASPNETCORE_ENVIRONMENT=Development
export ASPNETCORE_URLS="http://127.0.0.1:$api_port"
export POS_TEST_URL="$ASPNETCORE_URLS"
export ConnectionStrings__Pos="Host=127.0.0.1;Port=$POS_DB_PORT;Database=kasa;Username=kasa_api;Password=$POS_DB_APP_PASSWORD"
api_pid=
scanner_pid=
groq_pid=
scanner_port_file="$(mktemp)"
groq_port_file="$(mktemp)"
logs="$(mktemp)"
restore_dir=
cleanup() {
    if [[ -n "$api_pid" ]]; then kill "$api_pid" 2>/dev/null || true; fi
    "${compose[@]}" down --volumes >/dev/null 2>&1 || true
    if [[ -n "$scanner_pid" ]]; then kill "$scanner_pid" 2>/dev/null || true; fi
    if [[ -n "$groq_pid" ]]; then kill "$groq_pid" 2>/dev/null || true; fi
    rm -f "$logs" "$scanner_port_file" "$groq_port_file"
    if [[ -n "$restore_dir" ]]; then rm -rf "$restore_dir"; fi
}
trap cleanup EXIT
python3 tests/clamd_stub.py "$scanner_port_file" &
scanner_pid=$!
for ((i=0; i<100; i++)); do
    if [[ -s "$scanner_port_file" ]]; then break; fi
    sleep 0.05
done
export InvoiceScanner__Host=127.0.0.1
export InvoiceScanner__Port="$(cat "$scanner_port_file")"
[[ "$InvoiceScanner__Port" =~ ^[0-9]+$ ]] || { echo "Scanner fixture failed to start" >&2; exit 1; }
python3 tests/groq_stub.py "$groq_port_file" &
groq_pid=$!
for ((i=0; i<100; i++)); do
    if [[ -s "$groq_port_file" ]]; then break; fi
    sleep 0.05
done
groq_port="$(cat "$groq_port_file")"
[[ "$groq_port" =~ ^[0-9]+$ ]] || { echo "Groq fixture failed to start" >&2; exit 1; }
export Ai__Endpoint="http://127.0.0.1:$groq_port/openai/v1/"
export GROQ_TEST_URL="http://127.0.0.1:$groq_port"
"${compose[@]}" up -d --wait
if "$restore_check"; then
    pg_version=$("${compose[@]}" exec -T db psql -XAt -U kasa_owner -d kasa -v ON_ERROR_STOP=1 -c 'SHOW server_version_num')
    [[ "$pg_version" =~ ^17[0-9]{4}$ ]] || { echo "Expected PostgreSQL 17, found $pg_version" >&2; exit 1; }
    printf 'PASS: PostgreSQL 17 server (%s)\n' "$pg_version"
fi
"${compose[@]}" exec -T -e POS_TEST_REGISTER_KEY db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/fixtures.sql
"${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/replenishment_schedule.sql
"${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/sales_period_fixtures.sql
"${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/stock_movement_report_fixtures.sql
dotnet build src/Kasa.Api/Kasa.Api.csproj -c Release -warnaserror -p:RestoreLockedMode=true
env 'Ai__ProcurementAccess__00000000-0000-0000-0000-000000000001__00000000-0000-0000-0000-000000000011__00000000-0000-0000-0000-000000000036=true' \
    'Ai__ProcurementAccess__00000000-0000-0000-0000-000000000001__00000000-0000-0000-0000-000000000011__00000000-0000-0000-0000-000000003046=true' \
    dotnet src/Kasa.Api/bin/Release/net10.0/Kasa.Api.dll > "$logs" 2>&1 &
api_pid=$!
for ((i=0; i<60; i++)); do
    if curl -fsS "$POS_TEST_URL/health/live" >/dev/null 2>&1; then break; fi
    if ! kill -0 "$api_pid" 2>/dev/null || ((i==59)); then
        cat "$logs" >&2
        exit 1
    fi
    sleep 1
done
if ! python3 tests/smoke.py; then
    cat "$logs" >&2
    exit 1
fi
"${compose[@]}" exec -T db psql -U kasa_owner -d kasa -v ON_ERROR_STOP=1 < tests/sales_period_audit.sql
if "$restore_check"; then
    kill "$api_pid"
    wait "$api_pid" || true
    api_pid=
    restore_dir="$(mktemp -d)"
    "${compose[@]}" exec -T db psql -X --csv -U kasa_owner -d kasa -v ON_ERROR_STOP=1 \
        < tests/backup_control.sql > "$restore_dir/before.csv"
    "${compose[@]}" exec -T db pg_dump -U kasa_owner -d kasa --format=custom > "$restore_dir/kasa.dump"
    "${compose[@]}" exec -T db createdb -U kasa_owner --template=template0 kasa_restore
    "${compose[@]}" exec -T db pg_restore -U kasa_owner -d kasa_restore --exit-on-error \
        < "$restore_dir/kasa.dump"
    "${compose[@]}" exec -T db psql -X --csv -U kasa_owner -d kasa_restore -v ON_ERROR_STOP=1 \
        < tests/backup_control.sql > "$restore_dir/after.csv"
    python3 - "$restore_dir/before.csv" "$restore_dir/after.csv" <<'PY'
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
