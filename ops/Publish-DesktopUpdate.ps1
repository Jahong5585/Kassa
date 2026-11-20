[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Installer,
    [Parameter(Mandatory)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version,
    [Parameter(Mandatory)][ValidatePattern('^[0-9a-fA-F]{40}$')][string]$SignerThumbprint,
    [Parameter(Mandatory)][string]$RepositoryRoot,
    [ValidateLength(0,4000)][string]$ReleaseNotes = ''
)
$ErrorActionPreference = 'Stop'
$source = (Resolve-Path -LiteralPath $Installer).Path
$signature = Get-AuthenticodeSignature -LiteralPath $source
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Thumbprint -ne $SignerThumbprint) {
    throw 'The installer must have a valid Authenticode signature from the configured publisher.'
}
$info = [Diagnostics.FileVersionInfo]::GetVersionInfo($source)
if ($info.ProductName -ne 'Kassa Minimarket' -or
    "$($info.FileMajorPart).$($info.FileMinorPart).$($info.FileBuildPart)" -ne $Version -or $info.FilePrivatePart -ne 0) {
    throw 'Product name or installer version mismatch.'
}
$length = (Get-Item -LiteralPath $source).Length
if ($length -lt 2 -or $length -gt 134217728) { throw 'Invalid installer size.' }
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash
$root = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($RepositoryRoot)
[IO.Directory]::CreateDirectory($root) | Out-Null
$file = "KassaSetup-$Version.exe"
$target = Join-Path $root $file
$temporary = Join-Path $root ([guid]::NewGuid().ToString('N') + '.tmp')
try {
    Copy-Item -LiteralPath $source -Destination $temporary
    if ((Get-FileHash -LiteralPath $temporary -Algorithm SHA256).Hash -ne $hash) { throw 'Copy hash mismatch.' }
    if (Test-Path -LiteralPath $target) {
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $hash) {
            throw 'A different installer already uses this version. Publish a new version.'
        }
        Remove-Item -LiteralPath $temporary
    } else { [IO.File]::Move($temporary, $target) }
    $manifest = [ordered]@{
        schemaVersion = 1; channel = 'stable'; version = $Version; file = $file; sha256 = $hash
        bytes = $length; signerThumbprint = $SignerThumbprint.ToUpperInvariant()
        publishedAtUtc = [DateTime]::UtcNow.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"); releaseNotes = $ReleaseNotes
    } | ConvertTo-Json
    $latest = Join-Path $root 'latest.json'
    if (Test-Path -LiteralPath $latest) {
        $previous = Get-Content -LiteralPath $latest -Raw | ConvertFrom-Json
        if ([version]$previous.version -gt [version]$Version) { throw 'Publishing an older version is prohibited.' }
    }
    [IO.File]::WriteAllText($temporary, $manifest, [Text.UTF8Encoding]::new($false))
    if (Test-Path -LiteralPath $latest) { [IO.File]::Replace($temporary, $latest, $null) }
    else { [IO.File]::Move($temporary, $latest) }
    Write-Output "Published $file ($hash)"
} finally {
    if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary }
}
