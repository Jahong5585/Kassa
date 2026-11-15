param(
    [string]$EnvFile = 'D:\KassaSecrets\.env',
    [string]$KeycloakUrl = 'http://127.0.0.1:3500/auth'
)

$ErrorActionPreference = 'Stop'

function Read-Env([string]$Path) {
    $values = @{}
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line.TrimStart().StartsWith('#') -or -not $line.Contains('=')) { continue }
        $parts = $line.Split('=', 2)
        $value = $parts[1]
        if ($value.Length -ge 2 -and (($value[0] -eq '"' -and $value[-1] -eq '"') -or
                ($value[0] -eq "'" -and $value[-1] -eq "'"))) {
            $value = $value.Substring(1, $value.Length - 2)
        }
        $values[$parts[0].Trim()] = $value
    }
    return $values
}

function Set-Env([string]$Path, [hashtable]$Updates) {
    $lines = [Collections.Generic.List[string]]::new()
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($line in [IO.File]::ReadAllLines($Path)) {
        if ($line -match '^\s*([^#=]+)=') {
            $key = $matches[1].Trim()
            if ($Updates.ContainsKey($key)) {
                $lines.Add($key + '=' + $Updates[$key])
                [void]$seen.Add($key)
                continue
            }
        }
        $lines.Add($line)
    }
    foreach ($key in $Updates.Keys | Sort-Object) {
        if (-not $seen.Contains($key)) { $lines.Add($key + '=' + $Updates[$key]) }
    }
    [IO.File]::WriteAllLines($Path, $lines, [Text.UTF8Encoding]::new($false))
}

if (-not (Test-Path -LiteralPath $EnvFile)) { throw "Secret environment file not found: $EnvFile" }
$settings = Read-Env $EnvFile
if (-not $settings.ContainsKey('KEYCLOAK_BOOTSTRAP_ADMIN_PASSWORD')) {
    throw 'KEYCLOAK_BOOTSTRAP_ADMIN_PASSWORD is missing.'
}

$token = Invoke-RestMethod -Method Post -Uri "$KeycloakUrl/realms/master/protocol/openid-connect/token" `
    -ContentType 'application/x-www-form-urlencoded' -Body @{
        grant_type = 'password'; client_id = 'admin-cli'; username = 'master-admin'
        password = $settings.KEYCLOAK_BOOTSTRAP_ADMIN_PASSWORD
    }
$headers = @{ Authorization = 'Bearer ' + $token.access_token }
$realm = 'kasa'
$clientId = 'kasa-platform-provisioner'
$clients = @(Invoke-RestMethod -Headers $headers -Uri "$KeycloakUrl/admin/realms/$realm/clients?clientId=$clientId")

if ($clients.Count -eq 0) {
    $bytes = [byte[]]::new(48)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $secret = [Convert]::ToBase64String($bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $body = @{
        clientId = $clientId; name = 'Kassa platform identity provisioner'; protocol = 'openid-connect'
        enabled = $true; publicClient = $false; bearerOnly = $false; serviceAccountsEnabled = $true
        standardFlowEnabled = $false; implicitFlowEnabled = $false; directAccessGrantsEnabled = $false
        secret = $secret
    } | ConvertTo-Json
    Invoke-WebRequest -Method Post -Headers $headers -ContentType 'application/json' `
        -Uri "$KeycloakUrl/admin/realms/$realm/clients" -Body $body | Out-Null
    $clients = @(Invoke-RestMethod -Headers $headers -Uri "$KeycloakUrl/admin/realms/$realm/clients?clientId=$clientId")
} else {
    $secretResult = Invoke-RestMethod -Headers $headers `
        -Uri "$KeycloakUrl/admin/realms/$realm/clients/$($clients[0].id)/client-secret"
    $secret = $secretResult.value
}

if ($clients.Count -ne 1 -or [string]::IsNullOrWhiteSpace($secret)) {
    throw 'Could not create or resolve the identity provisioning client.'
}

$serviceAccount = Invoke-RestMethod -Headers $headers `
    -Uri "$KeycloakUrl/admin/realms/$realm/clients/$($clients[0].id)/service-account-user"
$management = @(Invoke-RestMethod -Headers $headers `
    -Uri "$KeycloakUrl/admin/realms/$realm/clients?clientId=realm-management")
if ($management.Count -ne 1) { throw 'realm-management client was not found.' }
$roles = @()
foreach ($name in @('manage-users', 'view-users', 'query-users')) {
    $roles += Invoke-RestMethod -Headers $headers `
        -Uri "$KeycloakUrl/admin/realms/$realm/clients/$($management[0].id)/roles/$name"
}
Invoke-WebRequest -Method Post -Headers $headers -ContentType 'application/json' `
    -Uri "$KeycloakUrl/admin/realms/$realm/users/$($serviceAccount.id)/role-mappings/clients/$($management[0].id)" `
    -Body ($roles | ConvertTo-Json -Depth 5 -AsArray) | Out-Null

Set-Env $EnvFile @{
    KASA_IDENTITY_PROVISIONING_ENABLED = 'true'
    KASA_IDENTITY_PROVISIONING_BASE_URL = 'http://keycloak:8080/auth/'
    KASA_IDENTITY_PROVISIONING_REALM = $realm
    KASA_IDENTITY_PROVISIONING_CLIENT_ID = $clientId
    KASA_IDENTITY_PROVISIONING_CLIENT_SECRET = $secret
}
& icacls.exe $EnvFile /inheritance:r /grant:r "$env:USERNAME`:(F)" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Could not restrict the secret environment file ACL.' }

[pscustomobject]@{
    ClientId = $clientId
    Realm = $realm
    Enabled = $true
    SecretStoredOutsideGit = $true
}
