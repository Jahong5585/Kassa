BEGIN;

ALTER TABLE stores
    ADD COLUMN platform_version integer NOT NULL DEFAULT 1 CHECK (platform_version > 0);

ALTER TABLE platform_store_events
    ADD COLUMN reason text NOT NULL DEFAULT 'legacy' CHECK (length(reason) BETWEEN 1 AND 500),
    ADD COLUMN previous_name text,
    ADD COLUMN new_name text;

COMMENT ON COLUMN stores.platform_version IS
    'Optimistic concurrency version for global platform changes.';
COMMENT ON COLUMN platform_store_events.reason IS
    'Required operator reason for a global platform change.';

GRANT SELECT, INSERT, UPDATE ON staff, staff_stores TO kasa_api;
GRANT SELECT, INSERT ON staff_roles TO kasa_api;

COMMIT;
