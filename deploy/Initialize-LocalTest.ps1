[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if ([IO.Path]::GetPathRoot($root) -ne 'D:\') { throw 'Local test deployment must stay on drive D:.' }
$settings = Join-Path $root '.env.local-test'
$dotnet = 'D:\CodexTools\dotnet\dotnet.exe'

function New-Hex([int]$bytes) {
    $buffer = [byte[]]::new($bytes)
    [Security.Cryptography.RandomNumberGenerator]::Fill($buffer)
    [Convert]::ToHexString($buffer).ToLowerInvariant()
}

function New-Password {
    $alphabet = 'ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz23456789_-'
    $bytes = [byte[]]::new(28)
    [Security.Cryptography.RandomNumberGenerator]::Fill($bytes)
    $chars = foreach ($byte in $bytes) { $alphabet[$byte % $alphabet.Length] }
    'A7_' + (-join $chars)
}

function Import-Settings {
    foreach ($line in [IO.File]::ReadAllLines($settings)) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
        $parts = $line.Split('=', 2)
        [Environment]::SetEnvironmentVariable($parts[0], $parts[1], 'Process')
    }
}

$generateSettings = -not (Test-Path -LiteralPath $settings)
if (-not $generateSettings) {
    $passwordLine = [IO.File]::ReadAllLines($settings) | Where-Object { $_.StartsWith('LOCAL_TEST_DEVELOPER_PASSWORD=') } | Select-Object -First 1
    $generateSettings = $passwordLine -notmatch '^LOCAL_TEST_DEVELOPER_PASSWORD=[A-Za-z0-9_-]{20,}$'
}
if ($generateSettings) {
    $password = New-Password
    $owner = New-Hex 32
    $app = New-Hex 32
    $developmentKey = New-Hex 48
    $localKey = New-Hex 48
    $lines = @(
        '# Generated locally. Never commit or share this file.',
        ('POS_DB_OWNER_PASSWORD=' + $owner),
        ('POS_DB_APP_PASSWORD=' + $app),
        'POS_DB_PORT=55435',
        'ASPNETCORE_ENVIRONMENT=Development',
        'ASPNETCORE_URLS=http://127.0.0.1:3510',
        ('ConnectionStrings__Pos=Host=127.0.0.1;Port=55435;Database=kasa;Username=kasa_api;Password=' + $app),
        'Auth__Audience=kasa-local-api',
        'Auth__AdminAcr=kasa-admin-webauthn',
        ('Auth__DevelopmentKey=' + $developmentKey),
        'Auth__AllowLocalTestDeveloper=true',
        ('Auth__LocalTestDeveloperSigningKey=' + $localKey),
        'Auth__LocalTestDeveloperTokenMinutes=30',
        'LOCAL_TEST_DEVELOPER_LOGIN=developer-test',
        ('LOCAL_TEST_DEVELOPER_PASSWORD=' + $password),
        'Ai__ProcurementEnabled=false'
    )
    [IO.File]::WriteAllLines($settings, $lines, [Text.UTF8Encoding]::new($false))
    & icacls.exe $settings /inheritance:r /grant:r "$env:USERNAME`:(F)" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Could not restrict the local settings ACL.' }
}

Import-Settings
& docker compose --project-name kasa-local-test --env-file $settings up -d db --wait
if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL 17 failed to initialize.' }

$passwordBytes = [Text.Encoding]::UTF8.GetBytes($env:LOCAL_TEST_DEVELOPER_PASSWORD)
$salt = [byte[]]::new(16)
[Security.Cryptography.RandomNumberGenerator]::Fill($salt)
$digest = [Security.Cryptography.Rfc2898DeriveBytes]::Pbkdf2(
    $passwordBytes, $salt, 210000, [Security.Cryptography.HashAlgorithmName]::SHA256, 32)
$env:LOCAL_PASSWORD_HASH = 'pbkdf2-sha256$210000$' + [Convert]::ToBase64String($salt) + '$' + [Convert]::ToBase64String($digest)
$seed = @'
\getenv password_hash LOCAL_PASSWORD_HASH
INSERT INTO platform_staff (issuer, subject, password_hash, can_read, can_write, active, updated_at)
VALUES ('kasa-local', 'developer-test', :'password_hash', true, true, true, now())
ON CONFLICT (issuer, subject) DO UPDATE SET
    password_hash = EXCLUDED.password_hash,
    can_read = true,
    can_write = true,
    active = true,
    updated_at = now();
'@
$seed | & docker compose --project-name kasa-local-test --env-file $settings exec -T -e LOCAL_PASSWORD_HASH `
    db psql -X -U kasa_owner -d kasa -v ON_ERROR_STOP=1
if ($LASTEXITCODE -ne 0) { throw 'Could not seed developer-test.' }
[Environment]::SetEnvironmentVariable('LOCAL_PASSWORD_HASH', $null, 'Process')

$verified = & docker compose --project-name kasa-local-test --env-file $settings exec -T db `
    psql -XAt -U kasa_owner -d kasa -v ON_ERROR_STOP=1 -c `
    "SELECT current_setting('server_version_num')::int / 10000 = 17 AND to_regclass('platform_staff') IS NOT NULL AND EXISTS (SELECT 1 FROM information_schema.columns WHERE table_name='stores' AND column_name='platform_version') AND EXISTS (SELECT 1 FROM platform_staff WHERE issuer='kasa-local' AND subject='developer-test' AND can_read AND can_write AND active)"
if ($LASTEXITCODE -ne 0 -or $verified.Trim() -ne 't') { throw 'Migration 036 or developer account verification failed.' }

& $dotnet restore (Join-Path $root 'src\Kasa.Api\Kasa.Api.csproj') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'API restore failed.' }
& $dotnet publish (Join-Path $root 'src\Kasa.Api\Kasa.Api.csproj') -c Release --no-restore `
    -o (Join-Path $root 'artifacts\local-test\api') -warnaserror
if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }
& (Join-Path $PSScriptRoot 'Manage-LocalTest.ps1') Start

[pscustomobject]@{
    Login = 'developer-test'
    Address = 'http://127.0.0.1:3510/'
    Settings = $settings
    Database = 'PostgreSQL 17 / kasa-local-test / migrations 001-036'
}
