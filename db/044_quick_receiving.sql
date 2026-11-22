BEGIN;
CREATE TABLE quick_receivings (
 org_id uuid NOT NULL, store_id uuid NOT NULL, operation_id uuid NOT NULL,
 body jsonb NOT NULL, result jsonb NOT NULL, created_by uuid NOT NULL,
 posted_at timestamptz NOT NULL DEFAULT statement_timestamp(),
 PRIMARY KEY(org_id,store_id,operation_id),
 FOREIGN KEY(org_id,store_id) REFERENCES stores(org_id,id),
 FOREIGN KEY(org_id,created_by) REFERENCES staff(org_id,id)
);
CREATE TABLE quick_receiving_lines (
 org_id uuid NOT NULL, store_id uuid NOT NULL, operation_id uuid NOT NULL,
 product_id uuid NOT NULL, lot_id uuid NOT NULL, quantity numeric(14,3) NOT NULL CHECK(quantity>0),
 unit_cost numeric(14,2) CHECK(unit_cost>=0), total_cost numeric(22,2) CHECK(total_cost>=0), manufactured_on date,
 PRIMARY KEY(org_id,store_id,operation_id,product_id),
 UNIQUE(org_id,store_id,product_id,lot_id),
 FOREIGN KEY(org_id,store_id,operation_id) REFERENCES quick_receivings,
 FOREIGN KEY(org_id,store_id,product_id,lot_id) REFERENCES stock_lots
);
CREATE INDEX quick_receiving_product ON quick_receiving_lines(org_id,store_id,product_id);
GRANT SELECT, INSERT ON quick_receivings, quick_receiving_lines TO kasa_api;
COMMIT;
