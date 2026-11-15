BEGIN;

CREATE FUNCTION enforce_test_store_isolation() RETURNS trigger
LANGUAGE plpgsql AS $$
DECLARE organization_is_test boolean;
BEGIN
    SELECT is_test INTO organization_is_test FROM organizations WHERE id = NEW.org_id;
    IF organization_is_test IS NULL OR organization_is_test IS DISTINCT FROM NEW.is_test THEN
        RAISE EXCEPTION 'store test marker must match its organization';
    END IF;
    RETURN NEW;
END;
$$;

CREATE TRIGGER stores_test_isolation
BEFORE INSERT OR UPDATE OF org_id, is_test ON stores
FOR EACH ROW EXECUTE FUNCTION enforce_test_store_isolation();

COMMIT;
