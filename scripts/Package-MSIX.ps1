[CmdletBinding()]
param(
    [string]$IdentityName = 'AudioMemo.Development',
    [string]$Publisher = 'CN=AudioMemo.Development',
    [string]$PublisherDisplayName = 'AudioMemo',
    [ValidatePattern('^\d+\.\d+\.\d+\.\d+$')]
    [string]$Version = '0.1.0.0',
    [string]$CertificateThumbprint,
    [string]$PublishedDirectory
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $PublishedDirectory) { $PublishedDirectory = Join-Path $root 'artifacts\app' }
if (-not (Test-Path -LiteralPath (Join-Path $PublishedDirectory 'AudioMemo.exe'))) {
    throw 'Run scripts\Publish.ps1 first.'
}
$sdkRoot = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
$sdk = Get-ChildItem -LiteralPath $sdkRoot -Directory |
    Where-Object { $_.Name -match '^\d+\.\d+\.\d+\.\d+$' -and (Test-Path -LiteralPath (Join-Path $_.FullName 'x64\makeappx.exe')) } |
    Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
if (-not $sdk) { throw 'Install the Windows 11 SDK (MakeAppx and SignTool) to build an MSIX.' }
$stage = Join-Path $root ('artifacts\msix-stage-' + [guid]::NewGuid().ToString('N'))
$packages = Join-Path $root 'artifacts\packages'
New-Item -ItemType Directory -Path $stage -Force | Out-Null
New-Item -ItemType Directory -Path $packages -Force | Out-Null
try {
    foreach ($entry in Get-ChildItem -LiteralPath $PublishedDirectory) {
        Copy-Item -LiteralPath $entry.FullName -Destination $stage -Recurse
    }
    $assets = Join-Path $stage 'Assets'
    New-Item -ItemType Directory -Force -Path $assets | Out-Null
    foreach ($logo in @('Square44x44Logo.png', 'Square150x150Logo.png', 'StoreLogo.png')) {
        Copy-Item -LiteralPath (Join-Path $root "src\AudioMemo\Assets\$logo") -Destination $assets
    }
    [xml]$manifest = Get-Content -LiteralPath (Join-Path $root 'packaging\AppxManifest.xml') -Raw
    $manifest.Package.Identity.Name = $IdentityName
    $manifest.Package.Identity.Publisher = $Publisher
    $manifest.Package.Identity.Version = $Version
    $manifest.Package.Properties.PublisherDisplayName = $PublisherDisplayName
    $manifest.Save((Join-Path $stage 'AppxManifest.xml'))
    $package = Join-Path $packages "AudioMemo-$Version-x64.msix"
    $packOutput = & (Join-Path $sdk.FullName 'x64\makeappx.exe') pack /d $stage /p $package /o 2>&1
    if ($LASTEXITCODE -ne 0) { throw ("MSIX creation or manifest validation failed.`n" + ($packOutput -join "`n")) }
    $packOutput | Select-Object -Last 4 | Write-Host
    if ($CertificateThumbprint) {
        & (Join-Path $sdk.FullName 'x64\signtool.exe') sign /sha1 $CertificateThumbprint /fd SHA256 $package
        if ($LASTEXITCODE -ne 0) { throw 'MSIX signing failed. The certificate subject must match Publisher.' }
    }
    else {
        Write-Warning 'MSIX is unsigned. Partner Center signs Store submissions. Local sideloading requires a trusted matching certificate.'
    }
    Write-Host "Created $package"
}
finally {
    # Only remove the uniquely created staging directory, never the publish output.
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
