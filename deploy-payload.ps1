$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$installRoot = Join-Path $env:LOCALAPPDATA "TungMEP\Revit2024"
$addinsDirectory = Join-Path $env:APPDATA "Autodesk\Revit\Addins\2024"
$manifestPath = Join-Path $addinsDirectory "TungMEP.addin"
$sourceLoader = Join-Path $root "bin\net48\TungMEP.Loader.dll"
$sourcePayloadDirectory = Join-Path $root "bin\net48\payload"
$targetPayloadDirectory = Join-Path $installRoot "payload"

& (Join-Path $root "build.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "TungMEP build failed with exit code $LASTEXITCODE."
}

if (-not (Test-Path -LiteralPath (Join-Path $installRoot "TungMEP.Loader.dll"))) {
    throw "The TungMEP hot-reload loader is not installed yet. Close Revit once and install the loader first."
}

$payloadFileName = (Get-Content -Raw (Join-Path $sourcePayloadDirectory "current.txt")).Trim()
$sourcePayload = Join-Path $sourcePayloadDirectory $payloadFileName
if (-not (Test-Path -LiteralPath $sourcePayload)) {
    throw "Built payload was not found: $sourcePayload"
}

New-Item -ItemType Directory -Force -Path $targetPayloadDirectory | Out-Null
Copy-Item -LiteralPath $sourcePayload -Destination (Join-Path $targetPayloadDirectory $payloadFileName) -Force
[System.IO.File]::WriteAllText((Join-Path $targetPayloadDirectory "current.txt"), $payloadFileName, [System.Text.Encoding]::ASCII)

$contentSource = Join-Path $root "bin\net48\content"
if (Test-Path -LiteralPath $contentSource) {
    Copy-Item -LiteralPath $contentSource -Destination $installRoot -Recurse -Force
}

# Ribbon changes live in the loader and cannot be hot-loaded into the current
# Revit process. Stage a hash-versioned loader beside the running one and point
# the manifest to it. The next Revit launch uses the new ribbon without asking
# the user to run another installer after closing Revit.
$loaderHash = (Get-FileHash -LiteralPath $sourceLoader -Algorithm SHA256).Hash.Substring(0, 12)
$loaderFileName = "TungMEP.Loader.$loaderHash.dll"
$stagedLoader = Join-Path $installRoot $loaderFileName
if (-not (Test-Path -LiteralPath $stagedLoader)) {
    Copy-Item -LiteralPath $sourceLoader -Destination $stagedLoader -Force
}
New-Item -ItemType Directory -Force -Path $addinsDirectory | Out-Null
$assemblyPath = [System.Security.SecurityElement]::Escape($stagedLoader)
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

Write-Host "Deployed payload: $payloadFileName"
Write-Host "In Revit, click Reload TungMEP or run a TungMEP command to use it."
Write-Host "Staged loader: $loaderFileName"
if (Get-Process Revit -ErrorAction SilentlyContinue) {
    Write-Host "Restart Revit once to show any newly added ribbon buttons."
}
