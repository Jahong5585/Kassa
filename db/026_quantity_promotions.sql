BEGIN;

CREATE TABLE store_quantity_promotions (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    enabled boolean NOT NULL,
    reason text NOT NULL CHECK (char_length(reason) BETWEEN 1 AND 500),
    PRIMARY KEY (org_id, store_id, product_id),
    FOREIGN KEY (org_id, store_id, product_id) REFERENCES store_prices (org_id, store_id, product_id)
);

ALTER TABLE sale_lines ADD COLUMN promotion_version integer CHECK (promotion_version > 0);
GRANT SELECT, INSERT ON store_quantity_promotions TO kasa_api;
GRANT UPDATE (version, enabled, reason) ON store_quantity_promotions TO kasa_api;

COMMIT;
