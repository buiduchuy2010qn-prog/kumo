param(
    [string]$OutDir = $PSScriptRoot
)

Add-Type -AssemblyName System.Drawing

function New-KumoLogoBitmap([int]$size) {
    $bmp = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
    $g.Clear([System.Drawing.Color]::Transparent)

    $pad = [float]($size * 0.055)
    $w = [float]($size - $pad * 2)
    $h = [float]($size - $pad * 2)
    $r = [float]($size * 0.27)

    # Squircle icon body
    $path = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = $r * 2
    $path.AddArc($pad, $pad, $d, $d, 180, 90)
    $path.AddArc($pad + $w - $d, $pad, $d, $d, 270, 90)
    $path.AddArc($pad + $w - $d, $pad + $h - $d, $d, $d, 0, 90)
    $path.AddArc($pad, $pad + $h - $d, $d, $d, 90, 90)
    $path.CloseFigure()

    # Deep sky-midnight gradient
    $bgBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($size, $size)),
        [System.Drawing.Color]::FromArgb(255, 22, 30, 52),
        [System.Drawing.Color]::FromArgb(255, 10, 14, 26)
    )
    $g.FillPath($bgBrush, $path)

    $clipSt = $g.Save()
    $g.SetClip($path)

    # Soft Kumo Cloud Aurora Glow at bottom of squircle (zero seam!)
    $glowRect = New-Object System.Drawing.RectangleF(0, 0, [float]$size, [float]$size)
    $glowBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF(0, [float]$size)),
        [System.Drawing.Color]::FromArgb(0, 56, 189, 248),
        [System.Drawing.Color]::FromArgb(140, 99, 102, 241)
    )
    $cb = New-Object System.Drawing.Drawing2D.ColorBlend(3)
    $cb.Positions = [float[]]@(0.0, 0.48, 1.0)
    $cb.Colors = [System.Drawing.Color[]]@(
        [System.Drawing.Color]::FromArgb(0, 56, 189, 248),
        [System.Drawing.Color]::FromArgb(0, 56, 189, 248),
        [System.Drawing.Color]::FromArgb(145, 99, 102, 241)
    )
    $glowBrush.InterpolationColors = $cb
    $g.FillRectangle($glowBrush, $glowRect)

    # Center Kumo Living Notch Pill with glowing underside
    $nw = [float]($size * 0.74)
    $nh = [float]($size * 0.44)
    $nx = [float](($size - $nw) * 0.5)
    $ny = [float]($size * 0.28)
    $nr = [float]($nh * 0.46)
    $nd = $nr * 2

    $npath = New-Object System.Drawing.Drawing2D.GraphicsPath
    $npath.AddArc($nx, $ny, $nd, $nd, 180, 90)
    $npath.AddArc($nx + $nw - $nd, $ny, $nd, $nd, 270, 90)
    $npath.AddArc($nx + $nw - $nd, $ny + $nh - $nd, $nd, $nd, 0, 90)
    $npath.AddArc($nx, $ny + $nh - $nd, $nd, $nd, 90, 90)
    $npath.CloseFigure()

    $blackBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::Black)
    $g.FillPath($blackBr, $npath)

    # Inner Kumo Cyan-Indigo Mascot Glow inside the Notch (smooth vertical fade + horizontal tint)
    $stNotch = $g.Save()
    $g.SetClip($npath, [System.Drawing.Drawing2D.CombineMode]::Intersect)
    for ($py = [int]($ny + $nh * 0.28); $py -le [int]($ny + $nh + 2); $py++) {
        $t = [Math]::Max(0.0, [Math]::Min(1.0, ($py - ($ny + $nh * 0.28)) / [Math]::Max(1.0, ($nh * 0.72))))
        $al = [int]([Math]::Min(242, [Math]::Pow($t, 1.55) * 242))
        if ($al -le 1) { continue }
        $rowBr = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
            (New-Object System.Drawing.PointF($nx, $py)),
            (New-Object System.Drawing.PointF(($nx + $nw), $py)),
            [System.Drawing.Color]::FromArgb($al, 14, 185, 255),
            [System.Drawing.Color]::FromArgb($al, 108, 76, 238)
        )
        $g.FillRectangle($rowBr, $nx, $py, $nw, 1.5)
        $rowBr.Dispose()
    }

    # Cute Happy "^ ^" Kumo Mascot Eyes + Waving Hand
    $eyePen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(1.6, $size * 0.048))
    $eyePen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
    $eyePen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
    $eyePen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

    $cy = [float]($ny + $nh * 0.50)
    $lx = [float]($size * 0.37)
    $rx = [float]($size * 0.56)
    $ew = [float]($size * 0.055)
    $eh = [float]($size * 0.055)

    $g.DrawBezier($eyePen,
        (New-Object System.Drawing.PointF(($lx - $ew), ($cy + $eh * 0.45))),
        (New-Object System.Drawing.PointF(($lx - $ew * 0.35), ($cy - $eh))),
        (New-Object System.Drawing.PointF(($lx + $ew * 0.35), ($cy - $eh))),
        (New-Object System.Drawing.PointF(($lx + $ew), ($cy + $eh * 0.45))))

    $g.DrawBezier($eyePen,
        (New-Object System.Drawing.PointF(($rx - $ew), ($cy + $eh * 0.45))),
        (New-Object System.Drawing.PointF(($rx - $ew * 0.35), ($cy - $eh))),
        (New-Object System.Drawing.PointF(($rx + $ew * 0.35), ($cy - $eh))),
        (New-Object System.Drawing.PointF(($rx + $ew), ($cy + $eh * 0.45))))

    if ($size -ge 32) {
        $hx = [float]($size * 0.72)
        $hy = [float]($ny + $nh * 0.60)
        $wBr = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $fPen = New-Object System.Drawing.Pen([System.Drawing.Color]::White, [Math]::Max(1.2, $size * 0.032))
        $fPen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
        $fPen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
        $pw = [float]($size * 0.085)
        $ph = [float]($size * 0.065)
        $g.FillEllipse($wBr, ($hx - $pw * 0.5), ($hy - $ph * 0.5), $pw, $ph)
        $g.DrawLine($fPen, ($hx - $pw * 0.35), $hy, ($hx - $pw * 0.72), ($hy - $ph * 0.3))
        $g.DrawLine($fPen, ($hx - $pw * 0.2), ($hy - $ph * 0.3), ($hx - $pw * 0.3), ($hy - $ph * 1.05))
        $g.DrawLine($fPen, ($hx + $pw * 0.08), ($hy - $ph * 0.35), ($hx + $pw * 0.14), ($hy - $ph * 1.12))
        $g.DrawLine($fPen, ($hx + $pw * 0.32), ($hy - $ph * 0.2), ($hx + $pw * 0.55), ($hy - $ph * 0.85))
        $wBr.Dispose(); $fPen.Dispose()
    }

    $g.Restore($stNotch)

    $nRim = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(70, 125, 211, 252), [Math]::Max(1.0, $size * 0.015))
    $g.DrawPath($nRim, $npath)

    $g.Restore($clipSt)

    # Outer glossy squircle border
    $borderBrush = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
        (New-Object System.Drawing.PointF(0, 0)),
        (New-Object System.Drawing.PointF($size, $size)),
        [System.Drawing.Color]::FromArgb(210, 56, 189, 248),
        [System.Drawing.Color]::FromArgb(200, 129, 140, 248)
    )
    $borderPen = New-Object System.Drawing.Pen($borderBrush, [Math]::Max(1.2, $size * 0.032))
    $g.DrawPath($borderPen, $path)

    $eyePen.Dispose(); $nRim.Dispose(); $blackBr.Dispose(); $npath.Dispose()
    $glowBrush.Dispose(); $borderPen.Dispose(); $borderBrush.Dispose(); $bgBrush.Dispose(); $path.Dispose()
    $g.Dispose()
    return $bmp
}

$sizes = @(256, 64, 48, 32, 16)
$pngBuffers = @()

foreach ($s in $sizes) {
    $bmp = New-KumoLogoBitmap $s
    if ($s -eq 256) {
        $bmp.Save((Join-Path $OutDir "logo_256.png"), [System.Drawing.Imaging.ImageFormat]::Png)
    }
    $ms = New-Object System.IO.MemoryStream
    $bmp.Save($ms, [System.Drawing.Imaging.ImageFormat]::Png)
    $pngBuffers += ,($ms.ToArray())
    $ms.Dispose()
    $bmp.Dispose()
}

$icoPath = Join-Path $OutDir "Kumo.ico"
$fs = [System.IO.File]::Create($icoPath)
$bw = New-Object System.IO.BinaryWriter($fs)

$bw.Write([uint16]0)
$bw.Write([uint16]1)
$bw.Write([uint16]$sizes.Count)

$offset = 6 + (16 * $sizes.Count)
for ($i = 0; $i -lt $sizes.Count; $i++) {
    $s = $sizes[$i]
    $data = $pngBuffers[$i]
    $bw.Write([byte]$(if ($s -eq 256) { 0 } else { $s }))
    $bw.Write([byte]$(if ($s -eq 256) { 0 } else { $s }))
    $bw.Write([byte]0)
    $bw.Write([byte]0)
    $bw.Write([uint16]1)
    $bw.Write([uint16]32)
    $bw.Write([uint32]$data.Length)
    $bw.Write([uint32]$offset)
    $offset += $data.Length
}

for ($i = 0; $i -lt $sizes.Count; $i++) {
    $bw.Write($pngBuffers[$i])
}

$bw.Flush()
$bw.Dispose()
$fs.Dispose()
Write-Output "Generated $icoPath and logo_256.png successfully."
