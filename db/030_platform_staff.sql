BEGIN;

CREATE TABLE platform_staff (
    issuer text NOT NULL CHECK (length(issuer) BETWEEN 1 AND 500),
    subject text NOT NULL CHECK (length(subject) BETWEEN 1 AND 256),
    password_hash text,
    can_read boolean NOT NULL DEFAULT false,
    can_write boolean NOT NULL DEFAULT false,
    active boolean NOT NULL DEFAULT true,
    created_at timestamptz NOT NULL DEFAULT now(),
    updated_at timestamptz NOT NULL DEFAULT now(),
    PRIMARY KEY (issuer, subject),
    CHECK (password_hash IS NULL OR length(password_hash) BETWEEN 40 AND 2048)
);

COMMENT ON TABLE platform_staff IS
    'Global platform principals. Password hashes are permitted only for the loopback Development login.';

COMMIT;
