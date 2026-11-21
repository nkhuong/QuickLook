// Copyright © 2017-2026 QL-Win Contributors
//
// This file is part of QuickLook program.
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <http://www.gnu.org/licenses/>.

using QuickLook.Common.Helpers;
using Microsoft.VisualBasic.FileIO;
using System.Linq;
using System.Threading.Tasks;
using QuickLook.Common.Plugin;
using QuickLook.Helpers;
using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.ExceptionServices;
using System.Windows;

namespace QuickLook;

public class ViewWindowManager : IDisposable
{
    private static ViewWindowManager _instance;

    private string _invokedPath = string.Empty;
    private ViewerWindow _viewerWindow;
    private bool _deleteNextInProgress;
    private bool _renameSkimInProgress;

    internal ViewWindowManager()
    {
        InitNewViewerWindow();
    }

    public void Dispose()
    {
        StopFocusMonitor();
    }

    public void RunAndClosePreview()
    {
        if (!_viewerWindow.IsVisible)
            return;

        // if the current focus is in Desktop or explorer windows, just close the preview window and leave the task to System.
        var focus = NativeMethods.QuickLook.GetFocusedWindowType();
        if (focus != NativeMethods.QuickLook.FocusedWindowType.Invalid)
        {
            StopFocusMonitor();
            _viewerWindow.Close();
            return;
        }

        // if the focus is in the preview window, run it
        if (!WindowHelper.IsForegroundWindowBelongToSelf())
            return;

        StopFocusMonitor();
        _viewerWindow.RunAndClose();
    }

    public void ClosePreview()
    {
        if (!_viewerWindow.IsVisible)
            return;

        StopFocusMonitor();
        _viewerWindow.Close();
    }

    // Called only by VideoViewer when Delete is pressed in the focused preview.
    // Files are moved to Recycle Bin; the next video is chosen by NAME, not Explorer sort order.
    // Next / previous VIDEO by file name, regardless of Explorer focus.
    // This does not follow Explorer custom sorting (Date, Length, etc.).
    // This comparison is intentionally by HWND, not Keyboard.FocusedElement:
    // focus may move to a header button or be cleared during preview switching.
    // The UI thread calls this after the preview content is loaded.
    // Interface check means other plugins never receive video commands.
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

        if (_viewerWindow.ContextObject.ViewerContent is IPreviewHotkeyHandler handler)
            handler.HandlePreviewHotkey(action);
    }
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
    internal bool IsPdfSkimPreviewForeground()
    {
        var window = _viewerWindow;
        if (window == null || !window.IsVisible ||
            window.Plugin?.GetType().FullName != "QuickLook.Plugin.PDFViewer.Plugin")
            return false;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero &&
               hwnd == QuickLook.Common.NativeMethods.User32.GetForegroundWindow();
    }

    internal bool IsSkimMediaPreviewForeground()
    {
        return IsVideoSkimPreviewForeground() ||
               IsImageSkimPreviewForeground() || IsPdfSkimPreviewForeground();
    }
    internal bool IsVideoSkimPreviewForeground()
    {
        var window = _viewerWindow;
        if (window == null || !window.IsVisible ||
            window.Plugin?.GetType().FullName != "QuickLook.Plugin.VideoViewer.Plugin")
            return false;

        var hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        return hwnd != IntPtr.Zero &&
               hwnd == QuickLook.Common.NativeMethods.User32.GetForegroundWindow();
    }
    public void PreviewAdjacentVideo(int direction)
    {
        if (_renameSkimInProgress || !_viewerWindow.IsVisible || direction == 0)
            return;

        try
        {
            var type = _viewerWindow.Plugin?.GetType().FullName;
            var next = type == "QuickLook.Plugin.PDFViewer.Plugin"
                ? FindAdjacentPdfSkimPath(_invokedPath, Math.Sign(direction))
                : type == "QuickLook.Plugin.ImageViewer.Plugin"
                    ? FindAdjacentImageSkimPath(_invokedPath, Math.Sign(direction))
                    : FindAdjacentVideoPath(_invokedPath, Math.Sign(direction));
            if (!string.IsNullOrEmpty(next) && File.Exists(next))
            {
                InvokePreview(next); // Reuse the current ViewerWindow.
            }
        }
        catch (Exception ex)
        {
            ProcessHelper.WriteLog("VideoSkim: Could not navigate video: " + ex);
        }
    }

    // VideoSkimNavigationSortCache: no media files are opened for sorting.
    // Cache avoids re-enumerating directories on every N/P/Delete. Rebuild after
    // 5 seconds, changing folders or settings, or a successful recycle operation.
    private readonly System.Collections.Generic.HashSet<string> _videoSkimExtensions =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".wmv", ".webm",
            ".mpg", ".mpeg", ".ts", ".mts", ".m2ts", ".flv", ".3gp"
        };
    private string[] _videoSkimCachedFiles;
    private string _videoSkimCachedFolder;
    private string _videoSkimCachedSort;
    private bool _videoSkimCachedDescending;
    private DateTime _videoSkimCacheExpiresUtc = DateTime.MinValue;

    private void InvalidateVideoSkimFileCache()
    {
        _videoSkimCacheExpiresUtc = DateTime.MinValue;
    }

    private string[] GetSortedVideoSkimFiles(string folder)
    {
        const string domain = "QuickLook.Plugin.VideoViewer";
        var sort = SettingHelper.Get("VideoSortBy", "Name", domain);
        if (sort != "Size" && sort != "Date") sort = "Name";
        bool desc = SettingHelper.Get("VideoSortDescending", false, domain);
        if (_videoSkimCachedFiles != null &&
            string.Equals(folder, _videoSkimCachedFolder, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(sort, _videoSkimCachedSort, StringComparison.Ordinal) &&
            desc == _videoSkimCachedDescending && DateTime.UtcNow < _videoSkimCacheExpiresUtc)
            return _videoSkimCachedFiles;

        var items = new DirectoryInfo(folder).EnumerateFiles()
            .Where(file => _videoSkimExtensions.Contains(file.Extension));
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
        _videoSkimCachedFiles = paths;
        _videoSkimCachedFolder = folder;
        _videoSkimCachedSort = sort;
        _videoSkimCachedDescending = desc;
        _videoSkimCacheExpiresUtc = DateTime.UtcNow.AddSeconds(5);
        return paths;
    }

    private string FindAdjacentVideoPath(string current, int direction)
    {
        if (direction == 0 || string.IsNullOrWhiteSpace(current) || !File.Exists(current))
            return null;

        var folder = Path.GetDirectoryName(current);
        for (int pass = 0; pass < 2; pass++)
        {
            var files = GetSortedVideoSkimFiles(folder);
            var index = Array.FindIndex(files,
                file => string.Equals(file, current, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                InvalidateVideoSkimFileCache();
                continue; // File list may have changed since the previous preview.
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
            if (!foundMissing) return null; // At first/last video: no rescan.
            if (pass == 0) InvalidateVideoSkimFileCache();
        }
        return null;
    }
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
    // File-level PDF navigation. This never changes the PDF viewer's PAGE controls.
    // Metadata-only sort: Name, Size or Date Modified + Asc/Desc, cached for 5s.
    private string[] _pdfSkimCachedFiles;
    private string _pdfSkimCachedFolder;
    private string _pdfSkimCachedSort;
    private bool _pdfSkimCachedDescending;
    private DateTime _pdfSkimCacheExpiresUtc = DateTime.MinValue;

    private void InvalidatePdfSkimFileCache() => _pdfSkimCacheExpiresUtc = DateTime.MinValue;

    private string[] GetSortedPdfSkimFiles(string folder)
    {
        const string domain = "QuickLook.Plugin.VideoViewer";
        var sort = SettingHelper.Get("VideoSortBy", "Name", domain);
        if (sort != "Size" && sort != "Date") sort = "Name";
        bool desc = SettingHelper.Get("VideoSortDescending", false, domain);
        if (_pdfSkimCachedFiles != null &&
            string.Equals(folder, _pdfSkimCachedFolder, StringComparison.OrdinalIgnoreCase) &&
            sort == _pdfSkimCachedSort && desc == _pdfSkimCachedDescending &&
            DateTime.UtcNow < _pdfSkimCacheExpiresUtc)
            return _pdfSkimCachedFiles;

        var items = new DirectoryInfo(folder).EnumerateFiles()
            .Where(file => string.Equals(file.Extension, ".pdf", StringComparison.OrdinalIgnoreCase));
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

        _pdfSkimCachedFiles = ordered.Select(file => file.FullName).ToArray();
        _pdfSkimCachedFolder = folder;
        _pdfSkimCachedSort = sort;
        _pdfSkimCachedDescending = desc;
        _pdfSkimCacheExpiresUtc = DateTime.UtcNow.AddSeconds(5);
        return _pdfSkimCachedFiles;
    }

    private string FindAdjacentPdfSkimPath(string current, int direction)
    {
        if (direction == 0 || string.IsNullOrWhiteSpace(current) || !File.Exists(current) ||
            !string.Equals(Path.GetExtension(current), ".pdf", StringComparison.OrdinalIgnoreCase))
            return null;

        var folder = Path.GetDirectoryName(current);
        for (int pass = 0; pass < 2; ++pass)
        {
            var files = GetSortedPdfSkimFiles(folder);
            var index = Array.FindIndex(files,
                file => string.Equals(file, current, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                InvalidatePdfSkimFileCache();
                continue;
            }
            bool missing = false;
            for (int idx = index + Math.Sign(direction);
                 idx >= 0 && idx < files.Length;
                 idx += Math.Sign(direction))
            {
                if (File.Exists(files[idx])) return files[idx];
                missing = true;
            }
            if (!missing) return null;
            if (pass == 0) InvalidatePdfSkimFileCache();
        }
        return null;
    }
    // Switch preview first, then recycle the OLD video after its DirectShow handle
    // has been released. The preview window stays open when a neighbor exists.
    // File-level F2 rename for QuickLook video, image and PDF previews.
    // No second confirmation: Enter in the name dialog immediately commits.
    // The file extension is intentionally immutable.
    public async void RenameCurrentSkimFile()
    {
        if (_renameSkimInProgress || _deleteNextInProgress ||
            !IsSkimMediaPreviewForeground())
            return;

        var current = _invokedPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
            return;
        if (current.StartsWith(@"\\", StringComparison.Ordinal))
        {
            MessageBox.Show(_viewerWindow, "Rename over network shares is disabled.",
                "QuickLook", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _renameSkimInProgress = true;
        bool previewClosed = false;
        try
        {
            var oldName = Path.GetFileNameWithoutExtension(current);
            var extension = Path.GetExtension(current);
            var input = new System.Windows.Controls.TextBox
            {
                Text = oldName,
                FontSize = 15,
                MinWidth = 300,
                VerticalContentAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 7, 0, 10)
            };
            var panel = new System.Windows.Controls.StackPanel
            {
                Margin = new Thickness(18)
            };
            panel.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = "New file name (extension " + extension + " is preserved):",
                TextWrapping = TextWrapping.Wrap
            });
            panel.Children.Add(input);
            var buttonRow = new System.Windows.Controls.StackPanel
            {
                Orientation = System.Windows.Controls.Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            var renameButton = new System.Windows.Controls.Button
            {
                Content = "Rename",
                Width = 90,
                Height = 29,
                IsDefault = true,
                Margin = new Thickness(0, 0, 8, 0)
            };
            var cancelButton = new System.Windows.Controls.Button
            {
                Content = "Cancel",
                Width = 80,
                Height = 29,
                IsCancel = true
            };
            buttonRow.Children.Add(renameButton);
            buttonRow.Children.Add(cancelButton);
            panel.Children.Add(buttonRow);

            var dialog = new Window
            {
                Title = "Rename file - " + Path.GetFileName(current),
                Owner = _viewerWindow,
                WindowStartupLocation = WindowStartupLocation.CenterOwner,
                ResizeMode = ResizeMode.NoResize,
                SizeToContent = SizeToContent.Height,
                Width = 470,
                ShowInTaskbar = false,
                Content = panel
            };
            renameButton.Click += (_, _) => dialog.DialogResult = true;
            cancelButton.Click += (_, _) => dialog.DialogResult = false;
            dialog.Loaded += (_, _) => { input.Focus(); input.SelectAll(); };

            if (dialog.ShowDialog() != true)
                return;

            var newBase = input.Text.Trim();
            if (string.IsNullOrWhiteSpace(newBase) || newBase == "." || newBase == ".." ||
                newBase.EndsWith(".", StringComparison.Ordinal) ||
                newBase.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                throw new ArgumentException("Invalid file name. Use F2 and enter a valid name.");

            var target = Path.Combine(Path.GetDirectoryName(current), newBase + extension);
            if (string.Equals(current, target, StringComparison.OrdinalIgnoreCase))
                return;
            if (File.Exists(target))
                throw new IOException("A file with that name already exists: " + Path.GetFileName(target));

            try
            {
                await Task.Run(() => File.Move(current, target));
            }
            catch (IOException ex) when (IsSkimFileSharingViolation(ex))
            {
                // Pdfium/DirectShow/ImageViewer may keep file handles open.
                ClosePreview();
                previewClosed = true;
                for (int attempt = 0; attempt < 10; ++attempt)
                {
                    await Task.Delay(180);
                    try
                    {
                        await Task.Run(() => File.Move(current, target));
                        break;
                    }
                    catch (IOException retry) when (IsSkimFileSharingViolation(retry) && attempt < 9)
                    {
                        // Give the renderer more time to release the old file.
                    }
                }
            }

            // All media-specific caches must be invalidated after renaming.
            InvalidateVideoSkimFileCache();
            InvalidateImageSkimFileCache();
            InvalidatePdfSkimFileCache();
            if (File.Exists(target))
                InvokePreview(target);
        }
        catch (Exception ex)
        {
            ProcessHelper.WriteLog("VideoSkim: rename failed: " + ex);
            if (previewClosed && File.Exists(current))
                InvokePreview(current);
            MessageBox.Show("Could not rename file:\n" + ex.Message,
                "QuickLook - Rename", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _renameSkimInProgress = false;
        }
    }

    private static bool IsSkimFileSharingViolation(IOException ex)
    {
        int code = ex.HResult & 0xFFFF;
        return code == 32 || code == 33; // Win32 sharing/lock violation.
    }
    public async void DeleteCurrentVideoAndPreviewNext()
    {
        if (_deleteNextInProgress || _renameSkimInProgress || !_viewerWindow.IsVisible)
            return;

        var current = _invokedPath;
        if (string.IsNullOrWhiteSpace(current) || !File.Exists(current))
            return;

        // No reliable Recycle Bin on UNC network paths; never delete permanently.
        if (current.StartsWith(@"\\", StringComparison.Ordinal))
        {
            MessageBox.Show(_viewerWindow, "Network files cannot be recycled by this feature.",
                "QuickLook VideoSkim", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        _deleteNextInProgress = true;
        try
        {
            var type = _viewerWindow.Plugin?.GetType().FullName;
            var next = type == "QuickLook.Plugin.PDFViewer.Plugin"
                ? (FindAdjacentPdfSkimPath(current, 1) ?? FindAdjacentPdfSkimPath(current, -1))
                : type == "QuickLook.Plugin.ImageViewer.Plugin"
                    ? (FindAdjacentImageSkimPath(current, 1) ?? FindAdjacentImageSkimPath(current, -1))
                    : (FindAdjacentVideoPath(current, 1) ?? FindAdjacentVideoPath(current, -1));

            if (!string.IsNullOrEmpty(next) && File.Exists(next))
            {
                InvokePreview(next); // Same ViewerWindow as normal Next; unloads old video.
                if (!string.Equals(_invokedPath, next, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Could not switch away from the current video. Nothing deleted.");
            }
            else
            {
                ClosePreview(); // No other video can be displayed.
            }

            // Cleanup of MediaUriPlayer is async in this QuickLook branch.
            await Task.Delay(250);

            // Do not block the WPF UI while the old file is moved to Recycle Bin.
            // The actual old file is captured above even though _invokedPath now points to next.
            for (int attempt = 0; attempt < 12; attempt++)
            {
                try
                {
                    await Task.Run(() => FileSystem.DeleteFile(current,
                        UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin));
                    if (File.Exists(current))
                        throw new IOException("The old video still exists after the recycle operation.");
                    InvalidateVideoSkimFileCache();
                    InvalidateImageSkimFileCache();
                    InvalidatePdfSkimFileCache();
                    return;
                }
                catch (IOException) when (attempt < 11)
                {
                    await Task.Delay(300); // Give DirectShow time to release old video.
                }
            }
        }
        catch (Exception ex)
        {
            ProcessHelper.WriteLog("VideoSkim: Recycle failed: " + ex);
            // Keep the NEW preview active. Do not force the old file open again.
            MessageBox.Show("Could not move the previous video to the Recycle Bin:\n" + ex.Message,
                "QuickLook VideoSkim", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _deleteNextInProgress = false;
        }
    }
    public void TogglePreview(string path = null, string options = null)
    {
        if (string.IsNullOrEmpty(path))
            path = NativeMethods.QuickLook.GetCurrentSelection();

        if (!string.IsNullOrEmpty(options))
            InvokePreviewWithOption(path, options);
        else
            if (_viewerWindow.IsVisible && (string.IsNullOrEmpty(path) || path == _invokedPath))
                ClosePreview();
            else
                InvokePreview(path);
    }

    private void RunFocusMonitor()
    {
        FocusMonitor.GetInstance().Start();
    }

    private void StopFocusMonitor()
    {
        FocusMonitor.GetInstance().Stop();
    }

    internal void ForgetCurrentWindow()
    {
        StopFocusMonitor();

        _viewerWindow.Pinned = true;

        InitNewViewerWindow();
    }

    public void SwitchPreview(string path = null)
    {
        if (!_viewerWindow.IsVisible)
            return;

        if (string.IsNullOrEmpty(path))
            path = NativeMethods.QuickLook.GetCurrentSelection();

        if (string.IsNullOrEmpty(path))
            return;

        InvokePreview(path);
    }

    public void InvokePreviewWithOption(string path = null, string options = null)
    {
        InvokePreview(path);

        if (string.IsNullOrWhiteSpace(options)) return;

        var cli = new CommandLineParser(options.Split(','));

        if (cli.Has("top"))
        {
            _viewerWindow.Topmost = true;
            _viewerWindow.buttonTop.Tag = "Top";
        }
        if (cli.Has("pin"))
        {
            _viewerWindow.Pinned = true;
            ForgetCurrentWindow();
        }
    }

    public void InvokePreview(string path = null)
    {
        if (string.IsNullOrEmpty(path))
            path = NativeMethods.QuickLook.GetCurrentSelection();

        if (string.IsNullOrEmpty(path))
            return;

        if (_viewerWindow.IsVisible && path == _invokedPath)
            return;

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            if (!path.StartsWith("::")) // CLSID
                return;

        // Check extension filtering before proceeding (skip for directories)
        if (!isDirectory && !ExtensionFilterHelper.IsExtensionAllowed(path))
            return;

        _invokedPath = path;

        RunFocusMonitor();

        var matchedPlugin = PluginManager.GetInstance().FindMatch(path);

        BeginShowNewWindow(path, matchedPlugin);
    }

    public void InvokePluginPreview(string plugin, string path = null)
    {
        if (string.IsNullOrEmpty(path))
            path = _invokedPath;

        if (string.IsNullOrEmpty(path))
            return;

        var isDirectory = Directory.Exists(path);
        if (!isDirectory && !File.Exists(path))
            return;

        // Check extension filtering before proceeding (skip for directories)
        if (!isDirectory && !ExtensionFilterHelper.IsExtensionAllowed(path))
            return;

        RunFocusMonitor();

        var matchedPlugin = PluginManager.GetInstance().LoadedPlugins.Find(p =>
        {
            return p.GetType().Assembly.GetName().Name == plugin;
        });

        if (matchedPlugin != null)
        {
            BeginShowNewWindow(path, matchedPlugin);
        }
    }

    public void ReloadPreview()
    {
        if (!_viewerWindow.IsVisible || string.IsNullOrEmpty(_invokedPath))
            return;

        var matchedPlugin = PluginManager.GetInstance().FindMatch(_invokedPath);

        BeginShowNewWindow(_invokedPath, matchedPlugin);
    }

    public void ToggleFullscreen()
    {
        if (!_viewerWindow.IsVisible)
            return;

        _viewerWindow.ToggleFullscreen();
    }

    private void BeginShowNewWindow(string path, IViewer matchedPlugin)
    {
        _viewerWindow.UnloadPlugin();

        _viewerWindow.BeginShow(matchedPlugin, path, CurrentPluginFailed);
    }

    private void CurrentPluginFailed(string path, ExceptionDispatchInfo e)
    {
        var plugin = _viewerWindow.Plugin?.GetType();

        _viewerWindow.Close();

        TrayIconManager.ShowNotification($"Failed to preview {Path.GetFileName(path)}",
            "Consider reporting this incident to QuickLook’s author.", true);

        Debug.WriteLine(e.SourceException.ToString());

        ProcessHelper.WriteLog(e.SourceException.ToString());

        if (plugin != PluginManager.GetInstance().DefaultPlugin.GetType())
            BeginShowNewWindow(path, PluginManager.GetInstance().DefaultPlugin);
        else
            e.Throw();
    }

    private void InitNewViewerWindow()
    {
        _viewerWindow = new ViewerWindow();
        _viewerWindow.Closed += (sender, e) =>
        {
            if (ProcessHelper.IsShuttingDown())
                return;
            if (sender is not ViewerWindow w)
                return;
            // Only skip if the window was already forgotten by ForgetCurrentWindow,
            // which sets Pinned=true AND replaces _viewerWindow with a new instance.
            if (w.Pinned && _viewerWindow != w)
                return;
            StopFocusMonitor();
            InitNewViewerWindow();
        };
    }

    public static ViewWindowManager GetInstance()
    {
        return _instance ??= new ViewWindowManager();
    }
}
