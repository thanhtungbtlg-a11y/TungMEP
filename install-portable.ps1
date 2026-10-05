$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$revitDirectory = "C:\Program Files\Autodesk\Revit 2024"
$installRoot = Join-Path $env:LOCALAPPDATA "TungMEP\Revit2024"
$addinsDirectory = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2024"
$manifestPath = Join-Path $addinsDirectory "TungMEP.addin"

if (Get-Process Revit -ErrorAction SilentlyContinue) {
    throw "Close Revit before installing TungMEP."
}
if (-not (Test-Path -LiteralPath (Join-Path $revitDirectory "RevitAPI.dll"))) {
    throw "Autodesk Revit 2024 was not found at: $revitDirectory"
}

& (Join-Path $root "build.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "TungMEP build failed with exit code $LASTEXITCODE."
}

$buildRoot = Join-Path $root "bin\net48"
$payloadDirectory = Join-Path $buildRoot "payload"
$payloadFileName = (Get-Content -Raw (Join-Path $payloadDirectory "current.txt")).Trim()
$payloadFile = Join-Path $payloadDirectory $payloadFileName
if (-not (Test-Path -LiteralPath $payloadFile)) {
    throw "Built payload was not found: $payloadFile"
}

New-Item -ItemType Directory -Force -Path $installRoot, (Join-Path $installRoot "payload"), $addinsDirectory | Out-Null
Copy-Item -LiteralPath (Join-Path $buildRoot "TungMEP.Loader.dll") -Destination (Join-Path $installRoot "TungMEP.Loader.dll") -Force
Copy-Item -LiteralPath $payloadFile -Destination (Join-Path $installRoot "payload\$payloadFileName") -Force
[System.IO.File]::WriteAllText((Join-Path $installRoot "payload\current.txt"), $payloadFileName, [System.Text.Encoding]::ASCII)
Copy-Item -LiteralPath (Join-Path $buildRoot "content") -Destination $installRoot -Recurse -Force

$pyRevitSource = Join-Path $root "pyrevit\TungMEP.extension"
$pyRevitRoot = Join-Path $env:APPDATA "pyRevit\Extensions"
$pyRevitTarget = Join-Path $pyRevitRoot "TungMEP.extension"
if ((Test-Path -LiteralPath "C:\Program Files\pyRevit-Master") -or
    (Test-Path -LiteralPath (Join-Path $env:APPDATA "pyRevit"))) {
    New-Item -ItemType Directory -Force -Path $pyRevitRoot | Out-Null
    if (Test-Path -LiteralPath $pyRevitTarget) {
        $expectedRoot = [System.IO.Path]::GetFullPath($pyRevitRoot) +
            [System.IO.Path]::DirectorySeparatorChar
        $resolvedTarget = [System.IO.Path]::GetFullPath($pyRevitTarget)
        if (-not $resolvedTarget.StartsWith(
            $expectedRoot,
            [System.StringComparison]::OrdinalIgnoreCase)) {
            throw "Unsafe pyRevit extension target: $resolvedTarget"
        }
        Remove-Item -LiteralPath $pyRevitTarget -Recurse -Force
    }
    Copy-Item -LiteralPath $pyRevitSource -Destination $pyRevitTarget -Recurse -Force
}

$assemblyPath = [System.Security.SecurityElement]::Escape((Join-Path $installRoot "TungMEP.Loader.dll"))
$manifest = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>TungMEP</Name>
    <Assembly>$assemblyPath</Assembly>
    <AddInId>82718F2F-609B-47AF-AD9E-3D40604DD102</AddInId>
    <FullClassName>TungMEP.Loader.Application</FullClassName>
    <VendorId>TUNG</VendorId>
    <VendorDescription>Tung MEP tools for Autodesk Revit</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
[System.IO.File]::WriteAllText($manifestPath, $manifest, [System.Text.Encoding]::UTF8)

Write-Host "Installed TungMEP loader: $installRoot"
Write-Host "Installed payload: $payloadFileName"
Write-Host "Open Revit, then use Reload TungMEP for future payload updates."
