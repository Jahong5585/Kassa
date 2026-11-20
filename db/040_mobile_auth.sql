BEGIN;

ALTER TABLE employee_credentials
    ADD COLUMN mobile_login text,
    ADD COLUMN mobile_login_normalized text,
    ADD COLUMN mobile_enabled boolean NOT NULL DEFAULT false;

ALTER TABLE employee_credentials
    ADD CONSTRAINT employee_credentials_mobile_login_check CHECK (
        (mobile_enabled AND mobile_login IS NOT NULL AND mobile_login_normalized IS NOT NULL) OR
        (NOT mobile_enabled)
    );

CREATE UNIQUE INDEX employee_credentials_mobile_login_unique
    ON employee_credentials (mobile_login_normalized)
    WHERE mobile_enabled;

CREATE TABLE mobile_sessions (
    id uuid PRIMARY KEY,
    org_id uuid NOT NULL,
    staff_id uuid NOT NULL,
    device_id uuid NOT NULL,
    access_token_digest bytea NOT NULL UNIQUE CHECK (octet_length(access_token_digest) = 32),
    refresh_token_digest bytea NOT NULL UNIQUE CHECK (octet_length(refresh_token_digest) = 32),
    created_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    access_expires_at timestamptz NOT NULL,
    refresh_expires_at timestamptz NOT NULL,
    owner_verified_at timestamptz,
    owner_role_expires_at timestamptz,
    revoked_at timestamptz,
    revoked_reason text,
    version integer NOT NULL DEFAULT 1 CHECK (version > 0),
    FOREIGN KEY (org_id, staff_id) REFERENCES staff (org_id, id),
    CHECK (access_expires_at <= refresh_expires_at),
    CHECK ((owner_verified_at IS NULL) = (owner_role_expires_at IS NULL)),
    CHECK (owner_role_expires_at IS NULL OR owner_verified_at <= owner_role_expires_at),
    CHECK ((revoked_at IS NULL) = (revoked_reason IS NULL))
);

CREATE INDEX mobile_sessions_staff_active_idx
    ON mobile_sessions (org_id, staff_id, device_id)
    WHERE revoked_at IS NULL;
CREATE INDEX mobile_sessions_device_active_idx
    ON mobile_sessions (org_id, device_id)
    WHERE revoked_at IS NULL;

CREATE TABLE mobile_login_rate_limits (
    login_digest bytea NOT NULL CHECK (octet_length(login_digest) = 32),
    device_id uuid NOT NULL,
    ip_digest bytea NOT NULL CHECK (octet_length(ip_digest) = 32),
    failed_attempts integer NOT NULL DEFAULT 0 CHECK (failed_attempts >= 0),
    locked_until timestamptz,
    last_failed_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (login_digest, device_id, ip_digest)
);

CREATE TABLE mobile_auth_audit_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    action text NOT NULL,
    organization_id uuid,
    staff_id uuid,
    session_id uuid,
    device_id uuid NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    trace_id text NOT NULL,
    result text NOT NULL,
    reason text,
    FOREIGN KEY (organization_id, staff_id) REFERENCES staff (org_id, id),
    FOREIGN KEY (session_id) REFERENCES mobile_sessions (id)
);

CREATE INDEX mobile_auth_audit_time_idx
    ON mobile_auth_audit_events (occurred_at DESC, id DESC);

GRANT SELECT, INSERT, UPDATE ON mobile_sessions TO kasa_api;
GRANT SELECT, INSERT, UPDATE, DELETE ON mobile_login_rate_limits TO kasa_api;
GRANT SELECT, INSERT ON mobile_auth_audit_events TO kasa_api;
GRANT USAGE, SELECT ON SEQUENCE mobile_auth_audit_events_id_seq TO kasa_api;
GRANT UPDATE (mobile_login, mobile_login_normalized, mobile_enabled) ON employee_credentials TO kasa_api;

COMMIT;
