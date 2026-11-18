BEGIN;

CREATE TABLE register_device_bindings (
    id uuid PRIMARY KEY,
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    register_id uuid NOT NULL,
    device_id uuid NOT NULL,
    device_name text NOT NULL CHECK (char_length(device_name) BETWEEN 1 AND 120),
    token_digest bytea NOT NULL UNIQUE CHECK (octet_length(token_digest) = 32),
    host_issuer text NOT NULL,
    host_subject text NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT now(),
    last_seen_at timestamptz NOT NULL DEFAULT now(),
    version integer NOT NULL DEFAULT 1 CHECK (version > 0),
    revoked_at timestamptz,
    revoked_by uuid,
    revoked_reason text,
    FOREIGN KEY (org_id, store_id, register_id) REFERENCES registers (org_id, store_id, id),
    FOREIGN KEY (org_id, created_by) REFERENCES staff (org_id, id),
    FOREIGN KEY (org_id, revoked_by) REFERENCES staff (org_id, id),
    CHECK ((revoked_at IS NULL AND revoked_by IS NULL AND revoked_reason IS NULL) OR
           (revoked_at IS NOT NULL AND revoked_by IS NOT NULL AND revoked_reason IS NOT NULL))
);

CREATE UNIQUE INDEX register_device_bindings_active_device_idx
    ON register_device_bindings (org_id, device_id) WHERE revoked_at IS NULL;
CREATE INDEX register_device_bindings_register_idx
    ON register_device_bindings (org_id, store_id, register_id, created_at DESC);

GRANT SELECT, INSERT, UPDATE ON register_device_bindings TO kasa_api;

COMMIT;
