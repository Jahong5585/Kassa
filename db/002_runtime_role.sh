#!/bin/sh
set -eu
psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --set ON_ERROR_STOP=1 <<'SQL'
\getenv runtime_password POS_DB_APP_PASSWORD
BEGIN;
CREATE ROLE kasa_api LOGIN PASSWORD :'runtime_password';
REVOKE CONNECT ON DATABASE kasa FROM PUBLIC;
GRANT CONNECT ON DATABASE kasa TO kasa_api;
REVOKE CREATE ON SCHEMA public FROM PUBLIC;
GRANT USAGE ON SCHEMA public TO kasa_api;
GRANT SELECT ON registers, stores, staff, staff_stores, cashier_registers,
    products, store_prices, stock_lots, shifts, sales, sale_lines TO kasa_api;
GRANT INSERT ON shifts, sales, sale_lines, audit_events TO kasa_api;
GRANT UPDATE (closed_at, counted_cash) ON shifts TO kasa_api;
GRANT UPDATE (quantity) ON stock_lots TO kasa_api;
GRANT USAGE ON SEQUENCE audit_events_id_seq TO kasa_api;
COMMIT;
SQL
