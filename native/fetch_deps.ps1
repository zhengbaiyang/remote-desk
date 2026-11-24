param(
    [string]$MirrorPrefix = ""
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$thirdParty = Join-Path $root 'third_party'
New-Item -ItemType Directory -Path $thirdParty -Force | Out-Null

function Format-GitUrl([string]$url) {
    if ([string]::IsNullOrWhiteSpace($MirrorPrefix)) {
        return $url
    }
    $prefix = $MirrorPrefix.TrimEnd('/')
    return "$prefix/$url"
}

$mlDir = Join-Path $thirdParty 'moonlight-common-c'
if (-not (Test-Path (Join-Path $mlDir 'CMakeLists.txt'))) {
    if (Test-Path $mlDir) { Remove-Item -Recurse -Force $mlDir }
    $mlUrl = Format-GitUrl 'https://github.com/moonlight-stream/moonlight-common-c.git'
    git clone --recursive --depth 1 $mlUrl $mlDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone moonlight-common-c" }
}

$opusDir = Join-Path $thirdParty 'opus'
if (-not (Test-Path (Join-Path $opusDir 'CMakeLists.txt'))) {
    if (Test-Path $opusDir) { Remove-Item -Recurse -Force $opusDir }
    $opusUrl = Format-GitUrl 'https://github.com/xiph/opus.git'
    git clone --branch v1.5.2 --depth 1 $opusUrl $opusDir
    if ($LASTEXITCODE -ne 0) { throw "Failed to clone opus" }
}

Write-Host "Native dependencies ready under $thirdParty"
