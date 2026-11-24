param([switch]$Clean)

$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot -Parent
$srcDir = Join-Path $root 'src\RemoteDesk.StreamCore'
$buildDir = Join-Path $srcDir 'build'
$outNativeDir = Join-Path $root 'src\RemoteDesk\Native'

$vsBase = "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools"
if (-not (Test-Path $vsBase)) {
    $vsBase = "C:\Program Files\Microsoft Visual Studio\2022\BuildTools"
}
$vcvars = Join-Path $vsBase "VC\Auxiliary\Build\vcvars64.bat"
$cmakeExe = Join-Path $vsBase "Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe"
$ninjaExe = Join-Path $vsBase "Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe"

if (-not (Test-Path $vcvars)) { throw "vcvars64.bat not found under $vsBase" }
if (-not (Test-Path $cmakeExe)) { throw "cmake.exe not found under $vsBase" }
if (-not (Test-Path $ninjaExe)) { throw "ninja.exe not found under $vsBase" }

$cmakeDir = Split-Path $cmakeExe -Parent
$ninjaDir = Split-Path $ninjaExe -Parent
$needConfigure = $Clean -or -not (Test-Path (Join-Path $buildDir 'build.ninja'))
if ($Clean -and (Test-Path $buildDir)) {
    Remove-Item -LiteralPath $buildDir -Recurse -Force
}
New-Item -ItemType Directory -Path $buildDir -Force | Out-Null
New-Item -ItemType Directory -Path $outNativeDir -Force | Out-Null

$configureCmd = if ($needConfigure) {
    "cmake -S `"$srcDir`" -B `"$buildDir`" -G Ninja -DCMAKE_BUILD_TYPE=Release -DCMAKE_POLICY_DEFAULT_CMP0141=NEW -DCMAKE_MSVC_DEBUG_INFORMATION_FORMAT=Embedded -DCMAKE_TRY_COMPILE_CONFIGURATION=Release`r`nif errorlevel 1 exit /b 1"
} else {
    ""
}

$cmdScript = @"
@echo off
call "$vcvars" >nul 2>&1
if errorlevel 1 exit /b 1
set "PATH=$cmakeDir;$ninjaDir;%PATH%"
$configureCmd
cmake --build "$buildDir" --target RemoteDesk.StreamCore --config Release
if errorlevel 1 exit /b 1
dumpbin /exports "$buildDir\RemoteDesk.StreamCore.dll"
"@

$tmpBat = Join-Path $buildDir 'build_invoke.bat'
Set-Content -LiteralPath $tmpBat -Value $cmdScript -Encoding Ascii
cmd.exe /c "`"$tmpBat`""
if ($LASTEXITCODE -ne 0) {
    throw "Native build failed with exit code $LASTEXITCODE"
}

$builtDll = Join-Path $buildDir 'RemoteDesk.StreamCore.dll'
if (-not (Test-Path $builtDll)) {
    throw "Expected built DLL missing: $builtDll"
}

Copy-Item -LiteralPath $builtDll -Destination (Join-Path $outNativeDir 'RemoteDesk.StreamCore.dll') -Force
Write-Host "Copied RemoteDesk.StreamCore.dll to $outNativeDir"
