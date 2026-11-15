BEGIN;

CREATE TABLE platform_login_audit (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    issuer text NOT NULL,
    subject text NOT NULL,
    succeeded boolean NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now()
);

CREATE INDEX platform_login_audit_subject_time ON platform_login_audit (issuer, subject, occurred_at DESC);

COMMENT ON TABLE platform_login_audit IS 'No passwords, tokens, keys or client addresses are stored here.';

COMMIT;
