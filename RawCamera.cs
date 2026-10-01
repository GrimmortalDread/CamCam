using System.Runtime.InteropServices;

namespace CamCam;

/// <summary>
/// The game's camera is NOT a free-floating position. Every frame, the engine
/// recalculates the camera's render position from a look-at point, a horizontal
/// rotation, a vertical rotation (pitch), and a zoom distance. Writing directly
/// to a "Position" field gets overwritten by that recalculation. This struct
/// overlays those underlying fields instead.
///
/// These offsets are confirmed against Hypostasis (github.com/UnknownX7/Hypostasis,
/// the library the real Cammy plugin depends on) - they match CamIdleHijack's own
/// reverse-engineering exactly, field for field. They're tied to a specific game
/// client build and can shift after an FFXIV patch; if that happens, check
/// Hypostasis's Game/Structures/GameCamera.cs for the current values first.
/// </summary>
[StructLayout(LayoutKind.Explicit)]
internal unsafe struct RawGameCamera
{
    // Vtable pointer, sitting at the start of the object as usual for a
    // polymorphic C++ class. Used to find getCameraTarget's real address
    // at runtime instead of hardcoding one (see WorldCameraTargetHook.cs).
    [FieldOffset(0x0)] public nint* VTable;

    // Raw world-space position. On worldCamera this is purely an OUTPUT the
    // engine recomputes every frame from LookAt/rotation/zoom below - writing
    // here does nothing lasting.
    [FieldOffset(0x60)] public float X;
    [FieldOffset(0x64)] public float Y;
    [FieldOffset(0x68)] public float Z;

    [FieldOffset(0x90)] public float LookAtX;
    [FieldOffset(0x94)] public float LookAtY;
    [FieldOffset(0x98)] public float LookAtZ;

    [FieldOffset(0x124)] public float CurrentZoom;
    [FieldOffset(0x128)] public float MinZoom;
    [FieldOffset(0x12C)] public float MaxZoom;

    [FieldOffset(0x140)] public float CurrentHRotation; // -pi .. pi
    [FieldOffset(0x144)] public float CurrentVRotation; // pitch

    [FieldOffset(0x158)] public float MinVRotation;
    [FieldOffset(0x15C)] public float MaxVRotation;

    // 0 = first person, 1 = third person. Zooming in far enough makes the
    // game auto-switch this to first person on its own.
    [FieldOffset(0x180)] public int Mode;
}

[StructLayout(LayoutKind.Explicit)]
internal unsafe struct RawCameraManager
{
    [FieldOffset(0x0)] public RawGameCamera* WorldCamera;
    [FieldOffset(0x8)] public RawGameCamera* IdleCamera;
    [FieldOffset(0x10)] public RawGameCamera* MenuCamera;
    [FieldOffset(0x18)] public RawGameCamera* SpectatorCamera;
}
