param(
    [string]$OutputDirectory,
    [string]$SettingsPath,
    [string]$WindowTitle,
    [ValidateRange(1, 1000)][int]$Limit = 1000,
    [ValidateRange(50, 1000)][int]$PollMilliseconds = 100,
    [ValidateRange(100, 2000)][int]$SettleMilliseconds = 300,
    [switch]$SelfTest
)

# Standalone Windows PowerShell 5.1 script. No OCR, Python, or .NET SDK needed.
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSEdition -eq 'Core') { throw 'Use Start-MapleMessageCapture.cmd or run this script with Windows powershell.exe (5.1), not pwsh.exe.' }
Add-Type -AssemblyName System.Drawing
if (-not ('MapleMessageCapture.Native' -as [type])) {
    Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace MapleMessageCapture
{
    public sealed class Signature
    {
        public int Width, Height, Ink;
        public byte[] Pixels;
        public string Hash;

        public Signature(int width, int height, byte[] pixels)
        {
            Width = width; Height = height; Pixels = pixels;
            foreach (byte p in pixels) Ink += p;
            byte[] data = new byte[pixels.Length + 8];
            Buffer.BlockCopy(BitConverter.GetBytes(width), 0, data, 0, 4);
            Buffer.BlockCopy(BitConverter.GetBytes(height), 0, data, 4, 4);
            Buffer.BlockCopy(pixels, 0, data, 8, pixels.Length);
            using (var sha = SHA256.Create())
                Hash = BitConverter.ToString(sha.ComputeHash(data)).Replace("-", "").ToLowerInvariant();
        }

        public static Signature FromBitmap(Bitmap image)
        {
            // The compact bar has a flat background. Comparing foreground shapes
            // avoids treating a small background/color change as a new message.
            var red = new List<int>(); var green = new List<int>(); var blue = new List<int>();
            for (int x = 0; x < image.Width; x += 4)
            {
                foreach (int y in new int[] { 0, image.Height - 1 })
                {
                    var c = image.GetPixel(x, y); red.Add(c.R); green.Add(c.G); blue.Add(c.B);
                }
            }
            red.Sort(); green.Sort(); blue.Sort();
            int r = red[red.Count / 2], g = green[green.Count / 2], b = blue[blue.Count / 2];
            byte[] pixels = new byte[image.Width * image.Height];
            for (int y = 0; y < image.Height; y++)
            for (int x = 0; x < image.Width; x++)
            {
                var c = image.GetPixel(x, y);
                int distance = Math.Max(Math.Abs(c.R - r), Math.Max(Math.Abs(c.G - g), Math.Abs(c.B - b)));
                pixels[y * image.Width + x] = (byte)(distance >= 30 ? 1 : 0);
            }
            return new Signature(image.Width, image.Height, pixels);
        }

        public static bool Same(Signature a, Signature b)
        {
            if (a == null || b == null || a.Width != b.Width || a.Height != b.Height) return false;
            int difference = 0;
            for (int i = 0; i < a.Pixels.Length; i++)
                if (a.Pixels[i] != b.Pixels[i] && ++difference > 2) return false;
            return true;
        }
    }

    public sealed class Watcher
    {
        private Signature pending, committed;
        private long since;
        private readonly int settle;
        private readonly HashSet<string> seen = new HashSet<string>(StringComparer.Ordinal);
        public Watcher(int settleMilliseconds) { settle = settleMilliseconds; }
        public void Remember(string hash) { if (!String.IsNullOrEmpty(hash)) seen.Add(hash); }
        public void Pause() { pending = null; }
        public bool Observe(Signature current, long now)
        {
            if (!Signature.Same(pending, current)) { pending = current; since = now; return false; }
            return now - since >= settle && current.Ink >= 20 &&
                !Signature.Same(committed, current) && !seen.Contains(current.Hash);
        }
        public bool MatchesPending(Signature current) { return Signature.Same(pending, current); }
        public void Commit(Signature current) { committed = current; seen.Add(current.Hash); }
    }

    public static class Native
    {
        [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
        private delegate bool EnumProc(IntPtr window, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, IntPtr parameter);
        [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
        [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out Rect rectangle);
        [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr window, ref Point point);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr window, StringBuilder title, int length);
        [DllImport("user32.dll")] private static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);
        [DllImport("user32.dll")] private static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

        public static void EnableDpi()
        {
            try { SetThreadDpiAwarenessContext(new IntPtr(-4)); }
            catch (EntryPointNotFoundException) { SetProcessDPIAware(); }
        }
        public static IntPtr FindWindow(string title)
        {
            IntPtr result = IntPtr.Zero;
            EnumWindows(delegate(IntPtr window, IntPtr parameter)
            {
                if (!IsWindowVisible(window)) return true;
                var text = new StringBuilder(512); GetWindowText(window, text, text.Capacity);
                if (!String.Equals(text.ToString(), title, StringComparison.OrdinalIgnoreCase)) return true;
                Rect r;
                if (GetClientRect(window, out r) && r.Right > 200 && r.Bottom > 150)
                { result = window; return false; }
                return true;
            }, IntPtr.Zero);
            return result;
        }
        public static Rectangle ReadyArea(IntPtr window)
        {
            Rect client; var origin = new Point();
            if (window == IntPtr.Zero || IsIconic(window) || GetForegroundWindow() != window ||
                !GetClientRect(window, out client) || client.Right < 200 || client.Bottom < 150 ||
                !ClientToScreen(window, ref origin)) return Rectangle.Empty;
            var area = new Rectangle(origin.X, origin.Y, client.Right, client.Bottom);
            var desktop = new Rectangle(GetSystemMetrics(76), GetSystemMetrics(77), GetSystemMetrics(78), GetSystemMetrics(79));
            if (!desktop.Contains(area)) return Rectangle.Empty;
            bool covered = false;
            EnumWindows(delegate(IntPtr other, IntPtr parameter)
            {
                if (other == window) return false;
                Rect r;
                if (IsWindowVisible(other) && !IsIconic(other) && GetWindowRect(other, out r) &&
                    area.IntersectsWith(Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom)))
                { covered = true; return false; }
                return true;
            }, IntPtr.Zero);
            return covered ? Rectangle.Empty : area;
        }
        public static Bitmap Capture(Rectangle area)
        {
            var image = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            try
            {
                using (var graphics = Graphics.FromImage(image))
                    graphics.CopyFromScreen(area.Location, System.Drawing.Point.Empty, area.Size, CopyPixelOperation.SourceCopy);
                return image;
            }
            catch { image.Dispose(); throw; }
        }
    }
}
'@
}

function Get-ImageCount([string]$Directory) {
    if (!(Test-Path -LiteralPath $Directory)) { return 0 }
    return @(Get-ChildItem -LiteralPath $Directory -File | Where-Object { $_.Extension -match '^\.(png|jpe?g|bmp)$' }).Count
}

function Get-WatchRectangle($Area, $Region) {
    $x = [int][Math]::Round($Region.X * $Area.Width)
    $y = [int][Math]::Round($Region.Y * $Area.Height)
    $width = [Math]::Min([int][Math]::Round($Region.Width * $Area.Width), $Area.Width - $x)
    $height = [Math]::Min([int][Math]::Round($Region.Height * $Area.Height), $Area.Height - $y)
    if ($width -lt 40 -or $height -lt 12) { throw 'Chat region is too small. Check Setup in MapleTracker.' }
    # Exclude the bar's borders; changes in the actual text remain at native resolution.
    $leftInset = [Math]::Min(10, [int][Math]::Floor($width / 8))
    $topInset = [Math]::Min(6, [int][Math]::Floor($height / 4))
    return [System.Drawing.Rectangle]::new($x + $leftInset, $y + $topInset, $width - $leftInset - 2, $height - $topInset * 2)
}

if ($SelfTest) {
    $checks = 0
    function Assert-Capture($Condition, [string]$Message) {
        if (!$Condition) { throw "FAIL: $Message" }
        $script:checks++
        Write-Host "PASS: $Message"
    }
    $a = New-Object byte[] 300
    $b = New-Object byte[] 300
    for ($i = 30; $i -lt 70; $i++) { $a[$i] = 1; $b[$i] = 1 }
    $b[75] = 1; $b[76] = 1; $b[77] = 1; $b[78] = 1
    $sa = [MapleMessageCapture.Signature]::new(30, 10, $a)
    $sb = [MapleMessageCapture.Signature]::new(30, 10, $b)
    $watcher = [MapleMessageCapture.Watcher]::new(300)
    Assert-Capture (!$watcher.Observe($sa, 0)) 'first observation waits for stable text'
    Assert-Capture (!$watcher.Observe($sb, 100)) 'a transition restarts the settle interval'
    Assert-Capture (!$watcher.Observe($sb, 399)) 'incomplete settle interval cannot capture'
    Assert-Capture ($watcher.Observe($sb, 400)) 'stable new text becomes capture-ready'
    $watcher.Commit($sb)
    Assert-Capture (!$watcher.Observe($sb, 60000)) 'unchanged text never repeats, even after a minute'
    Assert-Capture (!$watcher.Observe($sa, 60100)) 'a small four-pixel numeric change starts a new candidate'
    Assert-Capture ($watcher.Observe($sa, 60400)) 'a stable small numeric change is retained'
    $watcher.Commit($sa)
    $watcher.Observe($sb, 61000) | Out-Null
    Assert-Capture (!$watcher.Observe($sb, 61400)) 'an exact previously captured advertisement is skipped'
    $blank = [MapleMessageCapture.Signature]::new(30, 10, (New-Object byte[] 300))
    $watcher.Observe($blank, 62000) | Out-Null
    Assert-Capture (!$watcher.Observe($blank, 63000)) 'a blank bar is not captured'
    $noise = [byte[]]$a.Clone(); $noise[100] = 1
    Assert-Capture ([MapleMessageCapture.Signature]::Same($sa, [MapleMessageCapture.Signature]::new(30, 10, $noise))) 'one-pixel noise is ignored'
    $fresh = [MapleMessageCapture.Watcher]::new(300)
    $fresh.Observe($sa, 0) | Out-Null; $fresh.Pause()
    Assert-Capture (!$fresh.Observe($sa, 500)) 'returning from a paused window starts a fresh settle interval'
    Assert-Capture ($fresh.Observe($sa, 800)) 'stable text can capture after returning to the game'
    $fresh.Remember($sa.Hash)
    Assert-Capture (!$fresh.Observe($sa, 1000)) 'a remembered journal fingerprint suppresses restart duplicates'
    $area = [System.Drawing.Rectangle]::new(0, 0, 1920, 1080)
    $rect = Get-WatchRectangle $area ([pscustomobject]@{ X=.21; Y=.916; Width=.385; Height=.035 })
    Assert-Capture ($rect.X -eq 413 -and $rect.Y -eq 995 -and $rect.Width -eq 727 -and $rect.Height -eq 26) 'saved normalized calibration maps to native-pixel text bounds'
    $images = @()
    try {
        foreach ($background in @([System.Drawing.Color]::Gray, [System.Drawing.Color]::FromArgb(160,160,160))) {
            $image = [System.Drawing.Bitmap]::new(120, 24); $images += $image
            $g = [System.Drawing.Graphics]::FromImage($image)
            try { $g.Clear($background); $g.FillRectangle([System.Drawing.Brushes]::White, 8, 6, 50, 10) } finally { $g.Dispose() }
        }
        Assert-Capture ([MapleMessageCapture.Signature]::Same([MapleMessageCapture.Signature]::FromBitmap($images[0]), [MapleMessageCapture.Signature]::FromBitmap($images[1]))) 'background brightness alone does not create a new message'
    } finally { foreach ($image in $images) { $image.Dispose() } }
    Write-Host "$checks capture checks passed. No screen was captured or Desktop file written."
    return
}

if (!$OutputDirectory) {
    $desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::DesktopDirectory)
    if (!$desktop) { throw 'Could not locate your Desktop. Pass -OutputDirectory with a folder path.' }
    $OutputDirectory = Join-Path $desktop 'screenshotsmaple'
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
if (!$SettingsPath) {
    $localData = [Environment]::GetFolderPath([Environment+SpecialFolder]::LocalApplicationData)
    foreach ($profile in @('MapleTracker-OCR-preview', 'MapleTracker')) {
        $path = Join-Path (Join-Path $localData $profile) 'settings.json'
        if (Test-Path -LiteralPath $path) { $SettingsPath = $path; break }
    }
}
$region = [pscustomobject]@{ X=.210; Y=.916; Width=.385; Height=.035 }
if ($SettingsPath) {
    $settings = Get-Content -LiteralPath $SettingsPath -Raw | ConvertFrom-Json
    if ($settings.ChatHistoryOnly) { throw 'This collector needs the compact white chat bar. Select it in MapleTracker Setup first.' }
    if ($settings.Chat) { $region = $settings.Chat }
    if (!$WindowTitle -and $settings.WindowTitle) { $WindowTitle = $settings.WindowTitle }
}
if (!$WindowTitle) { $WindowTitle = 'MapleStory' }
foreach ($part in @('X','Y','Width','Height')) {
    $value = [double]$region.$part
    if ([double]::IsNaN($value) -or [double]::IsInfinity($value)) { throw 'Invalid chat calibration.' }
}
if ($region.X -lt 0 -or $region.Y -lt 0 -or $region.Width -le 0 -or $region.Height -le 0 -or
    $region.X + $region.Width -gt 1.00001 -or $region.Y + $region.Height -gt 1.00001) { throw 'Invalid chat calibration.' }

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$count = Get-ImageCount $OutputDirectory
if ($count -ge $Limit) { Write-Host "Already $count screenshots in $OutputDirectory. Limit reached; nothing captured."; return }
$lock = $null
try {
    try { $lock = [IO.File]::Open((Join-Path $OutputDirectory 'capture.lock'), 'OpenOrCreate', 'ReadWrite', 'None') }
    catch [IO.IOException] { throw 'Another collector may be using this folder. Close it before starting another.' }
    $watcher = [MapleMessageCapture.Watcher]::new($SettleMilliseconds)
    $journal = Join-Path $OutputDirectory 'captures.jsonl'
    if (Test-Path -LiteralPath $journal) {
        foreach ($line in [IO.File]::ReadLines($journal)) {
            try {
                $entry = $line | ConvertFrom-Json
                if ($entry.File -and [IO.Path]::GetFileName([string]$entry.File) -eq $entry.File -and
                    (Test-Path -LiteralPath (Join-Path $OutputDirectory $entry.File))) { $watcher.Remember([string]$entry.TextShapeSha256) }
            } catch { } # A partial final journal line must not invalidate existing screenshots.
        }
    }
    [MapleMessageCapture.Native]::EnableDpi()
    Write-Host "Maple message screenshots: $count / $Limit" -ForegroundColor Cyan
    Write-Host "Saving full game-client PNGs to: $OutputDirectory"
    Write-Host "Watching: $WindowTitle | chat region $($region.X), $($region.Y), $($region.Width), $($region.Height)"
    Write-Host 'Focus the game and leave its compact white chat bar visible. Avoid typing during collection.'
    Write-Host 'Minimized, unfocused, or covered game windows pause capture. Ctrl+C in this window stops early.'
    $clock = [Diagnostics.Stopwatch]::StartNew()
    $previousState = ''
    $game = [IntPtr]::Zero
    $nextSearch = 0L
    while ($count -lt $Limit) {
        if ($clock.ElapsedMilliseconds -ge $nextSearch) {
            $game = [MapleMessageCapture.Native]::FindWindow($WindowTitle)
            $nextSearch = $clock.ElapsedMilliseconds + 2000
        }
        $area = [MapleMessageCapture.Native]::ReadyArea($game)
        $state = if ($game -eq [IntPtr]::Zero) { 'Waiting for the game window...' } elseif ($area.IsEmpty) { 'Paused - focus the game and keep it uncovered.' } else { 'Watching for a stable new message...' }
        if ($state -ne $previousState) { Write-Host $state; $previousState = $state }
        if ($area.IsEmpty) { $watcher.Pause(); Start-Sleep -Milliseconds $PollMilliseconds; continue }
        $watch = Get-WatchRectangle $area $region
        $screenWatch = [System.Drawing.Rectangle]::new($area.X + $watch.X, $area.Y + $watch.Y, $watch.Width, $watch.Height)
        $sample = [MapleMessageCapture.Native]::Capture($screenWatch)
        try { $signature = [MapleMessageCapture.Signature]::FromBitmap($sample) } finally { $sample.Dispose() }
        if ($watcher.Observe($signature, $clock.ElapsedMilliseconds)) {
            # The chat may change between the small probe and the full screenshot.
            # Verify the saved pixels match the settled candidate before committing.
            $confirmedArea = [MapleMessageCapture.Native]::ReadyArea($game)
            if ($confirmedArea -ne $area) { $watcher.Pause(); continue }
            $frame = [MapleMessageCapture.Native]::Capture($area)
            try {
                if ([MapleMessageCapture.Native]::ReadyArea($game) -ne $area) { $watcher.Pause(); continue }
                $crop = $frame.Clone($watch, [System.Drawing.Imaging.PixelFormat]::Format24bppRgb)
                try { $confirmed = [MapleMessageCapture.Signature]::FromBitmap($crop) } finally { $crop.Dispose() }
                if (!$watcher.MatchesPending($confirmed)) { $watcher.Observe($confirmed, $clock.ElapsedMilliseconds) | Out-Null; continue }
                # Count existing images too: restarting never adds another 1,000 on top.
                $count = Get-ImageCount $OutputDirectory
                if ($count -ge $Limit) { break }
                $time = [DateTimeOffset]::Now
                $name = 'Maple_{0:D4}_{1}_{2}.png' -f ($count + 1), $time.ToString('yyyyMMdd_HHmmss_fff'), ([Guid]::NewGuid().ToString('N').Substring(0,6))
                $file = Join-Path $OutputDirectory $name
                $temporary = $file + '.pending'
                try { $frame.Save($temporary, [System.Drawing.Imaging.ImageFormat]::Png); [IO.File]::Move($temporary, $file) }
                finally { if (Test-Path -LiteralPath $temporary) { [IO.File]::Delete($temporary) } }
                $entry = [ordered]@{ File=$name; Time=$time.ToString('o'); WindowTitle=$WindowTitle; Width=$frame.Width; Height=$frame.Height;
                    Chat=[ordered]@{ X=$region.X; Y=$region.Y; Width=$region.Width; Height=$region.Height };
                    WatchPixels=[ordered]@{ X=$watch.X; Y=$watch.Y; Width=$watch.Width; Height=$watch.Height };
                    TextShapeSha256=$confirmed.Hash; ImageSha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant() }
                [IO.File]::AppendAllText($journal, (($entry | ConvertTo-Json -Depth 4 -Compress) + [Environment]::NewLine), [Text.UTF8Encoding]::new($false))
                $watcher.Commit($confirmed)
                $count++
                Write-Host ("Saved {0}/{1}: {2}" -f $count, $Limit, $name) -ForegroundColor Green
            } finally { $frame.Dispose() }
        }
        Start-Sleep -Milliseconds $PollMilliseconds
    }
    Write-Host "Finished: $count screenshots. $OutputDirectory" -ForegroundColor Cyan
} finally { if ($lock) { $lock.Dispose() } }
