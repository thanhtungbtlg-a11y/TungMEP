param(
    [switch]$SkipCacheClear
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$source = Join-Path $root "pyrevit\TungMEP.extension"
$extensionsRoot = Join-Path $env:APPDATA "pyRevit\Extensions"
$target = Join-Path $extensionsRoot "TungMEP.extension"

if (-not (Test-Path -LiteralPath (Join-Path $source "lib\tungmep_bridge.py"))) {
    throw "TungMEP pyRevit Companion source is incomplete: $source"
}

& (Join-Path $root "generate-pyrevit-icons.ps1")

$resolvedExtensionsRoot = [System.IO.Path]::GetFullPath($extensionsRoot) +
    [System.IO.Path]::DirectorySeparatorChar
$resolvedTarget = [System.IO.Path]::GetFullPath($target)
if (-not $resolvedTarget.StartsWith(
    $resolvedExtensionsRoot,
    [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe pyRevit extension target: $resolvedTarget"
}

New-Item -ItemType Directory -Force -Path $extensionsRoot | Out-Null
if (Test-Path -LiteralPath $target) {
    Remove-Item -LiteralPath $target -Recurse -Force
}
Copy-Item -LiteralPath $source -Destination $target -Recurse -Force

if (-not $SkipCacheClear -and
    -not (Get-Process Revit -ErrorAction SilentlyContinue) -and
    (Get-Command pyrevit -ErrorAction SilentlyContinue)) {
    & pyrevit caches clear 2024 | Out-Host
}
elseif (-not $SkipCacheClear -and (Get-Process Revit -ErrorAction SilentlyContinue)) {
    Write-Host "Revit is running; skipped pyRevit cache deletion. Use pyRevit Reload in Revit instead."
}

Write-Host "Installed TungMEP pyRevit Companion: $target"
Write-Host "In Revit, click pyRevit Reload once or restart Revit."
