BEGIN;

CREATE TABLE staff_roles (
    org_id uuid NOT NULL,
    staff_id uuid NOT NULL,
    role text NOT NULL CHECK (role IN ('cashier', 'receiver', 'admin', 'owner')),
    PRIMARY KEY (org_id, staff_id, role),
    FOREIGN KEY (org_id, staff_id) REFERENCES staff (org_id, id) ON DELETE CASCADE
);

INSERT INTO staff_roles (org_id, staff_id, role)
SELECT org_id, id, role FROM staff
ON CONFLICT DO NOTHING;

COMMENT ON TABLE staff_roles IS
    'Authoritative organization-level role assignments. staff.role is legacy compatibility only.';

COMMIT;
