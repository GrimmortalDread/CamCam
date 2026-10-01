using System;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace CamCam;

/// <summary>
/// Focus helpers shared by every piece of code that
/// reads raw OS keyboard state. GetAsyncKeyState and the low-level
/// keyboard hook are both system-wide, so without a focus check CamCam
/// would react to (and swallow) keys pressed in a browser or Discord
/// while the game sits in the background.
/// </summary>
public static class GameWindow
{
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private static readonly uint OwnProcessId = (uint)Environment.ProcessId;

    /// <summary>True while the FFXIV window is the foreground window.</summary>
    public static bool IsFocused
    {
        get
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero) return false;
            GetWindowThreadProcessId(hwnd, out var pid);
            return pid == OwnProcessId;
        }
    }

    /// <summary>True while the game's chat (or any game text field) or an ImGui text box has keyboard focus.</summary>
    public static unsafe bool IsTypingText
    {
        get
        {
            try
            {
                if (ImGui.GetIO().WantTextInput) return true;
                var atk = RaptureAtkModule.Instance();
                return atk != null && atk->IsTextInputActive();
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>Whether CamCam's hotkeys/fly keys should be read or swallowed right now.</summary>
    public static bool AcceptsHotkeys => IsFocused && !IsTypingText;
}
