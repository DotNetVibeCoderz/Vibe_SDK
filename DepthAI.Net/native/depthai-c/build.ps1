<#
.SYNOPSIS
    Membangun depthai-c.dll dan menyiapkan semua DLL runtime win-x64 di artifacts/native/win-x64.

.DESCRIPTION
    Tidak membangun depthai-core dari sumber: memakai rilis prebuilt Luxonis
    (depthai-core-v<versi>-win64.zip), lalu mengompilasi shim C di atasnya dengan MSVC.

    DLL prebuilt itu mengimpor opencv_world<versi>.dll yang tidak ikut di zip-nya, jadi
    DLL tersebut diambil dari rilis resmi OpenCV untuk Windows. Skrip memeriksa tabel
    impor depthai-core.dll supaya versi OpenCV yang diunduh pasti cocok.

    Runtime MSVC (msvcp140 dan kawan-kawan) disalin app-local dari folder Redist Visual
    Studio, agar paket berjalan di mesin tanpa VC++ Redistributable.

    Butuh: Visual Studio 2022+ dengan workload C++ (CMake dan Ninja bawaannya dipakai),
    serta 7-Zip untuk membuka installer OpenCV. Keduanya tersedia di runner windows-latest.

.EXAMPLE
    pwsh native/depthai-c/build.ps1
#>
[CmdletBinding()]
param(
    [string]$DepthAiVersion = '3.11.0',
    [string]$OpenCvVersion = '4.13.0'
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # progress bar Invoke-WebRequest memperlambat unduhan berkali lipat

$shimDir = $PSScriptRoot
$repoRoot = (Resolve-Path (Join-Path $shimDir '..\..')).Path
$depsDir = Join-Path $shimDir '.deps'
$buildDir = Join-Path $shimDir '.build'
$stageDir = Join-Path $repoRoot 'artifacts\native\win-x64'
New-Item -ItemType Directory -Force $depsDir, $stageDir | Out-Null

function Get-File([string]$url, [string]$path) {
    if (Test-Path $path) { return }
    Write-Host "Mengunduh $url"
    Invoke-WebRequest -Uri $url -OutFile "$path.partial" -UseBasicParsing
    Move-Item "$path.partial" $path
}

# --- Visual Studio, CMake, Ninja --------------------------------------------------------
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) { throw 'Visual Studio dengan workload C++ tidak ditemukan.' }
$vcvars = Join-Path $vs 'VC\Auxiliary\Build\vcvars64.bat'
$cmake = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'
$ninja = Join-Path $vs 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'

# --- depthai-core prebuilt ---------------------------------------------------------------
$coreName = "depthai-core-v$DepthAiVersion-win64"
$coreDir = Join-Path $depsDir $coreName
if (-not (Test-Path (Join-Path $coreDir 'bin\depthai-core.dll'))) {
    $zip = Join-Path $depsDir "$coreName.zip"
    Get-File "https://github.com/luxonis/depthai-core/releases/download/v$DepthAiVersion/$coreName.zip" $zip

    # Berkas SBOM di dalam zip punya path sangat panjang dan tidak dibutuhkan; dilewati
    # supaya ekstraksi tidak gagal oleh batas MAX_PATH Windows.
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try {
        foreach ($entry in $archive.Entries) {
            if ($entry.FullName -like '*/sbom/*' -or $entry.Name -eq '') { continue }
            $target = Join-Path $depsDir $entry.FullName
            New-Item -ItemType Directory -Force (Split-Path $target) | Out-Null
            [System.IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    }
    finally {
        $archive.Dispose()
    }
}

# --- OpenCV world DLL -------------------------------------------------------------------
$openCvTag = $OpenCvVersion -replace '\.', ''
$openCvDll = Join-Path $depsDir "opencv_world$openCvTag.dll"
if (-not (Test-Path $openCvDll)) {
    $installer = Join-Path $depsDir "opencv-$OpenCvVersion-windows.exe"
    Get-File "https://github.com/opencv/opencv/releases/download/$OpenCvVersion/opencv-$OpenCvVersion-windows.exe" $installer

    $sevenZip = (Get-Command 7z -ErrorAction SilentlyContinue).Source
    if (-not $sevenZip) { $sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe' }
    if (-not (Test-Path $sevenZip)) { throw '7-Zip dibutuhkan untuk membuka installer OpenCV.' }
    & $sevenZip e -y "-o$depsDir" $installer "opencv\build\x64\vc16\bin\opencv_world$openCvTag.dll" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Gagal mengekstrak opencv_world$openCvTag.dll." }
}

# --- Build shim -------------------------------------------------------------------------
# vcvars64.bat hanya berlaku di dalam proses cmd, jadi konfigurasi, build, dan pemeriksaan
# impor dijalankan bersama di satu skrip cmd.
$imports = Join-Path $buildDir 'depthai-core-imports.txt'
$script = Join-Path $depsDir 'build.cmd'
@"
@echo off
set "PATH=$(Split-Path $vswhere);%PATH%"
call "$vcvars" >nul || exit /b 1
"$cmake" -S "$shimDir" -B "$buildDir" -G Ninja -DCMAKE_BUILD_TYPE=Release "-DCMAKE_MAKE_PROGRAM=$ninja" "-DDEPTHAI_CORE_DIR=$coreDir" || exit /b 1
"$cmake" --build "$buildDir" || exit /b 1
dumpbin /dependents "$coreDir\bin\depthai-core.dll" > "$imports" || exit /b 1
"@ | Set-Content -Encoding ascii $script

& cmd.exe /c $script
if ($LASTEXITCODE -ne 0) { throw 'Build depthai-c gagal.' }

if (-not (Select-String -Path $imports -Pattern "opencv_world$openCvTag.dll" -Quiet)) {
    $actual = Select-String -Path $imports -Pattern 'opencv_world\d+\.dll' | ForEach-Object { $_.Matches.Value } | Select-Object -First 1
    throw "depthai-core $DepthAiVersion mengimpor $actual, bukan opencv_world$openCvTag.dll. Sesuaikan -OpenCvVersion."
}

# --- Kumpulkan runtime ------------------------------------------------------------------
Get-ChildItem $stageDir -Filter *.dll | Remove-Item -Force
Copy-Item (Join-Path $buildDir 'depthai-c.dll') $stageDir
foreach ($dll in 'depthai-core.dll', 'dynamic_calibration.dll', 'libusb-1.0.dll') {
    Copy-Item (Join-Path $coreDir "bin\$dll") $stageDir
}
Copy-Item $openCvDll $stageDir

$crt = Get-ChildItem (Join-Path $vs 'VC\Redist\MSVC') -Directory |
    Where-Object { $_.Name -match '^\d+\.' } |
    Sort-Object { [version]$_.Name } -Descending |
    ForEach-Object { Get-ChildItem (Join-Path $_.FullName 'x64') -Directory -Filter 'Microsoft.VC*.CRT' -ErrorAction SilentlyContinue } |
    Select-Object -First 1
if (-not $crt) { throw 'Folder Redist MSVC x64 tidak ditemukan di instalasi Visual Studio.' }
foreach ($dll in 'msvcp140.dll', 'msvcp140_2.dll', 'msvcp140_atomic_wait.dll', 'vcruntime140.dll', 'vcruntime140_1.dll', 'concrt140.dll') {
    Copy-Item (Join-Path $crt.FullName $dll) $stageDir
}

Write-Host "Runtime win-x64 siap di $stageDir"
Get-ChildItem $stageDir -Filter *.dll | ForEach-Object { '{0,-28} {1,10:N0} KB' -f $_.Name, ($_.Length / 1KB) }
