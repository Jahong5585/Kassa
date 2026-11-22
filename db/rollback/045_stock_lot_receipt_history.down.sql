BEGIN;

DROP TABLE IF EXISTS stock_lot_purchase_price_history;
DROP INDEX IF EXISTS stock_lots_fefo_fifo;
DROP TRIGGER IF EXISTS stock_lots_received_at_immutable ON stock_lots;
DROP FUNCTION IF EXISTS kasa_keep_lot_received_at();
ALTER TABLE stock_lots DROP COLUMN IF EXISTS received_at;

COMMIT;
