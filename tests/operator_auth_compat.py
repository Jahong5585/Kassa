"""Compatibility check while operator-session enforcement is disabled."""
from operator_auth import STORE, call

roles, _ = call(f"/v1/me/roles?storeId={STORE}")
assert roles["owner"] is True
assert roles["roles"] == ["admin", "cashier", "receiver"]
registers, _ = call(f"/v1/stores/{STORE}/registers", role="admin")
assert isinstance(registers, list)
print("PASS: legacy bearer-only store API remains available while the feature flag is false")
