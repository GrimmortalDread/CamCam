using System;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using FFXIVClientStructs.FFXIV.Client.UI;

namespace CamCam;

/// <summary>
/// Focus and real-user-input helpers shared by every piece of code that
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

    [StructLayout(LayoutKind.Sequential)]
    private struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll")]
    private static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);

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

    // Input CamCam itself synthesizes (the UI-toggle keypress) must not
    // count as "the user is back" - otherwise hiding the UI on engage
    // would immediately reset the AFK timer and disengage again.
    private static long syntheticInputUntilTick;
    private static uint lastRealInputTick = (uint)Environment.TickCount;
    private static uint lastSeenInputTick;

    public static void MarkSyntheticInput() => syntheticInputUntilTick = Environment.TickCount64 + 500;

    /// <summary>Seconds since the last keyboard/mouse input that CamCam didn't generate itself.</summary>
    public static float SecondsSinceRealInput
    {
        get
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            if (GetLastInputInfo(ref info) && info.dwTime != lastSeenInputTick)
            {
                lastSeenInputTick = info.dwTime;
                if (Environment.TickCount64 > syntheticInputUntilTick)
                    lastRealInputTick = info.dwTime;
            }
            return (uint)Environment.TickCount - lastRealInputTick > int.MaxValue
                ? 0f
                : ((uint)Environment.TickCount - lastRealInputTick) / 1000f;
        }
    }
}
