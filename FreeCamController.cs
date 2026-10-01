using System;
using System.Numerics;
using System.Runtime.InteropServices;

namespace CamCam;

/// <summary>
/// Tracks a free-fly camera position and updates it from held keys each
/// frame - movement keys translate along the current look direction,
/// turn/look keys rotate that direction (so you can actually turn around
/// mid-flight instead of only via the settings sliders). Uses raw
/// GetAsyncKeyState, the same input approach CamIdleHijack already uses
/// successfully.
///
/// All keys are configurable and default to a numpad layout (8/2 forward-
/// back, 4/6 turn, 7/9 strafe, +/- look up/down) rather than WASD - WASD doubles as FFXIV's own movement keys, and
/// GetAsyncKeyState only reads key state, it can't stop the game from
/// also treating the same keypress as a move command. An earlier version
/// tried to fix that with a live memory write to force the game's
/// movement off; that crashed the game (confirmed via crash dump - an
/// access violation inside that write). Using non-conflicting keys
/// instead sidesteps the problem with no memory-write risk at all.
/// </summary>
public class FreeCamController
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    private const int VK_NUMLOCK = 0x90;

    /// <summary>
    /// True while Num Lock is toggled on. Numpad keys only send numpad
    /// codes while this is true - with it off, Windows sends the same
    /// codes as arrows/Page Up/Page Down/Home/End/Insert/Delete for the
    /// same physical keys instead, which is functionally invisible from
    /// inside the game unless you check this directly.
    /// </summary>
    public static bool IsNumLockOn => (GetKeyState(VK_NUMLOCK) & 0x0001) != 0;

    public Vector3 Position { get; private set; }
    public bool HasStartingPosition { get; private set; }

    public void ResetTo(Vector3 startPosition)
    {
        Position = startPosition;
        HasStartingPosition = true;
    }

    public void ClearStartingPosition() => HasStartingPosition = false;

    /// <summary>
    /// Prevents flying below a given world Y - an approximation, not real
    /// terrain collision. It only knows "don't go below this one number,"
    /// so it works well near flat ground close to your character and
    /// poorly on stairs, cliffs, or multi-level areas.
    /// </summary>
    public void ClampMinimumY(float minY)
    {
        if (Position.Y < minY)
            Position = new Vector3(Position.X, minY, Position.Z);
    }

    // Set by SettingsWindow's per-preset D-pad buttons, reset to false at
    // the top of every Draw() call and re-set only if a button is
    // actively held that frame - so if the settings window ever stops
    // being drawn mid-hold, these simply stop being set to true again,
    // rather than getting stuck on. Checked alongside the real keyboard
    // state below so either the physical key or its on-screen button
    // equivalent works interchangeably.
    public static bool VirtualForwardHeld, VirtualBackHeld, VirtualStrafeLeftHeld, VirtualStrafeRightHeld;
    public static bool VirtualUpHeld, VirtualDownHeld, VirtualTurnLeftHeld, VirtualTurnRightHeld;
    public static bool VirtualLookUpHeld, VirtualLookDownHeld;

    public static void ResetVirtualButtons()
    {
        VirtualForwardHeld = VirtualBackHeld = VirtualStrafeLeftHeld = VirtualStrafeRightHeld = false;
        VirtualUpHeld = VirtualDownHeld = VirtualTurnLeftHeld = VirtualTurnRightHeld = false;
        VirtualLookUpHeld = VirtualLookDownHeld = false;
    }

    /// <summary>
    /// Applies turn/look keys to the given rotation (in place), then moves
    /// Position along that (possibly just-updated) facing direction based
    /// on held movement keys.
    /// </summary>
    public void Update(float deltaSeconds, ref float horizontalRotation, ref float verticalRotation, float moveSpeed, float turnSpeed, Configuration configuration)
    {
        // Swapped from the original -=/+= - the initial sign convention
        // had this backwards relative to the camera's actual left/right,
        // confirmed by direct testing.
        if (IsHeld(configuration.FlyTurnLeftKey) || VirtualTurnLeftHeld) horizontalRotation += turnSpeed * deltaSeconds;
        if (IsHeld(configuration.FlyTurnRightKey) || VirtualTurnRightHeld) horizontalRotation -= turnSpeed * deltaSeconds;
        horizontalRotation = WrapAngle(horizontalRotation);

        if (IsHeld(configuration.FlyLookUpKey) || VirtualLookUpHeld) verticalRotation += turnSpeed * deltaSeconds;
        if (IsHeld(configuration.FlyLookDownKey) || VirtualLookDownHeld) verticalRotation -= turnSpeed * deltaSeconds;
        verticalRotation = Math.Clamp(verticalRotation, -1.5f, 1.5f);

        var cosV = MathF.Cos(verticalRotation);
        var forward = new Vector3(
            -cosV * MathF.Sin(horizontalRotation),
            -MathF.Sin(verticalRotation),
            -cosV * MathF.Cos(horizontalRotation));

        var right = new Vector3(MathF.Cos(horizontalRotation), 0f, -MathF.Sin(horizontalRotation));

        var move = Vector3.Zero;
        if (IsHeld(configuration.FlyForwardKey) || VirtualForwardHeld) move += forward;
        if (IsHeld(configuration.FlyBackKey) || VirtualBackHeld) move -= forward;
        if (IsHeld(configuration.FlyRightKey) || VirtualStrafeRightHeld) move += right;
        if (IsHeld(configuration.FlyLeftKey) || VirtualStrafeLeftHeld) move -= right;
        if (IsHeld(configuration.FlyUpKey) || VirtualUpHeld) move += Vector3.UnitY;
        if (IsHeld(configuration.FlyDownKey) || VirtualDownHeld) move -= Vector3.UnitY;

        if (move != Vector3.Zero)
            Position += Vector3.Normalize(move) * moveSpeed * deltaSeconds;
    }

    private static float WrapAngle(float radians)
    {
        while (radians > MathF.PI) radians -= 2f * MathF.PI;
        while (radians < -MathF.PI) radians += 2f * MathF.PI;
        return radians;
    }

    /// <summary>Exposed so other code (e.g. follow-mode height adjustment) can reuse the same key resolution without duplicating it.</summary>
    public static bool IsHeld(string keyName)
    {
        var vk = ResolveVirtualKey(keyName);
        return vk != 0 && (GetAsyncKeyState(vk) & 0x8000) != 0;
    }

    /// <summary>Exposed so FlyKeyBlocker can resolve the same key names to VK codes for blocking.</summary>
    public static int ResolveKey(string keyName) => ResolveVirtualKey(keyName);

    /// <summary>Turns a saved key name into a Windows virtual-key code - now backed by KeyCatalog, the single shared source for both resolution and the settings window's dropdowns.</summary>
    private static int ResolveVirtualKey(string keyName) => KeyCatalog.Resolve(keyName);
}
