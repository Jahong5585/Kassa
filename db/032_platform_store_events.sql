BEGIN;

CREATE TABLE platform_store_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    issuer text NOT NULL,
    subject text NOT NULL,
    action text NOT NULL CHECK (action IN ('create_test_store', 'rename_test_store')),
    org_id uuid NOT NULL REFERENCES organizations,
    store_id uuid NOT NULL,
    store_name text NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (org_id, store_id) REFERENCES stores (org_id, id)
);

CREATE INDEX platform_store_events_store_time ON platform_store_events (store_id, occurred_at DESC);

COMMIT;
