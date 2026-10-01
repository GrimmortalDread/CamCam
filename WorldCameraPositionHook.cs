using System;
using System.Numerics;
using Dalamud.Hooking;
using Dalamud.Plugin.Services;

namespace CamCam;

/// <summary>
/// Hooks the camera's getCameraPosition virtual function (vtable slot 16,
/// confirmed via Hypostasis's GameCamera.cs) so the engine can be handed a
/// fully free-flown position instead of computing one from
/// LookAt/rotation/zoom. This is the actual mechanism Cammy's own FreeCam
/// feature uses for true free camera movement, not just orbiting a fixed
/// point - same technique and same source as WorldCameraTargetHook.cs.
/// </summary>
public unsafe class WorldCameraPositionHook : IDisposable
{
    private const int GetCameraPositionVTableIndex = 16;

    private delegate void GetCameraPositionDelegate(nint camera, nint target, Vector3* position, byte swapPerson);

    private readonly IGameInteropProvider gameInteropProvider;
    private readonly IPluginLog log;
    private Hook<GetCameraPositionDelegate>? hook;

    /// <summary>When set, the camera's rendered position is this, full stop - the engine's own computation is skipped entirely.</summary>
    public Vector3? OverridePosition;

    /// <summary>
    /// Set by CameraController to reflect configuration.FreeFly. Corrects
    /// a real mistake: the class comment above already correctly
    /// described Cammy's actual distinction ("only skips Original()
    /// entirely during its own true FreeCam mode"), but the Detour below
    /// was written to skip it - then later changed to always call it -
    /// unconditionally, for every mode, contradicting that. True Free
    /// Fly used to never invoke the native function at all, and never
    /// shook; once Original() started running unconditionally, Free Fly
    /// started shaking and became hard to actually fly, which is
    /// consistent with the native function having side effects beyond
    /// just writing its own position guess (updating InterpDistance or
    /// similar) even when that guess gets immediately overwritten.
    /// True Free Fly now goes back to skipping Original() entirely, the
    /// same as Cammy's own true FreeCam mode - the "call Original() first"
    /// behavior stays for orbit/tripod/translate, matching Cammy's
    /// "normal mode" behavior, which is where it was actually intended.
    /// </summary>
    public bool IsTrueFreeFly;

    public bool IsInstalled => hook != null;
    public string Status { get; private set; } = "Not installed";

    public WorldCameraPositionHook(IGameInteropProvider gameInteropProvider, IPluginLog log)
    {
        this.gameInteropProvider = gameInteropProvider;
        this.log = log;
    }

    internal void Install(RawGameCamera* camera)
    {
        if (hook != null) return;
        if (camera == null || camera->VTable == null)
        {
            Status = "Camera not ready";
            return;
        }

        try
        {
            nint targetAddress = camera->VTable[GetCameraPositionVTableIndex];
            if (targetAddress == 0)
            {
                Status = "Vtable slot 16 was null";
                return;
            }

            hook = gameInteropProvider.HookFromAddress<GetCameraPositionDelegate>(targetAddress, Detour);
            hook.Enable();
            Status = "Installed";
            log.Information($"[CamCam] getCameraPosition hook installed at 0x{targetAddress:X} (vtable slot {GetCameraPositionVTableIndex})");
        }
        catch (Exception ex)
        {
            Status = $"Failed to install: {ex.Message}";
            log.Warning(ex, "[CamCam] Failed to install getCameraPosition hook");
            hook = null;
        }
    }

    public void Remove()
    {
        hook?.Disable();
        hook?.Dispose();
        hook = null;
        OverridePosition = null;
        Status = "Not installed";
    }

    /// <summary>
    /// Counts every time the Detour actually runs, regardless of whether
    /// OverridePosition was set - lets CameraController verify the hook
    /// is genuinely firing every frame rather than assuming it. If the
    /// camera's own X/Y/Z keeps drifting away from what OverridePosition
    /// says between calls, that's a gap something else can move the
    /// camera through - if this count matches the game's own frame rate,
    /// that's ruled out and the drift is happening despite the hook
    /// firing normally, not because of it firing too rarely.
    /// </summary>
    public ulong DetourCallCount { get; private set; }

    /// <summary>
    /// Compared directly against Cammy's own working getCameraPosition
    /// hook (Game.cs, GetCameraPositionDetour): Cammy calls the native
    /// Original() first for its normal modes, then layers its own
    /// adjustment on top (height/side offset) - it only skips Original()
    /// entirely during its own true FreeCam mode. This hook used to skip
    /// Original() unconditionally whenever OverridePosition was set -
    /// essentially every frame in every follow mode - meaning the native
    /// function's own internal bookkeeping never got to run at all.
    /// Then it was changed the other way, to always call Original()
    /// unconditionally - which fixed that for orbit/tripod/translate, but
    /// broke true Free Fly, which had never called it before and never
    /// shook; once it started running there too, Free Fly started
    /// shaking and became hard to fly (user-confirmed). See
    /// IsTrueFreeFly's comment - this now correctly matches Cammy's own
    /// distinction: Original() is called for orbit/tripod/translate, and
    /// skipped entirely for true Free Fly.
    /// </summary>
    private void Detour(nint camera, nint target, Vector3* position, byte swapPerson)
    {
        DetourCallCount++;

        if (IsTrueFreeFly)
        {
            // True Free Fly - matches Cammy's own true FreeCam mode:
            // skip Original() entirely. Nothing about this position is
            // derived from anything the native function would compute
            // anyway, so there's no reason to invoke it and risk
            // whatever side effects it has beyond its own output.
            if (OverridePosition.HasValue)
                *position = OverridePosition.Value;
            return;
        }

        // Orbit/Tripod/Strafe - matches Cammy's own "normal" mode: call
        // Original() first so the native function's own bookkeeping
        // stays in sync, then overwrite with our own intended position.
        hook!.Original(camera, target, position, swapPerson);

        if (OverridePosition.HasValue)
        {
            *position = OverridePosition.Value;
        }
    }

    public void Dispose() => Remove();
}
