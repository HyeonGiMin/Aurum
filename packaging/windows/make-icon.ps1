# ============================================================
# Assets/icon.png → Assets/icon.ico (다중 해상도)
#
# 왜 필요한가: Windows 는 exe 에 박힌 .ico 로 작업 표시줄·탐색기 아이콘을
# 그린다. Avalonia 의 Window.Icon(png)만으로는 exe 아이콘이 기본값이 된다.
#
#   사용:  powershell -ExecutionPolicy Bypass -File tools/packaging/windows/make-icon.ps1
#   결과:  tools/src/PrismOne.Studio/Assets/icon.ico  (커밋 대상)
#
# 아이콘 원본(png) 자체를 다시 그리려면 앱을 IAPDM_RENDER_ICON=<경로> 로 실행한다.
# ============================================================
[CmdletBinding()]
param(
    [string]$OutPath
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$assetsDir = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..\..')) 'src\PrismOne.Studio\Assets'
$source = Join-Path $assetsDir 'icon_1024.png'
if (-not (Test-Path $source)) { $source = Join-Path $assetsDir 'icon.png' }
if (-not (Test-Path $source)) { throw "아이콘 원본을 찾지 못했습니다: $assetsDir\icon(_1024).png" }
if (-not $OutPath) { $OutPath = Join-Path $assetsDir 'icon.ico' }

$sizes = 16, 24, 32, 48, 64, 128, 256
$frames = @()

$src = [System.Drawing.Image]::FromFile($source)
try {
    foreach ($size in $sizes) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        try {
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $graphics.PixelOffsetMode = [System.Drawing.Drawing2D.PixelOffsetMode]::HighQuality
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.DrawImage($src, 0, 0, $size, $size)
        }
        finally { $graphics.Dispose() }

        $stream = New-Object System.IO.MemoryStream
        try {
            $bitmap.Save($stream, [System.Drawing.Imaging.ImageFormat]::Png)
            $frames += [pscustomobject]@{ Size = $size; Data = $stream.ToArray() }
        }
        finally { $stream.Dispose(); $bitmap.Dispose() }
    }
}
finally { $src.Dispose() }

# ICO 컨테이너: 헤더(6) + 디렉터리(16 x N) + PNG 블롭들
$bytes = New-Object System.Collections.Generic.List[byte]
$bytes.AddRange([BitConverter]::GetBytes([uint16]0))              # reserved
$bytes.AddRange([BitConverter]::GetBytes([uint16]1))              # type: icon
$bytes.AddRange([BitConverter]::GetBytes([uint16]$frames.Count))

$offset = 6 + (16 * $frames.Count)
foreach ($frame in $frames) {
    # 256 은 1바이트에 안 들어가므로 0 으로 기록한다 (ICO 규약)
    if ($frame.Size -ge 256) { $dimension = 0 } else { $dimension = $frame.Size }
    $bytes.Add([byte]$dimension)                                  # width
    $bytes.Add([byte]$dimension)                                  # height
    $bytes.Add([byte]0)                                           # palette colors
    $bytes.Add([byte]0)                                           # reserved
    $bytes.AddRange([BitConverter]::GetBytes([uint16]1))          # color planes
    $bytes.AddRange([BitConverter]::GetBytes([uint16]32))         # bits per pixel
    $bytes.AddRange([BitConverter]::GetBytes([uint32]$frame.Data.Length))
    $bytes.AddRange([BitConverter]::GetBytes([uint32]$offset))
    $offset += $frame.Data.Length
}
foreach ($frame in $frames) { $bytes.AddRange($frame.Data) }

[System.IO.File]::WriteAllBytes($OutPath, $bytes.ToArray())

$bytes = (Get-Item $OutPath).Length
Write-Output "icon.ico 생성: $OutPath ($($frames.Count) frames, $bytes bytes)"
