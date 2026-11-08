BEGIN;

ALTER TABLE audit_events ADD COLUMN object_code text;

CREATE TABLE suppliers (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    code text NOT NULL CHECK (code ~ '^[A-Z0-9][A-Z0-9_-]{0,31}$'),
    name text NOT NULL,
    tax_id text NOT NULL,
    phone text NOT NULL,
    email text NOT NULL,
    payment_terms text NOT NULL,
    lead_time_days integer NOT NULL CHECK (lead_time_days BETWEEN 0 AND 365),
    active boolean NOT NULL,
    version integer NOT NULL CHECK (version > 0),
    PRIMARY KEY (org_id, store_id, code),
    FOREIGN KEY (org_id, store_id) REFERENCES stores
);

CREATE TABLE invoice_files (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    receipt_id uuid NOT NULL,
    id uuid NOT NULL,
    file_name text NOT NULL,
    content_type text NOT NULL CHECK (content_type IN ('application/pdf', 'image/jpeg', 'image/png')),
    size_bytes integer NOT NULL CHECK (size_bytes BETWEEN 1 AND 2097152),
    digest bytea NOT NULL CHECK (octet_length(digest) = 32),
    scan_status text NOT NULL DEFAULT 'pending' CHECK (scan_status IN ('pending', 'clean', 'rejected')),
    contents bytea,
    created_by uuid NOT NULL,
    created_at timestamptz NOT NULL DEFAULT statement_timestamp(),
    PRIMARY KEY (org_id, store_id, receipt_id, id),
    UNIQUE (org_id, store_id, receipt_id, digest),
    FOREIGN KEY (org_id, store_id, receipt_id) REFERENCES receiving_documents,
    FOREIGN KEY (org_id, created_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id),
    CHECK ((scan_status = 'rejected') = (contents IS NULL)),
    CHECK (octet_length(contents) = size_bytes)
);

CREATE TABLE receipt_costs (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    receipt_id uuid NOT NULL,
    receipt_version integer NOT NULL CHECK (receipt_version > 0),
    updated_by uuid NOT NULL,
    body jsonb NOT NULL,
    total numeric(16,2) NOT NULL CHECK (total >= 0),
    PRIMARY KEY (org_id, store_id, receipt_id),
    FOREIGN KEY (org_id, store_id, receipt_id) REFERENCES receiving_documents,
    FOREIGN KEY (org_id, updated_by, store_id) REFERENCES staff_stores (org_id, staff_id, store_id)
);

CREATE TABLE receiving_movement_costs (
    org_id uuid NOT NULL,
    store_id uuid NOT NULL,
    receipt_id uuid NOT NULL,
    product_id uuid NOT NULL,
    lot_id uuid NOT NULL,
    total numeric(16,2) NOT NULL CHECK (total >= 0),
    PRIMARY KEY (org_id, store_id, receipt_id, product_id, lot_id),
    FOREIGN KEY (org_id, store_id, receipt_id, product_id, lot_id) REFERENCES receiving_movements
);

GRANT SELECT, INSERT ON suppliers, receipt_costs TO kasa_api;
GRANT UPDATE (name, tax_id, phone, email, payment_terms, lead_time_days, active, version) ON suppliers TO kasa_api;
GRANT UPDATE (receipt_version, updated_by, body, total) ON receipt_costs TO kasa_api;
GRANT SELECT, INSERT, DELETE ON invoice_files TO kasa_api;
GRANT UPDATE (scan_status, contents) ON invoice_files TO kasa_api;
GRANT INSERT ON receiving_movement_costs TO kasa_api;
COMMIT;
