\getenv password_hash POS_TEST_LOCAL_HASH
BEGIN;
INSERT INTO platform_staff (issuer, subject, password_hash, can_read, can_write, active)
VALUES ('kasa-local', 'developer-test', :'password_hash', true, true, true);
INSERT INTO platform_staff (issuer, subject, can_read, can_write, active)
VALUES ('kasa-local', '10000000-0000-0000-0000-000000000001', true, true, true);
COMMIT;
