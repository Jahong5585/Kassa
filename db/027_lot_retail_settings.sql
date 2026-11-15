BEGIN;

CREATE TABLE lot_retail_settings (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    label_id uuid NOT NULL UNIQUE,
    version integer NOT NULL CHECK (version > 0),
    unit_price numeric(14,2) CHECK (unit_price > 0),
    reason text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 500),
    PRIMARY KEY (org_id, store_id, product_id, lot_id),
    FOREIGN KEY (org_id, store_id, product_id, lot_id) REFERENCES stock_lots (org_id, store_id, product_id, id)
);

CREATE VIEW active_lot_markdowns AS
    SELECT ls.org_id, ls.store_id, ls.product_id, ls.lot_id, ls.version, ls.unit_price
    FROM lot_retail_settings ls
    JOIN store_prices sp USING (org_id, store_id, product_id)
    WHERE ls.unit_price < sp.unit_price;

ALTER TABLE sale_lines ADD COLUMN lot_price_version integer CHECK (lot_price_version > 0);
GRANT SELECT, INSERT ON lot_retail_settings TO kasa_api;
GRANT UPDATE (version, unit_price, reason) ON lot_retail_settings TO kasa_api;
GRANT SELECT ON active_lot_markdowns TO kasa_api;

COMMIT;
