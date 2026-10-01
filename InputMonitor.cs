using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.GamePad;
using Dalamud.Plugin.Services;

namespace CamCam;

/// <summary>
/// "Is the player actually back?" detection for the No-input idle mode.
/// Polls keyboard, mouse buttons/wheel, (optionally) mouse movement past a
/// threshold, and gamepad directly, rather than using GetLastInputInfo,
/// so it can ignore what shouldn't count: CamCam's own keys, the
/// synthetic UI-toggle keypress, clicks/typing in CamCam's window, and
/// small mouse nudges.
/// </summary>
public sealed class InputMonitor
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT point);

    private static readonly int[] MouseButtonVks = { 0x01, 0x02, 0x04, 0x05, 0x06 };
    private static readonly GamepadButtons[] PadButtons = (GamepadButtons[])Enum.GetValues(typeof(GamepadButtons));

    // Dalamud reports stick tilt on a -99..99 scale.
    private const float StickDeadzone = 20f;

    private readonly IGamepadState gamepad;
    private Vector2 mouseAnchor;
    private bool hasMouseAnchor;

    public float SecondsSinceInput { get; private set; }

    public InputMonitor(IGamepadState gamepad) => this.gamepad = gamepad;

    public void Reset() => SecondsSinceInput = 0f;

    public void Update(float deltaSeconds, Configuration configuration, HashSet<int> ignoredVirtualKeys)
    {
        if (DetectInput(configuration, ignoredVirtualKeys))
            SecondsSinceInput = 0f;
        else
            SecondsSinceInput += deltaSeconds;
    }

    private bool DetectInput(Configuration configuration, HashSet<int> ignoredVirtualKeys)
    {
        bool input = false;

        if (configuration.IdleGamepadCounts)
        {
            foreach (var button in PadButtons)
            {
                if (button != GamepadButtons.None && gamepad.Raw(button) > 0f)
                {
                    input = true;
                    break;
                }
            }

            if (gamepad.LeftStick.Length() > StickDeadzone || gamepad.RightStick.Length() > StickDeadzone)
                input = true;
        }

        // Keyboard/mouse only count while the game is focused - typing in
        // another app isn't "back at the game".
        if (!GameWindow.IsFocused)
        {
            hasMouseAnchor = false;
            return input;
        }

        var io = ImGui.GetIO();

        if (!io.WantCaptureKeyboard && !GameWindow.IsTypingText)
        {
            for (int vk = 0x08; vk <= 0xFE; vk++)
            {
                if (ignoredVirtualKeys.Contains(vk)) continue;
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    input = true;
                    break;
                }
            }
        }

        if (!io.WantCaptureMouse)
        {
            foreach (var vk in MouseButtonVks)
            {
                if ((GetAsyncKeyState(vk) & 0x8000) != 0)
                {
                    input = true;
                    break;
                }
            }

            if (io.MouseWheel != 0f || io.MouseWheelH != 0f)
                input = true;
        }

        if (GetCursorPos(out var point))
        {
            var cursor = new Vector2(point.X, point.Y);
            if (!hasMouseAnchor || input)
            {
                mouseAnchor = cursor;
                hasMouseAnchor = true;
            }
            else if (configuration.IdleMouseMovementCounts && !io.WantCaptureMouse
                     && Vector2.Distance(cursor, mouseAnchor) > configuration.IdleMouseMovementThresholdPixels)
            {
                input = true;
                mouseAnchor = cursor;
            }
        }

        return input;
    }
}
