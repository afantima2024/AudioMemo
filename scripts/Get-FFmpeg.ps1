[CmdletBinding()]
param([string]$CacheDirectory)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$manifestPath = Join-Path $root 'dependencies\ffmpeg.json'
$dependency = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (-not $CacheDirectory) { $CacheDirectory = Join-Path $root 'artifacts\ffmpeg-dependency' }
$CacheDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($CacheDirectory)
New-Item -ItemType Directory -Path $CacheDirectory -Force | Out-Null

function Get-VerifiedArchive([string]$name, [string]$url, [string]$hash) {
    $path = Join-Path $CacheDirectory $name
    if (-not (Test-Path -LiteralPath $path)) {
        $download = $path + '.download'
        & curl.exe --fail --location --silent --show-error --retry 2 $url --output $download
        if ($LASTEXITCODE -ne 0) { throw "Dependency download failed: $name." }
        if ((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash -ne $hash) {
            throw "Dependency checksum mismatch: $name. Nothing was installed or executed."
        }
        Move-Item -LiteralPath $download -Destination $path
    }
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $hash) {
        throw "Cached dependency checksum mismatch: $name."
    }
    return $path
}

function Copy-ArchiveEntry($zip, [string]$name, [string]$destination) {
    $entry = $zip.GetEntry($name)
    if (-not $entry) { throw "Required dependency file is missing: $name." }
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination)) | Out-Null
    $input = $entry.Open()
    try {
        $output = [IO.File]::Open($destination, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $input.CopyTo($output) } finally { $output.Dispose() }
    }
    finally { $input.Dispose() }
}

$archive = Get-VerifiedArchive $dependency.archive $dependency.url $dependency.sha256
$sourceArchive = Get-VerifiedArchive 'ffmpeg-source.zip' $dependency.sourceUrl $dependency.sourceSha256
$buildArchive = Get-VerifiedArchive 'ffmpeg-build-source.zip' $dependency.buildSourceUrl $dependency.buildSourceSha256
$runtime = Join-Path $root 'src\AudioMemo\Native\FFmpeg\win-x64'
$licenses = Join-Path $root 'src\AudioMemo\Licenses\FFmpeg'
foreach ($directory in @($runtime, $licenses)) {
    if (Test-Path -LiteralPath $directory) {
        $items = @(Get-Item -LiteralPath $directory; Get-ChildItem -LiteralPath $directory -Recurse -Force)
        if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
            throw "Dependency target contains links: $directory."
        }
    }
}
$zip = [IO.Compression.ZipFile]::OpenRead($archive)
try {
    $prefix = [IO.Path]::GetFileNameWithoutExtension($dependency.archive) + '/'
    foreach ($file in $dependency.files) {
        if ([IO.Path]::GetFileName($file) -ne $file) { throw 'Dependency filenames must not contain paths.' }
        Copy-ArchiveEntry $zip ($prefix + 'bin/' + $file) (Join-Path $runtime $file)
    }
    Copy-ArchiveEntry $zip ($prefix + 'LICENSE.txt') (Join-Path $licenses 'COPYING.LGPLv3.txt')
}
finally { $zip.Dispose() }
$sourceZip = [IO.Compression.ZipFile]::OpenRead($sourceArchive)
try {
    $prefix = 'FFmpeg-' + $dependency.sourceCommit + '/'
    Copy-ArchiveEntry $sourceZip ($prefix + 'COPYING.GPLv3') (Join-Path $licenses 'COPYING.GPLv3.txt')
    Copy-ArchiveEntry $sourceZip ($prefix + 'LICENSE.md') (Join-Path $licenses 'Upstream-LICENSE.md')
}
finally { $sourceZip.Dispose() }
$sources = Join-Path $licenses 'Sources'
New-Item -ItemType Directory -Path $sources -Force | Out-Null
Copy-Item -LiteralPath $sourceArchive -Destination (Join-Path $sources 'ffmpeg-source.zip') -Force
Copy-Item -LiteralPath $buildArchive -Destination (Join-Path $sources 'ffmpeg-build-source.zip') -Force
Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $licenses 'dependency.json') -Force
$version = & (Join-Path $runtime 'ffmpeg.exe') -version 2>&1
if ($LASTEXITCODE -ne 0 -or $version[0] -notlike ("ffmpeg version " + $dependency.version + '*')) {
    throw 'The downloaded FFmpeg version did not match the pinned dependency.'
}
$configuration = $version -join "`n"
if ($configuration -match '--enable-(gpl|nonfree)' -or $configuration -notmatch '--enable-shared') {
    throw 'The dependency is not the expected redistributable LGPL shared build.'
}
$configuration | Set-Content -LiteralPath (Join-Path $licenses 'build-configuration.txt') -Encoding utf8
$files = @($dependency.files | ForEach-Object {
    [ordered]@{ Name = $_; SHA256 = (Get-FileHash -LiteralPath (Join-Path $runtime $_) -Algorithm SHA256).Hash }
})
[ordered]@{ Version = $dependency.version; ArchiveSHA256 = $dependency.sha256; Files = $files } |
    ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $runtime 'manifest.json') -Encoding utf8
Write-Host ("Prepared pinned LGPL media tools: " + $dependency.version)
Write-Host "Runtime: $runtime"
Write-Host "Licenses, FFmpeg source, and dependency build recipes: $licenses"
