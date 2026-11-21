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
using QuickLook.Common.NativeMethods;
using QuickLook.Common.Plugin;
using QuickLook.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Windows.Forms;

namespace QuickLook;

internal class KeystrokeDispatcher : IDisposable
{
    private static KeystrokeDispatcher _instance;

    private static HashSet<Keys> _validKeys;

    private GlobalKeyboardHook _hook;
    private nint _winEventHook;
    private User32.WinEventProc _winEventProc; // keep reference to prevent GC
    private bool _isPreviewRequest;
    private bool _spaceIsDown;
    private long _spaceHoldTick;
    private long _lastInvalidKeyPressTick;
    private readonly HashSet<Keys> _videoSkimHeldKeys = new();
    private readonly HashSet<Keys> _videoSkimPlaybackHeldKeys = new();

    private const long HOLD_TO_PREVIEW_DURATION = TimeSpan.TicksPerMillisecond * 750;
    private const long VALID_KEY_PRESS_DELAY = TimeSpan.TicksPerSecond * 1;

    protected KeystrokeDispatcher()
    {
        InstallKeyHook(KeyDownEventHandler, KeyUpEventHandler);
        InstallForegroundWindowHook();

        _validKeys =
        [
            Keys.Up, Keys.Down, Keys.Left, Keys.Right,
            Keys.Enter, Keys.Space, Keys.Escape,
            Keys.F5, Keys.F11,
        ];
    }

    public void Dispose()
    {
        _hook?.Dispose();
        _hook = null;

        if (_winEventHook != IntPtr.Zero)
        {
            User32.UnhookWinEvent(_winEventHook);
            _winEventHook = IntPtr.Zero;
        }
    }

    private void KeyDownEventHandler(object sender, KeyEventArgs e)
    {
        CallViewWindowManagerInvokeRoutine(e, true);
    }

    private void KeyUpEventHandler(object sender, KeyEventArgs e)
    {
        CallViewWindowManagerInvokeRoutine(e, false);
    }

    private void CallViewWindowManagerInvokeRoutine(KeyEventArgs e, bool isKeyDown)
    {
        if (TryHandleVideoSkimGlobalKey(e, isKeyDown))
            return;
        if (TryHandleVideoSkimPlaybackKey(e, isKeyDown))
            return;

        // skip invalid keys, but record the timestamp
        if (!_validKeys.Contains(e.KeyCode))
        {
            Debug.WriteLine($"Invalid keypress: key={e.KeyCode},down={isKeyDown}, time={_lastInvalidKeyPressTick}");
            _lastInvalidKeyPressTick = DateTime.Now.Ticks;
            return;
        }

        // skip valid keys when modifiers are used
        if (isKeyDown && e.Modifiers != Keys.None)
            return;

        // skip if key is valid but too close after pressing an invalid key
        if (DateTime.Now.Ticks - _lastInvalidKeyPressTick < VALID_KEY_PRESS_DELAY)
            return;
        _lastInvalidKeyPressTick = 0L;

        // skip if user is holding Space (don't skip other valid keys)
        if (isKeyDown && e.KeyCode == Keys.Space)
        {
            if (_spaceIsDown)
                return;

            _spaceHoldTick = DateTime.Now.Ticks;
        }

        // check if the valid key is a preview request
        if (isKeyDown)
        {
            _isPreviewRequest = NativeMethods.QuickLook.GetFocusedWindowType() !=
                                NativeMethods.QuickLook.FocusedWindowType.Invalid;
            _isPreviewRequest |= WindowHelper.IsForegroundWindowBelongToSelf();
        } // else (when isKeyDown is false), _isPreviewRequest retain its current state

        // call InvokeRoutine only when user pressed a key in a valid window, or
        // released a key which was pressed in a valid window, with an exception of Space which
        // must be hold for 750ms before releasing.
        if (_isPreviewRequest)
        {
            if (isKeyDown || e.KeyCode != Keys.Space ||
                DateTime.Now.Ticks - _spaceHoldTick >= HOLD_TO_PREVIEW_DURATION)
            {
                InvokeRoutine(e.KeyCode, isKeyDown);

                if (isKeyDown && e.KeyCode == Keys.Space)
                    _spaceIsDown = true;
            }
        }

        // when the key has been released, reset variables
        if (!isKeyDown)
        {
            _isPreviewRequest = false;
            _spaceIsDown = e.KeyCode != Keys.Space && _spaceIsDown;
        }
    }

    // VideoSkim hotkeys must survive QuickLook's UnloadPlugin/Keyboard.ClearFocus.
    // GlobalKeyboardHook is already installed by QuickLook, so this does NOT
    // install a second global hook. No modifiers are accepted.
    // VideoSkimPdfV11: keyboard layout assumption: US < = Shift+Comma, > = Shift+Period.
    // Reuses QuickLook's existing keyboard hook (no second global hook).
    // VideoSkimCommaPeriodRenameV12: plain , and . for previous/next, F2 to rename.
    // This hook only acts when QuickLook's video/photo/PDF preview is foreground.
    private bool TryHandleVideoSkimGlobalKey(KeyEventArgs e, bool isKeyDown)
    {
        var key = e.KeyCode;
        if (key != Keys.Oemcomma && key != Keys.OemPeriod &&
            key != Keys.F2 && key != Keys.Delete)
            return false;

        // Release the held key based on the physical key, regardless of modifiers.
        if (!isKeyDown)
        {
            if (_videoSkimHeldKeys.Remove(key))
            {
                e.Handled = true;
                return true;
            }
            return false;
        }

        if (e.Modifiers != Keys.None)
            return false;

        try
        {
            var manager = ViewWindowManager.GetInstance();
            if (!manager.IsSkimMediaPreviewForeground())
                return false;

            e.Handled = true;
            if (!_videoSkimHeldKeys.Add(key))
                return true; // One action per physical key press.

            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (!manager.IsSkimMediaPreviewForeground())
                    return;

                switch (key)
                {
                    case Keys.Oemcomma:
                        manager.PreviewAdjacentVideo(-1);
                        break;
                    case Keys.OemPeriod:
                        manager.PreviewAdjacentVideo(1);
                        break;
                    case Keys.F2:
                        manager.RenameCurrentSkimFile();
                        break;
                    case Keys.Delete:
                        manager.DeleteCurrentVideoAndPreviewNext();
                        break;
                }
            }));
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("VideoSkim navigation/rename shortcut failed: " + ex);
            return false;
        }
    }
    private bool TryHandleVideoSkimPlaybackKey(KeyEventArgs e, bool isKeyDown)
    {
        var key = e.KeyCode;

        // Ignore key repeat while held; swallow KeyUp for keys handled on KeyDown.
        if (!isKeyDown)
        {
            if (_videoSkimPlaybackHeldKeys.Remove(key))
            {
                e.Handled = true;
                return true;
            }
            return false;
        }

        PreviewHotkey? action = null;
        switch (key)
        {
            case Keys.Left when e.Modifiers == Keys.Shift:
                action = PreviewHotkey.SeekBack10;
                break;
            case Keys.Right when e.Modifiers == Keys.Shift:
                action = PreviewHotkey.SeekForward10;
                break;
            case Keys.Left when e.Modifiers == Keys.Control:
                action = PreviewHotkey.SeekBack30;
                break;
            case Keys.Right when e.Modifiers == Keys.Control:
                action = PreviewHotkey.SeekForward30;
                break;
            case Keys.Home when e.Modifiers == Keys.None:
                action = PreviewHotkey.SeekStart;
                break;
            case Keys.End when e.Modifiers == Keys.None:
                action = PreviewHotkey.SeekEnd;
                break;
            case Keys.Oemplus when e.Modifiers == Keys.None || e.Modifiers == Keys.Shift:
            case Keys.Add when e.Modifiers == Keys.None:
                action = PreviewHotkey.SpeedUp;
                break;
            case Keys.OemMinus when e.Modifiers == Keys.None:
            case Keys.Subtract when e.Modifiers == Keys.None:
                action = PreviewHotkey.SpeedDown;
                break;
            case Keys.D0 when e.Modifiers == Keys.None:
            case Keys.NumPad0 when e.Modifiers == Keys.None:
                action = PreviewHotkey.SpeedReset;
                break;
        }
        if (!action.HasValue)
            return false;

        try
        {
            var manager = ViewWindowManager.GetInstance();
            bool imageZoomShortcut = action.Value == PreviewHotkey.SpeedUp ||
                                     action.Value == PreviewHotkey.SpeedDown ||
                                     action.Value == PreviewHotkey.SpeedReset;
            if (!manager.IsVideoSkimPreviewForeground() &&
                !(imageZoomShortcut && manager.CanZoomImageSkimPreview()))
                return false;

            e.Handled = true; // Do not deliver a duplicate WPF PreviewKeyDown.
            if (!_videoSkimPlaybackHeldKeys.Add(key))
                return true;

            var capturedAction = action.Value;
            // ViewerWindow.BeginShow loads the replacement VideoViewer on the UI
            // dispatcher. ContextIdle lets that load finish before handling a key.
            System.Windows.Application.Current.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (manager.IsVideoSkimPreviewForeground() || (imageZoomShortcut && manager.CanZoomImageSkimPreview()))
                    manager.DispatchVideoSkimPlaybackHotkey(capturedAction);
            }), System.Windows.Threading.DispatcherPriority.Background);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine("VideoSkim playback shortcut failed: " + ex);
            return false;
        }
    }
    private void InvokeRoutine(Keys key, bool isKeyDown)
    {
        Debug.WriteLine($"InvokeRoutine: key={key},down={isKeyDown}");

        if (isKeyDown)
        {
            switch (key)
            {
                case Keys.Enter:
                    PipeServerManager.SendMessage(PipeMessages.RunAndClose);
                    break;

                case Keys.Space:
                    PipeServerManager.SendMessage(PipeMessages.Toggle);
                    break;

                case Keys.F5:
                    PipeServerManager.SendMessage(PipeMessages.Reload);
                    break;

                case Keys.F11:
                    PipeServerManager.SendMessage(PipeMessages.Fullscreen);
                    break;
            }
        }
        else
        {
            switch (key)
            {
                case Keys.Up:
                case Keys.Down:
                case Keys.Left:
                case Keys.Right:
                    PipeServerManager.SendMessage(PipeMessages.Switch);
                    break;

                case Keys.Escape:
                    PipeServerManager.SendMessage(PipeMessages.Close);
                    break;

                case Keys.Space:
                    if (SettingHelper.Get("AutoCloseHolding", true, "QuickLook"))
                        PipeServerManager.SendMessage(PipeMessages.Toggle);
                    break;
            }
        }
    }

    private void InstallKeyHook(KeyEventHandler downHandler, KeyEventHandler upHandler)
    {
        _hook = GlobalKeyboardHook.GetInstance();

        _hook.KeyDown += downHandler;
        _hook.KeyUp += upHandler;
    }

    private void InstallForegroundWindowHook()
    {
        // When the foreground window changes (e.g. via Alt+Tab), reset the invalid-key
        // delay so the first Space press in the newly focused Explorer window works.
        // https://github.com/QL-Win/QuickLook/issues/1939
        _winEventProc = OnForegroundWindowChanged;
        _winEventHook = User32.SetWinEventHook(
            User32.EVENT_SYSTEM_FOREGROUND, User32.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc,
            0, 0, User32.WINEVENT_OUTOFCONTEXT);
    }

    private void OnForegroundWindowChanged(nint hWinEventHook, uint eventType, nint hwnd,
        int idObject, int idChild, uint idEventThread, uint dwmsEventTime)
    {
        // A different window is now in the foreground. Any invalid key presses that happened
        // before this switch (e.g. Alt+Tab keystrokes) belong to the previous context,
        // so they must not suppress valid keys in the new window.
        _lastInvalidKeyPressTick = 0L;

#if false // The problem of requiring two spaces has been solved -- comment the test code first.
        Debug.WriteLine($"Foreground window changed to {hwnd:X}, invalid-key delay cleared.");
#endif
    }

    internal static KeystrokeDispatcher GetInstance()
    {
        return _instance ??= new KeystrokeDispatcher();
    }
}
