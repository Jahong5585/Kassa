[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Start','Stop','Restart','Status')][string]$Action)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$settings = Join-Path $root '.env.local-test'
$runtime = Join-Path $root 'artifacts\local-test'
$pidFile = Join-Path $runtime 'api.pid'
$stdout = Join-Path $runtime 'api.stdout.log'
$stderr = Join-Path $runtime 'api.stderr.log'
$apiDll = Join-Path $runtime 'api\Kasa.Api.dll'
$dotnet = 'D:\CodexTools\dotnet\dotnet.exe'

function Import-Settings {
    if (-not (Test-Path -LiteralPath $settings -PathType Leaf)) { throw "Run deploy\Initialize-LocalTest.ps1 first." }
    foreach ($line in [IO.File]::ReadAllLines($settings)) {
        if ([string]::IsNullOrWhiteSpace($line) -or $line.StartsWith('#')) { continue }
        $parts = $line.Split('=', 2)
        if ($parts.Count -ne 2) { throw "Invalid local settings line." }
        [Environment]::SetEnvironmentVariable($parts[0], $parts[1], 'Process')
    }
}

function Get-ApiProcess {
    if (-not (Test-Path -LiteralPath $pidFile)) { return $null }
    $value = [IO.File]::ReadAllText($pidFile).Trim()
    if ($value -notmatch '^\d+$') { return $null }
    Get-Process -Id ([int]$value) -ErrorAction SilentlyContinue
}

function Start-LocalTest {
    Import-Settings
    if (-not (Test-Path -LiteralPath $dotnet -PathType Leaf)) { throw "Missing .NET SDK at $dotnet" }
    New-Item -ItemType Directory -Force -Path $runtime | Out-Null
    & docker compose --project-name kasa-local-test --env-file $settings up -d db --wait
    if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL failed to start.' }
    if (-not (Test-Path -LiteralPath $apiDll -PathType Leaf)) {
        & $dotnet publish (Join-Path $root 'src\Kasa.Api\Kasa.Api.csproj') -c Release --no-restore `
            -o (Join-Path $runtime 'api') -warnaserror
        if ($LASTEXITCODE -ne 0) { throw 'API publish failed.' }
    }
    if (Get-ApiProcess) { return }
    Remove-Item -LiteralPath $stdout,$stderr -Force -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $dotnet -ArgumentList @($apiDll) -WorkingDirectory $runtime `
        -WindowStyle Hidden -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
    [IO.File]::WriteAllText($pidFile, [string]$process.Id)
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        Start-Sleep -Milliseconds 500
        if ($process.HasExited) {
            $details = if (Test-Path -LiteralPath $stderr) { [IO.File]::ReadAllText($stderr) } else { '' }
            throw "API exited during startup. $details"
        }
        try {
            $status = Invoke-RestMethod -Uri 'http://127.0.0.1:3510/development/platform/status' -TimeoutSec 2
            if ($status.enabled -eq $true -and $status.environment -eq 'Development') { return }
        } catch { }
    }
    throw 'API did not become ready on http://127.0.0.1:3510/.'
}

function Stop-LocalTest {
    $process = Get-ApiProcess
    if ($process) { Stop-Process -Id $process.Id -Force; $process.WaitForExit(10000) | Out-Null }
    Remove-Item -LiteralPath $pidFile -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $settings) {
        Import-Settings
        & docker compose --project-name kasa-local-test --env-file $settings stop db
        if ($LASTEXITCODE -ne 0) { throw 'PostgreSQL failed to stop.' }
    }
}

switch ($Action) {
    'Start' { Start-LocalTest }
    'Stop' { Stop-LocalTest }
    'Restart' { Stop-LocalTest; Start-LocalTest }
    'Status' {
        $process = Get-ApiProcess
        $db = docker ps --filter 'label=com.docker.compose.project=kasa-local-test' --filter 'status=running' --format '{{.Names}}'
        [pscustomobject]@{
            Api = if ($process) { "running (PID $($process.Id))" } else { 'stopped' }
            Database = if ($db) { 'running' } else { 'stopped' }
            Address = 'http://127.0.0.1:3510/'
        }
    }
}
