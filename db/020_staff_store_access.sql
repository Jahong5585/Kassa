BEGIN;
ALTER TABLE staff_stores ADD COLUMN active boolean NOT NULL DEFAULT true;
GRANT INSERT (org_id, id, subject, role) ON staff TO kasa_api;
GRANT INSERT (org_id, staff_id, store_id), UPDATE (active) ON staff_stores TO kasa_api;
COMMIT;
