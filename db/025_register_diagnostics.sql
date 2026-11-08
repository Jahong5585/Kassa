BEGIN;

CREATE TABLE register_diagnostics (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    register_id uuid NOT NULL,
    id uuid NOT NULL,
    shift_id uuid NOT NULL,
    cashier_id uuid NOT NULL,
    code text NOT NULL CHECK (code IN ('saleOutcomeUnknown', 'printFailed', 'syncFailed')),
    operation_id uuid,
    received_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, register_id, id),
    FOREIGN KEY (org_id, store_id, register_id, shift_id, cashier_id)
        REFERENCES shifts (org_id, store_id, register_id, id, cashier_id),
    CHECK (code = 'syncFailed' OR operation_id IS NOT NULL)
);

CREATE INDEX register_diagnostics_by_store_code ON register_diagnostics (org_id, store_id, code, id, register_id);
GRANT SELECT, INSERT ON register_diagnostics TO kasa_api;

COMMIT;
