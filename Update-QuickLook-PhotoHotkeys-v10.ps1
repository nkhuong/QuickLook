# QuickLook VideoSkim v10: Photos use the SAME working v6-v9 global keyboard hook.
# N / P / Delete for supported photos. + / - zoom; 0 zoom-to-fit.
# Keeps video behavior and uses the existing Name/Size/Date sorting configuration.
# Run from the repository root containing QuickLook.slnx, AFTER v8 (and optionally v9).
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath '.\QuickLook.slnx')) {
    throw 'Run PowerShell from your QuickLook source folder, next to QuickLook.slnx.'
}

$dispatcherPath = 'QuickLook\KeystrokeDispatcher.cs'
$managerPath    = 'QuickLook\ViewWindowManager.cs'
$imagePath      = 'QuickLook.Plugin\QuickLook.Plugin.ImageViewer\ImagePanel.xaml.cs'
$interfacePath  = 'QuickLook.Common\Plugin\IPreviewHotkeyHandler.cs'
$paths = @($dispatcherPath, $managerPath, $imagePath)
$original = @{}
$updated  = @{}
$hadCRLF  = @{}

foreach ($p in $paths) {
    if (-not (Test-Path -LiteralPath $p)) { throw ('Missing source file: ' + $p) }
    $raw = [IO.File]::ReadAllText((Resolve-Path -LiteralPath $p).Path)
    $original[$p] = $raw
    $hadCRLF[$p] = $raw.Contains("`r`n")
    $updated[$p] = $raw.Replace("`r`n", "`n")
}
if (-not (Test-Path -LiteralPath $interfacePath)) {
    throw 'Missing v7 interface IPreviewHotkeyHandler.cs. Install v7-v9 first. No files changed.'
}

function Replace-Exactly([string]$source, [string]$before, [string]$after,
                         [int]$expected, [string]$label) {
    $count = ([regex]::Matches($source, [regex]::Escape($before))).Count
    if ($count -ne $expected) {
        throw ('Cannot locate ' + $label + ': expected ' + $expected +
            ' matching blocks, found ' + $count + '. No files changed.')
    }
    return $source.Replace($before, $after)
}

# Guard against applying to a branch/version other than local v8/v9.
$d = $updated[$dispatcherPath]
$m = $updated[$managerPath]
$i = $updated[$imagePath]
if ($d.Contains('imageZoomShortcut') -or $m.Contains('FindAdjacentImageSkimPath(') -or
    $i.Contains('IPreviewHotkeyHandler')) {
    throw 'Photo hotkeys v10 appear to be installed already. No files changed.'
}
if (-not $d.Contains('private bool TryHandleVideoSkimGlobalKey(') -or
    -not $d.Contains('private bool TryHandleVideoSkimPlaybackKey(') -or
    -not $m.Contains('private string FindAdjacentVideoPath(') -or
    -not $m.Contains('VideoSkimNavigationSortCache') -or
    -not $m.Contains('internal bool IsVideoSkimPreviewForeground()') -or
    -not $m.Contains('internal void DispatchVideoSkimPlaybackHotkey(') -or
    -not $i.Contains('public void DoZoomToFit()') -or
    -not $i.Contains('public void Zoom(double factor,')) {
    throw 'Expected local v8/v9 code is missing or has been edited. Nothing was changed.'
}

# --- A. Extend existing global hook, not the old WPF focus-dependent handlers. ---
$navStart = $d.IndexOf('    private bool TryHandleVideoSkimGlobalKey(', [StringComparison]::Ordinal)
$playStart = $d.IndexOf('    private bool TryHandleVideoSkimPlaybackKey(', [StringComparison]::Ordinal)
$playEnd = $d.IndexOf('    private void InvokeRoutine(', $playStart, [StringComparison]::Ordinal)
if ($navStart -lt 0 -or $playStart -le $navStart -or $playEnd -le $playStart) {
    throw 'Unexpected global keyboard handler layout. No files changed.'
}
$nav = $d.Substring($navStart, $playStart - $navStart)
$play = $d.Substring($playStart, $playEnd - $playStart)

# N/P/Delete: only the foreground QuickLook photo/video HWND; never Explorer.
$nav = Replace-Exactly $nav `
    'if (!manager.IsVideoSkimPreviewForeground())' `
    'if (!manager.IsVideoSkimPreviewForeground() && !manager.IsImageSkimPreviewForeground())' `
    2 'N/P/Delete foreground checks'

# +/-/0: videos retain their speed behavior; photos use the same actions for zoom.
# No Shift/Ctrl+Arrow or Home/End remapping for photos.
$playOld = @'
            var manager = ViewWindowManager.GetInstance();
            if (!manager.IsVideoSkimPreviewForeground())
                return false;

            e.Handled = true;
'@
$playNew = @'
            var manager = ViewWindowManager.GetInstance();
            bool imageZoomShortcut = action.Value == PreviewHotkey.SpeedUp ||
                                     action.Value == PreviewHotkey.SpeedDown ||
                                     action.Value == PreviewHotkey.SpeedReset;
            if (!manager.IsVideoSkimPreviewForeground() &&
                !(imageZoomShortcut && manager.CanZoomImageSkimPreview()))
                return false;

            e.Handled = true;
'@
$play = Replace-Exactly $play $playOld.TrimEnd() $playNew.TrimEnd() 1 'photo zoom key guard'
$play = Replace-Exactly $play `
    'if (manager.IsVideoSkimPreviewForeground())' `
    'if (manager.IsVideoSkimPreviewForeground() || (imageZoomShortcut && manager.CanZoomImageSkimPreview()))' `
    1 'photo zoom async guard'

$d = $d.Substring(0, $navStart) + $nav + $play + $d.Substring($playEnd)
$updated[$dispatcherPath] = $d

# --- B. ViewWindowManager: keep videos unchanged, add photo-only neighbors. ---
$managerMethods = @'
    // Photos use the existing global hook but are never mixed with video files.
    // Confirm the ACTUAL preview HWND is foreground (not Explorer / dialog).
    internal bool IsImageSkimPreviewForeground()
    {
        var window = _viewerWindow;
        if (window == null || !window.IsVisible ||
            window.Plugin?.GetType().FullName != "QuickLook.Plugin.ImageViewer.Plugin")
            return false;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero &&
               hwnd == QuickLook.Common.NativeMethods.User32.GetForegroundWindow();
    }

    // ImageViewer may use an embedded web panel for SVG or other formats.
    // Only consume zoom shortcuts when a native ImagePanel can handle them.
    internal bool CanZoomImageSkimPreview()
    {
        return IsImageSkimPreviewForeground() &&
               _viewerWindow.ContextObject.ViewerContent is IPreviewHotkeyHandler;
    }

'@
$m = Replace-Exactly $m `
    '    internal bool IsVideoSkimPreviewForeground()' `
    ($managerMethods + '    internal bool IsVideoSkimPreviewForeground()') `
    1 'image foreground guard insertion'

$oldRoute = @'
    internal void DispatchVideoSkimPlaybackHotkey(PreviewHotkey action)
    {
        if (!IsVideoSkimPreviewForeground())
            return;
'@
$newRoute = @'
    internal void DispatchVideoSkimPlaybackHotkey(PreviewHotkey action)
    {
        if (!IsVideoSkimPreviewForeground())
        {
            // For images only +/-/0 are meaningful, never video seeking.
            if (!CanZoomImageSkimPreview() ||
                (action != PreviewHotkey.SpeedUp && action != PreviewHotkey.SpeedDown &&
                 action != PreviewHotkey.SpeedReset))
                return;
        }
'@
$m = Replace-Exactly $m $oldRoute.TrimEnd() $newRoute.TrimEnd() 1 'shared playback dispatch guard'

$nextOld = @'
            var next = FindAdjacentVideoPath(_invokedPath, Math.Sign(direction));
'@
$nextNew = @'
            var next = _viewerWindow.Plugin?.GetType().FullName == "QuickLook.Plugin.ImageViewer.Plugin"
                ? FindAdjacentImageSkimPath(_invokedPath, Math.Sign(direction))
                : FindAdjacentVideoPath(_invokedPath, Math.Sign(direction));
'@
$m = Replace-Exactly $m $nextOld.TrimEnd() $nextNew.TrimEnd() 1 'N/P image navigation'

$deleteOld = @'
            var next = FindAdjacentVideoPath(current, 1)
                       ?? FindAdjacentVideoPath(current, -1);
'@
$deleteNew = @'
            var next = _viewerWindow.Plugin?.GetType().FullName == "QuickLook.Plugin.ImageViewer.Plugin"
                ? (FindAdjacentImageSkimPath(current, 1) ?? FindAdjacentImageSkimPath(current, -1))
                : (FindAdjacentVideoPath(current, 1) ?? FindAdjacentVideoPath(current, -1));
'@
$m = Replace-Exactly $m $deleteOld.TrimEnd() $deleteNew.TrimEnd() 1 'Delete next photo selection'

# Separate 5-second cache for photos. It follows the SAME Name / Size / Date
# Asc/Desc settings from the existing Video Settings UI. Metadata only, no image decodes.
$imageSortMethods = @'
    // Cached photo navigation. Mirrors the ImageViewer known-extension list;
    // videos are excluded so moving N/P stays within one media type.
    private readonly System.Collections.Generic.HashSet<string> _imageSkimExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".apng", ".ari", ".arw", ".avif", ".ani", ".bay", ".bmp",
            ".cap", ".cr2", ".cr3", ".crw", ".cur", ".clip",
            ".dcr", ".dcs", ".dds", ".dng", ".drf", ".dcm", ".dicom",
            ".eip", ".emf", ".erf", ".exr", ".excalidraw", ".fff",
            ".gfie", ".gfi", ".gif", ".hdr", ".heic", ".heif",
            ".ico", ".icon", ".icns", ".iiq", ".jfif", ".jp2",
            ".jpeg", ".jpg", ".jxl", ".j2k", ".jpf", ".jpx", ".jpm", ".jxr",
            ".k25", ".kdc", ".mdc", ".mef", ".mos", ".mrw", ".mj2", ".miff",
            ".nef", ".nrw", ".obm", ".orf", ".pbm", ".pcx", ".pef",
            ".pgm", ".png", ".pnm", ".ppm", ".psb", ".psd", ".ptx", ".pxn",
            ".qoi", ".r3d", ".raf", ".raw", ".rw2", ".rwl", ".rwz",
            ".sr2", ".srf", ".srw", ".svg", ".svgz", ".tga", ".tif",
            ".tiff", ".wdp", ".webp", ".wmf", ".x3f", ".xcf", ".xbm", ".xpm"
        };
    private string[] _imageSkimCachedFiles;
    private string _imageSkimCachedFolder;
    private string _imageSkimCachedSort;
    private bool _imageSkimCachedDescending;
    private DateTime _imageSkimCacheExpiresUtc = DateTime.MinValue;

    private void InvalidateImageSkimFileCache()
    {
        _imageSkimCacheExpiresUtc = DateTime.MinValue;
    }

    private string[] GetSortedImageSkimFiles(string folder)
    {
        const string domain = "QuickLook.Plugin.VideoViewer";
        var sort = SettingHelper.Get("VideoSortBy", "Name", domain);
        if (sort != "Size" && sort != "Date") sort = "Name";
        bool desc = SettingHelper.Get("VideoSortDescending", false, domain);
        if (_imageSkimCachedFiles != null &&
            string.Equals(folder, _imageSkimCachedFolder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(sort, _imageSkimCachedSort, StringComparison.Ordinal) &&
            desc == _imageSkimCachedDescending && DateTime.UtcNow < _imageSkimCacheExpiresUtc)
            return _imageSkimCachedFiles;

        var items = new DirectoryInfo(folder).EnumerateFiles()
            .Where(file => _imageSkimExtensions.Contains(file.Extension));
        System.Collections.Generic.IEnumerable<FileInfo> ordered;
        switch (sort)
        {
            case "Size":
                ordered = desc
                    ? items.OrderByDescending(file => file.Length)
                        .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(file => file.Length)
                        .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase);
                break;
            case "Date":
                ordered = desc
                    ? items.OrderByDescending(file => file.LastWriteTimeUtc)
                        .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(file => file.LastWriteTimeUtc)
                        .ThenBy(file => file.Name, StringComparer.OrdinalIgnoreCase);
                break;
            default:
                ordered = desc
                    ? items.OrderByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                    : items.OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase);
                break;
        }

        var paths = ordered.Select(file => file.FullName).ToArray();
        _imageSkimCachedFiles = paths;
        _imageSkimCachedFolder = folder;
        _imageSkimCachedSort = sort;
        _imageSkimCachedDescending = desc;
        _imageSkimCacheExpiresUtc = DateTime.UtcNow.AddSeconds(5);
        return paths;
    }

    private string FindAdjacentImageSkimPath(string current, int direction)
    {
        if (direction == 0 || string.IsNullOrWhiteSpace(current) || !File.Exists(current) ||
            !_imageSkimExtensions.Contains(Path.GetExtension(current)))
            return null;

        var folder = Path.GetDirectoryName(current);
        for (int pass = 0; pass < 2; pass++)
        {
            var files = GetSortedImageSkimFiles(folder);
            var index = Array.FindIndex(files,
                file => string.Equals(file, current, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                InvalidateImageSkimFileCache();
                continue;
            }

            bool foundMissing = false;
            for (int neighbor = index + Math.Sign(direction);
                 neighbor >= 0 && neighbor < files.Length;
                 neighbor += Math.Sign(direction))
            {
                if (File.Exists(files[neighbor]))
                    return files[neighbor];
                foundMissing = true;
            }
            if (!foundMissing) return null;
            if (pass == 0) InvalidateImageSkimFileCache();
        }
        return null;
    }

'@
$m = Replace-Exactly $m `
    '    // Switch preview first, then recycle the OLD video after its DirectShow handle' `
    ($imageSortMethods + '    // Switch preview first, then recycle the OLD video after its DirectShow handle') `
    1 'photo cached sorting insertion'

# Clear both caches after recycling; otherwise deleted photo remains in list up to 5sec.
$m = Replace-Exactly $m `
    '                    InvalidateVideoSkimFileCache();' `
    "                    InvalidateVideoSkimFileCache();`n                    InvalidateImageSkimFileCache();" `
    1 'photo cache invalidation after recycle'
$updated[$managerPath] = $m

# --- C. ImageViewer: implement the existing common hotkey interface. ---
$i = Replace-Exactly $i `
    'public partial class ImagePanel : UserControl, INotifyPropertyChanged, IDisposable' `
    'public partial class ImagePanel : UserControl, INotifyPropertyChanged, IDisposable, IPreviewHotkeyHandler' `
    1 'ImagePanel hotkey interface'
$photoHandler = @'
    // Global player shortcuts (only when this native photo panel is foreground).
    // Video +/- means speed; on photos the same keys zoom the image instead.
    public void HandlePreviewHotkey(PreviewHotkey action)
    {
        if (viewPanelImage?.Source == null)
            return;

        switch (action)
        {
            case PreviewHotkey.SpeedUp:
                Zoom(ZoomFactor * 1.2d);
                break;
            case PreviewHotkey.SpeedDown:
                Zoom(ZoomFactor / 1.2d);
                break;
            case PreviewHotkey.SpeedReset:
                DoZoomToFit();
                break;
        }
    }

'@
$i = Replace-Exactly $i `
    '    public void DoZoomToFit()' `
    ($photoHandler + '    public void DoZoomToFit()') `
    1 'photo zoom hotkey handler'
$updated[$imagePath] = $i

# --- D. Backups and writes only after ALL validation and edits succeed. ---
$backup = Join-Path $env:TEMP ('QuickLook-PhotoHotkeys-v10-' + (Get-Date -Format 'yyyyMMdd-HHmmss'))
New-Item -Path $backup -ItemType Directory -Force | Out-Null
foreach ($p in $paths) {
    $target = Join-Path $backup $p
    New-Item -ItemType Directory -Force -Path (Split-Path $target -Parent) | Out-Null
    [IO.File]::WriteAllText($target, $original[$p])
}
$utf8 = New-Object Text.UTF8Encoding($false)
foreach ($p in $paths) {
    $content = $updated[$p]
    if ($hadCRLF[$p]) { $content = $content.Replace("`n", "`r`n") }
    [IO.File]::WriteAllText((Resolve-Path -LiteralPath $p).Path, $content, $utf8)
}
Write-Host 'SUCCESS: Photo N/P/Delete and +/-/0 zoom hotkeys installed.' -ForegroundColor Green
Write-Host ('Backup: ' + $backup)
Write-Host 'Modified: QuickLook/KeystrokeDispatcher.cs, QuickLook/ViewWindowManager.cs, ImageViewer/ImagePanel.xaml.cs'
Write-Host 'Next: git diff --check; git diff --stat; build Release/x64 solution.'
Write-Host 'Test deletion with COPIES of photos first (Recycle Bin).' -ForegroundColor Yellow
