BEGIN;

CREATE TABLE organizations (
    id uuid PRIMARY KEY,
    name text NOT NULL
);

CREATE TABLE stores (
    org_id uuid NOT NULL REFERENCES organizations,
    id uuid NOT NULL,
    name text NOT NULL,
    time_zone text NOT NULL DEFAULT 'Asia/Tashkent',
    PRIMARY KEY (org_id, id)
);

CREATE TABLE registers (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    id uuid NOT NULL UNIQUE,
    name text NOT NULL,
    key_digest bytea NOT NULL CHECK (octet_length(key_digest) = 32),
    active boolean NOT NULL DEFAULT true,
    PRIMARY KEY (org_id, store_id, id),
    FOREIGN KEY (org_id, store_id) REFERENCES stores
);

CREATE TABLE staff (
    org_id uuid NOT NULL REFERENCES organizations,
    id uuid NOT NULL,
    subject text NOT NULL UNIQUE,
    role text NOT NULL CHECK (role IN ('cashier', 'receiver', 'admin')),
    active boolean NOT NULL DEFAULT true,
    PRIMARY KEY (org_id, id)
);

CREATE TABLE staff_stores (
    org_id uuid NOT NULL,
    staff_id uuid NOT NULL,
    store_id uuid NOT NULL,
    PRIMARY KEY (org_id, staff_id, store_id),
    FOREIGN KEY (org_id, staff_id) REFERENCES staff,
    FOREIGN KEY (org_id, store_id) REFERENCES stores
);

CREATE TABLE cashier_registers (
    org_id uuid NOT NULL,
    staff_id uuid NOT NULL,
    store_id uuid NOT NULL,
    register_id uuid NOT NULL,
    PRIMARY KEY (org_id, staff_id, store_id, register_id),
    FOREIGN KEY (org_id, staff_id, store_id) REFERENCES staff_stores,
    FOREIGN KEY (org_id, store_id, register_id) REFERENCES registers
);

CREATE TABLE products (
    org_id uuid NOT NULL REFERENCES organizations,
    id uuid NOT NULL,
    name text NOT NULL,
    barcode text NOT NULL,
    unit text NOT NULL CHECK (unit IN ('piece', 'kg')),
    requires_expiry boolean NOT NULL DEFAULT false,
    PRIMARY KEY (org_id, id),
    UNIQUE (org_id, barcode)
);

CREATE TABLE store_prices (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    unit_price numeric(14,2) NOT NULL CHECK (unit_price > 0),
    version integer NOT NULL CHECK (version > 0),
    active boolean NOT NULL DEFAULT true,
    PRIMARY KEY (org_id, store_id, product_id),
    FOREIGN KEY (org_id, store_id) REFERENCES stores,
    FOREIGN KEY (org_id, product_id) REFERENCES products
);

CREATE TABLE stock_lots (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    product_id uuid NOT NULL,
    id uuid NOT NULL,
    expiry_date date,
    quantity numeric(14,3) NOT NULL CHECK (quantity >= 0),
    quarantined boolean NOT NULL DEFAULT false,
    PRIMARY KEY (org_id, store_id, product_id, id),
    FOREIGN KEY (org_id, store_id, product_id) REFERENCES store_prices
);

CREATE TABLE shifts (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    register_id uuid NOT NULL,
    id uuid NOT NULL,
    cashier_id uuid NOT NULL,
    opened_at timestamptz NOT NULL DEFAULT now(),
    closed_at timestamptz,
    opening_cash numeric(16,2) NOT NULL CHECK (opening_cash >= 0),
    counted_cash numeric(16,2) CHECK (counted_cash >= 0),
    PRIMARY KEY (org_id, store_id, register_id, id),
    UNIQUE (org_id, store_id, register_id, id, cashier_id),
    FOREIGN KEY (org_id, store_id, register_id) REFERENCES registers,
    FOREIGN KEY (org_id, cashier_id, store_id) REFERENCES staff_stores,
    CHECK ((closed_at IS NULL) = (counted_cash IS NULL))
);
CREATE UNIQUE INDEX one_open_shift_per_register ON shifts (org_id, store_id, register_id)
    WHERE closed_at IS NULL;
CREATE UNIQUE INDEX one_open_shift_per_cashier ON shifts (org_id, cashier_id)
    WHERE closed_at IS NULL;

CREATE TABLE sales (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    register_id uuid NOT NULL,
    id uuid NOT NULL,
    shift_id uuid NOT NULL,
    cashier_id uuid NOT NULL,
    method text NOT NULL CHECK (method IN ('cash', 'manualCashless')),
    total numeric(16,2) NOT NULL CHECK (total > 0),
    tendered numeric(16,2),
    change_due numeric(16,2),
    request_digest bytea NOT NULL CHECK (octet_length(request_digest) = 32),
    created_at timestamptz NOT NULL,
    PRIMARY KEY (org_id, store_id, register_id, id),
    FOREIGN KEY (org_id, store_id, register_id, shift_id, cashier_id)
        REFERENCES shifts (org_id, store_id, register_id, id, cashier_id),
    CHECK (
        (method = 'cash' AND tendered IS NOT NULL AND change_due IS NOT NULL
            AND tendered >= total AND change_due = tendered - total)
        OR (method = 'manualCashless' AND tendered IS NULL AND change_due IS NULL)
    )
);
CREATE INDEX sales_by_shift ON sales (org_id, store_id, register_id, shift_id);

-- Each line is also the immutable negative stock movement for its exact lot.
CREATE TABLE sale_lines (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    register_id uuid NOT NULL,
    sale_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    name text NOT NULL,
    unit text NOT NULL CHECK (unit IN ('piece', 'kg')),
    quantity numeric(14,3) NOT NULL CHECK (quantity > 0),
    unit_price numeric(14,2) NOT NULL CHECK (unit_price > 0),
    line_total numeric(16,2) NOT NULL CHECK (line_total > 0),
    PRIMARY KEY (org_id, store_id, register_id, sale_id, product_id, lot_id),
    FOREIGN KEY (org_id, store_id, register_id, sale_id) REFERENCES sales,
    FOREIGN KEY (org_id, store_id, product_id, lot_id) REFERENCES stock_lots
);

CREATE TABLE audit_events (
    id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    register_id uuid NOT NULL,
    staff_id uuid NOT NULL,
    action text NOT NULL,
    object_id uuid NOT NULL,
    request_id text NOT NULL,
    occurred_at timestamptz NOT NULL DEFAULT now(),
    FOREIGN KEY (org_id, store_id, register_id) REFERENCES registers,
    FOREIGN KEY (org_id, staff_id) REFERENCES staff
);

COMMIT;
