#!/bin/sh
set -eu
psql --username "$POSTGRES_USER" --dbname "$POSTGRES_DB" --set ON_ERROR_STOP=1 <<'SQL'
\getenv admin_subject KASA_ADMIN_SUBJECT
BEGIN;
INSERT INTO organizations (id, name)
VALUES ('10000000-0000-0000-0000-000000000001', 'Касса Минимаркет');
INSERT INTO stores (org_id, id, name)
VALUES ('10000000-0000-0000-0000-000000000001',
        '10000000-0000-0000-0000-000000000011', 'Основная точка');
INSERT INTO staff (org_id, id, subject, role)
VALUES ('10000000-0000-0000-0000-000000000001',
        '10000000-0000-0000-0000-000000000034', :'admin_subject', 'admin');
INSERT INTO staff_stores (org_id, staff_id, store_id)
VALUES ('10000000-0000-0000-0000-000000000001',
        '10000000-0000-0000-0000-000000000034',
        '10000000-0000-0000-0000-000000000011');
COMMIT;
SQL
