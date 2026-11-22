BEGIN;

DROP TRIGGER IF EXISTS catalog_change_product_barcodes ON product_barcodes;
DROP TRIGGER IF EXISTS catalog_change_products ON products;
DROP TRIGGER IF EXISTS catalog_change_quantity_promotions ON store_quantity_promotions;
DROP TRIGGER IF EXISTS catalog_change_lot_markdowns ON lot_retail_settings;
DROP TRIGGER IF EXISTS catalog_change_stock_lots ON stock_lots;
DROP TRIGGER IF EXISTS catalog_change_store_prices ON store_prices;
DROP FUNCTION IF EXISTS record_shared_catalog_change();
DROP FUNCTION IF EXISTS record_store_catalog_change();
DROP TABLE IF EXISTS catalog_change_feed;

COMMIT;
