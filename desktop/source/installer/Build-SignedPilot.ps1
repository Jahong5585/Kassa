[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Payload,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$CertificateThumbprint,
    [Parameter(Mandatory)][uri]$TimestampUrl,
    [Parameter(Mandatory)][string]$Output,
    [string]$SignTool = 'signtool.exe',
    [string]$MakeNsis = 'makensis.exe'
)

$ErrorActionPreference = 'Stop'
if ($TimestampUrl.Scheme -ne 'https') { throw 'The timestamp service must use HTTPS.' }
$payloadPath = (Resolve-Path -LiteralPath $Payload).Path
$program = Join-Path $payloadPath 'KasaDesktop.exe'
if (-not (Test-Path -LiteralPath $program -PathType Leaf)) { throw 'Missing application executable.' }
$setupPath = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Output)
if (Test-Path -LiteralPath $setupPath) { throw 'Choose an output path for a new installer.' }
$installerSource = Join-Path $PSScriptRoot 'Kassa.nsi'

function Invoke-Checked {
    param([string]$Tool, [string[]]$ToolArgs)
    & $Tool @ToolArgs
    if ($LASTEXITCODE -ne 0) { throw "$Tool exited with code $LASTEXITCODE." }
}

function Sign-PilotFile {
    param([string]$File)
    Invoke-Checked -Tool $SignTool -ToolArgs @('sign', '/sha1', $CertificateThumbprint,
        '/fd', 'SHA256', '/tr', $TimestampUrl.AbsoluteUri, '/td', 'SHA256', $File)
    Invoke-Checked -Tool $SignTool -ToolArgs @('verify', '/pa', '/v', $File)
}

Sign-PilotFile $program
Invoke-Checked -Tool $MakeNsis -ToolArgs @("/DPAYLOAD=$payloadPath", "/DOUTPUT=$setupPath", $installerSource)
Sign-PilotFile $setupPath
Get-FileHash -LiteralPath $program, $setupPath -Algorithm SHA256
