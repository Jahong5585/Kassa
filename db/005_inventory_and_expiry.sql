BEGIN;

ALTER TABLE stock_lots ADD COLUMN revision bigint NOT NULL DEFAULT 1 CHECK (revision > 0);

CREATE FUNCTION bump_stock_lot_revision() RETURNS trigger
LANGUAGE plpgsql AS $$
BEGIN
    IF (NEW.quantity, NEW.quarantine_quantity, NEW.quarantined, NEW.expiry_date)
       IS DISTINCT FROM (OLD.quantity, OLD.quarantine_quantity, OLD.quarantined, OLD.expiry_date) THEN
        NEW.revision := OLD.revision + 1;
    END IF;
    RETURN NEW;
END;
$$;
CREATE TRIGGER stock_lot_revision BEFORE UPDATE ON stock_lots
FOR EACH ROW EXECUTE FUNCTION bump_stock_lot_revision();

CREATE TABLE shelf_tasks (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    quantity numeric(14,3) NOT NULL CHECK (quantity > 0),
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    resolved_by uuid,
    resolved_at timestamptz,
    resolution text,
    PRIMARY KEY (org_id, store_id, id),
    FOREIGN KEY (org_id, store_id, product_id, lot_id) REFERENCES stock_lots,
    FOREIGN KEY (org_id, created_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    FOREIGN KEY (org_id, resolved_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    CHECK ((resolved_at IS NULL) = (resolved_by IS NULL) AND (resolved_at IS NULL) = (resolution IS NULL))
);
CREATE UNIQUE INDEX one_open_shelf_task_per_lot
    ON shelf_tasks (org_id, store_id, product_id, lot_id) WHERE resolved_at IS NULL;

CREATE TABLE stock_operations (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    kind text NOT NULL CHECK (kind IN ('writeOffStock', 'writeOffQuarantine', 'quarantine', 'inventory')),
    reason text NOT NULL,
    actor_id uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, id),
    FOREIGN KEY (org_id, actor_id, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);
CREATE INDEX stock_operations_recent ON stock_operations (org_id, store_id, created_at DESC, id);

CREATE TABLE stock_operation_lines (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    operation_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    stock_before numeric(14,3) NOT NULL CHECK (stock_before >= 0),
    stock_after numeric(14,3) NOT NULL CHECK (stock_after >= 0),
    quarantine_before numeric(14,3) NOT NULL CHECK (quarantine_before >= 0),
    quarantine_after numeric(14,3) NOT NULL CHECK (quarantine_after >= 0),
    PRIMARY KEY (org_id, store_id, operation_id, product_id, lot_id),
    FOREIGN KEY (org_id, store_id, operation_id) REFERENCES stock_operations,
    FOREIGN KEY (org_id, store_id, product_id, lot_id) REFERENCES stock_lots
);

CREATE TABLE inventories (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    reason text NOT NULL,
    status text NOT NULL DEFAULT 'draft' CHECK (status IN ('draft', 'counted', 'posted')),
    version integer NOT NULL DEFAULT 1 CHECK (version > 0),
    posted_operation_id uuid,
    PRIMARY KEY (org_id, store_id, id),
    FOREIGN KEY (org_id, created_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    FOREIGN KEY (org_id, store_id, posted_operation_id) REFERENCES stock_operations,
    CHECK ((status = 'posted') = (posted_operation_id IS NOT NULL)),
    CHECK (posted_operation_id IS NULL OR posted_operation_id = id)
);

CREATE TABLE inventory_lines (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    inventory_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    counted_revision bigint CHECK (counted_revision > 0),
    expected_stock numeric(14,3) CHECK (expected_stock >= 0),
    expected_quarantine numeric(14,3) CHECK (expected_quarantine >= 0),
    counted_stock numeric(14,3) CHECK (counted_stock >= 0),
    counted_quarantine numeric(14,3) CHECK (counted_quarantine >= 0),
    PRIMARY KEY (org_id, store_id, inventory_id, product_id, lot_id),
    FOREIGN KEY (org_id, store_id, inventory_id) REFERENCES inventories,
    FOREIGN KEY (org_id, store_id, product_id, lot_id) REFERENCES stock_lots,
    CHECK ((counted_revision IS NULL) = (expected_stock IS NULL)
        AND (counted_revision IS NULL) = (expected_quarantine IS NULL)
        AND (counted_revision IS NULL) = (counted_stock IS NULL)
        AND (counted_revision IS NULL) = (counted_quarantine IS NULL))
);

GRANT SELECT, INSERT ON shelf_tasks, stock_operations, stock_operation_lines, inventories, inventory_lines TO kasa_api;
GRANT UPDATE (resolved_by, resolved_at, resolution) ON shelf_tasks TO kasa_api;
GRANT UPDATE (status, version, posted_operation_id) ON inventories TO kasa_api;
GRANT UPDATE (counted_revision, expected_stock, expected_quarantine, counted_stock, counted_quarantine)
    ON inventory_lines TO kasa_api;
COMMIT;
