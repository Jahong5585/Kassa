INSERT INTO staff_stores (org_id, staff_id, store_id, active)
VALUES ('00000000-0000-0000-0000-000000000001', '00000000-0000-0000-0000-000000003050',
        '00000000-0000-0000-0000-000000000013', true)
ON CONFLICT (org_id, staff_id, store_id) DO UPDATE SET active = true;
