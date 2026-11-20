BEGIN;
DROP TABLE receiving_product_creations;
DROP INDEX product_barcodes_shared_lookup;
ALTER TABLE products DROP COLUMN shelf_life_days;
COMMIT;
