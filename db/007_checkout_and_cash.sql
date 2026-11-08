BEGIN;
ALTER TABLE sale_lines DROP CONSTRAINT sale_lines_line_total_check;
ALTER TABLE sale_lines ADD CHECK (line_total >= 0),
    ADD COLUMN discount_amount numeric(16,2) NOT NULL DEFAULT 0 CHECK (discount_amount >= 0),
    ADD COLUMN price_version integer CHECK (price_version > 0);
ALTER TABLE sales ADD COLUMN discount_reason text;

CREATE TABLE cash_movements (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    register_id uuid NOT NULL,
    id uuid NOT NULL,
    shift_id uuid NOT NULL,
    cashier_id uuid NOT NULL,
    direction text NOT NULL CHECK (direction IN ('in', 'out')),
    amount numeric(16,2) NOT NULL CHECK (amount > 0),
    reason text NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, register_id, id),
    FOREIGN KEY (org_id, store_id, register_id, shift_id, cashier_id)
        REFERENCES shifts (org_id, store_id, register_id, id, cashier_id)
);
CREATE INDEX cash_movements_by_shift ON cash_movements (org_id, store_id, register_id, shift_id, created_at, id);

CREATE TABLE parked_carts (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    register_id uuid NOT NULL,
    id uuid NOT NULL,
    shift_id uuid NOT NULL,
    cashier_id uuid NOT NULL,
    body jsonb NOT NULL,
    version integer NOT NULL DEFAULT 1 CHECK (version > 0),
    status text NOT NULL DEFAULT 'parked' CHECK (status IN ('parked', 'sold', 'cancelled')),
    sale_id uuid,
    cancel_reason text,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    expires_at timestamptz NOT NULL DEFAULT statement_timestamp() + interval '4 hours',
    PRIMARY KEY (org_id, store_id, register_id, id),
    FOREIGN KEY (org_id, store_id, register_id, shift_id, cashier_id)
        REFERENCES shifts (org_id, store_id, register_id, id, cashier_id),
    FOREIGN KEY (org_id, store_id, register_id, sale_id) REFERENCES sales,
    CHECK ((status = 'sold') = (sale_id IS NOT NULL)),
    CHECK ((status = 'cancelled') = (cancel_reason IS NOT NULL))
);
CREATE INDEX parked_carts_by_shift ON parked_carts (org_id, store_id, register_id, shift_id, status, expires_at);
CREATE INDEX sales_history_by_store ON sales (org_id, store_id, created_at DESC, id);
CREATE INDEX sales_history_by_cashier ON sales (org_id, store_id, register_id, cashier_id, created_at DESC, id);

GRANT SELECT, INSERT ON cash_movements, parked_carts TO kasa_api;
GRANT UPDATE (body, version, status, sale_id, cancel_reason) ON parked_carts TO kasa_api;
COMMIT;
