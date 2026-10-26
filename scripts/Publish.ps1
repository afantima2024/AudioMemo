[CmdletBinding()]
param(
    [string]$NuGetConfig,
    [string]$OutputDirectory,
    [switch]$NoRestore
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $OutputDirectory) { $OutputDirectory = Join-Path $root 'artifacts\app' }
$OutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
$supportDirectory = Join-Path $OutputDirectory 'support'
$runtimeDirectory = Join-Path $supportDirectory 'Runtime'
$docsDirectory = Join-Path $supportDirectory 'Docs'
$licenseDirectory = Join-Path $supportDirectory 'Licenses'
$executables = @((Join-Path $OutputDirectory 'AudioMemo.exe'),
    (Join-Path $supportDirectory 'AudioMemo.exe'), (Join-Path $runtimeDirectory 'AudioMemo.exe'))
$running = @(Get-Process | Where-Object { $_.Path -in $executables })
if ($running.Count -gt 0) { throw 'Close this AudioMemo release before publishing over it, or choose a different output directory.' }
if (Test-Path -LiteralPath $OutputDirectory) {
    if (@(Get-ChildItem -LiteralPath $OutputDirectory -Force).Count -gt 0) {
        throw 'Choose an empty output directory, for example -OutputDirectory artifacts\app-next. Existing files were not changed.'
    }
}
$project = Join-Path $root 'src\AudioMemo\AudioMemo.csproj'
& (Join-Path $PSScriptRoot 'Generate-Icons.ps1')
if (-not $NoRestore) {
    $restore = @('restore', $project, '--runtime', 'win-x64', '-p:SelfContained=true', '--verbosity', 'minimal')
    if ($NuGetConfig) { $restore += @('--configfile', $NuGetConfig) }
    & dotnet @restore
    if ($LASTEXITCODE -ne 0) { throw 'Dependency restore failed.' }
}
& dotnet publish $project --configuration Release --runtime win-x64 --self-contained true `
    --no-restore -p:PublishSingleFile=false -p:PublishTrimmed=false --output $runtimeDirectory --verbosity minimal
if ($LASTEXITCODE -ne 0) { throw 'Publishing AudioMemo failed.' }
$runtime = Get-Content -LiteralPath (Join-Path $runtimeDirectory 'AudioMemo.runtimeconfig.json') -Raw | ConvertFrom-Json
if (-not $runtime.runtimeOptions.includedFrameworks) { throw 'The output is not self-contained.' }
Move-Item -LiteralPath (Join-Path $runtimeDirectory 'Licenses') -Destination $licenseDirectory
$assets = Get-Content -LiteralPath (Join-Path $root 'src\AudioMemo\obj\project.assets.json') -Raw | ConvertFrom-Json
$packageRoots = @($assets.packageFolders.PSObject.Properties.Name)
foreach ($framework in $runtime.runtimeOptions.includedFrameworks) {
    $relative = ($framework.name.ToLowerInvariant() + '.runtime.win-x64\' + $framework.version)
    $packagePath = $packageRoots | ForEach-Object { Join-Path $_ $relative } |
        Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
    if (-not $packagePath) { throw "Could not locate runtime notices for $($framework.name)." }
    $notices = @(Get-ChildItem -LiteralPath $packagePath -File |
        Where-Object { $_.Name -match '^(LICENSE|THIRD.*NOTICES)' })
    if ($notices.Count -eq 0) { throw "No license notices found for $($framework.name)." }
    $noticeFolder = Join-Path $licenseDirectory $framework.name
    New-Item -ItemType Directory -Path $noticeFolder -Force | Out-Null
    foreach ($notice in $notices) { Copy-Item -LiteralPath $notice.FullName -Destination $noticeFolder -Force }
}
New-Item -ItemType Directory -Path $docsDirectory | Out-Null
foreach ($file in @('README.md', 'PRIVACY.md', 'THIRD-PARTY-NOTICES.txt')) {
    Copy-Item -LiteralPath (Join-Path $root $file) -Destination $docsDirectory
}
& dotnet msbuild $project -target:CreatePortableHost -property:Configuration=Release -property:SelfContained=true `
    "-property:PortableOutputDirectory=$OutputDirectory" -verbosity:minimal -nologo
if ($LASTEXITCODE -ne 0) { throw 'Creating the portable AudioMemo executable failed.' }
Write-Host "AudioMemo is ready: $(Join-Path $OutputDirectory 'AudioMemo.exe')"
