[CmdletBinding()]
param(
    [ValidateSet('start','stop','restart','status','logs','pull','backup')]
    [string]$Action = 'status',
    [string]$SecretsFile = 'D:\KassaSecrets\.env'
)

$ErrorActionPreference = 'Stop'
$deploymentDirectory = Split-Path -Parent $PSCommandPath
$projectDirectory = Split-Path -Parent $deploymentDirectory
$composeFile = Join-Path $deploymentDirectory 'compose.production.yaml'
$arguments = @('compose', '--env-file', $SecretsFile, '--file', $composeFile, '--project-name', 'kasa-production')
$ensureDocker = {
    & docker info *> $null
    if ($LASTEXITCODE -eq 0) { return }

    $dockerDesktop = 'D:\Docker\Desktop\Docker Desktop.exe'
    if (-not (Test-Path -LiteralPath $dockerDesktop)) {
        throw "Docker daemon is unavailable and Docker Desktop was not found at $dockerDesktop."
    }

    Start-Process -FilePath $dockerDesktop -WindowStyle Hidden
    $deadline = (Get-Date).AddMinutes(5)
    do {
        Start-Sleep -Seconds 5
        & docker info *> $null
        if ($LASTEXITCODE -eq 0) { return }
    } while ((Get-Date) -lt $deadline)
    throw 'Docker Desktop did not become ready within five minutes.'
}
$publish = {
    $dotnet = 'D:\CodexTools\dotnet\dotnet.exe'
    $output = Join-Path $projectDirectory '.deploy\publish'
    & $dotnet publish (Join-Path $projectDirectory 'src\Kasa.Api\Kasa.Api.csproj') -c Release --nologo `
        --no-restore -o $output /p:UseAppHost=false
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }
}
switch ($Action) {
    'start' { & $ensureDocker; & $publish; & docker @arguments up -d --build }
    'stop' { & $ensureDocker; & docker @arguments down }
    'restart' { & $ensureDocker; & $publish; & docker @arguments up -d --build --force-recreate }
    'status' { & $ensureDocker; & docker @arguments ps }
    'logs' { & $ensureDocker; & docker @arguments logs --tail 200 }
    'pull' { & $ensureDocker; & docker @arguments pull }
    'backup' {
        & $ensureDocker
        $backupDirectory = 'D:\KassaBackups'
        New-Item -ItemType Directory -Force -Path $backupDirectory | Out-Null
        $target = Join-Path $backupDirectory (Get-Date -Format 'yyyyMMdd-HHmmss')
        New-Item -ItemType Directory -Path $target | Out-Null
        & docker @arguments exec -T db pg_dump -U kasa_owner -d kasa --format=custom --file=/tmp/kasa.dump
        & docker cp 'kasa-production-db-1:/tmp/kasa.dump' (Join-Path $target 'kasa.dump')
        & docker @arguments exec -T db rm -f /tmp/kasa.dump
        Write-Output "Backup created: $target\kasa.dump"
    }
}
if ($LASTEXITCODE -ne 0) { throw "Docker Compose action '$Action' failed with exit code $LASTEXITCODE." }
