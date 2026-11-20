BEGIN;

DROP TABLE IF EXISTS mobile_auth_audit_events;
DROP TABLE IF EXISTS mobile_login_rate_limits;
DROP TABLE IF EXISTS mobile_sessions;
DROP INDEX IF EXISTS employee_credentials_mobile_login_unique;
ALTER TABLE employee_credentials DROP CONSTRAINT IF EXISTS employee_credentials_mobile_login_check;
ALTER TABLE employee_credentials
    DROP COLUMN IF EXISTS mobile_enabled,
    DROP COLUMN IF EXISTS mobile_login_normalized,
    DROP COLUMN IF EXISTS mobile_login;

COMMIT;
