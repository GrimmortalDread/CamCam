using System;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;

namespace CamCam;

/// <summary>
/// Hooks the camera's getCameraTarget virtual function so the camera can
/// orbit a different player entirely, using the engine's OWN native
/// position/collision math - instead of fighting the engine's per-frame
/// LookAt recompute, which CamIdleHijack's own testing found only sticks
/// reliably for your own character. This is the same mechanism the real,
/// actively-used Cammy plugin uses for its spectate feature.
///
/// Unlike CamIdleHijack's own (unconfirmed) attempt at this same hook,
/// this does NOT use a hardcoded module address. It reads the function's
/// real address directly off the camera object's own vtable at runtime -
/// vtable slot 18, confirmed against Hypostasis (the library Cammy
/// itself depends on for this exact hook). Slot indices for a class's
/// virtual functions are generally far more stable across FFXIV patches
/// than a raw hardcoded address, since they only change if the game's
/// own code adds/removes/reorders that class's virtual methods (which
/// Hypostasis's own comments note has historically only happened at
/// expansion boundaries, not routine patches).
///
/// Still real, live function hooking, though - if the camera or anything
/// else looks wrong after turning this on, restart the game.
/// </summary>
public unsafe class WorldCameraTargetHook : IDisposable
{
    // Confirmed via Hypostasis's GameCamera.cs: getCameraTarget is vtable
    // slot 18 on the camera's own vtable.
    private const int GetCameraTargetVTableIndex = 18;

    private delegate nint GetCameraTargetDelegate(nint camera);

    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IPluginLog log;
    private Hook<GetCameraTargetDelegate>? hook;

    /// <summary>When set, the camera orbits this GameObject instead of whatever the engine would normally pick.</summary>
    public nint? OverrideTargetAddress;

    // Same reasoning as WorldCameraPositionHook.worldCameraAddress - only
    // the world camera is ever redirected.
    private nint worldCameraAddress;

    public bool IsInstalled => hook != null;
    public string Status { get; private set; } = "Not installed";

    public WorldCameraTargetHook(IGameInteropProvider gameInteropProvider, IPluginLog log)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.log = log;
    }

    /// <summary>
    /// Installs the hook using the real getCameraTarget address, read
    /// live from the given camera's own vtable pointer (slot 18).
    /// </summary>
    internal void Install(GameCamera* camera)
    {
        if (camera != null) worldCameraAddress = (nint)camera;
        if (hook != null) return;
        if (camera == null || *(nint**)camera == null)
        {
            Status = "Camera not ready";
            return;
        }

        try
        {
            nint targetAddress = (*(nint**)camera)[GetCameraTargetVTableIndex];
            if (targetAddress == 0)
            {
                Status = "Vtable slot 18 was null";
                return;
            }

            hook = gameInteropProvider.HookFromAddress<GetCameraTargetDelegate>(targetAddress, Detour);
            hook.Enable();
            Status = "Installed";
            log.Information($"[CamCam] getCameraTarget hook installed at 0x{targetAddress:X} (vtable slot {GetCameraTargetVTableIndex})");
        }
        catch (Exception ex)
        {
            Status = $"Failed to install: {ex.Message}";
            log.Warning(ex, "[CamCam] Failed to install getCameraTarget hook");
            hook = null;
        }
    }

    /// <summary>Stops overriding without unhooking.</summary>
    public void Release() => OverrideTargetAddress = null;

    public void Remove()
    {
        hook?.Disable();
        hook?.Dispose();
        hook = null;
        OverrideTargetAddress = null;
        Status = "Not installed";
    }

    private nint Detour(nint camera)
    {
        if (camera == worldCameraAddress && OverrideTargetAddress.HasValue)
            return OverrideTargetAddress.Value;

        return hook!.Original(camera);
    }

    public void Dispose() => Remove();
}
