// VideoSkim: preview-level playback keys handled independently of WPF focus.
namespace QuickLook.Common.Plugin;

public enum PreviewHotkey
{
    SeekBack10,
    SeekForward10,
    SeekBack30,
    SeekForward30,
    SeekStart,
    SeekEnd,
    SpeedUp,
    SpeedDown,
    SpeedReset
}

public interface IPreviewHotkeyHandler
{
    void HandlePreviewHotkey(PreviewHotkey action);
}