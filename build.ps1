$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$compiler = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
$revit = "C:\Program Files\Autodesk\Revit 2024"
$windowsBase = "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\WindowsBase\v4.0_4.0.0.0__31bf3856ad364e35\WindowsBase.dll"
$presentationCore = "$env:WINDIR\Microsoft.NET\assembly\GAC_64\PresentationCore\v4.0_4.0.0.0__31bf3856ad364e35\PresentationCore.dll"
$outputRoot = Join-Path $root "bin\net48"
$payloadDirectory = Join-Path $outputRoot "payload"
$loaderOutput = Join-Path $outputRoot "TungMEP.Loader.dll"
$buildVersion = Get-Date -Format "yyyyMMddHHmmss"
$payloadFileName = "TungMEP.Payload.$buildVersion.dll"
$payloadOutput = Join-Path $payloadDirectory $payloadFileName
$loaderSources = Get-ChildItem (Join-Path $root "src\Loader") -Recurse -Filter *.cs | Select-Object -ExpandProperty FullName
$payloadSources = Get-ChildItem (Join-Path $root "src\Modules") -Recurse -Filter *.cs |
    Where-Object { $_.Name -ne "AutoSprinklerModule.cs" } |
    Select-Object -ExpandProperty FullName
New-Item -ItemType Directory -Force -Path $outputRoot, $payloadDirectory | Out-Null

& $compiler /target:library /nologo /optimize+ /out:$loaderOutput `
    /reference:"$revit\RevitAPI.dll" `
    /reference:"$revit\RevitAPIUI.dll" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    /reference:"$windowsBase" `
    /reference:"$presentationCore" `
    $loaderSources

if ($LASTEXITCODE -ne 0) {
    throw "TungMEP loader build failed with exit code $LASTEXITCODE."
}

& $compiler /target:library /nologo /optimize+ /out:$payloadOutput `
    /reference:"$revit\RevitAPI.dll" `
    /reference:"$revit\RevitAPIUI.dll" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Windows.Forms.dll `
    $payloadSources

if ($LASTEXITCODE -ne 0) {
    throw "TungMEP payload build failed with exit code $LASTEXITCODE."
}

[System.IO.File]::WriteAllText((Join-Path $payloadDirectory "current.txt"), $payloadFileName, [System.Text.Encoding]::ASCII)

$contentTarget = Join-Path $root "bin\net48\content\AutoSprinkler"
$defaultFamilySource = Join-Path $root "content\TungMEP Test Sprinkler.rfa"

if (Test-Path $defaultFamilySource) {
    New-Item -ItemType Directory -Force -Path $contentTarget | Out-Null
    Copy-Item -LiteralPath $defaultFamilySource -Destination $contentTarget -Force
}

Write-Host "Built loader: $loaderOutput"
Write-Host "Built payload: $payloadOutput"
