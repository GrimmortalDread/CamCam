using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using Dalamud.Plugin.Services;

namespace CamCam;

/// <summary>
/// Swallows CamCam's configured fly keys at the OS input level while
/// they're in use, so pressing them moves the camera ONLY - not the
/// game's own hotbars or other keybinds too.
///
/// This is a genuinely different technique from anything tried earlier
/// in this project, not a variation on the memory write that crashed the
/// game. GetAsyncKeyState (used everywhere else here) only reads
/// whether a key is currently down - it can't stop anyone else from also
/// seeing that same keypress, which is exactly why the game's hotbars
/// were still firing. A low-level keyboard hook (SetWindowsHookEx with
/// WH_KEYBOARD_LL) intercepts input before it reaches other applications
/// at all, and can choose not to pass specific keys along. It never
/// touches game memory or any other process's memory - it's the same
/// standard Windows mechanism tools like AutoHotkey use. Only the
/// specific keys CamCam is configured to use for flying are swallowed;
/// everything else passes through completely untouched.
/// </summary>
public sealed class FlyKeyBlocker : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    // Kept as a field so the GC never collects the delegate while Windows
    // still holds a native pointer to it - a classic and dangerous pitfall
    // for hooks like this in managed code if left as a local/lambda.
    private readonly LowLevelKeyboardProc proc;
    private readonly IPluginLog log;
    private IntPtr hookHandle = IntPtr.Zero;
    private readonly HashSet<int> blockedKeys = new();

    public bool IsInstalled => hookHandle != IntPtr.Zero;
    public string Status { get; private set; } = "Not installed";

    public FlyKeyBlocker(IPluginLog log)
    {
        this.log = log;
        proc = HookCallback;
    }

    /// <summary>Call every frame while active - cheap, and keeps the blocked set current if key bindings change live. Only swallows while the game window is focused and nobody is typing - the hook itself is system-wide.</summary>
    public void SetBlockedKeys(IEnumerable<string> keyNames)
    {
        blockedKeys.Clear();
        foreach (var name in keyNames)
        {
            var vk = FreeCamController.ResolveKey(name);
            if (vk != 0)
                blockedKeys.Add(vk);
        }
    }

    public void Install()
    {
        if (hookHandle != IntPtr.Zero) return;
        hookHandle = SetWindowsHookEx(WH_KEYBOARD_LL, proc, IntPtr.Zero, 0);
        if (hookHandle == IntPtr.Zero)
        {
            int error = Marshal.GetLastWin32Error();
            Status = $"Failed to install (Win32 error {error})";
            log.Warning($"[CamCam] FlyKeyBlocker failed to install: Win32 error {error}");
        }
        else
        {
            Status = "Installed";
            log.Information("[CamCam] FlyKeyBlocker installed.");
        }
    }

    public void Remove()
    {
        if (hookHandle == IntPtr.Zero) return;
        UnhookWindowsHookEx(hookHandle);
        hookHandle = IntPtr.Zero;
        Status = "Not installed";
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && blockedKeys.Count > 0 && GameWindow.AcceptsHotkeys)
        {
            int msg = wParam.ToInt32();
            if (msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (blockedKeys.Contains((int)data.vkCode))
                    return (IntPtr)1; // swallow - do NOT call CallNextHookEx, so nothing else sees this key
            }
        }

        return CallNextHookEx(hookHandle, nCode, wParam, lParam);
    }

    public void Dispose() => Remove();
}
