BEGIN;

DROP TABLE IF EXISTS operator_audit_events;
DROP TABLE IF EXISTS employee_idempotency;
DROP TABLE IF EXISTS operator_login_rate_limits;
DROP TABLE IF EXISTS operator_sessions;
DROP TABLE IF EXISTS employee_credentials;
DROP TRIGGER IF EXISTS staff_store_roles_legacy_role_compat ON staff_roles;
DROP TRIGGER IF EXISTS staff_store_roles_membership_compat ON staff_stores;
DROP FUNCTION IF EXISTS sync_staff_store_roles_from_legacy_role();
DROP FUNCTION IF EXISTS sync_staff_store_roles_from_membership();
DROP TABLE IF EXISTS staff_store_roles;

DROP INDEX IF EXISTS staff_subject_unique_not_null;
ALTER TABLE staff DROP CONSTRAINT IF EXISTS staff_identity_subject_check;
ALTER TABLE staff ALTER COLUMN subject SET NOT NULL;
ALTER TABLE staff ADD CONSTRAINT staff_subject_key UNIQUE (subject);
ALTER TABLE staff
    DROP COLUMN IF EXISTS updated_at,
    DROP COLUMN IF EXISTS created_at,
    DROP COLUMN IF EXISTS version,
    DROP COLUMN IF EXISTS identity_kind,
    DROP COLUMN IF EXISTS display_name,
    DROP COLUMN IF EXISTS last_name,
    DROP COLUMN IF EXISTS first_name;

COMMIT;
