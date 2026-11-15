BEGIN;

ALTER TABLE staff
    ADD COLUMN first_name text,
    ADD COLUMN last_name text,
    ADD COLUMN display_name text,
    ADD COLUMN identity_kind text NOT NULL DEFAULT 'oidc'
        CHECK (identity_kind IN ('oidc', 'local')),
    ADD COLUMN version integer NOT NULL DEFAULT 1 CHECK (version > 0),
    ADD COLUMN created_at timestamptz NOT NULL DEFAULT now(),
    ADD COLUMN updated_at timestamptz NOT NULL DEFAULT now();

ALTER TABLE staff ALTER COLUMN subject DROP NOT NULL;
ALTER TABLE staff DROP CONSTRAINT staff_subject_key;
ALTER TABLE staff ADD CONSTRAINT staff_identity_subject_check
    CHECK ((identity_kind = 'oidc' AND subject IS NOT NULL) OR (identity_kind = 'local' AND subject IS NULL));
CREATE UNIQUE INDEX staff_subject_unique_not_null ON staff (subject) WHERE subject IS NOT NULL;

UPDATE staff
SET display_name = COALESCE(NULLIF(display_name, ''), subject), updated_at = now()
WHERE display_name IS NULL;

CREATE TABLE staff_store_roles (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    staff_id uuid NOT NULL,
    role text NOT NULL CHECK (role IN ('owner', 'admin', 'cashier', 'receiver')),
    created_at timestamptz NOT NULL DEFAULT now(),
    created_by uuid,
    PRIMARY KEY (org_id, store_id, staff_id, role),
    FOREIGN KEY (org_id, store_id) REFERENCES stores (org_id, id),
    FOREIGN KEY (org_id, staff_id, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    FOREIGN KEY (org_id, created_by) REFERENCES staff (org_id, id)
);

INSERT INTO staff_store_roles (org_id, store_id, staff_id, role, created_by)
SELECT ss.org_id, ss.store_id, ss.staff_id, sr.role, NULL
FROM staff_stores ss
JOIN staff_roles sr ON (sr.org_id, sr.staff_id) = (ss.org_id, ss.staff_id)
ON CONFLICT DO NOTHING;

CREATE FUNCTION sync_staff_store_roles_from_membership() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO staff_store_roles (org_id, store_id, staff_id, role, created_by)
    SELECT NEW.org_id, NEW.store_id, NEW.staff_id, r.role, NULL
    FROM staff_roles r WHERE (r.org_id, r.staff_id) = (NEW.org_id, NEW.staff_id)
    ON CONFLICT DO NOTHING;
    RETURN NEW;
END;
$$;
CREATE TRIGGER staff_store_roles_membership_compat
AFTER INSERT ON staff_stores FOR EACH ROW EXECUTE FUNCTION sync_staff_store_roles_from_membership();

CREATE FUNCTION sync_staff_store_roles_from_legacy_role() RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    INSERT INTO staff_store_roles (org_id, store_id, staff_id, role, created_by)
    SELECT NEW.org_id, s.store_id, NEW.staff_id, NEW.role, NULL
    FROM staff_stores s WHERE (s.org_id, s.staff_id) = (NEW.org_id, NEW.staff_id)
    ON CONFLICT DO NOTHING;
    RETURN NEW;
END;
$$;
CREATE TRIGGER staff_store_roles_legacy_role_compat
AFTER INSERT ON staff_roles FOR EACH ROW EXECUTE FUNCTION sync_staff_store_roles_from_legacy_role();

CREATE TABLE employee_credentials (
    org_id uuid NOT NULL,
    staff_id uuid NOT NULL,
    password_hash text NOT NULL,
    password_changed_at timestamptz NOT NULL DEFAULT now(),
    failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until timestamptz,
    must_change_password boolean NOT NULL DEFAULT false,
    version integer NOT NULL DEFAULT 1 CHECK (version > 0),
    PRIMARY KEY (org_id, staff_id),
    FOREIGN KEY (org_id, staff_id) REFERENCES staff (org_id, id) ON DELETE CASCADE,
    CHECK (password_hash LIKE '$argon2id$%')
);

CREATE TABLE operator_sessions (
    id uuid PRIMARY KEY,
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    staff_id uuid NOT NULL,
    host_issuer text NOT NULL,
    host_subject text NOT NULL,
    device_id uuid NOT NULL,
    register_id uuid,
    token_digest bytea NOT NULL UNIQUE CHECK (octet_length(token_digest) = 32),
    created_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    idle_expires_at timestamptz NOT NULL,
    absolute_expires_at timestamptz NOT NULL,
    revoked_at timestamptz,
    revoked_reason text,
    FOREIGN KEY (org_id, staff_id, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    FOREIGN KEY (org_id, store_id, register_id) REFERENCES registers (org_id, store_id, id),
    CHECK (idle_expires_at <= absolute_expires_at),
    CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL))
);
CREATE INDEX operator_sessions_staff_active_idx
    ON operator_sessions (org_id, store_id, staff_id) WHERE revoked_at IS NULL;

CREATE TABLE operator_login_rate_limits (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    staff_id uuid NOT NULL,
    device_id uuid NOT NULL,
    ip_digest bytea NOT NULL CHECK (octet_length(ip_digest) = 32),
    failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until timestamptz,
    last_failed_at timestamptz,
    PRIMARY KEY (org_id, store_id, staff_id, device_id, ip_digest),
    FOREIGN KEY (org_id, staff_id, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

CREATE TABLE employee_idempotency (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    idempotency_key uuid NOT NULL,
    request_digest bytea NOT NULL CHECK (octet_length(request_digest) = 32),
    employee_id uuid NOT NULL,
    response_body jsonb NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (org_id, store_id, idempotency_key),
    FOREIGN KEY (org_id, store_id) REFERENCES stores (org_id, id),
    FOREIGN KEY (org_id, employee_id) REFERENCES staff (org_id, id)
);

CREATE TABLE operator_audit_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    action text NOT NULL,
    organization_id uuid NOT NULL,
    store_id uuid NOT NULL,
    actor_staff_id uuid,
    target_staff_id uuid,
    active_role text,
    device_id uuid,
    register_id uuid,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    trace_id text NOT NULL,
    result text NOT NULL,
    reason text,
    FOREIGN KEY (organization_id, store_id) REFERENCES stores (org_id, id),
    FOREIGN KEY (organization_id, actor_staff_id) REFERENCES staff (org_id, id),
    FOREIGN KEY (organization_id, target_staff_id) REFERENCES staff (org_id, id)
);
CREATE INDEX operator_audit_store_time_idx
    ON operator_audit_events (organization_id, store_id, occurred_at DESC, id DESC);

GRANT SELECT, INSERT, DELETE ON staff_store_roles TO kasa_api;
GRANT UPDATE (created_by) ON staff_store_roles TO kasa_api;
GRANT SELECT, INSERT, UPDATE ON employee_credentials TO kasa_api;
GRANT SELECT, INSERT, UPDATE ON operator_sessions TO kasa_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON operator_login_rate_limits TO kasa_api;
GRANT SELECT, INSERT ON employee_idempotency, operator_audit_events TO kasa_api;
GRANT USAGE, SELECT ON SEQUENCE operator_audit_events_id_seq TO kasa_api;
GRANT UPDATE (first_name, last_name, display_name, version, updated_at, active) ON staff TO kasa_api;
GRANT DELETE ON cashier_registers TO kasa_api;

COMMIT;
