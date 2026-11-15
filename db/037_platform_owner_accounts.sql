BEGIN;

ALTER TABLE platform_store_events DROP CONSTRAINT platform_store_events_action_check;
ALTER TABLE platform_store_events ADD CONSTRAINT platform_store_events_action_check
    CHECK (action IN ('create_test_store', 'rename_test_store', 'assign_owner'));

COMMIT;
