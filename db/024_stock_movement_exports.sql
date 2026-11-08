BEGIN;

CREATE TABLE stock_movement_exports (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    actor_id uuid NOT NULL,
    id uuid NOT NULL,
    from_date date NOT NULL,
    to_date date NOT NULL CHECK (to_date - from_date BETWEEN 0 AND 92),
    product_id uuid,
    lot_id uuid CHECK (lot_id IS NULL OR product_id IS NOT NULL),
    kind text,
    time_zone text NOT NULL,
    request_id text NOT NULL,
    queued_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    status text NOT NULL DEFAULT 'queued',
    finished_at timestamptz,
    expires_at timestamptz,
    row_count integer CHECK (row_count BETWEEN 0 AND 10000),
    error_code text,
    contents bytea CHECK (octet_length(contents) BETWEEN 1 AND 8388608),
    PRIMARY KEY (org_id, store_id, actor_id, id),
    FOREIGN KEY (org_id, actor_id, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    CHECK (
        (status = 'queued' AND finished_at IS NULL AND expires_at IS NULL AND row_count IS NULL AND error_code IS NULL AND contents IS NULL)
        OR (status IN ('ready', 'expired') AND finished_at IS NOT NULL AND expires_at > finished_at AND expires_at IS NOT NULL
            AND row_count IS NOT NULL AND error_code IS NULL AND ((status = 'ready') = (contents IS NOT NULL)))
        OR (status = 'failed' AND finished_at IS NOT NULL AND expires_at IS NULL AND row_count IS NULL AND error_code IS NOT NULL AND contents IS NULL)
    )
);

CREATE INDEX stock_movement_exports_queue_idx ON stock_movement_exports (queued_at, org_id, store_id, actor_id, id)
    WHERE status = 'queued';
CREATE INDEX stock_movement_exports_expiry_idx ON stock_movement_exports (expires_at)
    WHERE status = 'ready';
CREATE INDEX stock_movement_exports_store_active_idx ON stock_movement_exports (org_id, store_id)
    WHERE status IN ('queued', 'ready');

GRANT SELECT, INSERT ON stock_movement_exports TO kasa_api;
GRANT UPDATE (status, finished_at, expires_at, row_count, error_code, contents) ON stock_movement_exports TO kasa_api;

COMMIT;
