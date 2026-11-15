# Store operator authentication

This release separates the OIDC-authenticated store owner (the session host) from the operator whose actions are
recorded. Bootstrap routes require only the OIDC bearer token. Business routes also validate the opaque
`X-Operator-Session` token and `X-Active-Role` when `Auth:RequireOperatorSession=true`.

Store `admin` is an ordinary operational role. It does not require `Auth:AdminAcr`. Platform routes keep their
existing WebAuthn/FIDO2, USB and platform-staff checks in `PlatformService`.

## Rollout

1. Create a PostgreSQL custom-format backup and record its checksum.
2. Restore the backup into an isolated PostgreSQL 17 database and compare the control queries.
3. Apply `db/038_store_operator_auth.sql`.
4. Deploy the API with `Auth:RequireOperatorSession=false`.
5. Run the legacy Windows-client smoke suite.
6. Release the new Windows client and verify owner and employee login in a test store.
7. Set `Auth:RequireOperatorSession=true` and restart the API.
8. Run bootstrap, operator-session, role, register and checkout smoke tests.

Do not enable the flag until every supported client sends `X-Operator-Session`. The API validates a supplied
operator token even while the flag is false, so the new client can be tested before enforcement.

## Rollback

1. Set `Auth:RequireOperatorSession=false` and restart the API.
2. Roll back the API binaries to the preceding compatible release.
3. Prefer leaving migration 038 in place: it is additive and does not remove legacy staff or role data.
4. If schema rollback is mandatory and no new employees/sessions must be retained, run
   `db/rollback/038_store_operator_auth.down.sql` in a transaction after taking another backup.
5. If operator-era data must be retained, restore the verified pre-migration backup instead of running the down script.

The down script intentionally refuses to make local employees valid legacy OIDC identities. Export or discard new
operator data before using it.

## Legacy `staff_roles` removal plan

`staff_store_roles` is authoritative for new authorization. `staff_roles` remains populated for old clients and
platform owner-management code. Remove it only in a later release after:

1. all reads use `staff_store_roles`;
2. the compatibility triggers and old staff endpoints have no callers;
3. production backfill counts and per-store role sets have been reconciled;
4. a release has run with telemetry proving no legacy path usage;
5. a separate backup-tested migration removes the triggers, compatibility writes and table.

## Verification

- `dotnet test tests/Kasa.Api.Tests/Kasa.Api.Tests.csproj -c Release`
- `powershell -File tests/run_operator_auth.ps1`
- `powershell -File tests/test_operator_migration_rollback.ps1`
- `bash tests/run.sh` on a host with Bash, Docker, Python 3 and .NET 10

The focused suite verifies Argon2id hashes with unique salts, bootstrap isolation, owner and employee sessions,
idempotent employee creation, role/store denial, session revocation, lockout with `Retry-After`, digest-only tokens,
and absence of the test password from application logs and audit rows.
