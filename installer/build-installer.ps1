$ErrorActionPreference = "Stop"

$isccCandidates = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "C:\Program Files (x86)\Inno Setup 6\ISCC.exe",
    "C:\Program Files\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path -LiteralPath $_ } | Select-Object -First 1
if (-not $iscc) {
    throw "Inno Setup 6 compiler was not found."
}

$installerRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$projectRoot = Split-Path -Parent $installerRoot

& (Join-Path $projectRoot "generate-pyrevit-icons.ps1")

& (Join-Path $projectRoot "build.ps1")
if ($LASTEXITCODE -ne 0) {
    throw "TungMEP build failed with exit code $LASTEXITCODE."
}

$requiredFiles = @(
    (Join-Path $projectRoot "bin\net48\TungMEP.Loader.dll"),
    (Join-Path $projectRoot "bin\net48\payload\current.txt"),
    (Join-Path $projectRoot "bin\net48\content\AutoSprinkler\TungMEP Test Sprinkler.rfa"),
    (Join-Path $projectRoot "pyrevit\TungMEP.extension\lib\tungmep_bridge.py")
)
foreach ($file in $requiredFiles) {
    if (-not (Test-Path -LiteralPath $file)) {
        throw "Required installer input is missing: $file"
    }
}

$payloadRoot = Join-Path $projectRoot "bin\net48\payload"
$payloadFileName = (Get-Content -Raw (Join-Path $payloadRoot "current.txt")).Trim()
$payloadFile = Join-Path $payloadRoot $payloadFileName
if (-not (Test-Path -LiteralPath $payloadFile)) {
    throw "Current payload is missing: $payloadFile"
}

$stageRoot = Join-Path $installerRoot "stage"
$expectedStageRoot = [System.IO.Path]::GetFullPath($stageRoot)
$expectedInstallerRoot = [System.IO.Path]::GetFullPath($installerRoot) + [System.IO.Path]::DirectorySeparatorChar
if (-not $expectedStageRoot.StartsWith($expectedInstallerRoot, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "Unsafe installer stage path: $expectedStageRoot"
}
if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
New-Item -ItemType Directory -Force -Path `
    $stageRoot, `
    (Join-Path $stageRoot "payload"), `
    (Join-Path $stageRoot "content"), `
    (Join-Path $stageRoot "pyrevit") | Out-Null
Copy-Item -LiteralPath (Join-Path $projectRoot "bin\net48\TungMEP.Loader.dll") `
    -Destination (Join-Path $stageRoot "TungMEP.Loader.dll") -Force
Copy-Item -LiteralPath $payloadFile -Destination (Join-Path $stageRoot "payload\$payloadFileName") -Force
[System.IO.File]::WriteAllText(
    (Join-Path $stageRoot "payload\current.txt"),
    $payloadFileName,
    [System.Text.Encoding]::ASCII)
Copy-Item -Path (Join-Path $projectRoot "bin\net48\content\*") `
    -Destination (Join-Path $stageRoot "content") -Recurse -Force
Copy-Item -LiteralPath (Join-Path $projectRoot "pyrevit\TungMEP.extension") `
    -Destination (Join-Path $stageRoot "pyrevit\TungMEP.extension") -Recurse -Force

& $iscc (Join-Path $installerRoot "TungMEP.iss")
if ($LASTEXITCODE -ne 0) {
    throw "Installer compilation failed with exit code $LASTEXITCODE."
}

$output = Join-Path $installerRoot "output\TungMEP_Setup_1.8.3_Revit2024.exe"
Write-Host "Built installer: $output"
