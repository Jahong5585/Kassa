BEGIN;

CREATE TABLE operating_expenses (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    incurred_on date NOT NULL,
    category text NOT NULL CHECK (category IN
        ('rent', 'salary', 'utilities', 'delivery', 'repair', 'advertising', 'tax', 'other')),
    amount numeric(16,2) NOT NULL CHECK (amount > 0),
    payment_method text NOT NULL CHECK (payment_method IN
        ('cash', 'manualCashless', 'bankTransfer', 'other')),
    description text NOT NULL CHECK (char_length(description) BETWEEN 1 AND 500),
    recorded_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    reversal_id uuid,
    reversal_reason text CHECK (reversal_reason IS NULL OR char_length(reversal_reason) BETWEEN 1 AND 500),
    reversed_by uuid,
    reversed_at timestamptz,
    PRIMARY KEY (org_id, store_id, id),
    UNIQUE (org_id, store_id, reversal_id),
    FOREIGN KEY (org_id, store_id) REFERENCES stores,
    FOREIGN KEY (org_id, recorded_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    FOREIGN KEY (org_id, reversed_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    CHECK ((reversal_id IS NULL) = (reversal_reason IS NULL)),
    CHECK ((reversal_id IS NULL) = (reversed_by IS NULL)),
    CHECK ((reversal_id IS NULL) = (reversed_at IS NULL))
);

CREATE INDEX operating_expenses_by_store_date
    ON operating_expenses (org_id, store_id, incurred_on DESC, created_at DESC, id);
CREATE INDEX operating_expenses_active_by_store_date
    ON operating_expenses (org_id, store_id, incurred_on, category) WHERE reversed_at IS NULL;

GRANT SELECT, INSERT ON operating_expenses TO kasa_api;
GRANT UPDATE (reversal_id, reversal_reason, reversed_by, reversed_at) ON operating_expenses TO kasa_api;

COMMIT;
