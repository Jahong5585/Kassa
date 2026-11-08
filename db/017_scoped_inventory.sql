BEGIN;

ALTER TABLE inventories
    ADD COLUMN scope_kind text NOT NULL DEFAULT 'selected',
    ADD COLUMN scope_product_id uuid,
    ADD COLUMN scope_category text,
    ADD COLUMN last_count_digest bytea CHECK (octet_length(last_count_digest) = 32),
    ADD CONSTRAINT inventory_scope_product_fk FOREIGN KEY (org_id, store_id, scope_product_id) REFERENCES store_prices,
    ADD CONSTRAINT inventory_scope_check CHECK (
        (scope_kind IN ('selected', 'full') AND scope_product_id IS NULL AND scope_category IS NULL) OR
        (scope_kind = 'product' AND scope_product_id IS NOT NULL AND scope_category IS NULL) OR
        (scope_kind = 'category' AND scope_product_id IS NULL AND scope_category IS NOT NULL
         AND char_length(scope_category) BETWEEN 1 AND 120)
    );

GRANT UPDATE (last_count_digest) ON inventories TO kasa_api;

COMMIT;
