param(
    [int]$DatabasePort = 55442,
    [int]$ApiPort = 5192
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$project = 'kasa-operator-auth-' + $PID
$dotnet = 'D:\CodexTools\dotnet\dotnet.exe'
$logs = @(
    (Join-Path $root '.tmp-operator-fixture.log'),
    (Join-Path $root '.tmp-operator-api.out.log'),
    (Join-Path $root '.tmp-operator-api.err.log'),
    (Join-Path $root '.tmp-operator-security.log'),
    (Join-Path $root '.tmp-operator-compat.out.log'),
    (Join-Path $root '.tmp-operator-compat.err.log')
)
$api = $null

try {
    $env:POS_DB_OWNER_PASSWORD = [Guid]::NewGuid().ToString('N')
    $env:POS_DB_APP_PASSWORD = [Guid]::NewGuid().ToString('N')
    $env:POS_DB_PORT = $DatabasePort.ToString()
    $env:POS_TEST_REGISTER_KEY = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    $env:Auth__DevelopmentKey = [Guid]::NewGuid().ToString('N') + [Guid]::NewGuid().ToString('N')
    $env:Auth__Authority = 'kasa-local'
    $env:Auth__Audience = 'kasa-operator-test'
    $env:Auth__AdminAcr = 'kasa-platform-hardware'
    $env:Auth__RequireOperatorSession = 'true'
    $env:ASPNETCORE_ENVIRONMENT = 'Development'
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$ApiPort"
    $env:ConnectionStrings__Pos = "Host=127.0.0.1;Port=$DatabasePort;Database=kasa;Username=kasa_api;Password=$($env:POS_DB_APP_PASSWORD)"
    $env:RateLimiting__PermitLimit = '2000'
    $env:POS_TEST_URL = $env:ASPNETCORE_URLS
    $env:POS_TEST_OPERATOR_PASSWORD = 'Op9!' + [Guid]::NewGuid().ToString('N').Substring(0, 20)

    & $dotnet build (Join-Path $root 'src\Kasa.Api\Kasa.Api.csproj') -c Release --no-restore -warnaserror
    if ($LASTEXITCODE -ne 0) { throw 'API build failed.' }

    docker compose --file (Join-Path $root 'compose.yaml') --project-name $project up -d --wait db
    if ($LASTEXITCODE -ne 0) { throw 'Disposable PostgreSQL failed to start.' }

    Get-Content -LiteralPath (Join-Path $root 'tests\fixtures.sql') -Raw |
        docker compose --file (Join-Path $root 'compose.yaml') --project-name $project exec -T `
            -e POS_TEST_REGISTER_KEY db psql -X -U kasa_owner -d kasa -v ON_ERROR_STOP=1 *>&1 |
        Set-Content -LiteralPath $logs[0] -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Database fixtures failed.' }
    Get-Content -LiteralPath (Join-Path $root 'tests\operator_auth_setup.sql') -Raw |
        docker compose --file (Join-Path $root 'compose.yaml') --project-name $project exec -T `
            db psql -X -U kasa_owner -d kasa -v ON_ERROR_STOP=1 *>&1 |
        Add-Content -LiteralPath $logs[0] -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Operator fixtures failed.' }
    $env:POS_TEST_DB_CONTAINER = "$project-db-1"

    $api = Start-Process -FilePath $dotnet `
        -ArgumentList @('src\Kasa.Api\bin\Release\net10.0\Kasa.Api.dll') `
        -WorkingDirectory $root -WindowStyle Hidden `
        -RedirectStandardOutput $logs[1] -RedirectStandardError $logs[2] -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri "$($env:POS_TEST_URL)/health/live" -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Disposable API failed to start.' }

    python (Join-Path $root 'tests\operator_auth.py')
    if ($LASTEXITCODE -ne 0) { throw 'Operator integration checks failed.' }

    Get-Content -LiteralPath (Join-Path $root 'tests\operator_auth_security.sql') -Raw |
        docker compose --file (Join-Path $root 'compose.yaml') --project-name $project exec -T `
            db psql -X -U kasa_owner -d kasa -v ON_ERROR_STOP=1 *>&1 |
        Set-Content -LiteralPath $logs[3] -Encoding utf8
    if ($LASTEXITCODE -ne 0) { throw 'Operator security SQL checks failed.' }

    $api.Kill($true)
    $api.WaitForExit()
    $api = $null
    $env:Auth__RequireOperatorSession = 'false'
    $api = Start-Process -FilePath $dotnet `
        -ArgumentList @('src\Kasa.Api\bin\Release\net10.0\Kasa.Api.dll') `
        -WorkingDirectory $root -WindowStyle Hidden `
        -RedirectStandardOutput $logs[4] -RedirectStandardError $logs[5] -PassThru
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
        try {
            $response = Invoke-WebRequest -UseBasicParsing -Uri "$($env:POS_TEST_URL)/health/live" -TimeoutSec 2
            if ($response.StatusCode -eq 200) { $ready = $true; break }
        } catch { }
        Start-Sleep -Seconds 1
    }
    if (-not $ready) { throw 'Compatibility API failed to start.' }
    python (Join-Path $root 'tests\operator_auth_compat.py')
    if ($LASTEXITCODE -ne 0) { throw 'Feature-flag compatibility checks failed.' }

    if (Select-String -LiteralPath $logs[1], $logs[2], $logs[4], $logs[5] -SimpleMatch -Quiet `
            -Pattern $env:POS_TEST_OPERATOR_PASSWORD) {
        throw 'Operator password leaked into application logs.'
    }
    Write-Output 'PASS: isolated operator API, PostgreSQL and security suite'
}
catch {
    if (Test-Path -LiteralPath $logs[1]) { Get-Content -LiteralPath $logs[1] -Tail 100 }
    if (Test-Path -LiteralPath $logs[2]) { Get-Content -LiteralPath $logs[2] -Tail 100 }
    throw
}
finally {
    if ($api -and -not $api.HasExited) {
        $api.Kill($true)
        $api.WaitForExit()
    }
    docker compose --file (Join-Path $root 'compose.yaml') --project-name $project down --volumes *> $null
    foreach ($log in $logs) {
        if (Test-Path -LiteralPath $log) { Remove-Item -LiteralPath $log -Force }
    }
}
