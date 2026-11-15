BEGIN;

CREATE TABLE weighed_price_labels (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    id uuid NOT NULL,
    weight numeric(14,3) NOT NULL CHECK (weight > 0),
    printed_total numeric(16,2) NOT NULL CHECK (printed_total > 0),
    sold_register_id uuid,
    sold_sale_id uuid,
    PRIMARY KEY (org_id, store_id, id),
    FOREIGN KEY (org_id, store_id, product_id, lot_id) REFERENCES stock_lots (org_id, store_id, product_id, id),
    FOREIGN KEY (org_id, store_id, sold_register_id, sold_sale_id, product_id, lot_id) REFERENCES sale_lines,
    CHECK ((sold_register_id IS NULL) = (sold_sale_id IS NULL))
);

CREATE UNIQUE INDEX weighed_price_labels_sale_line ON weighed_price_labels
    (org_id, store_id, sold_register_id, sold_sale_id, product_id, lot_id) WHERE sold_sale_id IS NOT NULL;

GRANT SELECT, INSERT ON weighed_price_labels TO kasa_api;
GRANT UPDATE (sold_register_id, sold_sale_id) ON weighed_price_labels TO kasa_api;

COMMIT;
