BEGIN;

ALTER TABLE stock_lots ADD COLUMN received_at timestamptz;

WITH known_receipts AS (
    SELECT q.org_id, q.store_id, q.product_id, q.lot_id, b.posted_at AS received_at
    FROM quick_receiving_lines q
    JOIN quick_receivings b USING (org_id, store_id, operation_id)
    UNION ALL
    SELECT m.org_id, m.store_id, m.product_id, m.lot_id, d.posted_at
    FROM receiving_movements m
    JOIN receiving_documents d
      ON (d.org_id, d.store_id, d.id) = (m.org_id, m.store_id, m.receipt_id)
    WHERE d.posted_at IS NOT NULL
    UNION ALL
    SELECT o.org_id, o.store_id, o.product_id, o.lot_id, b.created_at
    FROM opening_stock_lines o
    JOIN stock_operations b
      ON (b.org_id, b.store_id, b.id) = (o.org_id, o.store_id, o.operation_id)
), earliest AS (
    SELECT org_id, store_id, product_id, lot_id, min(received_at) AS received_at
    FROM known_receipts
    WHERE received_at IS NOT NULL
    GROUP BY org_id, store_id, product_id, lot_id
)
UPDATE stock_lots l
SET received_at = e.received_at
FROM earliest e
WHERE (l.org_id, l.store_id, l.product_id, l.id) =
      (e.org_id, e.store_id, e.product_id, e.lot_id);

-- Existing lots without a trustworthy source timestamp intentionally remain NULL.
ALTER TABLE stock_lots ALTER COLUMN received_at SET DEFAULT statement_timestamp();

CREATE OR REPLACE FUNCTION kasa_keep_lot_received_at()
RETURNS trigger LANGUAGE plpgsql AS $$
BEGIN
    IF NEW.received_at IS DISTINCT FROM OLD.received_at THEN
        RAISE EXCEPTION 'stock_lot_received_at_immutable' USING ERRCODE = '23514';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER stock_lots_received_at_immutable
BEFORE UPDATE OF received_at ON stock_lots
FOR EACH ROW EXECUTE FUNCTION kasa_keep_lot_received_at();

CREATE INDEX stock_lots_fefo_fifo
    ON stock_lots (org_id, store_id, product_id, expiry_date, received_at, id);

CREATE TABLE stock_lot_purchase_price_history (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    source_kind text NOT NULL CHECK (source_kind IN ('receipt', 'quickReceiving', 'opening')),
    source_id uuid NOT NULL,
    quantity numeric(14,3) NOT NULL CHECK (quantity > 0),
    unit_cost numeric(14,2) NOT NULL CHECK (unit_cost >= 0),
    total_cost numeric(16,2) NOT NULL CHECK (total_cost >= 0),
    changed_at timestamptz NOT NULL,
    changed_by uuid,
    reason text NOT NULL,
    PRIMARY KEY (org_id, store_id, product_id, lot_id, source_kind, source_id),
    FOREIGN KEY (org_id, store_id, product_id, lot_id)
        REFERENCES stock_lots (org_id, store_id, product_id, id),
    FOREIGN KEY (org_id, changed_by) REFERENCES staff (org_id, id)
);

INSERT INTO stock_lot_purchase_price_history
    (org_id, store_id, product_id, lot_id, source_kind, source_id, quantity,
     unit_cost, total_cost, changed_at, changed_by, reason)
SELECT q.org_id, q.store_id, q.product_id, q.lot_id, 'quickReceiving', q.operation_id,
       q.quantity, q.unit_cost, q.total_cost, b.posted_at, b.created_by, 'quick_receiving'
FROM quick_receiving_lines q
JOIN quick_receivings b USING (org_id, store_id, operation_id)
WHERE q.unit_cost IS NOT NULL AND q.total_cost IS NOT NULL
ON CONFLICT DO NOTHING;

INSERT INTO stock_lot_purchase_price_history
    (org_id, store_id, product_id, lot_id, source_kind, source_id, quantity,
     unit_cost, total_cost, changed_at, changed_by, reason)
SELECT m.org_id, m.store_id, m.product_id, m.lot_id, 'receipt', m.receipt_id,
       m.quantity, round(c.total / m.quantity, 2), c.total, d.posted_at, d.posted_by,
       concat_ws(' / ', nullif(d.supplier_ref, ''), nullif(d.invoice_number, ''))
FROM receiving_movements m
JOIN receiving_documents d
  ON (d.org_id, d.store_id, d.id) = (m.org_id, m.store_id, m.receipt_id)
JOIN receiving_movement_costs c
  ON (c.org_id, c.store_id, c.receipt_id, c.product_id, c.lot_id) =
     (m.org_id, m.store_id, m.receipt_id, m.product_id, m.lot_id)
WHERE d.posted_at IS NOT NULL
ON CONFLICT DO NOTHING;

INSERT INTO stock_lot_purchase_price_history
    (org_id, store_id, product_id, lot_id, source_kind, source_id, quantity,
     unit_cost, total_cost, changed_at, changed_by, reason)
SELECT o.org_id, o.store_id, o.product_id, o.lot_id, 'opening', o.operation_id,
       m.stock_after + m.quarantine_after,
       round(o.total_cost / (m.stock_after + m.quarantine_after), 2), o.total_cost,
       b.created_at, b.actor_id, b.reason
FROM opening_stock_lines o
JOIN stock_operations b
  ON (b.org_id, b.store_id, b.id) = (o.org_id, o.store_id, o.operation_id)
JOIN stock_operation_lines m
  ON (m.org_id, m.store_id, m.operation_id, m.product_id, m.lot_id) =
     (o.org_id, o.store_id, o.operation_id, o.product_id, o.lot_id)
WHERE o.total_cost IS NOT NULL AND m.stock_after + m.quarantine_after > 0
ON CONFLICT DO NOTHING;

GRANT SELECT, INSERT ON stock_lot_purchase_price_history TO kasa_api;
GRANT SELECT (received_at) ON stock_lots TO kasa_api;

COMMIT;
