using System;
using System.Runtime.InteropServices;

namespace CamCam;

/// <summary>
/// Simulates FFXIV's own "Toggle UI Display Mode" keypress to auto-hide
/// the UI while CamCam is actively controlling the camera, then restores
/// it when CamCam turns off - mirroring CamIdleHijack's own approach for
/// its idle cinematic camera. This only simulates a real keypress via
/// Windows' standard input API; it never touches game memory, so it
/// carries none of the risk that live memory writes do.
/// </summary>
public static class UiToggleHelper
{
    [DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private const uint KEYEVENTF_KEYUP = 0x0002;

    public static void SimulateToggle(string keyName)
    {
        byte vk = (byte)ResolveVirtualKey(keyName);
        if (vk == 0) return;
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static int ResolveVirtualKey(string keyName)
    {
        if (string.IsNullOrWhiteSpace(keyName)) return 0;
        string k = keyName.Trim().ToUpperInvariant();
        if (k.Length == 1)
        {
            char c = k[0];
            if (c >= 'A' && c <= 'Z') return c;
            if (c >= '0' && c <= '9') return c;
        }
        return k switch
        {
            "SCROLLLOCK" or "SCROLL LOCK" => 0x91,
            "TAB" => 0x09,
            "SPACE" => 0x20,
            "ENTER" => 0x0D,
            "F1" => 0x70,
            "F2" => 0x71,
            "F3" => 0x72,
            "F4" => 0x73,
            "F5" => 0x74,
            "F6" => 0x75,
            "F7" => 0x76,
            "F8" => 0x77,
            "F9" => 0x78,
            "F10" => 0x79,
            "F11" => 0x7A,
            "F12" => 0x7B,
            _ => 0
        };
    }
}
