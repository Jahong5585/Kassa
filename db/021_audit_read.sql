BEGIN;

CREATE INDEX audit_events_store_time_idx ON audit_events (org_id, store_id, occurred_at DESC, id DESC);

CREATE FUNCTION public.read_store_audit(uuid, uuid, date, date, text, text, timestamptz, bigint, integer)
RETURNS TABLE (
    id bigint, occurred_at timestamptz, action text, staff_id uuid, object_id uuid, register_id uuid, request_id text
)
LANGUAGE sql STABLE SECURITY DEFINER SET search_path = pg_catalog, pg_temp
AS $$
    SELECT e.id, e.occurred_at, e.action, e.staff_id, e.object_id, e.register_id, e.request_id
    FROM public.audit_events e
    WHERE (e.org_id, e.store_id) = ($1, $2)
      AND $4 - $3 BETWEEN 0 AND 92
      AND e.occurred_at >= ($3::timestamp AT TIME ZONE $5)
      AND e.occurred_at < (($4 + 1)::timestamp AT TIME ZONE $5)
      AND ($6 = '' OR e.action = $6)
      AND ($8 = 0 OR (e.occurred_at, e.id) < ($7, $8))
    ORDER BY e.occurred_at DESC, e.id DESC
    LIMIT least(greatest($9, 1), 101);
$$;

REVOKE ALL ON FUNCTION public.read_store_audit(uuid, uuid, date, date, text, text, timestamptz, bigint, integer) FROM PUBLIC;
GRANT EXECUTE ON FUNCTION public.read_store_audit(uuid, uuid, date, date, text, text, timestamptz, bigint, integer) TO kasa_api;

COMMIT;
