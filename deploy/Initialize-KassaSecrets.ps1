[CmdletBinding()]
param([string]$SecretsDirectory = 'D:\KassaSecrets')

$ErrorActionPreference = 'Stop'
$environmentFile = Join-Path $SecretsDirectory '.env'
$realmDirectory = Join-Path $SecretsDirectory 'keycloak'
$realmFile = Join-Path $realmDirectory 'realm-kasa.json'
if ((Test-Path -LiteralPath $environmentFile) -or (Test-Path -LiteralPath $realmFile)) {
    throw "Secrets already exist in $SecretsDirectory. Existing files were not overwritten."
}

New-Item -ItemType Directory -Force -Path $realmDirectory | Out-Null
function New-Secret([int]$Bytes = 32) {
    $buffer = [byte[]]::new($Bytes)
    $generator = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $generator.GetBytes($buffer) } finally { $generator.Dispose() }
    return -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

$adminSubject = [guid]::NewGuid().ToString()
$adminInitialPassword = New-Secret 24
$monitorSecret = New-Secret 32
$values = [ordered]@{
    KASA_SECRETS_DIR = ($SecretsDirectory -replace '\\','/')
    POS_DB_PORT = '5432'
    POS_DB_OWNER_PASSWORD = New-Secret
    POS_DB_APP_PASSWORD = New-Secret
    KEYCLOAK_DB_PASSWORD = New-Secret
    KEYCLOAK_BOOTSTRAP_ADMIN_PASSWORD = New-Secret 24
    KASA_ADMIN_SUBJECT = $adminSubject
    KASA_ADMIN_INITIAL_PASSWORD = $adminInitialPassword
    KASA_MONITOR_CLIENT_SECRET = $monitorSecret
}
$environmentText = ($values.GetEnumerator() | ForEach-Object { '{0}={1}' -f $_.Key, $_.Value }) -join "`n"
[IO.File]::WriteAllText($environmentFile, $environmentText + "`n", [Text.UTF8Encoding]::new($false))

$audienceMapper = [ordered]@{
    name = 'kasa-api-audience'; protocol = 'openid-connect'; protocolMapper = 'oidc-audience-mapper'
    consentRequired = $false
    config = [ordered]@{
        'included.client.audience' = 'kasa-api'; 'id.token.claim' = 'false'
        'access.token.claim' = 'true'; 'introspection.token.claim' = 'true'
    }
}
$realm = [ordered]@{
    realm = 'kasa'; enabled = $true; displayName = 'Касса Минимаркет'
    sslRequired = 'external'; registrationAllowed = $false; resetPasswordAllowed = $true
    loginWithEmailAllowed = $false; bruteForceProtected = $true
    accessTokenLifespan = 300; ssoSessionIdleTimeout = 900; ssoSessionMaxLifespan = 28800
    otpPolicyType = 'totp'; otpPolicyAlgorithm = 'HmacSHA256'; otpPolicyDigits = 6; otpPolicyPeriod = 30
    clients = @(
        [ordered]@{ clientId = 'kasa-api'; name = 'Kasa API'; enabled = $true; bearerOnly = $true
            protocol = 'openid-connect' },
        [ordered]@{ clientId = 'kasa-windows'; name = 'Kasa Windows'; enabled = $true; publicClient = $true
            standardFlowEnabled = $true; directAccessGrantsEnabled = $false; protocol = 'openid-connect'
            redirectUris = @('http://127.0.0.1/*'); webOrigins = @('http://127.0.0.1')
            attributes = [ordered]@{ 'pkce.code.challenge.method' = 'S256' }
            protocolMappers = @($audienceMapper) },
        [ordered]@{ clientId = 'kasa-monitor'; name = 'Kasa protected health check'; enabled = $true
            publicClient = $false; secret = $monitorSecret; serviceAccountsEnabled = $true
            standardFlowEnabled = $false; directAccessGrantsEnabled = $false; protocol = 'openid-connect'
            protocolMappers = @($audienceMapper) }
    )
    users = @(
        [ordered]@{ id = $adminSubject; username = 'kasa-admin'; enabled = $true
            credentials = @([ordered]@{ type = 'password'; value = $adminInitialPassword; temporary = $true })
            requiredActions = @('UPDATE_PASSWORD', 'CONFIGURE_TOTP') }
    )
}
[IO.File]::WriteAllText($realmFile, ($realm | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))

& icacls.exe $SecretsDirectory /inheritance:r /grant:r "${env:USERNAME}:(OI)(CI)F" /t /c | Out-Null
& icacls.exe (Join-Path $SecretsDirectory '*') /grant:r "${env:USERNAME}:F" /t /c | Out-Null
Write-Output "Created protected deployment secrets in $SecretsDirectory"
Write-Output 'The initial application-admin password is stored in .env and must be changed at first sign-in.'
