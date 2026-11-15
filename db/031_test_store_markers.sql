BEGIN;

ALTER TABLE organizations ADD COLUMN is_test boolean NOT NULL DEFAULT false;
ALTER TABLE stores ADD COLUMN is_test boolean NOT NULL DEFAULT false;

CREATE INDEX organizations_test_id ON organizations (id) WHERE is_test;
CREATE INDEX stores_test_id ON stores (id) WHERE is_test;

COMMENT ON COLUMN organizations.is_test IS 'True only for isolated non-production organizations.';
COMMENT ON COLUMN stores.is_test IS 'True only for stores belonging to an isolated test organization.';

COMMIT;
