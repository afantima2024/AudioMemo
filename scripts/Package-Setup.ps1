#requires -Version 7.0
[CmdletBinding()]
param(
    [string]$PublishedDirectory,
    [string]$OutputDirectory,
    [string]$CompilerPath
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
if (-not $PublishedDirectory) { $PublishedDirectory = Join-Path $root 'artifacts\app' }
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\packages' }
$published = (Resolve-Path -LiteralPath $PublishedDirectory).Path.TrimEnd('\')
$output = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory).TrimEnd('\')
if ($output -eq $published -or $output.StartsWith($published + '\', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The installer output must be outside the published application folder.'
}

if (-not $CompilerPath) {
    $command = Get-Command makensis.exe -ErrorAction SilentlyContinue
    $candidates = @(
        $(if ($command) { $command.Source }),
        "${env:ProgramFiles(x86)}\NSIS\makensis.exe",
        "$env:ProgramFiles\NSIS\makensis.exe"
    )
    $CompilerPath = $candidates | Where-Object { $_ -and (Test-Path -LiteralPath $_ -PathType Leaf) } | Select-Object -First 1
}
if (-not $CompilerPath -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw 'NSIS 3.13 or later is required. Use the official ZIP from https://nsis.sourceforge.io/Download and pass -CompilerPath to its makensis.exe; no compiler installation is required.'
}
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
$compilerVersion = (& $compiler /VERSION | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $compilerVersion -notmatch '^v(\d+\.\d+(?:\.\d+)?)$' -or
    [version]$Matches[1] -lt [version]'3.13') {
    throw "Unsupported NSIS compiler: $compilerVersion. Use NSIS 3.13 or later."
}
$compilerLicense = Join-Path (Split-Path -Parent $compiler) 'COPYING'
if (-not (Test-Path -LiteralPath $compilerLicense -PathType Leaf)) { throw 'The NSIS distribution is missing its COPYING notices.' }

$entries = @(Get-Item -LiteralPath $published; Get-ChildItem -LiteralPath $published -Recurse -Force)
if (@($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
    throw 'Linked files or directories cannot be packaged.'
}
$top = @(Get-ChildItem -LiteralPath $published -Force)
$support = Join-Path $published 'support'
if ($top.Count -ne 2 -or -not (Test-Path -LiteralPath (Join-Path $published 'AudioMemo.exe') -PathType Leaf) -or
    -not (Test-Path -LiteralPath $support -PathType Container)) {
    throw 'Expected a portable release containing only AudioMemo.exe and support.'
}
$groups = @(Get-ChildItem -LiteralPath $support -Force)
if ($groups.Count -ne 3 -or @($groups | Where-Object { -not $_.PSIsContainer -or $_.Name -notin @('Runtime', 'Docs', 'Licenses') }).Count) {
    throw 'Expected support\Runtime, support\Docs, and support\Licenses only.'
}
$assembly = Join-Path $support 'Runtime\AudioMemo.dll'
$versionInfo = [Diagnostics.FileVersionInfo]::GetVersionInfo($assembly)
$version = [version]$versionInfo.FileVersion
$displayVersion = $version.ToString(3)
$outputName = "AudioMemo-$displayVersion-Setup-x64.exe"
$package = Join-Path $output $outputName
$checksum = $package + '.sha256'
$manifestPath = [IO.Path]::ChangeExtension($package, '.manifest.json')
foreach ($path in @($package, $checksum, $manifestPath)) {
    if (Test-Path -LiteralPath $path) { throw "Output already exists and will not be overwritten: $path" }
}
$stage = Join-Path $root ('artifacts\setup-stage-' + [guid]::NewGuid().ToString('N'))
$payload = Join-Path $stage 'payload'
New-Item -ItemType Directory -Path $payload | Out-Null
try {
    foreach ($directory in $entries | Where-Object { $_.PSIsContainer -and $_.FullName -ne $published }) {
        [IO.Directory]::CreateDirectory((Join-Path $payload ([IO.Path]::GetRelativePath($published, $directory.FullName)))) | Out-Null
    }
    foreach ($file in $entries | Where-Object { -not $_.PSIsContainer }) {
        $target = Join-Path $payload ([IO.Path]::GetRelativePath($published, $file.FullName))
        $before = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
        Copy-Item -LiteralPath $file.FullName -Destination $target
        if ((Get-FileHash -LiteralPath $target -Algorithm SHA256).Hash -ne $before -or
            (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash -ne $before) {
            throw "Published input changed while copying: $($file.FullName)"
        }
    }
    foreach ($document in @('README.md', 'PRIVACY.md', 'THIRD-PARTY-NOTICES.txt')) {
        Copy-Item -LiteralPath (Join-Path $root $document) -Destination (Join-Path $payload "support\Docs\$document") -Force
    }
    Copy-Item -LiteralPath (Join-Path $root 'packaging\INSTALLATION.txt') -Destination (Join-Path $payload 'support\Docs\INSTALLATION.txt')
    Copy-Item -LiteralPath $compilerLicense -Destination (Join-Path $payload 'support\Licenses\NSIS.txt')

    $manifest = @(Get-ChildItem -LiteralPath $payload -File -Recurse -Force | Sort-Object FullName | ForEach-Object {
        [pscustomobject]@{
            Path = [IO.Path]::GetRelativePath($payload, $_.FullName)
            Length = $_.Length
            SHA256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    })
    $directories = @(Get-ChildItem -LiteralPath $payload -Directory -Recurse -Force |
        ForEach-Object { [IO.Path]::GetRelativePath($payload, $_.FullName) } | Sort-Object Length -Descending)
    function Escape-Nsis([string]$text) { $text.Replace('$', '$$').Replace('"', '$\"') }
    $include = [Collections.Generic.List[string]]::new()
    $include.Add('!macro InstallPayload')
    foreach ($entry in $manifest) {
        $directory = [IO.Path]::GetDirectoryName($entry.Path)
        $destination = if ($directory) { '$INSTDIR\' + (Escape-Nsis $directory) } else { '$INSTDIR' }
        $include.Add('SetOutPath "' + $destination + '"')
        $include.Add('File "' + (Escape-Nsis (Join-Path $payload $entry.Path)) + '"')
    }
    $include.Add('!macroend')
    $include.Add('!macro RemovePayloadFiles')
    foreach ($entry in $manifest) { $include.Add('Delete "$INSTDIR\' + (Escape-Nsis $entry.Path) + '"') }
    $include.Add('!macroend')
    $include.Add('!macro RemovePayloadDirectories')
    foreach ($directory in $directories) { $include.Add('RMDir "$INSTDIR\' + (Escape-Nsis $directory) + '"') }
    $include.Add('!macroend')
    $includePath = Join-Path $stage 'payload.nsh'
    [IO.File]::WriteAllLines($includePath, $include, [Text.UTF8Encoding]::new($false))
    $builtPackage = Join-Path $stage $outputName
    $sizeKb = [Math]::Ceiling(($manifest | Measure-Object Length -Sum).Sum / 1KB)
    $arguments = @('/NOCONFIG', '/NOCD', '/INPUTCHARSET', 'UTF8', '/OUTPUTCHARSET', 'UTF8', '/WX', '/V3',
        "/DAPP_VERSION=$($version.ToString(4))", "/DDISPLAY_VERSION=$displayVersion",
        "/DAPP_COPYRIGHT=$($versionInfo.LegalCopyright)", "/DOUTPUT_NAME=$outputName",
        "/DOUTPUT_FILE=$builtPackage", "/DPAYLOAD_INCLUDE=$includePath", "/DINSTALL_SIZE_KB=$sizeKb",
        "/DAPP_ICON=$(Join-Path $root 'src\AudioMemo\Assets\AudioMemo.ico')", (Join-Path $root 'packaging\AudioMemo.nsi'))
    & $compiler @arguments
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $builtPackage -PathType Leaf)) {
        throw 'Setup compilation failed; no installer was published.'
    }
    foreach ($entry in $manifest) {
        if ((Get-FileHash -LiteralPath (Join-Path $payload $entry.Path) -Algorithm SHA256).Hash -ne $entry.SHA256) {
            throw "Payload changed during compilation: $($entry.Path)"
        }
    }
    $hash = (Get-FileHash -LiteralPath $builtPackage -Algorithm SHA256).Hash
    $report = [ordered]@{
        Product = 'AudioMemo'
        Version = $displayVersion
        Architecture = 'x64'
        Platform = 'Windows 11'
        Installer = $outputName
        InstallerSHA256 = $hash
        Signed = $false
        Compiler = "NSIS $compilerVersion"
        CompilerSHA256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
        CreatedAt = [DateTimeOffset]::UtcNow.ToString('O')
        Payload = $manifest
    }
    $builtChecksum = Join-Path $stage ([IO.Path]::GetFileName($checksum))
    $builtManifest = Join-Path $stage ([IO.Path]::GetFileName($manifestPath))
    [IO.File]::WriteAllText($builtChecksum, "$hash *$outputName`r`n", [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText($builtManifest, ($report | ConvertTo-Json -Depth 5), [Text.UTF8Encoding]::new($false))
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    [IO.File]::Move($builtPackage, $package, $false)
    [IO.File]::Move($builtChecksum, $checksum, $false)
    [IO.File]::Move($builtManifest, $manifestPath, $false)
    Write-Warning 'This Setup.exe is unsigned. No certificates, trust settings, shortcuts, or installed applications were changed by packaging.'
    [pscustomobject]@{ Installer = $package; SizeMB = [Math]::Round((Get-Item -LiteralPath $package).Length / 1MB, 1); SHA256 = $hash; Manifest = $manifestPath; PayloadFiles = $manifest.Count }
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}
