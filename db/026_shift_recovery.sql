BEGIN;

ALTER TABLE shifts ADD COLUMN recovered_by uuid;
ALTER TABLE shifts ADD COLUMN recovery_reason text;
ALTER TABLE shifts ADD CONSTRAINT shifts_recovery_state CHECK (
    (recovered_by IS NULL) = (recovery_reason IS NULL) AND
    (recovery_reason IS NULL OR length(btrim(recovery_reason)) BETWEEN 1 AND 500));
ALTER TABLE shifts ADD CONSTRAINT shifts_recovered_by_staff
    FOREIGN KEY (org_id, recovered_by) REFERENCES staff (org_id, id);

GRANT UPDATE (recovered_by, recovery_reason) ON shifts TO kasa_api;

COMMIT;
