[CmdletBinding()]
param(
    [string]$SecretsFile = 'D:\KassaSecrets\.env',
    [string]$TestSecretsFile = 'D:\KassaSecrets\test-accounts.env'
)

$ErrorActionPreference = 'Stop'
$baseUrl = 'https://kassa.qr-space.uz'
$realm = 'kasa'
$flutterClientId = 'kasa-flutter'
$loginTestClientId = 'kasa-login-test'
$cashierUsername = 'test-cashier'
$adminUsername = 'test-admin'
$organizationId = '10000000-0000-0000-0000-000000000001'
$mainStoreId = '10000000-0000-0000-0000-000000000011'
$testStoreId = '20000000-0000-0000-0000-000000000011'
$testRegisterId = '20000000-0000-0000-0000-000000000021'
$cashierStaffId = '20000000-0000-0000-0000-000000000031'
$adminStaffId = '20000000-0000-0000-0000-000000000034'
$composeFile = Join-Path (Split-Path -Parent $PSCommandPath) 'compose.production.yaml'
$composeArgs = @('compose', '--env-file', $SecretsFile, '--file', $composeFile, '--project-name', 'kasa-production')

function Read-EnvFile([string]$Path) {
    $values = @{}
    Get-Content -LiteralPath $Path | ForEach-Object {
        if ($_ -and -not $_.StartsWith('#')) {
            $parts = $_ -split '=', 2
            if ($parts.Count -eq 2) { $values[$parts[0]] = $parts[1] }
        }
    }
    return $values
}

function New-HexSecret([int]$Bytes) {
    $buffer = [byte[]]::new($Bytes)
    [Security.Cryptography.RandomNumberGenerator]::Fill($buffer)
    return -join ($buffer | ForEach-Object { $_.ToString('x2') })
}

function ConvertTo-Base32([byte[]]$Bytes) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
    $output = [Text.StringBuilder]::new()
    [uint64]$buffer = 0
    $bits = 0
    foreach ($value in $Bytes) {
        $buffer = ($buffer -shl 8) -bor $value
        $bits += 8
        while ($bits -ge 5) {
            $bits -= 5
            [void]$output.Append($alphabet[($buffer -shr $bits) -band 31])
        }
        if ($bits -gt 0) { $buffer = $buffer -band (([uint64]1 -shl $bits) - 1) } else { $buffer = 0 }
    }
    if ($bits -gt 0) { [void]$output.Append($alphabet[($buffer -shl (5 - $bits)) -band 31]) }
    return $output.ToString()
}

function ConvertFrom-Base32([string]$Value) {
    $alphabet = 'ABCDEFGHIJKLMNOPQRSTUVWXYZ234567'
    $output = [Collections.Generic.List[byte]]::new()
    [uint64]$buffer = 0
    $bits = 0
    foreach ($character in $Value.TrimEnd('=').ToUpperInvariant().ToCharArray()) {
        $index = $alphabet.IndexOf($character)
        if ($index -lt 0) { throw 'Invalid Base32 character.' }
        $buffer = ($buffer -shl 5) -bor $index
        $bits += 5
        if ($bits -ge 8) {
            $bits -= 8
            $output.Add([byte](($buffer -shr $bits) -band 255))
            if ($bits -gt 0) { $buffer = $buffer -band (([uint64]1 -shl $bits) - 1) } else { $buffer = 0 }
        }
    }
    return $output.ToArray()
}

function Get-Totp([string]$SecretValue) {
    $Secret = ConvertFrom-Base32 $SecretValue
    $counter = [uint64][Math]::Floor([DateTimeOffset]::UtcNow.ToUnixTimeSeconds() / 30)
    $counterBytes = [BitConverter]::GetBytes($counter)
    if ([BitConverter]::IsLittleEndian) { [Array]::Reverse($counterBytes) }
    $hmac = [Security.Cryptography.HMACSHA1]::new($Secret)
    try { $hash = $hmac.ComputeHash($counterBytes) } finally { $hmac.Dispose() }
    $offset = $hash[$hash.Length - 1] -band 15
    $binary = (($hash[$offset] -band 127) -shl 24) -bor
        (($hash[$offset + 1] -band 255) -shl 16) -bor
        (($hash[$offset + 2] -band 255) -shl 8) -bor
        ($hash[$offset + 3] -band 255)
    return ($binary % 1000000).ToString('D6')
}

function Decode-JwtPayload([string]$Token) {
    $part = $Token.Split('.')[1].Replace('-', '+').Replace('_', '/')
    while ($part.Length % 4) { $part += '=' }
    return [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($part)) | ConvertFrom-Json
}

function Invoke-AdminJson([string]$Method, [string]$Uri, $Headers, $Body = $null) {
    $parameters = @{ Method = $Method; Uri = $Uri; Headers = $Headers; TimeoutSec = 30 }
    if ($null -ne $Body) {
        $parameters.ContentType = 'application/json'
        $parameters.Body = $Body | ConvertTo-Json -Depth 20 -Compress
    }
    return Invoke-RestMethod @parameters
}

function Find-AdminObject([string]$Uri, $Headers) {
    return @(Invoke-RestMethod -Uri $Uri -Headers $Headers -TimeoutSec 30)
}

if (-not (Test-Path -LiteralPath $SecretsFile)) { throw "Missing $SecretsFile" }
if (Test-Path -LiteralPath $TestSecretsFile) {
    throw "Test secret file already exists: $TestSecretsFile. Refusing to overwrite it."
}

$config = Read-EnvFile $SecretsFile
$cashierPassword = New-HexSecret 24
$adminPassword = New-HexSecret 24
$registerKey = New-HexSecret 32
$otpBytes = [byte[]]::new(20)
[Security.Cryptography.RandomNumberGenerator]::Fill($otpBytes)
$otpSecret = ConvertTo-Base32 $otpBytes
$temporaryAdminClient = 'kasa-bootstrap-' + (New-HexSecret 6)
$temporaryAdminSecret = New-HexSecret 32

$secretLines = @(
    "TEST_STORE_ID=$testStoreId",
    "TEST_REGISTER_ID=$testRegisterId",
    "TEST_CASHIER_USERNAME=$cashierUsername",
    "TEST_CASHIER_PASSWORD=$cashierPassword",
    "TEST_ADMIN_USERNAME=$adminUsername",
    "TEST_ADMIN_PASSWORD=$adminPassword",
    "TEST_ADMIN_TOTP_SECRET=$otpSecret",
    "TEST_REGISTER_KEY=$registerKey",
    "KASA_FLUTTER_CLIENT_ID=$flutterClientId",
    'KASA_FLUTTER_REDIRECT_URI=uz.qrspace.kassa:/oauth2redirect'
)
[IO.File]::WriteAllLines($TestSecretsFile, $secretLines, [Text.UTF8Encoding]::new($false))
& icacls $TestSecretsFile '/inheritance:r' '/grant:r' "${env:USERNAME}:(F)" | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Failed to restrict the test secret file ACL.' }

$keycloakStopped = $false
$temporaryClientCreated = $false
$adminHeaders = $null
$temporaryClientInternalId = $null

try {
    & docker @composeArgs stop keycloak | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to stop Keycloak.' }
    $keycloakStopped = $true

    $env:KC_DB_PASSWORD = $config['KEYCLOAK_DB_PASSWORD']
    $env:TEMP_ADMIN_SECRET = $temporaryAdminSecret
    & docker run --rm --network kasa-production_default `
        -e KC_DB=postgres `
        -e KC_DB_URL=jdbc:postgresql://db:5432/keycloak `
        -e KC_DB_USERNAME=keycloak `
        -e KC_DB_PASSWORD `
        -e TEMP_ADMIN_SECRET `
        quay.io/keycloak/keycloak:26.8.2 `
        bootstrap-admin service --client-id $temporaryAdminClient `
        '--client-secret:env=TEMP_ADMIN_SECRET' --no-prompt
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create the temporary Keycloak admin service account.' }
    $temporaryClientCreated = $true

    & docker @composeArgs start keycloak | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to restart Keycloak.' }
    $keycloakStopped = $false
    $deadline = (Get-Date).AddMinutes(4)
    do {
        Start-Sleep -Seconds 5
        try {
            $discovery = Invoke-RestMethod -Uri "$baseUrl/auth/realms/$realm/.well-known/openid-configuration" -TimeoutSec 10
        } catch { $discovery = $null }
    } while (-not $discovery -and (Get-Date) -lt $deadline)
    if (-not $discovery) { throw 'Keycloak did not become ready.' }

    $adminTokenReply = Invoke-RestMethod -Method Post `
        -Uri "$baseUrl/auth/realms/master/protocol/openid-connect/token" `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{ grant_type = 'client_credentials'; client_id = $temporaryAdminClient; client_secret = $temporaryAdminSecret } `
        -TimeoutSec 30
    if (-not $adminTokenReply.access_token) { throw 'Temporary admin token was not issued.' }
    $adminHeaders = @{ Authorization = "Bearer $($adminTokenReply.access_token)" }
    $masterClients = Find-AdminObject "$baseUrl/auth/admin/realms/master/clients?clientId=$temporaryAdminClient" $adminHeaders
    if ($masterClients.Count -ne 1) { throw 'Temporary admin client could not be located.' }
    $temporaryClientInternalId = $masterClients[0].id

    foreach ($clientId in @($flutterClientId, $loginTestClientId)) {
        $existing = Find-AdminObject "$baseUrl/auth/admin/realms/$realm/clients?clientId=$clientId" $adminHeaders
        foreach ($client in $existing) {
            Invoke-AdminJson Delete "$baseUrl/auth/admin/realms/$realm/clients/$($client.id)" $adminHeaders | Out-Null
        }
    }
    foreach ($username in @($cashierUsername, $adminUsername)) {
        $existing = Find-AdminObject "$baseUrl/auth/admin/realms/$realm/users?username=$username&exact=true" $adminHeaders
        foreach ($user in $existing) {
            Invoke-AdminJson Delete "$baseUrl/auth/admin/realms/$realm/users/$($user.id)" $adminHeaders | Out-Null
        }
    }

    $audienceMapper = @{
        name = 'kasa-api-audience'
        protocol = 'openid-connect'
        protocolMapper = 'oidc-audience-mapper'
        consentRequired = $false
        config = @{
            'included.client.audience' = 'kasa-api'
            'id.token.claim' = 'false'
            'access.token.claim' = 'true'
        }
    }
    $flutterClient = @{
        clientId = $flutterClientId
        name = 'Kassa Flutter mobile application'
        enabled = $true
        protocol = 'openid-connect'
        publicClient = $true
        standardFlowEnabled = $true
        directAccessGrantsEnabled = $false
        serviceAccountsEnabled = $false
        redirectUris = @('uz.qrspace.kassa:/oauth2redirect', 'kassa://oauth/callback')
        webOrigins = @('+')
        attributes = @{
            'pkce.code.challenge.method' = 'S256'
            'post.logout.redirect.uris' = 'uz.qrspace.kassa:/oauth2redirect##kassa://oauth/callback'
        }
        protocolMappers = @($audienceMapper)
    }
    Invoke-AdminJson Post "$baseUrl/auth/admin/realms/$realm/clients" $adminHeaders $flutterClient | Out-Null

    $loginTestClient = @{
        clientId = $loginTestClientId
        name = 'Temporary Kassa login verification client'
        enabled = $true
        protocol = 'openid-connect'
        publicClient = $true
        standardFlowEnabled = $false
        directAccessGrantsEnabled = $true
        serviceAccountsEnabled = $false
        redirectUris = @()
        webOrigins = @()
        protocolMappers = @($audienceMapper)
    }
    Invoke-AdminJson Post "$baseUrl/auth/admin/realms/$realm/clients" $adminHeaders $loginTestClient | Out-Null

    $cashierUser = @{
        username = $cashierUsername
        enabled = $true
        emailVerified = $true
        firstName = 'Test'
        lastName = 'Cashier'
        email = 'test-cashier@kassa.invalid'
        requiredActions = @()
        credentials = @(@{ type = 'password'; value = $cashierPassword; temporary = $false })
    }
    Invoke-AdminJson Post "$baseUrl/auth/admin/realms/$realm/users" $adminHeaders $cashierUser | Out-Null

    $otpCredential = @{
        type = 'otp'
        userLabel = 'Kassa test authenticator'
        secretData = (@{ value = $otpSecret } | ConvertTo-Json -Compress)
        credentialData = (@{
            subType = 'totp'; digits = 6; counter = 0; period = 30; algorithm = 'HmacSHA1'; secretEncoding = 'BASE32'
        } | ConvertTo-Json -Compress)
    }
    $adminUser = @{
        username = $adminUsername
        enabled = $true
        emailVerified = $true
        firstName = 'Test'
        lastName = 'Administrator'
        email = 'test-admin@kassa.invalid'
        requiredActions = @()
        credentials = @(
            @{ type = 'password'; value = $adminPassword; temporary = $false },
            $otpCredential
        )
    }
    Invoke-AdminJson Post "$baseUrl/auth/admin/realms/$realm/users" $adminHeaders $adminUser | Out-Null

    $cashierUsers = Find-AdminObject "$baseUrl/auth/admin/realms/$realm/users?username=$cashierUsername&exact=true" $adminHeaders
    $adminUsers = Find-AdminObject "$baseUrl/auth/admin/realms/$realm/users?username=$adminUsername&exact=true" $adminHeaders
    if ($cashierUsers.Count -ne 1 -or $adminUsers.Count -ne 1) { throw 'Created test users could not be located.' }
    $cashierSubject = $cashierUsers[0].id
    $adminSubject = $adminUsers[0].id

    $hash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($registerKey))
    $hashHex = -join ($hash | ForEach-Object { $_.ToString('x2') })
    $databaseSql = @"
BEGIN;
INSERT INTO stores (org_id, id, name, time_zone)
VALUES ('$organizationId', '$testStoreId', 'Тестовый магазин', 'Asia/Tashkent')
ON CONFLICT (org_id, id) DO UPDATE SET name = EXCLUDED.name, time_zone = EXCLUDED.time_zone;
INSERT INTO registers (org_id, store_id, id, name, key_digest, active)
VALUES ('$organizationId', '$testStoreId', '$testRegisterId', 'Тестовая касса', decode('$hashHex', 'hex'), true)
ON CONFLICT (org_id, store_id, id) DO UPDATE
SET name = EXCLUDED.name, key_digest = EXCLUDED.key_digest, active = true;
INSERT INTO staff (org_id, id, subject, role, active)
VALUES ('$organizationId', '$cashierStaffId', '$cashierSubject', 'cashier', true)
ON CONFLICT (org_id, id) DO UPDATE SET subject = EXCLUDED.subject, role = 'cashier', active = true;
INSERT INTO staff (org_id, id, subject, role, active)
VALUES ('$organizationId', '$adminStaffId', '$adminSubject', 'admin', true)
ON CONFLICT (org_id, id) DO UPDATE SET subject = EXCLUDED.subject, role = 'admin', active = true;
DELETE FROM cashier_registers WHERE org_id = '$organizationId' AND staff_id IN ('$cashierStaffId', '$adminStaffId');
DELETE FROM staff_stores WHERE org_id = '$organizationId' AND staff_id IN ('$cashierStaffId', '$adminStaffId')
    AND store_id <> '$testStoreId';
INSERT INTO staff_stores (org_id, staff_id, store_id, active)
VALUES ('$organizationId', '$cashierStaffId', '$testStoreId', true)
ON CONFLICT (org_id, staff_id, store_id) DO UPDATE SET active = true;
INSERT INTO staff_stores (org_id, staff_id, store_id, active)
VALUES ('$organizationId', '$adminStaffId', '$testStoreId', true)
ON CONFLICT (org_id, staff_id, store_id) DO UPDATE SET active = true;
INSERT INTO cashier_registers (org_id, staff_id, store_id, register_id)
VALUES ('$organizationId', '$cashierStaffId', '$testStoreId', '$testRegisterId')
ON CONFLICT DO NOTHING;
COMMIT;
"@
    $databaseSql | & docker exec -i kasa-production-db-1 psql -U kasa_owner -d kasa --set ON_ERROR_STOP=1 | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Failed to create the application test assignments.' }

    $tokenEndpoint = "$baseUrl/auth/realms/$realm/protocol/openid-connect/token"
    $cashierTokenReply = Invoke-RestMethod -Method Post -Uri $tokenEndpoint `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{ grant_type = 'password'; client_id = $loginTestClientId; username = $cashierUsername; password = $cashierPassword; scope = 'openid' } `
        -TimeoutSec 30
    if (-not $cashierTokenReply.access_token) { throw 'Cashier token was not issued.' }

    $passwordOnlyRejected = $false
    try {
        Invoke-RestMethod -Method Post -Uri $tokenEndpoint -ContentType 'application/x-www-form-urlencoded' `
            -Body @{ grant_type = 'password'; client_id = $loginTestClientId; username = $adminUsername; password = $adminPassword; scope = 'openid' } `
            -TimeoutSec 30 | Out-Null
    } catch {
        if ([int]$_.Exception.Response.StatusCode -eq 401 -or [int]$_.Exception.Response.StatusCode -eq 400) {
            $passwordOnlyRejected = $true
        } else { throw }
    }
    if (-not $passwordOnlyRejected) { throw 'Administrator login unexpectedly succeeded without TOTP.' }

    $totp = Get-Totp $otpSecret
    $adminTokenReply = Invoke-RestMethod -Method Post -Uri $tokenEndpoint `
        -ContentType 'application/x-www-form-urlencoded' `
        -Body @{ grant_type = 'password'; client_id = $loginTestClientId; username = $adminUsername; password = $adminPassword; totp = $totp; scope = 'openid' } `
        -TimeoutSec 30
    if (-not $adminTokenReply.access_token) { throw 'Administrator token with TOTP was not issued.' }

    $cashierClaims = Decode-JwtPayload $cashierTokenReply.access_token
    $adminClaims = Decode-JwtPayload $adminTokenReply.access_token
    if ($cashierClaims.sub -ne $cashierSubject -or $adminClaims.sub -ne $adminSubject) {
        throw 'OIDC token subject mismatch.'
    }
    if (@($cashierClaims.aud) -notcontains 'kasa-api' -or @($adminClaims.aud) -notcontains 'kasa-api') {
        throw 'OIDC token audience mismatch.'
    }
    if (([int64]$cashierClaims.exp - [int64]$cashierClaims.iat) -gt 600 -or
        ([int64]$adminClaims.exp - [int64]$adminClaims.iat) -gt 600) {
        throw 'OIDC token lifetime exceeds the API limit.'
    }

    $cashierHeaders = @{
        Authorization = "Bearer $($cashierTokenReply.access_token)"
        'X-Register-Id' = $testRegisterId
        'X-Register-Key' = $registerKey
    }
    $adminApiHeaders = @{ Authorization = "Bearer $($adminTokenReply.access_token)" }
    $cashierCatalog = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/v1/catalog/search?q=test" `
        -Headers $cashierHeaders -TimeoutSec 30
    if ($cashierCatalog.StatusCode -ne 200) { throw 'Cashier API access failed.' }
    $adminStaff = Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/v1/stores/$testStoreId/staff" `
        -Headers $adminApiHeaders -TimeoutSec 30
    if ($adminStaff.StatusCode -ne 200) { throw 'Administrator API access failed.' }

    $cashierAdminDenied = $false
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/v1/stores/$testStoreId/staff" `
            -Headers $cashierHeaders -TimeoutSec 30 | Out-Null
    } catch {
        if ([int]$_.Exception.Response.StatusCode -eq 403) { $cashierAdminDenied = $true } else { throw }
    }
    if (-not $cashierAdminDenied) { throw 'Cashier unexpectedly accessed the administrator route.' }

    $mainStoreDenied = $false
    try {
        Invoke-WebRequest -UseBasicParsing -Uri "$baseUrl/v1/stores/$mainStoreId/staff" `
            -Headers $adminApiHeaders -TimeoutSec 30 | Out-Null
    } catch {
        if ([int]$_.Exception.Response.StatusCode -eq 404) { $mainStoreDenied = $true } else { throw }
    }
    if (-not $mainStoreDenied) { throw 'Test administrator unexpectedly accessed the main store.' }

    $challenge = New-HexSecret 32
    $challengeHash = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::ASCII.GetBytes($challenge))
    $codeChallenge = [Convert]::ToBase64String($challengeHash).TrimEnd('=').Replace('+', '-').Replace('/', '_')
    $authorizeUri = "$baseUrl/auth/realms/$realm/protocol/openid-connect/auth?client_id=$flutterClientId&redirect_uri=$([Uri]::EscapeDataString('uz.qrspace.kassa:/oauth2redirect'))&response_type=code&scope=openid&code_challenge=$codeChallenge&code_challenge_method=S256"
    $authorizationPage = Invoke-WebRequest -UseBasicParsing -Uri $authorizeUri -MaximumRedirection 5 -TimeoutSec 30
    if ($authorizationPage.StatusCode -ne 200) { throw 'Flutter PKCE authorization endpoint did not return a login page.' }

    $testClients = Find-AdminObject "$baseUrl/auth/admin/realms/$realm/clients?clientId=$loginTestClientId" $adminHeaders
    foreach ($client in $testClients) {
        Invoke-AdminJson Delete "$baseUrl/auth/admin/realms/$realm/clients/$($client.id)" $adminHeaders | Out-Null
    }
    $flutterClients = Find-AdminObject "$baseUrl/auth/admin/realms/$realm/clients?clientId=$flutterClientId" $adminHeaders
    if ($flutterClients.Count -ne 1 -or -not $flutterClients[0].publicClient -or
        -not $flutterClients[0].standardFlowEnabled -or $flutterClients[0].directAccessGrantsEnabled -or
        $flutterClients[0].attributes.'pkce.code.challenge.method' -ne 'S256') {
        throw 'Flutter client security configuration mismatch.'
    }

    Invoke-AdminJson Delete "$baseUrl/auth/admin/realms/master/clients/$temporaryClientInternalId" $adminHeaders | Out-Null
    $temporaryClientCreated = $false

    Write-Output 'TEST_IDENTITY_CONFIGURATION=PASS'
    Write-Output "CASHIER_USERNAME=$cashierUsername"
    Write-Output "ADMIN_USERNAME=$adminUsername"
    Write-Output "TEST_STORE_ID=$testStoreId"
    Write-Output "TEST_REGISTER_ID=$testRegisterId"
    Write-Output "FLUTTER_CLIENT_ID=$flutterClientId"
    Write-Output "OIDC_ISSUER=$baseUrl/auth/realms/$realm"
    Write-Output "OIDC_AUTHORIZATION_ENDPOINT=$baseUrl/auth/realms/$realm/protocol/openid-connect/auth"
    Write-Output "OIDC_TOKEN_ENDPOINT=$baseUrl/auth/realms/$realm/protocol/openid-connect/token"
    Write-Output 'FLUTTER_PKCE=S256'
    Write-Output 'FLUTTER_DIRECT_GRANTS=False'
    Write-Output 'CASHIER_LOGIN=PASS'
    Write-Output 'ADMIN_PASSWORD_ONLY=REJECTED'
    Write-Output 'ADMIN_PASSWORD_PLUS_TOTP=PASS'
    Write-Output 'ROLE_BOUNDARIES=PASS'
    Write-Output "SECRETS_FILE=$TestSecretsFile"
}
finally {
    Remove-Item Env:KC_DB_PASSWORD -ErrorAction SilentlyContinue
    Remove-Item Env:TEMP_ADMIN_SECRET -ErrorAction SilentlyContinue
    if ($keycloakStopped) {
        & docker @composeArgs start keycloak | Out-Null
    }
    if ($temporaryClientCreated -and $adminHeaders -and $temporaryClientInternalId) {
        try {
            Invoke-AdminJson Delete "$baseUrl/auth/admin/realms/master/clients/$temporaryClientInternalId" $adminHeaders | Out-Null
        } catch { }
    }
}
