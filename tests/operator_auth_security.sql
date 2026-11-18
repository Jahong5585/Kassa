DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM employee_credentials
        WHERE staff_id = '00000000-0000-0000-0000-000000009001'
          AND password_hash LIKE '$argon2id$v=19$m=%'
          AND password_hash NOT LIKE '%integration security test%'
    ) THEN
        RAISE EXCEPTION 'employee password was not stored as Argon2id';
    END IF;
    IF EXISTS (
        SELECT 1 FROM operator_audit_events
        WHERE coalesce(reason, '') LIKE '%wrong-password%'
           OR coalesce(reason, '') LIKE '%argon2id%'
    ) THEN
        RAISE EXCEPTION 'credential material leaked into operator audit';
    END IF;
    IF EXISTS (
        SELECT 1 FROM operator_sessions WHERE octet_length(token_digest) <> 32
    ) THEN
        RAISE EXCEPTION 'operator session token digest is invalid';
    END IF;
    IF EXISTS (
        SELECT 1 FROM register_device_bindings WHERE octet_length(token_digest) <> 32
    ) THEN
        RAISE EXCEPTION 'register device token digest is invalid';
    END IF;
    IF NOT EXISTS (
        SELECT 1 FROM audit_events
        WHERE action = 'shift.opened' AND staff_id = '00000000-0000-0000-0000-000000009001'
    ) THEN
        RAISE EXCEPTION 'business audit does not identify the selected employee';
    END IF;
END;
$$;
