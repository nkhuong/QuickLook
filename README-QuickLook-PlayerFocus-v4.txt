QuickLook VideoSkim v4 — maintain player keyboard focus after N/P/Delete
===========================================================================

Fixes:
- Navigation initiated inside the video player (N/P/Delete) reuses ViewerWindow
  and re-activates/keyboard-focuses the NEW ViewerPanel after it is loaded.
- Repeated N/N/N or Delete/Delete/Delete should work without mouse clicks.
- File Explorer-driven selections are NOT changed.
- No changes to Recycle Bin, StartAtSeconds, speed/jump, or Settings behavior.
- Explorer custom sort order is intentionally NOT implemented; keeps A-Z.

Prerequisite:
- Local source already has SmoothDelete-Navigation-v3 installed and working.
- Quit QuickLook from the system tray before rebuilding.

Install:
1. Copy Update-QuickLook-RestorePlayerFocus-v4.ps1 next to QuickLook.slnx.
2. In PowerShell from that source folder run:
       .\Update-QuickLook-RestorePlayerFocus-v4.ps1
   If PowerShell blocks local scripts, review it and use per-process policy only:
       Set-ExecutionPolicy -Scope Process -ExecutionPolicy Bypass
3. Inspect:
       git diff --check
       git diff -- QuickLook/ViewWindowManager.cs
4. Visual Studio 2026 -> Release / x64 -> Build Solution (or Rebuild QuickLook).
5. Run Build\Release\QuickLook.exe.
6. In a TEST folder of disposable video copies, click the video panel ONCE:
       N -> N -> P -> Delete -> Delete
   Observe keyboard focus stays on preview throughout.

Safety:
- Delete still sends to Recycle Bin, no confirmation, as in v3.
- Test with disposable copies before real video libraries.
- The script checks exact anchor text before writing and saves a backup to %TEMP%.
- This patch was checked against the v3 updater's generated code but has NOT
  been built or run on the user's Windows 10 PC.
