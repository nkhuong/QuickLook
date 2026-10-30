# QuickLook VideoSkim v4 - restore player keyboard focus after N/P/Delete navigation.
# Requires SmoothDelete-Navigation-v3 applied to local source.
# Run from the folder containing QuickLook.slnx.
$ErrorActionPreference = 'Stop'
if (-not (Test-Path '.\QuickLook.slnx')) {
    throw 'Run from the QuickLook source folder containing QuickLook.slnx.'
}

$path = 'QuickLook\ViewWindowManager.cs'
if (-not (Test-Path $path)) { throw ('Missing file: ' + $path) }
$full = (Resolve-Path $path).Path
$raw = [System.IO.File]::ReadAllText($full)
$crlf = $raw.Contains("`r`n")
$code = $raw.Replace("`r`n", "`n")

function ReplaceExactlyOnce([string]$text, [string]$old, [string]$new, [string]$label) {
    $count = ([regex]::Matches($text, [regex]::Escape($old))).Count
    if ($count -ne 1) {
        throw ('Expected 1 code match for ' + $label + '; found ' + $count + '. No files changed.')
    }
    return $text.Replace($old, $new)
}

if ($code.Contains('private void RestoreVideoPlayerFocus(')) {
    throw 'Focus fix v4 is already installed. Nothing changed.'
}
if (-not $code.Contains('public void PreviewAdjacentVideo(int direction)') -or
    -not $code.Contains('public async void DeleteCurrentVideoAndPreviewNext()')) {
    throw 'The v3 navigation/delete code is missing. Install v3 first. Nothing changed.'
}

$navigationBefore = @'
            if (!string.IsNullOrEmpty(next) && File.Exists(next))
                InvokePreview(next); // Reuses the existing ViewerWindow; no Close/Show.
'@
$navigationBefore = $navigationBefore.TrimEnd()
$navigationAfter = @'
            if (!string.IsNullOrEmpty(next) && File.Exists(next))
            {
                InvokePreview(next); // Reuse the current ViewerWindow.
                RestoreVideoPlayerFocus(next);
            }
'@
$navigationAfter = $navigationAfter.TrimEnd()
$code = ReplaceExactlyOnce $code $navigationBefore $navigationAfter 'N/P navigation'

$deleteBefore = '                InvokePreview(next); // Same ViewerWindow as normal Next; unloads old video.'
$deleteAfter = @'
                InvokePreview(next); // Same ViewerWindow as normal Next; unloads old video.
                RestoreVideoPlayerFocus(next);
'@
$deleteAfter = $deleteAfter.TrimEnd()
$code = ReplaceExactlyOnce $code $deleteBefore $deleteAfter 'Delete navigation'

$anchor = '    private static string FindAdjacentVideoPath(string current, int direction)'
$focusMethod = @'
    // The original QuickLook UnloadPlugin() clears WPF keyboard focus.
    // Once the replacement ViewerPanel has been created and laid out,
    // return focus to it so N / P / Delete work repeatedly without a click.
    // Call this ONLY for player-initiated navigation (not Explorer-driven preview).
    private void RestoreVideoPlayerFocus(string expectedPath)
    {
        var window = _viewerWindow;
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (!window.IsVisible || !ReferenceEquals(window, _viewerWindow) ||
                !string.Equals(_invokedPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (window.ContextObject.ViewerContent is System.Windows.FrameworkElement panel)
            {
                window.Activate();
                panel.Focus();
                System.Windows.Input.Keyboard.Focus(panel);
            }
        }), System.Windows.Threading.DispatcherPriority.ContextIdle);
    }

'@
$code = ReplaceExactlyOnce $code $anchor ($focusMethod + $anchor) 'focus helper insertion'

# Back up the original before modifying.
$backupDir = Join-Path $env:TEMP ('QuickLook-VideoSkim-focus-v4-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
$backupFile = Join-Path $backupDir 'ViewWindowManager.cs'
Copy-Item -LiteralPath $full -Destination $backupFile

if ($crlf) { $code = $code.Replace("`n", "`r`n") }
$utf8 = New-Object System.Text.UTF8Encoding($false)
[System.IO.File]::WriteAllText($full, $code, $utf8)
Write-Host 'SUCCESS: player focus restores after N/P/Delete.' -ForegroundColor Green
Write-Host ('Backup: ' + $backupFile)
Write-Host 'Next: git diff --check; git diff -- QuickLook/ViewWindowManager.cs'
