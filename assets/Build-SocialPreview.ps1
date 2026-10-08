# Rebuild the GitHub social preview from the documented illustrative screenshots.
Add-Type -AssemblyName System.Drawing
$canvas = [System.Drawing.Bitmap]::new(1280, 640)
$drawing = [System.Drawing.Graphics]::FromImage($canvas)
$drawing.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit
$drawing.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
$background = [System.Drawing.ColorTranslator]::FromHtml('#111815')
$drawing.Clear($background)
$white = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#eef5f0'))
$muted = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#adbcaf'))
$green = [System.Drawing.SolidBrush]::new([System.Drawing.ColorTranslator]::FromHtml('#a8de56'))
$titleFont = [System.Drawing.Font]::new('Segoe UI', 48, [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
$labelFont = [System.Drawing.Font]::new('Segoe UI', 23, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
$smallFont = [System.Drawing.Font]::new('Segoe UI', 19, [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
try {
    $drawing.FillRectangle($green, 60, 45, 7, 115)
    $drawing.DrawString('Codex & Claude', $titleFont, $white, 88, 38)
    $drawing.DrawString('Usage and Reset Tracker', $titleFont, $white, 88, 96)
    $drawing.DrawString('MajorCommand Edition  |  Windows desktop widget', $labelFont, $muted, 88, 175)
    $faces = @(
        @{ Label = 'Usage & forecasts'; File = 'usage-overview-beta4.png'; X = 88 },
        @{ Label = 'Reset dates & countdowns'; File = 'reset-details-beta4.png'; X = 470 },
        @{ Label = 'Claude allowances'; File = 'claude-usage-beta6.png'; X = 852 }
    )
    foreach ($face in $faces) {
        $drawing.DrawString($face.Label, $labelFont, $white, $face.X, 248)
        $screenshot = [System.Drawing.Image]::FromFile((Join-Path $PSScriptRoot $face.File))
        try {
            $drawing.DrawImage($screenshot, [System.Drawing.Rectangle]::new($face.X, 292, 310, [int][Math]::Round($screenshot.Height * 310 / $screenshot.Width)))
        } finally { $screenshot.Dispose() }
    }
    $drawing.DrawString('github.com/majorcommand/codex-bar', $smallFont, $green, 88, 598)
    $drawing.DrawString('Illustrative readings', $smallFont, $muted, 1008, 598)
    $canvas.Save((Join-Path $PSScriptRoot 'social-preview.png'), [System.Drawing.Imaging.ImageFormat]::Png)
} finally {
    $titleFont.Dispose(); $labelFont.Dispose(); $smallFont.Dispose()
    $white.Dispose(); $muted.Dispose(); $green.Dispose()
    $drawing.Dispose(); $canvas.Dispose()
}
