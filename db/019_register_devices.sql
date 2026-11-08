BEGIN;
ALTER TABLE registers ADD COLUMN version integer NOT NULL DEFAULT 1 CHECK (version >= 1);
GRANT INSERT (org_id, store_id, id, name, key_digest), UPDATE (key_digest, active, version) ON registers TO kasa_api;
GRANT INSERT (org_id, staff_id, store_id, register_id) ON cashier_registers TO kasa_api;
COMMIT;
