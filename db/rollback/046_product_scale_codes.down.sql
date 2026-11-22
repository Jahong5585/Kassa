BEGIN;

DROP TRIGGER IF EXISTS catalog_change_product_scale_codes ON product_scale_codes;
DROP TABLE IF EXISTS product_scale_codes;

COMMIT;
