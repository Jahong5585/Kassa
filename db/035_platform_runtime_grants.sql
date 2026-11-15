BEGIN;

GRANT SELECT, INSERT, UPDATE ON platform_staff TO kasa_api;
GRANT SELECT, INSERT, UPDATE ON organizations, stores TO kasa_api;
GRANT SELECT, INSERT ON platform_store_events, platform_login_audit TO kasa_api;
GRANT USAGE, SELECT ON SEQUENCE platform_store_events_id_seq, platform_login_audit_id_seq TO kasa_api;

COMMIT;
