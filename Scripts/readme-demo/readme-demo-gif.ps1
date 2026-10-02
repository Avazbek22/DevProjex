#Requires -Version 7.0
# Turns the raw README demo recordings and their event logs into one GIF. Each segment is
# synchronised with its log, trimmed, given click rings, a drawn pointer where the real one was not
# captured, and captions; segments are joined with a crossfade and encoded with one palette.
param(
    # Recording stems in playback order: <stem>.mkv and <stem>.events.json. A single
    # comma-separated string is accepted too, which is how `pwsh -File` passes a list.
    [Parameter(Mandatory)] [string[]]$Segments,
    [Parameter(Mandatory)] [string]$Output,
    [Parameter(Mandatory)] [string]$Ffmpeg,
    [string]$Gifsicle,
    [int]$Width = 1440,
    [int]$Fps = 20,
    [int]$Lossy = 30,
    [switch]$NoCaptions,
    [switch]$NoRings,
    [double]$CrossfadeSeconds = 0.6,
    # Recording pixels: the strip of wallpaper under the window.
    [double]$CaptionCenterY = 1230,
    # Output pixels for a 1600-pixel-wide GIF, scaled with -Width.
    [double]$CaptionFontSize = 30,
    [string]$FontFile = 'C:/Windows/Fonts/seguisb.ttf',
    [string]$SymbolFontFile = 'C:/Windows/Fonts/seguisym.ttf'
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing
$invariant = [Globalization.CultureInfo]::InvariantCulture
function F([double]$Value) { $Value.ToString('0.###', $invariant) }
function ConvertTo-FilterPath([string]$Path) { $Path.Replace('\', '/').Replace(':', '\:') }
$work = Join-Path ([IO.Path]::GetTempPath()) ('devprojex-readme-gif-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $work | Out-Null

# The log clock and the capture start differ by ffmpeg's start-up time. The sync event marks a
# visible change at a known log time; the first frame that differs from the one before it gives
# the video time of that event.
function Find-SyncVideoTime([string]$Recording, $Sync, $Start) {
    $x0 = [int]$Sync.x - [int]$Start.x - 4; $y0 = [int]$Sync.y - [int]$Start.y - 4
    $patch = Join-Path $work ('sync-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory $patch | Out-Null
    & $Ffmpeg -hide_banner -loglevel error -y -t ([double]$Sync.t + 3) -i $Recording -vf "crop=40:40:$($x0):$($y0)" -fps_mode passthrough (Join-Path $patch '%04d.png')
    $times = @(& $Ffmpeg -hide_banner -t ([double]$Sync.t + 3) -i $Recording -vf showinfo -f null - 2>&1 |
        Select-String 'pts_time:([0-9.]+)' | ForEach-Object { [double]::Parse($_.Matches[0].Groups[1].Value, $invariant) })
    $frames = @(Get-ChildItem $patch -Filter *.png | Sort-Object Name)
    $reference = [System.Drawing.Bitmap]::new($frames[0].FullName)
    try {
        for ($i = 1; $i -lt $frames.Count; $i++) {
            $bitmap = [System.Drawing.Bitmap]::new($frames[$i].FullName); $difference = 0
            for ($x = 0; $x -lt 40; $x++) {
                for ($y = 0; $y -lt 40; $y++) {
                    $a = $reference.GetPixel($x, $y); $b = $bitmap.GetPixel($x, $y)
                    $difference += [Math]::Abs($a.R - $b.R) + [Math]::Abs($a.G - $b.G) + [Math]::Abs($a.B - $b.B)
                }
            }
            $bitmap.Dispose()
            if ($difference -gt 1500) { return $times[$i] }
        }
    }
    finally { $reference.Dispose() }
    throw "The sync point was not found in $Recording."
}

# An expanding, fading ring played at each click.
$ringDirectory = Join-Path $work 'ring'
New-Item -ItemType Directory $ringDirectory | Out-Null
for ($i = 0; $i -lt 14; $i++) {
    $k = $i / 13.0
    $bitmap = [System.Drawing.Bitmap]::new(96, 96, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap); $graphics.SmoothingMode = 'AntiAlias'
    $graphics.Clear([System.Drawing.Color]::Transparent)
    $radius = 10 + 34 * (1 - [Math]::Pow(1 - $k, 3))
    $fill = [System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb([int](70 * (1 - $k)), 45, 206, 229))
    $pen = [System.Drawing.Pen]::new([System.Drawing.Color]::FromArgb([int](230 * (1 - $k)), 45, 206, 229), [float](5 - 3 * $k))
    $graphics.FillEllipse($fill, [float](48 - $radius), [float](48 - $radius), [float](2 * $radius), [float](2 * $radius))
    $graphics.DrawEllipse($pen, [float](48 - $radius), [float](48 - $radius), [float](2 * $radius), [float](2 * $radius))
    $graphics.Dispose()
    $bitmap.Save((Join-Path $ringDirectory ('{0:D2}.png' -f $i)), [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()
}

# A standard arrow pointer for segments whose real pointer was not captured.
$pointerPath = Join-Path $work 'pointer.png'
$bitmap = [System.Drawing.Bitmap]::new(40, 52, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
$graphics = [System.Drawing.Graphics]::FromImage($bitmap); $graphics.SmoothingMode = 'AntiAlias'
$graphics.Clear([System.Drawing.Color]::Transparent)
$arrow = [System.Drawing.PointF[]]@(@(1, 1), @(1, 25), @(7, 19.5), @(11.5, 29), @(15.5, 27.2), @(11, 18), @(19, 18) |
    ForEach-Object { [System.Drawing.PointF]::new($_[0] * 1.3 + 1, $_[1] * 1.3 + 1) })
$shadow = [System.Drawing.PointF[]]($arrow | ForEach-Object { [System.Drawing.PointF]::new($_.X + 1.5, $_.Y + 2) })
$graphics.FillPolygon([System.Drawing.SolidBrush]::new([System.Drawing.Color]::FromArgb(90, 0, 0, 0)), $shadow)
$graphics.FillPolygon([System.Drawing.Brushes]::White, $arrow)
$graphics.DrawPolygon([System.Drawing.Pen]::new([System.Drawing.Color]::Black, 1.6), $arrow)
$graphics.Dispose(); $bitmap.Save($pointerPath, [System.Drawing.Imaging.ImageFormat]::Png); $bitmap.Dispose()

try {
    $Segments = @($Segments | ForEach-Object { $_ -split ',' } | ForEach-Object { $_.Trim().Trim('"') } | Where-Object { $_ })
    $rendered = @()
    for ($index = 0; $index -lt $Segments.Count; $index++) {
        $stem = $Segments[$index]
        $recording = "$stem.mkv"
        $log = Get-Content "$stem.events.json" -Raw | ConvertFrom-Json
        $start = $log | Where-Object type -eq 'start' | Select-Object -First 1
        $sync = $log | Where-Object type -eq 'sync' | Select-Object -First 1
        $end = $log | Where-Object type -eq 'end' | Select-Object -First 1
        $rx = [int]$start.x; $ry = [int]$start.y; $rw = [int]$start.w; $rh = [int]$start.h
        $syncVideo = Find-SyncVideoTime $recording $sync $start
        $offset = $syncVideo - [double]$sync.t            # video time = log time + offset
        $trimStart = $syncVideo + 0.5                     # past the sync marker
        $trimEnd = [double]$end.t + $offset - 0.05
        $duration = $trimEnd - $trimStart
        Write-Host ("{0}: offset {1:N3} s, {2:N1} s" -f (Split-Path $stem -Leaf), $offset, $duration)

        $outHeight = [int]([Math]::Round($rh * $Width / $rw / 2) * 2)
        $graph = [System.Collections.Generic.List[string]]::new()
        $graph.Add("[0:v]trim=start=$(F $trimStart):end=$(F $trimEnd),setpts=PTS-STARTPTS,fps=$Fps[base]")
        $last = 'base'

        $clicks = @(if (-not $NoRings) { $log | Where-Object type -eq 'click' })
        if ($clicks.Count -gt 0) {
            $graph.Add("[1:v]format=rgba,split=$($clicks.Count)" + (-join (0..($clicks.Count - 1) | ForEach-Object { "[r$_]" })))
            for ($i = 0; $i -lt $clicks.Count; $i++) {
                $t = [Math]::Max(0, [double]$clicks[$i].t + $offset - $trimStart)
                $graph.Add("[r$i]setpts=PTS-STARTPTS+$(F $t)/TB[rs$i]")
                $graph.Add("[$last][rs$i]overlay=x=$([int]$clicks[$i].x - $rx - 48):y=$([int]$clicks[$i].y - $ry - 48):eof_action=pass:enable='between(t,$(F $t),$(F ($t + 0.56)))'[o$i]")
                $last = "o$i"
            }
        }

        # Pointer path, interpolated per output frame and fed to the overlay through sendcmd.
        $path = @($log | Where-Object type -eq 'mouse' | Sort-Object { [double]$_.t })
        $drawPointer = $path.Count -gt 0
        if ($drawPointer) {
            $commands = [System.Text.StringBuilder]::new(); $previous = $null; $j = 0; $first = $null
            for ($frame = 0; $frame -le [int]($duration * $Fps); $frame++) {
                $videoTime = $frame / $Fps; $logTime = $videoTime + $trimStart - $offset
                while ($j + 1 -lt $path.Count -and [double]$path[$j + 1].t -le $logTime) { $j++ }
                $a = $path[$j]; $b = if ($j + 1 -lt $path.Count) { $path[$j + 1] } else { $a }
                $span = [double]$b.t - [double]$a.t
                $f = if ($span -gt 0 -and $logTime -gt [double]$a.t) { [Math]::Min(1, ($logTime - [double]$a.t) / $span) } else { 0 }
                $x = [int]([double]$a.x + ([double]$b.x - [double]$a.x) * $f) - $rx - 2
                $y = [int]([double]$a.y + ([double]$b.y - [double]$a.y) * $f) - $ry - 2
                if ($null -eq $first) { $first = @($x, $y) }
                if ("$x,$y" -ne $previous) { [void]$commands.AppendLine("$(F $videoTime) overlay@pointer x $x, overlay@pointer y $y;"); $previous = "$x,$y" }
            }
            $commandFile = Join-Path $work "pointer-$index.txt"
            [IO.File]::WriteAllText($commandFile, $commands.ToString(), [Text.UTF8Encoding]::new($false))
            $graph.Add("[$last]sendcmd=f='$(ConvertTo-FilterPath $commandFile)'[sd]")
            $graph.Add("[sd][2:v]overlay@pointer=x=$($first[0]):y=$($first[1]):shortest=1[pt]")
            $last = 'pt'
        }

        $graph.Add("[$last]scale=$($Width):$($outHeight):flags=lanczos[sc]"); $last = 'sc'
        $captions = @(if (-not $NoCaptions) { $log | Where-Object type -eq 'caption' })
        $captionY = [int]($CaptionCenterY * $Width / $rw)
        $fontSize = $CaptionFontSize * $Width / 1600
        for ($i = 0; $i -lt $captions.Count; $i++) {
            $s = [Math]::Max(0, [double]$captions[$i].t + $offset - $trimStart)
            $e = if ($i + 1 -lt $captions.Count) { [double]$captions[$i + 1].t + $offset - $trimStart - 0.12 } else { $duration }
            $textFile = Join-Path $work "caption-$index-$i.txt"
            [IO.File]::WriteAllText($textFile, [string]$captions[$i].text, [Text.UTF8Encoding]::new($false))
            # Segoe UI has no dingbats; a caption with a symbol such as the delivery marker uses Segoe UI Symbol.
            $font = if ([string]$captions[$i].text -match '[\u2600-\u27BF]') { $SymbolFontFile } else { $FontFile }
            $alpha = "if(lt(t,$(F $s)+0.3),(t-$(F $s))/0.3,if(gt(t,$(F $e)-0.3),($(F $e)-t)/0.3,1))"
            $graph.Add("[$last]drawtext=fontfile='$($font.Replace(':', '\:'))':textfile='$(ConvertTo-FilterPath $textFile)':" +
                "fontsize=$(F $fontSize):fontcolor=white:box=1:boxcolor=0x0B1220@0.80:boxborderw=$([int](16 * $Width / 1600)):" +
                "x=(w-text_w)/2:y='$captionY-text_h/2+6*(1-min(1,(t-$(F $s))/0.3))':alpha='$alpha':enable='between(t,$(F $s),$(F $e))'[c$i]")
            $last = "c$i"
        }
        $graph.Add("[$last]format=rgb24[out]")
        $graphFile = Join-Path $work "graph-$index.txt"
        [IO.File]::WriteAllText($graphFile, ($graph -join ";`n"), [Text.UTF8Encoding]::new($false))
        $segmentPath = Join-Path $work "segment-$index.mkv"
        $pointerInput = if ($drawPointer) { @('-loop', '1', '-i', $pointerPath) } else { @() }
        & $Ffmpeg -hide_banner -loglevel error -y -i $recording -framerate 25 -i (Join-Path $ringDirectory '%02d.png') @pointerInput `
            -filter_complex_script $graphFile -map '[out]' -c:v ffv1 $segmentPath
        if ($LASTEXITCODE -ne 0) { throw "Rendering the segment '$stem' failed." }
        $rendered += [pscustomobject]@{ Path = $segmentPath; Duration = $duration }
    }

    # Crossfades between segments, then one palette for the whole GIF.
    $inputs = @(); foreach ($segment in $rendered) { $inputs += @('-i', $segment.Path) }
    $chain = [System.Collections.Generic.List[string]]::new()
    $last = '0:v'; $elapsed = $rendered[0].Duration
    for ($i = 1; $i -lt $rendered.Count; $i++) {
        $fadeAt = $elapsed - $CrossfadeSeconds
        $chain.Add("[$last][$($i):v]xfade=transition=fade:duration=$(F $CrossfadeSeconds):offset=$(F $fadeAt)[x$i]")
        $last = "x$i"; $elapsed = $fadeAt + $rendered[$i].Duration
    }
    $chain.Add("[$last]split[g1][g2]")
    $chain.Add('[g1]palettegen=max_colors=256:stats_mode=full[palette]')
    $chain.Add('[g2][palette]paletteuse=dither=sierra2_4a:diff_mode=rectangle[gif]')
    $chainFile = Join-Path $work 'join.txt'
    [IO.File]::WriteAllText($chainFile, ($chain -join ";`n"), [Text.UTF8Encoding]::new($false))
    & $Ffmpeg -hide_banner -loglevel error -y @inputs -filter_complex_script $chainFile -map '[gif]' -loop 0 $Output
    if ($LASTEXITCODE -ne 0) { throw 'GIF encoding failed.' }
    if ($Gifsicle) {
        $optimized = "$Output.optimized.gif"
        $options = @('-O3', '--no-warnings'); if ($Lossy -gt 0) { $options += "--lossy=$Lossy" }
        & $Gifsicle @options $Output -o $optimized
        if ($LASTEXITCODE -eq 0 -and (Get-Item $optimized).Length -lt (Get-Item $Output).Length) { Move-Item $optimized $Output -Force }
        else { Remove-Item $optimized -ErrorAction SilentlyContinue }
    }
    "{0}: {1:N1} MB, {2} px wide, {3} fps, {4:N1} s" -f (Split-Path $Output -Leaf), ((Get-Item $Output).Length / 1MB), $Width, $Fps, $elapsed
}
finally {
    Remove-Item $work -Recurse -Force -ErrorAction SilentlyContinue
}
