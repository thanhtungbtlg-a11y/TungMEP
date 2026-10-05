$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$panel = Join-Path $root "pyrevit\TungMEP.extension\Tung MEP QA.tab\pyRevit Companion.panel"
$qaPanel = Join-Path $root "pyrevit\TungMEP.extension\Tung MEP QA.tab\Project QA.panel"

function New-TungMepIcon {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][ValidateSet("Launcher", "Repeat", "Reload", "Diagnostics", "Health")][string]$Kind
    )

    $bitmap = New-Object System.Drawing.Bitmap 32, 32
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $blue = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(20, 77, 125))
    $red = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(220, 55, 42))
    $whitePen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 2.2
    $redPen = New-Object System.Drawing.Pen ([System.Drawing.Color]::FromArgb(220, 55, 42)), 2.2
    try {
        $graphics.FillRectangle($blue, 1, 1, 30, 30)
        if ($Kind -eq "Launcher") {
            $graphics.DrawRectangle($whitePen, 6, 6, 7, 7)
            $graphics.DrawRectangle($whitePen, 19, 6, 7, 7)
            $graphics.DrawRectangle($whitePen, 6, 19, 7, 7)
            $graphics.DrawLine($redPen, 19, 22.5, 26, 22.5)
            $graphics.DrawLine($redPen, 22.5, 19, 22.5, 26)
        }
        elseif ($Kind -eq "Repeat") {
            $graphics.DrawArc($whitePen, 7, 7, 18, 18, 205, 270)
            $graphics.DrawLine($whitePen, 22, 7, 17, 6)
            $graphics.DrawLine($whitePen, 22, 7, 21, 12)
            $graphics.FillEllipse($red, 7, 20, 7, 7)
        }
        elseif ($Kind -eq "Reload") {
            $graphics.DrawArc($whitePen, 7, 6, 18, 18, 210, 135)
            $graphics.DrawArc($whitePen, 7, 8, 18, 18, 30, 135)
            $graphics.DrawLine($whitePen, 22, 7, 18, 6)
            $graphics.DrawLine($whitePen, 22, 7, 21, 11)
            $graphics.DrawLine($redPen, 9, 25, 13, 26)
            $graphics.DrawLine($redPen, 9, 25, 10, 21)
        }
        elseif ($Kind -eq "Diagnostics") {
            $graphics.FillEllipse($red, 6, 6, 20, 20)
            $font = New-Object System.Drawing.Font "Segoe UI", 15, ([System.Drawing.FontStyle]::Bold), ([System.Drawing.GraphicsUnit]::Pixel)
            try {
                $graphics.DrawString("i", $font, [System.Drawing.Brushes]::White, 13, 7)
            }
            finally {
                $font.Dispose()
            }
        }
        else {
            $graphics.DrawLine($whitePen, 6, 9, 12, 9)
            $graphics.DrawLine($whitePen, 6, 16, 12, 16)
            $graphics.DrawLine($whitePen, 6, 23, 12, 23)
            $graphics.FillEllipse($red, 14, 6, 5, 5)
            $graphics.FillEllipse($red, 14, 13, 5, 5)
            $graphics.FillEllipse($red, 14, 20, 5, 5)
            $graphics.DrawLine($whitePen, 20, 9, 26, 9)
            $graphics.DrawLine($whitePen, 20, 16, 26, 16)
            $graphics.DrawLine($whitePen, 20, 23, 26, 23)
        }
        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $Path) | Out-Null
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
    }
    finally {
        $whitePen.Dispose()
        $redPen.Dispose()
        $blue.Dispose()
        $red.Dispose()
        $graphics.Dispose()
        $bitmap.Dispose()
    }
}

New-TungMepIcon -Path (Join-Path $panel "TungMEP Launcher.pushbutton\icon.png") -Kind Launcher
New-TungMepIcon -Path (Join-Path $panel "Repeat Last.pushbutton\icon.png") -Kind Repeat
New-TungMepIcon -Path (Join-Path $panel "Reload Payload.pushbutton\icon.png") -Kind Reload
New-TungMepIcon -Path (Join-Path $panel "Diagnostics.pushbutton\icon.png") -Kind Diagnostics
New-TungMepIcon -Path (Join-Path $qaPanel "MEP Health Dashboard.pushbutton\icon.png") -Kind Health

Write-Host "Generated TungMEP pyRevit Companion icons."
