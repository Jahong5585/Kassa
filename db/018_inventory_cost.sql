BEGIN;

ALTER TABLE stock_lots ADD COLUMN inventory_value numeric(22,2) CHECK (inventory_value >= 0);
ALTER TABLE sale_lines ADD COLUMN inventory_cost numeric(22,2) CHECK (inventory_cost >= 0);
ALTER TABLE refund_lines ADD COLUMN inventory_cost numeric(22,2) CHECK (inventory_cost >= 0);
ALTER TABLE stock_operation_lines ADD COLUMN inventory_cost_delta numeric(22,2);
GRANT UPDATE (inventory_value) ON stock_lots TO kasa_api;

-- Use an exact quotient in minor units so values close to a half cent round correctly.
CREATE FUNCTION kasa_weighted_cost_delta(value numeric, quantity_before numeric, quantity_delta numeric)
RETURNS numeric LANGUAGE sql IMMUTABLE SET search_path = pg_catalog AS $$
    SELECT CASE WHEN quantity_delta = 0 THEN 0
                WHEN abs(amount) < 100000000000000000000 THEN amount
           END
    FROM (SELECT sign(quantity_delta)
        * div(abs(quantity_delta) * value * 100 + quantity_before * 0.5, nullif(quantity_before, 0))
        * 0.01 AS amount) calculated
$$;

CREATE FUNCTION kasa_stock_value_after(value numeric, quantity_before numeric, quantity_after numeric, delta numeric)
RETURNS numeric LANGUAGE sql IMMUTABLE SET search_path = pg_catalog AS $$
    SELECT CASE WHEN quantity_after = 0 THEN 0
                WHEN amount < 100000000000000000000 THEN amount
           END
    FROM (SELECT CASE WHEN quantity_before = 0 THEN delta ELSE value + delta END AS amount) calculated
$$;

-- Backfill only lots whose balances reconcile to priced sources with no quantity consumed or gained afterwards.
WITH incoming AS (
    SELECT m.org_id, m.store_id, m.product_id, m.lot_id, m.quantity, c.total
    FROM receiving_movements m JOIN receiving_documents d
        ON (d.org_id, d.store_id, d.id) = (m.org_id, m.store_id, m.receipt_id) AND d.status = 'posted'
    LEFT JOIN receiving_movement_costs c
        ON (c.org_id, c.store_id, c.receipt_id, c.product_id, c.lot_id) =
           (m.org_id, m.store_id, m.receipt_id, m.product_id, m.lot_id)
    UNION ALL
    SELECT m.org_id, m.store_id, m.product_id, m.lot_id, m.stock_after + m.quarantine_after, o.total_cost
    FROM opening_stock_lines o JOIN stock_operation_lines m
        USING (org_id, store_id, operation_id, product_id, lot_id)
), priced AS (
    SELECT org_id, store_id, product_id, lot_id, sum(quantity) AS quantity, sum(total) AS total
    FROM incoming GROUP BY org_id, store_id, product_id, lot_id
    HAVING bool_and(total IS NOT NULL) AND sum(total) < 100000000000000000000
), changed AS (
    SELECT org_id, store_id, product_id, lot_id FROM sale_lines
    UNION ALL
    SELECT org_id, store_id, product_id, lot_id FROM refund_lines
    UNION ALL
    SELECT l.org_id, l.store_id, l.product_id, l.lot_id
    FROM stock_operation_lines l JOIN stock_operations o
        ON (l.org_id, l.store_id, l.operation_id) = (o.org_id, o.store_id, o.id)
    WHERE o.kind <> 'opening' AND l.stock_before + l.quarantine_before <> l.stock_after + l.quarantine_after
)
UPDATE stock_lots l SET inventory_value = p.total
FROM priced p
WHERE (l.org_id, l.store_id, l.product_id, l.id) = (p.org_id, p.store_id, p.product_id, p.lot_id)
  AND l.quantity + l.quarantine_quantity = p.quantity
  AND NOT EXISTS (SELECT 1 FROM changed c
        WHERE (c.org_id, c.store_id, c.product_id, c.lot_id) = (p.org_id, p.store_id, p.product_id, p.lot_id));

COMMIT;
