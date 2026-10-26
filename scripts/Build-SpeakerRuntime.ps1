[CmdletBinding()]
param(
    [string]$WorkDirectory,
    [ValidateRange(1, 16)][int]$Parallel = 4
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
if (-not $WorkDirectory) { $WorkDirectory = Join-Path $root 'artifacts\native-speaker-build' }
$WorkDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($WorkDirectory)
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) { throw 'Install Visual Studio with C++ tools and CMake first.' }
$vs = & $vswhere -latest -products '*' -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio x64 C++ build tools were not found.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'
foreach ($tool in @($vcvars, $cmake, $ninja)) {
    if (-not (Test-Path -LiteralPath $tool)) { throw "Required build tool not found: $tool" }
}
New-Item -ItemType Directory -Path $WorkDirectory -Force | Out-Null
$archive = Join-Path $WorkDirectory 'sherpa-onnx-1.13.8.tar.gz'
$sourceHash = 'B0374CC56DBC186D442AE73D5DE743BB092470B640C4C50CE7B029044C0C4FA8'
if (-not (Test-Path -LiteralPath $archive)) {
    & curl.exe --fail --location --silent --show-error `
        'https://github.com/k2-fsa/sherpa-onnx/archive/refs/tags/v1.13.8.tar.gz' --output $archive
    if ($LASTEXITCODE -ne 0) { throw 'Speaker-runtime source download failed.' }
}
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $sourceHash) {
    throw 'Speaker-runtime source checksum mismatch. No build was started.'
}
$source = Join-Path $WorkDirectory 'sherpa-onnx-1.13.8'
if (-not (Test-Path -LiteralPath (Join-Path $source 'CMakeLists.txt'))) {
    & tar.exe -xzf $archive -C $WorkDirectory
    if ($LASTEXITCODE -ne 0) { throw 'Source extraction failed.' }
}
$build = Join-Path $WorkDirectory 'build'
$configure = '"{0}" -S "{1}" -B "{2}" -G Ninja -DCMAKE_MAKE_PROGRAM="{3}" -DCMAKE_VS_PLATFORM_NAME=x64 -DCMAKE_BUILD_TYPE=Release -DBUILD_SHARED_LIBS=ON -DSHERPA_ONNX_ENABLE_TTS=OFF -DSHERPA_ONNX_ENABLE_SPEAKER_DIARIZATION=ON -DSHERPA_ONNX_ENABLE_BINARY=OFF -DSHERPA_ONNX_ENABLE_WEBSOCKET=OFF -DSHERPA_ONNX_ENABLE_PORTAUDIO=OFF -DSHERPA_ONNX_BUILD_C_API_EXAMPLES=OFF -DSHERPA_ONNX_ENABLE_PYTHON=OFF -DSHERPA_ONNX_ENABLE_TESTS=OFF' -f $cmake,$source,$build,$ninja
$command = 'call "{0}" >nul && {1} && "{2}" --build "{3}" --target sherpa-onnx-c-api --parallel {4}' -f $vcvars,$configure,$cmake,$build,$Parallel
$log = Join-Path $WorkDirectory 'native-build.log'
& $env:ComSpec /d /s /c $command *> $log
if ($LASTEXITCODE -ne 0) {
    Get-Content -LiteralPath $log -Tail 60
    throw "Speaker-runtime build failed. See $log"
}
$destination = Join-Path $root 'src\AudioMemo\Native\win-x64'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
$library = Join-Path $destination 'sherpa-onnx-c-api.dll'
Copy-Item -LiteralPath (Join-Path $build 'bin\sherpa-onnx-c-api.dll') -Destination $library -Force
Write-Host "Built no-TTS speaker runtime: $library"
Write-Host ('SHA-256: ' + (Get-FileHash -LiteralPath $library -Algorithm SHA256).Hash)
Write-Host 'Retain the dependency license notices, then rerun speaker and portable checks before publishing.'
