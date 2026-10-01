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
        GameWindow.MarkSyntheticInput();
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    private static int ResolveVirtualKey(string keyName) => KeyCatalog.Resolve(keyName);
}
