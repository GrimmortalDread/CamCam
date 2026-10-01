using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;

namespace CamCam;

/// <summary>
/// Every frame, if CamCam is enabled, this drives the camera one of three
/// ways:
///  - FreeFly on: true 3D movement + turning via the configured keys,
///    using the getCameraPosition hook (vtable slot 16). Optionally
///    clamped above an approximate ground height. Engages immediately -
///    flying is already a deliberate action, not something that should
///    wait for you to stand still.
///  - FreeFly off, FollowMode set: fully manual position (look-at point +
///    rotation + zoom + independent height offset), angle relative to
///    the target's own facing so it stays consistent across whoever you
///    cycle to. Only engages after standing still for IdleThresholdSeconds,
///    same "cinematic idle camera" framing CamIdleHijack uses.
///  - Neither: orbits your own character, same as the base game.
///
/// In every mode, holding right or left mouse button (the game's own
/// camera-rotate / click controls) makes CamCam back off completely for
/// as long as it's held plus a short cooldown after release - same fix
/// CamIdleHijack already uses so the plugin doesn't fight your own manual
/// camera input.
/// </summary>
public unsafe class CameraController
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_RBUTTON = 0x02;
    private const int VK_LBUTTON = 0x01;
    private const float ManualInputCooldownSeconds = 2f;

    // Replaces the old fixed 1.3f lookAt height offset. Derived from real
    // /xllog "height diag" data: the dev's own character reads
    // realHeight=0.930, and 1.3f was the value that already looked right
    // for that character (tuned by eye before this existed), so
    // 1.3 / 0.93 ~= 1.4 is the scale that reproduces the old, known-good
    // number for an average-height character while scaling proportionally
    // down for lalafells (~0.6 -> ~0.84) and up for the tallest races
    // (~1.2 -> ~1.68). If a race still looks off after this, that's the
    // number to nudge.
    private const float HeightScaleFactor = 1.4f;

    private readonly Configuration configuration;
    private readonly ITargetManager targetManager;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly WorldCameraTargetHook targetHook;
    private readonly WorldCameraPositionHook positionHook;
    private readonly FreeCamController freeCam;
    private readonly FlyKeyBlocker keyBlocker;
    private readonly IKeyState keyState;
    private readonly IPluginLog log;

    private uint? cycleSelectedPlayerEntityId;
    private int cycleViewIndex;
    private float cycleTimer;
    private float presetCycleTimer;
    private SavedView? activePanView;
    private float panCurrentDegrees;
    private int panDirection = 1;
    private int panLegsCompleted;
    private bool panMotionComplete;
    private float vPanCurrentDegrees;
    private int vPanDirection = 1;
    private int vPanLegsCompleted;
    private bool vPanMotionComplete;
    private float zoomPanCurrentValue;
    private int zoomPanDirection = 1;
    private int zoomPanLegsCompleted;
    private bool zoomPanMotionComplete;
    // Shared clock for all three pan axes' start delays - how long the
    // current activePanView has been active. Reset in SetActivePanView
    // whenever a genuinely new view becomes active.
    private float activePanElapsedSeconds;
    private bool panAdvanceHasFired;
    private float? originalMinZoom;
    private float? originalMaxVRotation;
    private float? originalMinVRotation;
    // Vertical-angle jump detector - separate from the position-based
    // shake detector, since nothing built so far specifically watches
    // rotation for a sudden change. See where it's used for what it
    // checks.
    private float previousVRotation2;
    private bool hasPreviousVRotation2;
    private uint previousVRotationTargetEntityId;
    private readonly Random random = new();
    private int? previousRandomViewIndex;
    private CameraFollowMode? lastFollowMode;

    /// <summary>
    /// Random index different from both the current index and the one
    /// used immediately before it (the "second-to-last" used preset), so
    /// a short random sequence doesn't feel like it's just bouncing
    /// between two presets. Falls back to only avoiding the current index
    /// when there aren't enough presets to avoid both without leaving
    /// nothing to pick (e.g. exactly two SavedViews total, or "previous"
    /// is stale/out of range because presets were added or removed since
    /// it was last set). Used only by the preset-cycle timer, not manual
    /// Next/Previous or the pan-completion trigger.
    /// </summary>
    private int NextRandomViewIndex(int current, int? previous, int count)
    {
        if (count <= 1) return current;

        bool canAvoidBoth = previous.HasValue
            && previous.Value >= 0 && previous.Value < count
            && previous.Value != current
            && count > 2;

        int next;
        do { next = random.Next(count); } while (next == current || (canAvoidBoth && next == previous!.Value));
        return next;
    }

    /// <summary>
    /// When true, both the Auto cycle (player) and Auto preset cycle
    /// timers are frozen in place - manual Next/Previous still works.
    /// Session-only, not persisted, so it can't accidentally stay stuck
    /// on across game restarts.
    /// </summary>
    public bool CyclePaused { get; set; }
    private bool uiCurrentlyHiddenByUs;
    private float manualInputCooldownTimer;
    // See the stuck-key-state safety net comment in WriteCameraPose.
    private float continuousGrabDurationSeconds;
    private bool freeFlyToggleKeyWasHeld;
    private bool numpadBlockToggleKeyWasHeld;
    private bool numpadBlockManuallyDisabled;
    // Tracks whether SelectChosenTarget's result actually changed frame
    // to frame while Free Fly is active - see the Free Fly branch's own
    // comment for why this matters (avoiding fighting manual rotation
    // every frame just because cycling happens to be on).
    private uint? lastFreeFlyTargetEntityId;
    private bool toggleCamCamKeyWasHeld;
    private float manualSliderCooldownTimer;
    private const float ManualSliderCooldownSeconds = 2.5f;
    private Vector3 lastPlayerPos;
    private bool hasLastPlayerPos;
    private float idleTimer;
    private float statusLogTimer;
    private const float StatusLogIntervalSeconds = 2f;

    // Diagnostic only, for tracking down "never switches while the target
    // is moving" reports - not used to gate any cycling behavior, since
    // nothing in this file currently ties cycle advancement to whether
    // the target (as opposed to you, the local player - see idleTimer
    // above) is moving at all.
    private Vector3 lastCycleTargetPos;
    private uint lastCycleTargetEntityId;
    private bool hasLastCycleTargetPos;
    private bool cycleTargetIsMovingForLog;

    // Position smoothing state - see PositionSmoothingSeconds' comment in
    // Configuration.cs. lastSmoothedTargetEntityId lets the smoothing
    // step detect when the actual SUBJECT changed (a different player in
    // Cycle, or a FollowMode switch) versus just continuing to orbit the
    // same one - only the latter should ease; the former is a real cut
    // and should snap immediately or it'd visibly glide across the map.
    private Vector3 smoothedPosition;
    private bool hasSmoothedPosition;
    private uint lastSmoothedTargetEntityId;

    // Shake detector - see the comment where it's used, right after the
    // position write. Not used to drive any behavior, purely diagnostic.
    private Vector3 previousFrameFinalPos;
    private bool hasPreviousFrameWrite;
    private uint previousFrameTargetEntityId;
    private ulong previousDetourCallCount;

    // See GetStableTargetRotation's comment.
    private float stableTargetRotation;
    private bool hasStableTargetRotation;
    private uint lastStableRotationTargetEntityId;

    // Diagnostic only, for the raw (unfiltered) rotation delta in the
    // per-frame log - separate from the stabilization fields above so
    // the log always shows the true raw jitter regardless of the
    // deadzone filtering that GetStableTargetRotation applies.
    private float previousRawTargetRotation;
    private bool hasPreviousRawTargetRotation;
    private uint previousRawTargetRotationTargetEntityId;
    // See the rawTargetPositionDelta comment further down - same idea as
    // the rotation tracking above, for the target's own position.
    private Vector3 previousRawTargetPosition;
    private bool hasPreviousRawTargetPosition;
    private uint previousRawTargetPositionEntityId;

    // Fixed-camera pass-by snapshot - see FixedCameraPassBy's comment in
    // Configuration.cs. Captured once when a (view, target) combination
    // first becomes active, then held until either changes.
    private bool hasFixedCameraSnapshot;
    private SavedView? fixedCameraSnapshotView;
    private uint fixedCameraSnapshotTargetEntityId;
    private Vector3 fixedCameraSnapshotPosition;
    private float fixedCameraSnapshotTargetRotation;
    // Only used when TranslateInsteadOfPan is also on - the camera's own
    // facing is frozen too, not just its position, so it needs its own
    // snapshot of H/V rotation (distinct from fixedCameraSnapshotTargetRotation,
    // which is the TARGET's facing, not the camera's).
    private float fixedCameraSnapshotHRotation;
    private float fixedCameraSnapshotVRotation;
    // Independent of activePanElapsedSeconds (which only increments while
    // Horizontal/Vertical/Zoom pan is enabled) - a Translate-only preset
    // may have none of those on at all, so its own start delay needs its
    // own clock.
    private float fixedCameraSnapshotElapsedSeconds;
    private float translatePanCurrentDistance;
    private int translatePanDirection = 1;
    private int translatePanLegsCompleted;
    private bool translatePanMotionComplete;
    private string equipmentIdsForLog = "";

    public string Status { get; private set; } = "Idle";
    public string CurrentCycleName { get; private set; } = "(none nearby)";

    /// <summary>
    /// Index into SavedViews of whichever view was most recently Loaded
    /// or auto-cycled to - used by the settings window to highlight which
    /// row is currently active, so it's obvious which one Update would
    /// actually resave. Meaningless (defaults to 0) until something has
    /// actually been Loaded or Cycle has advanced at least once.
    /// </summary>
    public int CurrentViewIndex => cycleViewIndex;

    /// <summary>
    /// Direct access to Free Fly's own raw position, one axis at a time -
    /// what "a slider for every parameter, matching what the arrows do"
    /// needs, since Free Fly's position isn't naturally expressed as
    /// H/V/Zoom the way orbit mode's is (it's not orbiting a target at a
    /// fixed distance, it's a free point in space). Setting any of these
    /// calls FreeCamController.ResetTo under the hood, same as loading a
    /// preset does - the new position sticks (HasStartingPosition stays
    /// true), rather than getting treated as a fresh flight needing
    /// re-initialization.
    /// </summary>
    public float FreeFlyPositionX
    {
        get => freeCam.Position.X;
        set => freeCam.ResetTo(new Vector3(value, freeCam.Position.Y, freeCam.Position.Z));
    }
    public float FreeFlyPositionY
    {
        get => freeCam.Position.Y;
        set => freeCam.ResetTo(new Vector3(freeCam.Position.X, value, freeCam.Position.Z));
    }
    public float FreeFlyPositionZ
    {
        get => freeCam.Position.Z;
        set => freeCam.ResetTo(new Vector3(freeCam.Position.X, freeCam.Position.Y, value));
    }

    public CameraController(
        Configuration configuration,
        ITargetManager targetManager,
        IObjectTable objectTable,
        IClientState clientState,
        WorldCameraTargetHook targetHook,
        WorldCameraPositionHook positionHook,
        FreeCamController freeCam,
        FlyKeyBlocker keyBlocker,
        IKeyState keyState,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.targetManager = targetManager;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.targetHook = targetHook;
        this.positionHook = positionHook;
        this.freeCam = freeCam;
        this.keyBlocker = keyBlocker;
        this.keyState = keyState;
        this.log = log;
    }

    public void CycleNext()
    {
        var players = GetCycleCandidates();
        if (players.Count > 0)
        {
            int idx = (ResolvePlayerIndex(players) + 1) % players.Count;
            cycleSelectedPlayerEntityId = players[idx].EntityId;
        }
        if (configuration.CycleUseSavedViews && configuration.SavedViews.Count > 0)
            cycleViewIndex = (cycleViewIndex + 1) % configuration.SavedViews.Count;
        cycleTimer = 0f;
        presetCycleTimer = 0f;
    }

    public void CyclePrevious()
    {
        var players = GetCycleCandidates();
        if (players.Count > 0)
        {
            int idx = (ResolvePlayerIndex(players) - 1 + players.Count) % players.Count;
            cycleSelectedPlayerEntityId = players[idx].EntityId;
        }
        if (configuration.CycleUseSavedViews && configuration.SavedViews.Count > 0)
            cycleViewIndex = (cycleViewIndex - 1 + configuration.SavedViews.Count) % configuration.SavedViews.Count;
        cycleTimer = 0f;
        presetCycleTimer = 0f;
    }

    /// <summary>
    /// Finds where the currently-selected player sits in a freshly-built
    /// candidate list, by stable identity (EntityId) rather than trusting
    /// a remembered index - the list's order/contents can change frame to
    /// frame as players enter or leave render range, so an index alone
    /// would silently point at whoever's now at that position instead of
    /// the actual person selected. Falls back to 0 if there's no prior
    /// selection or it's no longer in range.
    /// </summary>
    private int ResolvePlayerIndex(List<IPlayerCharacter> players)
    {
        if (cycleSelectedPlayerEntityId.HasValue)
        {
            int idx = players.FindIndex(p => p.EntityId == cycleSelectedPlayerEntityId.Value);
            if (idx >= 0) return idx;

            // The previously-selected player isn't in this frame's
            // candidate list even though nothing asked to advance -
            // likely they dropped out of GetCycleCandidates (out of
            // render range, gender filter, zone change, logged out) for
            // a moment. Falling back to index 0 below picks whoever's
            // first in the list, which can look like unwanted cycling
            // even with Auto cycle off. Logging this so it's visible
            // whether that's actually what's happening.
            log.Information($"[CamCam] cycle target lost: entityId={cycleSelectedPlayerEntityId.Value} not in candidate list of {players.Count} - falling back to index 0");
        }
        return 0;
    }

    /// <summary>Call while the horizontal/vertical/zoom sliders are actively being dragged, so saved-views auto-cycling doesn't overwrite the change mid-adjustment.</summary>
    public void NotifySliderAdjusted() => manualSliderCooldownTimer = ManualSliderCooldownSeconds;

    /// <summary>
    /// Loads a saved view's settings into the live config, for the
    /// settings window's Load button. Routed through here rather than
    /// writing config fields directly so a panning view starts animating
    /// correctly instead of just being applied once.
    /// </summary>
    public void LoadView(SavedView view, int index)
    {
        // Forces the next frame's position to snap straight to the
        // freshly-loaded values instead of easing toward them if
        // PositionSmoothingSeconds is on for this preset - Load is an
        // explicit "show me this now" action, same reasoning as the
        // activePanView reset right below for pan. Without this, editing
        // a preset's own numbers and clicking Load to check them would
        // visibly ease in over PositionSmoothingSeconds instead of
        // updating immediately, which looked like Load wasn't working at
        // all until Cycle happened to swap targets on its own (the one
        // other place a hard reset already happened).
        hasSmoothedPosition = false;

        // Same idea again: while Free Fly is active, HorizontalRotation/
        // VerticalRotation getting set below (from the loaded preset)
        // wouldn't actually move the Free Fly camera at all, since its
        // starting position is only computed once per flight (see the
        // HasStartingPosition guard in the Free Fly branch above).
        // Clearing it here means Load actually repositions Free Fly to
        // the loaded preset's angle/zoom on the very next frame, instead
        // of silently doing nothing while flying.
        //
        // User clarified the actual want: the SAME relative framing
        // (H/V/Zoom) reproduced on whoever the current target is, exactly
        // like orbit mode already does - not a fixed absolute position
        // (which was tried first, based on a misreading of the original
        // complaint). The original bug wasn't relative-vs-absolute: it
        // was that the reset computation always referenced LocalPlayer
        // regardless of who was actually targeted, and used a hardcoded
        // zoom instead of the preset's own saved one - both fixed at the
        // source, in the Free Fly branch's own reset computation.
        freeCam.ClearStartingPosition();

        // Same reasoning as the pan reset below - Load should always
        // restart a Tripod/Translate preset fresh too, not silently keep
        // whatever snapshot and translate progress were already in
        // place from before Load was clicked. Without this,
        // hasExistingFixedCameraSnapshot's own "only reset if the view
        // or target actually changed" check no-ops on a re-click of the
        // same already-active preset, which is exactly why Load wasn't
        // visibly doing anything on a Translate-mode preset.
        if (view.FixedCameraPassBy || view.TranslateInsteadOfPan)
        {
            hasFixedCameraSnapshot = false;
        }

        configuration.Zoom = view.Zoom;
        configuration.FollowHeightOffset = view.HeightOffset;
        configuration.FollowMinZoom = view.MinZoom;
        configuration.FollowMaxAngleDegrees = view.MaxAngleDegrees;
        configuration.FollowHeightLockToGround = view.HeightLockToGround;
        configuration.FollowHeightGroundClearance = view.HeightGroundClearance;
        configuration.FollowPositionSmoothingSeconds = view.PositionSmoothingSeconds;
        configuration.FollowAvoidWallsAndObjects = view.AvoidWallsAndObjects;
        configuration.FollowWallAvoidanceBuffer = view.WallAvoidanceBuffer;

        if (view.PanEnabled || view.VerticalPanEnabled || view.ZoomPanEnabled)
        {
            // Force-reset even if this is already the active pan view -
            // clicking Load should always restart the preset fresh,
            // regardless of whether it was already selected. Without
            // this, SetActivePanView's "only reset if the view actually
            // changed" check silently no-ops on a re-click of the same
            // preset, since it's still the same object reference.
            activePanView = null;
            SetActivePanView(view);
            if (!view.PanEnabled) configuration.HorizontalRotation = view.HorizontalRotation;
            if (!view.VerticalPanEnabled) configuration.VerticalRotation = view.VerticalRotation;
            if (!view.ZoomPanEnabled) configuration.Zoom = view.Zoom;
        }
        else
        {
            configuration.HorizontalRotation = view.HorizontalRotation;
            configuration.VerticalRotation = view.VerticalRotation;
            SetActivePanView(null);
        }

        configuration.Save();
        SetCycleView(index);
    }

    /// <summary>
    /// Declares which saved view's Horizontal angle should be driven by
    /// cinematic panning right now, if any - shared by both manually
    /// Loading a panning view and Cycle mode auto-cycling onto one, so
    /// there's exactly one pan mechanism regardless of how a view became
    /// active. Resets sweep progress back to the view's own saved
    /// HorizontalRotation whenever the active view actually changes;
    /// repeated calls with the same view leave progress alone. Pass null
    /// (or a non-panning view) to stop.
    /// </summary>
    /// <summary>
    /// Signed shortest-path distance from 'from' to 'to' (both degrees,
    /// each independently within +-180), normalized to (-180, 180]. Plain
    /// "to >= from" comparisons break near the wrap seam - going from 170
    /// to -170 is really a short +20 step through 180/-180, not a long
    /// -340 step the other way, and worse, near that seam both a
    /// positive and negative To can land on the same side of from,
    /// making the sign of To look like it does nothing at all.
    /// </summary>
    /// <summary>
    /// Real ground height at a given X/Z, found via the game's own
    /// collision raycast system (BGCollisionModule) instead of
    /// approximating with the target's own Y position - the target's Y
    /// is only accurate directly under the target, not wherever the
    /// camera's own X/Z happens to be after a pan sweeps it elsewhere,
    /// which is exactly the mismatch that caused the old Ground lock's
    /// popping/shaking on uneven terrain. Casts straight down from
    /// startY (the higher of the camera's own candidate Y and the
    /// target's Y, plus a small buffer - see the call site). Deliberately
    /// NOT a large fixed offset like +15 above the target: in an indoor
    /// or multi-story area (housing, buildings with upper floors), a tall
    /// blind offset can hit a roof/ceiling/floor above instead of the
    /// real ground underneath, clamping the camera to the WRONG,
    /// too-high position - confirmed via /xllog showing a rock-steady
    /// ~0.9 unit Y-axis gap between what was written and what the camera
    /// actually settled at, not the noisy/growing pattern seen elsewhere.
    /// Returns null if nothing was hit within range (e.g. over a void, or
    /// the raycast genuinely failed) - callers should fall back to the
    /// old approximation rather than leave the camera completely
    /// unclamped.
    /// </summary>
    private static float? RaycastGroundHeight(float x, float z, float startY)
    {
        var origin = new Vector3(x, startY, z);
        if (BGCollisionModule.RaycastMaterialFilter(origin, new Vector3(0f, -1f, 0f), out var hit, 50f))
            return hit.Point.Y;
        return null;
    }

    /// <summary>
    /// Checks whether anything blocks the straight line from lookAtPoint
    /// to desiredPos, and if so, returns a position pulled in to just
    /// short of the hit point instead. This is the standard third-person
    /// camera collision technique - determining, ourselves, whether a
    /// wall or object sits between the subject and the camera, under our
    /// own deterministic control, rather than relying on (and fighting)
    /// whatever the game's native camera does internally. Returns
    /// desiredPos unchanged if the line is clear or the raycast fails.
    /// </summary>
    private static Vector3 AvoidWallsAndObjects(Vector3 lookAtPoint, Vector3 desiredPos, float buffer)
    {
        Vector3 offset = desiredPos - lookAtPoint;
        float desiredDistance = offset.Length();
        if (desiredDistance < 0.01f) return desiredPos;

        Vector3 direction = offset / desiredDistance;
        if (BGCollisionModule.RaycastMaterialFilter(lookAtPoint, direction, out var hit, desiredDistance)
            && hit.Distance < desiredDistance)
        {
            float safeDistance = MathF.Max(hit.Distance - buffer, 0.1f);
            return lookAtPoint + direction * safeDistance;
        }
        return desiredPos;
    }

    /// <summary>
    /// The actual fix for the numpad-still-triggers-game-actions problem.
    /// FlyKeyBlocker's WH_KEYBOARD_LL hook only blocks the Windows
    /// message queue - it was never able to stop the game from reacting
    /// to these keys, because FFXIV (like most games) reads its own
    /// keyboard state from an internal buffer, not the message queue.
    /// IKeyState is Dalamud's own official wrapper around that exact
    /// buffer - writable, not just readable. Setting a key false here
    /// tells the game directly "this key isn't pressed" regardless of
    /// what's physically happening at the OS level, which is the actual
    /// mechanism other established plugins (e.g. Brio) use for this same
    /// purpose. CamCam's own GetAsyncKeyState-based reads (in
    /// FreeCamController) are unaffected by this - they read the real OS
    /// state directly, not this buffer, so camera movement itself still
    /// works normally. Called every frame while flying is active, since
    /// the game likely keeps refreshing its own buffer continuously - a
    /// one-time clear wouldn't stick.
    /// </summary>
    private void ClearGameKeyState(IEnumerable<string> keyNames)
    {
        foreach (var name in keyNames)
        {
            if (string.IsNullOrWhiteSpace(name)) continue;
            var vk = FreeCamController.ResolveKey(name);
            if (vk == 0) continue;

            try
            {
                if (keyState.IsVirtualKeyValid(vk))
                    keyState[vk] = false;
            }
            catch (Exception ex)
            {
                log.Warning(ex, $"[CamCam] ClearGameKeyState failed for '{name}' (vk 0x{vk:X2})");
            }
        }
    }

    private static float ShortestAngleDeltaDegrees(float from, float to)
    {
        float delta = (to - from) % 360f;
        if (delta > 180f) delta -= 360f;
        else if (delta < -180f) delta += 360f;
        return delta;
    }

    /// <summary>Same as ShortestAngleDeltaDegrees, in radians.</summary>
    private static float ShortestAngleDeltaRadians(float from, float to)
    {
        float delta = (to - from) % (2f * MathF.PI);
        if (delta > MathF.PI) delta -= 2f * MathF.PI;
        else if (delta < -MathF.PI) delta += 2f * MathF.PI;
        return delta;
    }

    private const float TargetRotationDeadzoneRadians = 0.01745f; // ~1 degree

    /// <summary>
    /// Filters out sub-degree, frame-to-frame noise in a target's own
    /// facing before it's used for anything - idle sway, breathing
    /// animation, or network interpolation correction on a character
    /// that isn't your own can all cause tiny, high-frequency rotation
    /// jitter that's easy to miss reading raw numbers but very visible
    /// once it's multiplied by zoom distance into camera position every
    /// single frame. Holds the last stable value until the target
    /// actually rotates past the deadzone (a real, deliberate turn),
    /// then jumps straight to the new raw value - no smoothing lag on
    /// genuine turns, just immunity to noise too small to be a turn at
    /// all. Resets immediately (no deadzone) on the first read for a
    /// given target, so switching targets never lags behind by a frame.
    /// </summary>
    private float GetStableTargetRotation(IGameObject target)
    {
        float raw = target.Rotation;
        if (!hasStableTargetRotation || lastStableRotationTargetEntityId != target.EntityId)
        {
            stableTargetRotation = raw;
            hasStableTargetRotation = true;
            lastStableRotationTargetEntityId = target.EntityId;
        }
        else if (MathF.Abs(ShortestAngleDeltaRadians(stableTargetRotation, raw)) > TargetRotationDeadzoneRadians)
        {
            stableTargetRotation = raw;
        }
        return stableTargetRotation;
    }

    private void SetActivePanView(SavedView? view)
    {
        bool anyPanEnabled = view != null && (view.PanEnabled || view.VerticalPanEnabled || view.ZoomPanEnabled);
        if (!anyPanEnabled)
        {
            activePanView = null;
            return;
        }

        if (!ReferenceEquals(activePanView, view))
        {
            activePanView = view;
            panAdvanceHasFired = false;
            activePanElapsedSeconds = 0f;

            // Always starts from the preset's own saved angle on each
            // axis - not a separate "From" value that could silently
            // drift out of sync with what's actually saved for this
            // preset. An axis that isn't enabled for this view is marked
            // already-complete so it doesn't block the other axis's
            // completion trigger from firing.
            if (view!.PanEnabled)
            {
                float startDegrees = view.HorizontalRotation * (180f / MathF.PI);
                panCurrentDegrees = startDegrees;
                // Always heads toward PanToDegrees first, via whichever
                // way round is actually shorter - see
                // ShortestAngleDeltaDegrees for why a plain numeric
                // comparison here breaks near +-180.
                panDirection = ShortestAngleDeltaDegrees(startDegrees, view.PanToDegrees) >= 0f ? 1 : -1;
                panLegsCompleted = 0;
                panMotionComplete = false;
            }
            else
            {
                panMotionComplete = true;
            }

            if (view.VerticalPanEnabled)
            {
                float vStartDegrees = view.VerticalRotation * (180f / MathF.PI);
                vPanCurrentDegrees = vStartDegrees;
                vPanDirection = view.VerticalPanToDegrees >= vStartDegrees ? 1 : -1;
                vPanLegsCompleted = 0;
                vPanMotionComplete = false;
            }
            else
            {
                vPanMotionComplete = true;
            }

            if (view.ZoomPanEnabled)
            {
                zoomPanCurrentValue = view.Zoom;
                zoomPanDirection = view.ZoomPanToValue >= view.Zoom ? 1 : -1;
                zoomPanLegsCompleted = 0;
                zoomPanMotionComplete = false;
            }
            else
            {
                zoomPanMotionComplete = true;
            }
        }
    }

    /// <summary>A cinematic pan is a one-time move, not a perpetual back-and-forth scan - sweeps from the preset's own saved Horizontal angle to PanToDegrees once (or there and back once if PanReturnBeforeAdvance is on), then holds still at wherever it finished. No-op once panMotionComplete is set, until the active view changes again via SetActivePanView.</summary>
    private void AdvanceActivePan(float deltaSeconds)
    {
        if (activePanView == null || panMotionComplete) return;
        var view = activePanView;
        if (activePanElapsedSeconds < view.HorizontalPanStartDelaySeconds) return;

        float startDegrees = view.HorizontalRotation * (180f / MathF.PI);
        panCurrentDegrees += panDirection * view.PanSpeedDegreesPerSecond * deltaSeconds;

        // Unwrapped target: startDegrees plus the shortest signed delta,
        // which can land outside +-180 (e.g. 170 -> -170 becomes a target
        // of 190, not -170) so the sweep can keep going straight through
        // the wrap seam instead of hitting a bound computed on the wrong
        // side of it. Sin/Cos downstream don't care that this temporarily
        // exceeds +-180 - they're periodic either way.
        float target = startDegrees + ShortestAngleDeltaDegrees(startDegrees, view.PanToDegrees);
        float lo = MathF.Min(startDegrees, target);
        float hi = MathF.Max(startDegrees, target);

        bool justHitBound = false;
        if (panCurrentDegrees >= hi) { panCurrentDegrees = hi; justHitBound = true; }
        else if (panCurrentDegrees <= lo) { panCurrentDegrees = lo; justHitBound = true; }

        configuration.HorizontalRotation = panCurrentDegrees * (MathF.PI / 180f);

        if (justHitBound)
        {
            panLegsCompleted++;
            int legsNeeded = view.PanReturnBeforeAdvance ? 2 : 1;
            if (panLegsCompleted >= legsNeeded)
            {
                // Sweep is done - stop moving and hold at this position.
                panMotionComplete = true;
            }
            else
            {
                // Only reached when PanReturnBeforeAdvance is on and this
                // was the first leg (start -> To) - turn around for the
                // return trip back to the starting angle.
                panDirection = -panDirection;
            }
        }
    }

    /// <summary>Same idea as AdvanceActivePan, for Vertical angle instead of Horizontal.</summary>
    private void AdvanceActiveVerticalPan(float deltaSeconds)
    {
        if (activePanView == null || vPanMotionComplete) return;
        var view = activePanView;
        if (activePanElapsedSeconds < view.VerticalPanStartDelaySeconds) return;

        float startDegrees = view.VerticalRotation * (180f / MathF.PI);
        vPanCurrentDegrees += vPanDirection * view.VerticalPanSpeedDegreesPerSecond * deltaSeconds;
        float lo = MathF.Min(startDegrees, view.VerticalPanToDegrees);
        float hi = MathF.Max(startDegrees, view.VerticalPanToDegrees);

        bool justHitBound = false;
        if (vPanCurrentDegrees >= hi) { vPanCurrentDegrees = hi; justHitBound = true; }
        else if (vPanCurrentDegrees <= lo) { vPanCurrentDegrees = lo; justHitBound = true; }

        configuration.VerticalRotation = vPanCurrentDegrees * (MathF.PI / 180f);

        if (justHitBound)
        {
            vPanLegsCompleted++;
            int legsNeeded = view.PanReturnBeforeAdvance ? 2 : 1;
            if (vPanLegsCompleted >= legsNeeded)
            {
                vPanMotionComplete = true;
            }
            else
            {
                vPanDirection = -vPanDirection;
            }
        }
    }

    /// <summary>Same idea as AdvanceActivePan/AdvanceActiveVerticalPan, for Zoom (camera distance) instead of an angle. No angle-wrap concerns here since zoom is a plain linear distance.</summary>
    private void AdvanceActiveZoomPan(float deltaSeconds)
    {
        if (activePanView == null || zoomPanMotionComplete) return;
        var view = activePanView;
        if (activePanElapsedSeconds < view.ZoomPanStartDelaySeconds) return;

        zoomPanCurrentValue += zoomPanDirection * view.ZoomPanSpeed * deltaSeconds;
        float lo = MathF.Min(view.Zoom, view.ZoomPanToValue);
        float hi = MathF.Max(view.Zoom, view.ZoomPanToValue);

        bool justHitBound = false;
        if (zoomPanCurrentValue >= hi) { zoomPanCurrentValue = hi; justHitBound = true; }
        else if (zoomPanCurrentValue <= lo) { zoomPanCurrentValue = lo; justHitBound = true; }

        configuration.Zoom = zoomPanCurrentValue;

        if (justHitBound)
        {
            zoomPanLegsCompleted++;
            int legsNeeded = view.PanReturnBeforeAdvance ? 2 : 1;
            if (zoomPanLegsCompleted >= legsNeeded)
            {
                zoomPanMotionComplete = true;
            }
            else
            {
                zoomPanDirection = -zoomPanDirection;
            }
        }
    }

    /// <summary>Fires the "advance to next preset" trigger once both enabled pan axes (horizontal, vertical, or both) have finished - checked once per frame after both axes have had a chance to advance, so a fast horizontal sweep doesn't trigger the advance while a slower vertical one is still going.</summary>
    private void CheckPanAdvanceComplete()
    {
        if (activePanView == null || panAdvanceHasFired) return;
        var view = activePanView;
        if (!view.PanAdvanceCycleOnComplete) return;

        bool hDone = !view.PanEnabled || panMotionComplete;
        bool vDone = !view.VerticalPanEnabled || vPanMotionComplete;
        bool zDone = !view.ZoomPanEnabled || zoomPanMotionComplete;
        if (!hDone || !vDone || !zDone) return;

        panAdvanceHasFired = true;
        if (configuration.FollowMode == CameraFollowMode.Cycle && configuration.CycleUseSavedViews)
        {
            CycleNext();
        }
    }

    /// <summary>
    /// Same idea as CheckPanAdvanceComplete, but for a translate-mode
    /// fixed camera specifically - that preset type may have no
    /// Horizontal/Vertical/Zoom pan enabled at all (translation is its
    /// own, separate motion), meaning activePanView often stays null and
    /// CheckPanAdvanceComplete's own gate never even runs. Reuses
    /// panAdvanceHasFired to avoid double-firing if a preset somehow has
    /// both kinds of motion enabled at once.
    /// </summary>
    private void CheckTranslatePanAdvanceComplete(SavedView view)
    {
        if (panAdvanceHasFired || !translatePanMotionComplete || !view.PanAdvanceCycleOnComplete) return;

        panAdvanceHasFired = true;
        if (configuration.FollowMode == CameraFollowMode.Cycle && configuration.CycleUseSavedViews)
        {
            CycleNext();
        }
    }

    /// <summary>
    /// Called when a saved view is manually Loaded from the settings
    /// window - syncs the auto-cycle's internal index to match, so the
    /// manual choice actually sticks instead of being overwritten by the
    /// next auto-cycle tick a moment later. Resets BOTH timers, not just
    /// cycleTimer - if presetCycleTimer happened to already be seconds
    /// away from its own threshold, it could fire moments after this
    /// manual Load and immediately auto-advance away from the preset
    /// that was just explicitly picked. Matches what CycleNext/
    /// CyclePrevious/CyclePaused already do for the same reason.
    /// </summary>
    public void SetCycleView(int index)
    {
        if (configuration.SavedViews.Count == 0) return;
        cycleViewIndex = Math.Clamp(index, 0, configuration.SavedViews.Count - 1);
        cycleTimer = 0f;
        presetCycleTimer = 0f;
    }

    /// <summary>
    /// True when the currently active saved view has "Advance to next
    /// preset after pan completes" on and its pan (horizontal, vertical,
    /// or both, whichever it has enabled) hasn't reached its target angle
    /// yet. Used to gate BOTH the preset-cycle timer and the player-cycle
    /// timer, so they can't drift out of sync with each other - before
    /// this was shared, the player timer ran on a totally separate clock
    /// with no idea a pan was still sweeping, so it could swap to a new
    /// player mid-sweep even while the preset timer was correctly holding
    /// off for the same pan.
    /// </summary>
    private bool IsCurrentPanStillInProgress()
    {
        if (configuration.SavedViews.Count == 0) return false;

        int idx = Math.Clamp(cycleViewIndex, 0, configuration.SavedViews.Count - 1);
        var view = configuration.SavedViews[idx];

        return view.PanAdvanceCycleOnComplete
            && ReferenceEquals(activePanView, view)
            && ((view.PanEnabled && !panMotionComplete) || (view.VerticalPanEnabled && !vPanMotionComplete));
    }

    /// <summary>
    /// Applies whichever saved view cycleViewIndex currently points at -
    /// Zoom/Height/Angle limits and pan - to live config, and advances to
    /// the next one on its own timer if "Auto-advance saved views" is on.
    /// Called once per frame after chosenTarget is picked, regardless of
    /// which FollowMode picked it (Myself/Target/Cycle all go through
    /// this now). Previously this whole block only ran inside Cycle mode,
    /// which caused two related bugs: Auto-advance never fired while
    /// orbiting Myself or /target (nothing was driving cycleViewIndex
    /// forward), and switching out of Cycle into Myself dropped whatever
    /// pan was active with no way to get it back short of clicking Load,
    /// since nothing re-applied a view afterward. Running this in every
    /// mode means the mode-change handler's SetActivePanView(null) above
    /// gets immediately followed by a fresh SetActivePanView(view) here
    /// in the same frame whenever saved views are in play - the pan
    /// restarts cleanly instead of just staying off.
    /// </summary>
    /// <summary>
    /// Walks forward from startIndex (wrapping) to find a view that's
    /// actually usable - skips past any
    /// RequireTargetSitting view whose specific RequiredSittingType
    /// (Any/Ground/Furniture) the target doesn't satisfy. Falls back to
    /// startIndex itself if nothing in the whole list qualifies, so
    /// cycling never gets stuck refusing to pick anything at all.
    /// </summary>
    private int FindEligibleViewIndex(int startIndex, CharacterSittingState targetSittingState)
    {
        int count = configuration.SavedViews.Count;
        if (count == 0) return 0;

        for (int i = 0; i < count; i++)
        {
            int idx = (startIndex + i) % count;
            var v = configuration.SavedViews[idx];
            if (!v.RequireTargetSitting || SatisfiesSittingRequirement(targetSittingState, v.RequiredSittingType)) return idx;
        }
        return startIndex;
    }

    /// <summary>
    /// Extracted from the main orbit path so Free Fly can call the same
    /// target/preset selection logic (who's chosen, cycle timer advance,
    /// which preset applies) without duplicating it - see the Free Fly
    /// branch's own call site for why. Deliberately does NOT include the
    /// idle-wait gate that used to sit around this block inline - that
    /// stays specific to the non-Free-Fly caller, since flying is already
    /// active use and shouldn't wait for idle at all.
    /// </summary>
    private IGameObject? SelectChosenTarget(float deltaSeconds)
    {
        IGameObject? chosenTarget = null;

        if (configuration.FollowMode == CameraFollowMode.None)
        {
            // Orbiting yourself now goes through the same manual position
            // computation as an actual follow mode, using the local player
            // as the "target" - the old approach wrote directly to the
            // game's native camera fields, which had no way to apply
            // Height offset at all.
            chosenTarget = objectTable.LocalPlayer;
            CurrentCycleName = "(none - orbiting you)";
            ApplyCurrentSavedView(deltaSeconds, chosenTarget);
        }
        else if (configuration.FollowMode == CameraFollowMode.CurrentTarget)
        {
            chosenTarget = targetManager.Target;
            CurrentCycleName = chosenTarget != null ? chosenTarget.Name.TextValue : "(nothing /targeted)";
            ApplyCurrentSavedView(deltaSeconds, chosenTarget);
        }
        else // Cycle
        {
            var players = GetCycleCandidates();

            if (configuration.CycleUseSavedViews)
            {
                if (players.Count > 0 && configuration.SavedViews.Count > 0)
                {
                    int playerIdx = ResolvePlayerIndex(players);

                    // Same gate ApplyCurrentSavedView uses for the preset
                    // timer below - if the active preset's own pan is
                    // still sweeping and it's set to drive the advance
                    // itself, don't switch players out from under it
                    // either. Without this, the two timers ran fully
                    // independently and could disagree about whether it
                    // was time to move on.
                    if (configuration.CycleAutoAdvance && !CyclePaused && !IsCurrentPanStillInProgress())
                    {
                        cycleTimer += deltaSeconds;
                        if (cycleTimer >= configuration.CycleIntervalSeconds)
                        {
                            cycleTimer = 0f;
                            playerIdx = (playerIdx + 1) % players.Count;
                        }
                    }

                    cycleSelectedPlayerEntityId = players[playerIdx].EntityId;
                    chosenTarget = players[playerIdx];

                    if (!hasLastCycleTargetPos || lastCycleTargetEntityId != chosenTarget.EntityId)
                    {
                        lastCycleTargetPos = chosenTarget.Position;
                        lastCycleTargetEntityId = chosenTarget.EntityId;
                        hasLastCycleTargetPos = true;
                        cycleTargetIsMovingForLog = false;
                    }
                    else
                    {
                        cycleTargetIsMovingForLog = Vector3.Distance(chosenTarget.Position, lastCycleTargetPos) > 0.05f;
                        lastCycleTargetPos = chosenTarget.Position;
                    }

                    // Advances cycleViewIndex (if Auto-advance is on) and
                    // applies whatever it currently points at - shared
                    // with Myself/Target now, see ApplyCurrentSavedView's
                    // doc comment for why this moved out of here.
                    ApplyCurrentSavedView(deltaSeconds, chosenTarget);

                    var view = configuration.SavedViews[cycleViewIndex];
                    CurrentCycleName = $"{chosenTarget.Name.TextValue} - {view.Name} ({playerIdx + 1}/{players.Count}, {cycleViewIndex + 1}/{configuration.SavedViews.Count})";
                }
                else if (players.Count == 0)
                {
                    CurrentCycleName = "(no players nearby)";
                }
                else
                {
                    CurrentCycleName = "(no saved views)";
                }
            }
            else
            {
                if (players.Count > 0)
                {
                    int playerIdx = ResolvePlayerIndex(players);

                    if (configuration.CycleAutoAdvance && !CyclePaused)
                    {
                        cycleTimer += deltaSeconds;
                        if (cycleTimer >= configuration.CycleIntervalSeconds)
                        {
                            cycleTimer = 0f;
                            playerIdx = (playerIdx + 1) % players.Count;
                        }
                    }

                    cycleSelectedPlayerEntityId = players[playerIdx].EntityId;
                    chosenTarget = players[playerIdx];
                    CurrentCycleName = $"{chosenTarget.Name.TextValue} ({playerIdx + 1}/{players.Count})";
                }
                else
                {
                    CurrentCycleName = "(no players nearby)";
                }
            }
        }

        return chosenTarget;
    }

    private void ApplyCurrentSavedView(float deltaSeconds, IGameObject? chosenTarget)
    {
        // Deliberately NOT checking configuration.CycleUseSavedViews here -
        // that toggle is documented in the settings UI as "Only affects
        // Cycle mode," so requiring it for the Myself/Target call sites
        // too meant this whole method silently no-op'd in those modes
        // unless that specific Cycle-only box happened to be checked,
        // which nobody testing Myself mode would think to do. The Cycle
        // call site below still gates on it itself before calling in,
        // so Cycle mode's own on/off behavior is unaffected.
        if (configuration.SavedViews.Count == 0) return;

        var targetSittingState = chosenTarget != null ? GetSittingState(chosenTarget.Address, out _, out _) : CharacterSittingState.NotSitting;

        cycleViewIndex = Math.Clamp(cycleViewIndex, 0, configuration.SavedViews.Count - 1);
        // Whatever we landed on last frame might not be eligible for
        // THIS target (e.g. the player-cycle timer just swapped to
        // someone standing while cycleViewIndex is sitting-only) - check
        // before doing anything else, not just after a timer advance.
        // Runs the same way in every mode now - GetCycleCandidates keeps
        // sitting people reachable in the pool whenever a sitting preset
        // exists, so this always has a real chance of finding a match
        // rather than needing to be skipped for Cycle mode specifically.
        cycleViewIndex = FindEligibleViewIndex(cycleViewIndex, targetSittingState);

        // Same "don't cut a pan off mid-sweep" wait as before - a preset
        // whose own pan is meant to drive the advance shouldn't get
        // interrupted by this fixed timer. Shared with the player-cycle
        // timer below (via IsCurrentPanStillInProgress) so switching
        // players and advancing the preset are gated by the same signal
        // instead of running on two totally independent clocks - see that
        // method's comment for why they were fighting each other before.
        bool waitingForPanToFinish = IsCurrentPanStillInProgress();

        if (configuration.PresetCycleEnabled && !CyclePaused && !waitingForPanToFinish)
        {
            presetCycleTimer += deltaSeconds;
            if (presetCycleTimer >= configuration.PresetCycleIntervalSeconds)
            {
                presetCycleTimer = 0f;
                int nextViewIndex;
                if (configuration.PresetCycleRandom)
                {
                    nextViewIndex = NextRandomViewIndex(cycleViewIndex, previousRandomViewIndex, configuration.SavedViews.Count);
                    previousRandomViewIndex = cycleViewIndex;
                }
                else
                {
                    nextViewIndex = (cycleViewIndex + 1) % configuration.SavedViews.Count;
                }
                cycleViewIndex = FindEligibleViewIndex(nextViewIndex, targetSittingState);
            }
        }

        var view = configuration.SavedViews[cycleViewIndex];
        if (manualSliderCooldownTimer <= 0f && !CyclePaused)
        {
            SetActivePanView(view.PanEnabled || view.VerticalPanEnabled || view.ZoomPanEnabled ? view : null);

            if (activePanView != null) activePanElapsedSeconds += deltaSeconds;

            if (view.PanEnabled)
                AdvanceActivePan(deltaSeconds);
            else
                configuration.HorizontalRotation = view.HorizontalRotation;

            if (view.VerticalPanEnabled)
                AdvanceActiveVerticalPan(deltaSeconds);
            else
                configuration.VerticalRotation = view.VerticalRotation;

            if (view.ZoomPanEnabled)
                AdvanceActiveZoomPan(deltaSeconds);
            else
                configuration.Zoom = view.Zoom;

            CheckPanAdvanceComplete();

            configuration.FollowHeightOffset = view.HeightOffset;
            configuration.FollowMinZoom = view.MinZoom;
            configuration.FollowMaxAngleDegrees = view.MaxAngleDegrees;
            configuration.FollowHeightLockToGround = view.HeightLockToGround;
            configuration.FollowHeightGroundClearance = view.HeightGroundClearance;
            configuration.FollowPositionSmoothingSeconds = view.PositionSmoothingSeconds;
            configuration.FollowAvoidWallsAndObjects = view.AvoidWallsAndObjects;
            configuration.FollowWallAvoidanceBuffer = view.WallAvoidanceBuffer;
        }
    }

    /// <summary>
    /// ExcludeSitting is skipped entirely (sitting people stay in the
    /// pool) whenever at least one SavedView has RequireTargetSitting on
    /// - otherwise that preset would want sitting people but ExcludeSitting
    /// would have already filtered every one of them out before player
    /// selection even happens, making the preset permanently unreachable.
    /// Rotation itself stays broad either way - it's FindEligibleViewIndex
    /// (in ApplyCurrentSavedView) that actually matches a sitting-required
    /// preset to whichever player rotation happens to land on, only when
    /// that player is actually sitting. An earlier version of this tried
    /// to force the WHOLE pool to sitting-only whenever a sitting preset
    /// happened to be the currently active one, which fixed reachability
    /// but broke normal rotation - as long as that preset stayed selected
    /// (nothing was advancing it away), player-cycling got stuck only
    /// ever picking sitters. This is the simpler, correct version.
    /// </summary>
    private List<IPlayerCharacter> GetCycleCandidates()
    {
        bool anySavedViewNeedsSitting = false;
        foreach (var v in configuration.SavedViews)
        {
            if (v.RequireTargetSitting) { anySavedViewNeedsSitting = true; break; }
        }

        var list = new List<IPlayerCharacter>();
        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;

            if (configuration.CycleGenderFilterMode != CycleGenderFilter.Any)
            {
                var customize = pc.Customize;
                if (customize.Length > 1)
                {
                    bool isFemale = customize[1] == 1;
                    if (configuration.CycleGenderFilterMode == CycleGenderFilter.MaleOnly && isFemale) continue;
                    if (configuration.CycleGenderFilterMode == CycleGenderFilter.FemaleOnly && !isFemale) continue;
                }
            }

            if (configuration.ExcludeLalafells)
            {
                var customize = pc.Customize;
                const byte lalafellRaceId = 3;
                if (customize.Length > 0 && customize[0] == lalafellRaceId) continue;
            }

            if (!anySavedViewNeedsSitting && configuration.ExcludeSitting
                && GetSittingState(pc.Address, out _, out _) != CharacterSittingState.NotSitting)
            {
                continue;
            }

            if (configuration.ExcludeCrafters)
            {
                uint jobId = pc.ClassJob.RowId;
                // Carpenter=8, Blacksmith=9, Armorer=10, Goldsmith=11,
                // Leatherworker=12, Weaver=13, Alchemist=14, Culinarian=15 -
                // the standard ClassJob sheet IDs for the 8 Disciples of
                // the Hand, contiguous and stable since 2.0.
                if (jobId is >= 8 and <= 15) continue;
            }

            if (configuration.ExcludedEquipmentItemIds.Count > 0)
            {
                bool wearingExcluded = false;
                for (int slot = 0; slot < 10; slot++)
                {
                    if (configuration.ExcludedEquipmentItemIds.Contains(GetEquippedItemId(pc.Address, slot)))
                    {
                        wearingExcluded = true;
                        break;
                    }
                }
                if (wearingExcluded) continue;
            }

            list.Add(pc);
        }
        return list;
    }

    public void Shutdown()
    {
        RestoreUiIfHidden();
        keyBlocker.Remove();
        targetHook.Remove();
        positionHook.Remove();
    }

    private void RestoreUiIfHidden()
    {
        if (!uiCurrentlyHiddenByUs) return;
        UiToggleHelper.SimulateToggle(configuration.UiToggleKeyName);
        uiCurrentlyHiddenByUs = false;
    }

    private static bool IsUserManuallyControllingCamera()
    {
        bool rightHeld = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        bool leftHeld = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;

        if (!rightHeld && !leftHeld) return false;

        // Left-click also drives our own settings window's buttons -
        // don't treat a click on our own UI as "grab the camera."
        if (leftHeld && ImGui.GetIO().WantCaptureMouse)
            return rightHeld;

        return true;
    }

    public void OnFrameworkUpdate(IFramework framework)
    {
        // Hard gate, checked before anything else: a crash was traced to
        // CamCam's hooks/writes still being active during a login/character-
        // select transition - the game tears down and reinitializes its own
        // camera system there, and a hook or write landing mid-transition is
        // a plausible cause. Not logged in means fully disengage, no
        // exceptions, regardless of Enabled or any toggle key.
        if (!clientState.IsLoggedIn)
        {
            if (Status != "Not logged in")
                log.Information("[CamCam] Not logged in - removing all hooks.");
            Status = "Not logged in";
            targetHook.Remove();
            positionHook.Remove();
            keyBlocker.Remove();
            freeCam.ClearStartingPosition();
            lastFreeFlyTargetEntityId = null;
            RestoreUiIfHidden();
            return;
        }

        float deltaSeconds = (float)framework.UpdateDelta.TotalSeconds;

        // Pause froze these instead of resetting them - they'd sit
        // wherever they were when pause was engaged (possibly seconds
        // away from firing, if cycling had already been running for a
        // while) and could fire almost immediately the instant pause was
        // lifted, snapping the camera to a different player/preset right
        // when everything was supposed to stay put. Keeping both pinned
        // at zero for the whole duration of the pause means unpausing
        // always starts from a full, fresh interval.
        if (CyclePaused)
        {
            cycleTimer = 0f;
            presetCycleTimer = 0f;
        }

        // Both toggle keys work regardless of the Enable checkbox.
        if (!string.IsNullOrWhiteSpace(configuration.ToggleCamCamKeyName))
        {
            bool camCamHeld = FreeCamController.IsHeld(configuration.ToggleCamCamKeyName);
            if (camCamHeld && !toggleCamCamKeyWasHeld)
            {
                configuration.Enabled = !configuration.Enabled;
                configuration.Save();
                log.Information($"[CamCam] CamCam toggled to {configuration.Enabled} via keybind '{configuration.ToggleCamCamKeyName}'.");
            }
            toggleCamCamKeyWasHeld = camCamHeld;
        }

        // Turns CamCam on (if it wasn't already) and starts flying in one press.
        if (!string.IsNullOrWhiteSpace(configuration.FreeFlyToggleKeyName))
        {
            bool toggleHeld = FreeCamController.IsHeld(configuration.FreeFlyToggleKeyName);
            if (toggleHeld && !freeFlyToggleKeyWasHeld)
            {
                configuration.FreeFly = !configuration.FreeFly;
                if (configuration.FreeFly)
                    configuration.Enabled = true;
                configuration.Save();
                log.Information($"[CamCam] Free Fly toggled to {configuration.FreeFly} via keybind '{configuration.FreeFlyToggleKeyName}' (resolves to VK 0x{FreeCamController.ResolveKey(configuration.FreeFlyToggleKeyName):X2}). At the moment of the press: manualInputCooldown={manualInputCooldownTimer:0.0}s idleTimer={idleTimer:0.0}/{configuration.IdleThresholdSeconds:0.0}s. If manualInputCooldown was above 0, that's why engagement would have visibly lagged the press.");
            }
            freeFlyToggleKeyWasHeld = toggleHeld;
        }

        // Independent on/off switch for numpad blocking specifically -
        // always checked regardless of numpadBlockManuallyDisabled's
        // current state, otherwise there'd be no way to press it again
        // to turn blocking back on once it's off.
        if (!string.IsNullOrWhiteSpace(configuration.NumpadBlockToggleKeyName))
        {
            bool numpadToggleHeld = FreeCamController.IsHeld(configuration.NumpadBlockToggleKeyName);
            if (numpadToggleHeld && !numpadBlockToggleKeyWasHeld)
            {
                numpadBlockManuallyDisabled = !numpadBlockManuallyDisabled;
                log.Information($"[CamCam] Numpad key blocking manually {(numpadBlockManuallyDisabled ? "disabled" : "re-enabled")} via keybind '{configuration.NumpadBlockToggleKeyName}'.");
            }
            numpadBlockToggleKeyWasHeld = numpadToggleHeld;
        }

        if (!configuration.Enabled)
        {
            if (Status != "Idle")
                log.Information("[CamCam] Disabled - removing hooks.");
            Status = "Idle";
            targetHook.Remove();
            positionHook.Remove();
            keyBlocker.Remove();
            freeCam.ClearStartingPosition();
            lastFreeFlyTargetEntityId = null;
            RestoreUiIfHidden();
            return;
        }

        if (configuration.AutoHideUi && !uiCurrentlyHiddenByUs)
        {
            UiToggleHelper.SimulateToggle(configuration.UiToggleKeyName);
            uiCurrentlyHiddenByUs = true;
        }
        else if (!configuration.AutoHideUi && uiCurrentlyHiddenByUs)
        {
            RestoreUiIfHidden();
        }

        if (manualSliderCooldownTimer > 0f)
            manualSliderCooldownTimer -= deltaSeconds;

        bool needsKeyBlocking = !numpadBlockManuallyDisabled
            && (configuration.FreeFly
            || configuration.FollowMode != CameraFollowMode.None
            || !string.IsNullOrWhiteSpace(configuration.FreeFlyToggleKeyName)
            || !string.IsNullOrWhiteSpace(configuration.ToggleCamCamKeyName));
        if (needsKeyBlocking)
        {
            keyBlocker.Install();
            // User-found bug: only the specific keys CamCam itself uses
            // (Numpad8, Numpad2, etc.) were being blocked - Numpad1,
            // Numpad3, Numpad5 (and others CamCam has no binding for)
            // were never in this list at all, so they kept leaking
            // through untouched. "Block numpad" should mean the whole
            // numpad, not just the handful of keys CamCam happens to
            // bind - blocking every numpad key now, regardless of
            // whether it's one of CamCam's own configured bindings.
            var flyKeyNames = new[]
            {
                "Numpad0", "Numpad1", "Numpad2", "Numpad3", "Numpad4",
                "Numpad5", "Numpad6", "Numpad7", "Numpad8", "Numpad9",
                "Numpad+", "Numpad-", "Numpad*", "Numpad/", "Numpad.",
                configuration.FreeFlyToggleKeyName, configuration.ToggleCamCamKeyName,
            };
            keyBlocker.SetBlockedKeys(flyKeyNames);
            ClearGameKeyState(flyKeyNames);
        }
        else
        {
            keyBlocker.Remove();
        }

        try
        {
            WriteCameraPose(deltaSeconds);
        }
        catch (Exception ex)
        {
            Status = $"Error: {ex.Message}";
            log.Warning(ex, "CamCam failed to write camera pose");
        }

        statusLogTimer += deltaSeconds;
        if (statusLogTimer >= StatusLogIntervalSeconds)
        {
            statusLogTimer = 0f;
            log.Information(
                $"[CamCam] status={Status} loggedIn={clientState.IsLoggedIn} freeFly={configuration.FreeFly} " +
                $"followMode={configuration.FollowMode} cycleTarget={CurrentCycleName} " +
                $"targetHook={targetHook.Status} bypassTargetHook={configuration.ExperimentalBypassTargetHook} positionHook={positionHook.Status} " +
                $"keyBlocker={keyBlocker.Status} uiHidden={uiCurrentlyHiddenByUs} " +
                $"idleTimer={idleTimer:0.0}/{configuration.IdleThresholdSeconds:0.0} " +
                $"manualInputCooldown={manualInputCooldownTimer:0.0} continuousGrabDuration={continuousGrabDurationSeconds:0.0} " +
                $"numLock={(FreeCamController.IsNumLockOn ? "ON" : "OFF")} " +
                $"freeFlyToggleKey='{configuration.FreeFlyToggleKeyName}' " +
                $"(VK 0x{(string.IsNullOrWhiteSpace(configuration.FreeFlyToggleKeyName) ? 0 : FreeCamController.ResolveKey(configuration.FreeFlyToggleKeyName)):X2}) " +
                $"currentTarget={(targetManager.Target != null ? targetManager.Target.Name.TextValue : "none")} " +
                $"worldCam(mode={worldCamModeForLog} H={worldCamHRotationForLog:0.00} V={worldCamVRotationForLog:0.00} Z={worldCamZoomForLog:0.00}) " +
                $"gameplayCam({(gameplayCameraAvailableForLog ? $"distance={gameplayCamDistanceForLog:0.000} maxDistance={gameplayCamMaxDistanceForLog:0.000} interpDistance={gameplayCamInterpDistanceForLog:0.000}" : "unavailable")}) " +
                $"pan(active={(activePanView != null ? activePanView.Name : "none")} deg={panCurrentDegrees:0.0} dir={panDirection} complete={panMotionComplete} vComplete={vPanMotionComplete} legs={panLegsCompleted}) " +
                $"cycleTimer={cycleTimer:0.0}/{configuration.CycleIntervalSeconds:0.0} autoAdvance={configuration.CycleAutoAdvance} " +
                $"presetCycleTimer={presetCycleTimer:0.0}/{configuration.PresetCycleIntervalSeconds:0.0} panStillInProgress={IsCurrentPanStillInProgress()} " +
                $"cycleTargetMoving={cycleTargetIsMovingForLog} " +
                $"equipment({equipmentIdsForLog})");
        }
    }

    private void UpdateIdleTimer(float deltaSeconds)
    {
        var player = objectTable.LocalPlayer;
        if (player == null)
        {
            idleTimer = 0f;
            return;
        }

        if (!hasLastPlayerPos)
        {
            lastPlayerPos = player.Position;
            hasLastPlayerPos = true;
        }

        if (Vector3.Distance(player.Position, lastPlayerPos) > 0.05f)
        {
            idleTimer = 0f;
            lastPlayerPos = player.Position;
        }
        else
        {
            idleTimer += deltaSeconds;
        }
    }

    private void WriteCameraPose(float deltaSeconds)
    {
        var rawManager = (RawCameraManager*)CameraManager.Instance();
        if (rawManager == null)
        {
            Status = "CameraManager not ready";
            return;
        }

        var camera = rawManager->WorldCamera;
        if (camera == null)
        {
            Status = "WorldCamera not ready";
            return;
        }

        if (camera->MaxZoom <= 0f)
        {
            Status = "WorldCamera not active";
            return;
        }

        // Yield completely to your own manual camera control - right or
        // left mouse held, plus a short cooldown after release so it
        // doesn't snap back mid-adjustment.
        bool grabbingCamera = IsUserManuallyControllingCamera();

        // Safety net for a real, known category of Windows/DirectX bug:
        // GetAsyncKeyState can report a mouse button as still held long
        // after it's actually been released, particularly around certain
        // focus-changing events with an ImGui overlay (like Dalamud's)
        // drawn on top of the game. If that happens here, grabbingCamera
        // gets stuck true, which yields the camera to the game's own
        // native control indefinitely (both hooks removed every frame
        // below) - producing exactly the kind of persistent shake that
        // carries across preset switches, since CamCam simply isn't
        // driving the camera at all while stuck. Confirmed via user
        // report: opening chat and pressing Enter - a genuine focus
        // transition - reliably cleared it, which is consistent with a
        // stuck GetAsyncKeyState read rather than an actual held button.
        // No real manual camera adjustment plausibly lasts this long
        // continuously, so timing out and forcing control back is safe.
        const float stuckGrabTimeoutSeconds = 15f;
        if (grabbingCamera)
        {
            continuousGrabDurationSeconds += deltaSeconds;
            if (continuousGrabDurationSeconds > stuckGrabTimeoutSeconds)
            {
                log.Warning($"[CamCam] Manual camera grab detected continuously for over {stuckGrabTimeoutSeconds:0}s - likely a stuck GetAsyncKeyState read rather than an actual held button, forcing control back.");
                grabbingCamera = false;
                continuousGrabDurationSeconds = 0f;
            }
        }
        else
        {
            continuousGrabDurationSeconds = 0f;
        }

        if (grabbingCamera)
        {
            manualInputCooldownTimer = ManualInputCooldownSeconds;
            idleTimer = 0f; // taking the camera counts as "you're here"
        }
        else if (manualInputCooldownTimer > 0f)
        {
            manualInputCooldownTimer -= deltaSeconds;
        }

        if (grabbingCamera || manualInputCooldownTimer > 0f)
        {
            targetHook.Remove();
            positionHook.Remove();
            Status = "Yielding to your manual camera control";
            return;
        }

        camera->Mode = 1;

        if (!configuration.FreeFly)
        {
            // Captured once - the game's own natural limits, before CamCam
            // ever touches them. Previously each frame only ever widened
            // further (MathF.Min/Max against whatever was already there),
            // so once one preset's MinZoom/MaxAngle widened the camera,
            // it stayed that wide forever after - a later preset with its
            // own, different MinZoom/MaxAngle never actually took effect
            // independently. Resetting to the true baseline every frame
            // before re-widening means each preset's limits apply on
            // their own, consistently, regardless of what was active before.
            originalMinZoom ??= camera->MinZoom;
            originalMaxVRotation ??= camera->MaxVRotation;
            originalMinVRotation ??= camera->MinVRotation;

            float maxAngleRadians = configuration.FollowMaxAngleDegrees * (MathF.PI / 180f);
            camera->MinZoom = MathF.Min(originalMinZoom.Value, configuration.FollowMinZoom);
            camera->MaxVRotation = MathF.Max(originalMaxVRotation.Value, maxAngleRadians);
            camera->MinVRotation = MathF.Min(originalMinVRotation.Value, -maxAngleRadians);
        }

        float safeMaxVRotation = camera->MaxVRotation - 0.02f;
        float safeMinVRotation = camera->MinVRotation + 0.02f;

        if (configuration.FreeFly)
        {
            targetHook.Remove();
            idleTimer = 0f; // flying is active use, not idle - reset for next time a follow mode is used

            positionHook.Install(camera);
            positionHook.IsTrueFreeFly = true;

            // Runs the same target/preset selection the orbit path uses -
            // cycle timer advance, Next/Previous, which preset is active -
            // so Cycle mode (and Myself/Target) keep working while Free
            // Fly is active, not frozen the whole time. But this also
            // writes configuration.HorizontalRotation/VerticalRotation/
            // Zoom to the newly-selected preset's saved values every
            // single frame it runs (see ApplyCurrentSavedView) - fine for
            // orbit mode, which recomputes position from these every
            // frame anyway, but it would fight Free Fly's own manual
            // rotation constantly if left as-is. So: snapshot before,
            // call it, then only keep the result if the target actually
            // changed this frame - otherwise revert back to what the user
            // had, so manual turning/moving isn't overwritten every frame
            // just because cycling happens to be on.
            float preSelectH = configuration.HorizontalRotation;
            float preSelectV = configuration.VerticalRotation;
            float preSelectZoom = configuration.Zoom;
            uint? previousFreeFlyTargetEntityId = lastFreeFlyTargetEntityId;

            var freeFlyChosenTarget = SelectChosenTarget(deltaSeconds);
            bool freeFlyTargetChanged = freeFlyChosenTarget != null
                && (previousFreeFlyTargetEntityId == null || previousFreeFlyTargetEntityId.Value != freeFlyChosenTarget.EntityId);
            lastFreeFlyTargetEntityId = freeFlyChosenTarget?.EntityId;

            if (freeFlyTargetChanged)
            {
                // Genuinely a new target (first target this flight, or
                // cycle/Next/Previous actually advanced) - keep the
                // freshly-applied preset values and snap the Free Fly
                // camera to match, same as Load already does. User
                // clarified this should be RELATIVE to whoever the new
                // target is (same H/V/Zoom framing reproduced on each
                // person, matching orbit mode's own behavior) - not a
                // fixed absolute position, which was tried first and was
                // the wrong interpretation. The actual bug behind the
                // original complaint wasn't relative-vs-absolute at all -
                // it was that this computation used to always reference
                // LocalPlayer (yourself) regardless of who was actually
                // targeted, and used a hardcoded zoom instead of the
                // saved one. See the fixed computation below.
                freeCam.ClearStartingPosition();
            }
            else
            {
                // Same target as last frame - normally revert
                // configuration's H/V/Zoom back to whatever the user had,
                // undoing ApplyCurrentSavedView's per-frame overwrite so
                // manual Free Fly control isn't fought. BUT: if the active
                // preset has any pan enabled, that per-frame change IS the
                // animation actually progressing (AdvanceActivePan etc.,
                // called inside ApplyCurrentSavedView) - reverting it
                // every frame would freeze the pan at its starting value
                // the instant Free Fly became active, which is exactly
                // why pan/strafe never appeared to work while flying.
                // Keep the animated values in this case; freeCam.Update
                // below still builds on top of them normally for manual
                // turning, and manual position movement (D-pad, arrows)
                // is unaffected either way since it never touched H/V/Zoom
                // at all.
                bool activePresetHasPan = configuration.SavedViews.Count > cycleViewIndex
                    && (configuration.SavedViews[cycleViewIndex].PanEnabled
                        || configuration.SavedViews[cycleViewIndex].VerticalPanEnabled
                        || configuration.SavedViews[cycleViewIndex].ZoomPanEnabled);
                if (!activePresetHasPan)
                {
                    configuration.HorizontalRotation = preSelectH;
                    configuration.VerticalRotation = preSelectV;
                    configuration.Zoom = preSelectZoom;
                }
            }

            // Bug fix: this used to call freeCam.ResetTo(startPos)
            // unconditionally every single frame, even though
            // FreeCamController already has HasStartingPosition
            // specifically to guard against exactly that. The result was
            // position getting reset to this computed starting point
            // every frame, with only one frame's worth of movement ever
            // surviving before the next reset wiped it out again -
            // matching exactly what testing showed: a "nudge" each frame
            // that never accumulated into real travel, while
            // turning still worked because rotation gets written back
            // into configuration.HorizontalRotation/VerticalRotation,
            // which THIS reset calculation reads from - so turning moved
            // where the reset point itself was, which looked like partial
            // movement. Now only resets once, on the frame Free Fly
            // actually starts (or after LoadView/a cycle target change
            // explicitly clears HasStartingPosition - see above).
            if (!freeCam.HasStartingPosition)
            {
                // Three real bugs fixed here, all contributing to the same
                // symptom (Load/cycling never landing where expected):
                // 1. playerPos used to always be objectTable.LocalPlayer -
                //    literally always yourself, regardless of who's
                //    actually being targeted in Cycle/CurrentTarget mode.
                //    Now uses freeFlyChosenTarget (computed above via
                //    SelectChosenTarget) - whoever's actually the current
                //    target, falling back to LocalPlayer only if there
                //    genuinely isn't one.
                // 2. startZoom was a hardcoded 3f, completely ignoring
                //    configuration.Zoom (the preset's own saved distance) -
                //    every single reset landed exactly 3 units out no
                //    matter what was saved. Now uses configuration.Zoom.
                // 3. The look-at point only used the target's own real
                //    height, never configuration.FollowHeightOffset (the
                //    preset's own adjustable offset) - orbit mode's
                //    equivalent computation (see chosenTarget's
                //    lookAtPoint further down) folds both in together.
                //    Now matches that exactly.
                var freeFlyLookAtTarget = freeFlyChosenTarget ?? objectTable.LocalPlayer;
                var playerPos = freeFlyLookAtTarget?.Position ?? new Vector3(camera->X, camera->Y, camera->Z);
                float playerRealHeight = GetCharacterHeight(freeFlyLookAtTarget?.Address ?? nint.Zero);
                float playerHeightOffset = (playerRealHeight > 0f ? playerRealHeight * HeightScaleFactor : 1.3f) + configuration.FollowHeightOffset;
                var lookAt = playerPos + new Vector3(0f, playerHeightOffset, 0f);
                float startZoom = configuration.Zoom;
                float startVRotation = Math.Clamp(configuration.VerticalRotation, safeMinVRotation, safeMaxVRotation);
                float startCosV = MathF.Cos(startVRotation);
                var startPos = lookAt + startZoom * new Vector3(
                    startCosV * MathF.Sin(configuration.HorizontalRotation),
                    MathF.Sin(startVRotation),
                    startCosV * MathF.Cos(configuration.HorizontalRotation));
                freeCam.ResetTo(startPos);
            }

            float hRotation = configuration.HorizontalRotation;
            float vRotation = Math.Clamp(configuration.VerticalRotation, safeMinVRotation, safeMaxVRotation);

            freeCam.Update(deltaSeconds, ref hRotation, ref vRotation, configuration.FreeFlySpeed, configuration.FreeFlyTurnSpeed, configuration);
            vRotation = Math.Clamp(vRotation, safeMinVRotation, safeMaxVRotation);

            if (configuration.FreeFlyLockToGround)
            {
                var groundY = objectTable.LocalPlayer?.Position.Y ?? freeCam.Position.Y;
                freeCam.ClampMinimumY(groundY + configuration.FreeFlyGroundClearance);
            }

            configuration.HorizontalRotation = hRotation;
            configuration.VerticalRotation = vRotation;

            camera->CurrentHRotation = hRotation;
            camera->CurrentVRotation = vRotation;
            positionHook.OverridePosition = freeCam.Position;

            ReadWorldCameraStateForLog(camera);

            Status = positionHook.IsInstalled ? "Free flying" : "Free fly failed to install";
            return;
        }

        freeCam.ClearStartingPosition();
        lastFreeFlyTargetEntityId = null;

        // Orbit mode just changed - any pan that was driving the camera
        // in the previous mode must not keep silently running in the new
        // one. Without this, loading a panning preset even once would
        // leave it driving Horizontal angle in the background regardless
        // of what mode you switched to afterward, corrupting whatever
        // you thought you were manually setting up next.
        if (lastFollowMode != configuration.FollowMode)
        {
            string savedH = configuration.SavedViews.Count > cycleViewIndex
                ? (configuration.SavedViews[cycleViewIndex].HorizontalRotation * 180f / MathF.PI).ToString("0.0")
                : "n/a";
            log.Information(
                $"[CamCam] Orbit mode changed {lastFollowMode} -> {configuration.FollowMode}. " +
                $"cycleViewIndex={cycleViewIndex} liveH={configuration.HorizontalRotation * 180f / MathF.PI:0.0}deg " +
                $"savedViewH={savedH}deg CycleUseSavedViews={configuration.CycleUseSavedViews}");
            SetActivePanView(null);
            lastFollowMode = configuration.FollowMode;
        }

        // A manually Loaded panning view keeps animating on its own here
        // in any mode where saved views aren't otherwise driving it -
        // ApplyCurrentSavedView (below, called after chosenTarget is
        // known) now handles pan advancement itself whenever saved views
        // are in play, regardless of which FollowMode picked the target,
        // so this generic path only needs to run when they're not. Mirrors
        // exactly which modes actually call ApplyCurrentSavedView below:
        // Myself/Target always do (when there are saved views at all),
        // Cycle only does when its own "Cycle through saved views" toggle
        // is on.
        bool savedViewsAreDriving = configuration.SavedViews.Count > 0
            && (configuration.FollowMode != CameraFollowMode.Cycle || configuration.CycleUseSavedViews);
        if (!savedViewsAreDriving && !CyclePaused && manualSliderCooldownTimer <= 0f)
        {
            if (activePanView != null) activePanElapsedSeconds += deltaSeconds;
            AdvanceActivePan(deltaSeconds);
            AdvanceActiveVerticalPan(deltaSeconds);
            AdvanceActiveZoomPan(deltaSeconds);
            CheckPanAdvanceComplete();
        }

        // Same idle-wait gate for every mode now, including orbiting
        // yourself - previously "Myself" bypassed this entirely while "My
        // /target" and "Cycle" respected it, which was inconsistent. Not
        // part of SelectChosenTarget itself since Free Fly's own call
        // site doesn't want this gate - flying is already active use.
        UpdateIdleTimer(deltaSeconds);
        if (idleTimer < configuration.IdleThresholdSeconds)
        {
            targetHook.Remove();
            positionHook.Remove();
            Status = $"Waiting to go idle ({idleTimer:0}/{configuration.IdleThresholdSeconds:0}s)";
            return;
        }

        IGameObject? chosenTarget = SelectChosenTarget(deltaSeconds);

        if (chosenTarget == null)
        {
            targetHook.Remove();
            positionHook.Remove();
            Status = configuration.FollowMode == CameraFollowMode.None
                ? "Nobody to orbit - are you logged in?"
                : "Follow mode on, but nobody to look at";
            return;
        }

        // EXPERIMENTAL - see ExperimentalBypassTargetHook's comment in
        // Configuration.cs. Position and rotation are fully computed and
        // written below regardless of this toggle, so the target hook's
        // only remaining job is whatever else the engine internally uses
        // getCameraTarget() for - possibly including wall/ground
        // avoidance, possibly other things this hasn't been tested
        // against yet.
        if (configuration.ExperimentalBypassTargetHook)
        {
            targetHook.Remove();
        }
        else
        {
            targetHook.Install(camera);
            targetHook.OverrideTargetAddress = chosenTarget.Address;
        }

        positionHook.Install(camera);
        positionHook.IsTrueFreeFly = false;

        if (FreeCamController.IsHeld(configuration.FlyUpKey)) configuration.FollowHeightOffset += configuration.FollowHeightAdjustSpeed * deltaSeconds;
        if (FreeCamController.IsHeld(configuration.FlyDownKey)) configuration.FollowHeightOffset -= configuration.FollowHeightAdjustSpeed * deltaSeconds;

        // Fixed-camera pass-by: see FixedCameraPassBy's comment in
        // Configuration.cs. Determined here, before vRotation2/
        // effectiveHRotation/orbitPos are computed, since all three need
        // to know whether to use a frozen snapshot this frame.
        SavedView? activeViewForFixedCamera = configuration.CycleUseSavedViews && configuration.SavedViews.Count > 0
            ? configuration.SavedViews[Math.Clamp(cycleViewIndex, 0, configuration.SavedViews.Count - 1)]
            : null;
        bool useFixedCamera = activeViewForFixedCamera?.FixedCameraPassBy == true || activeViewForFixedCamera?.TranslateInsteadOfPan == true;
        bool useTranslateMode = useFixedCamera && activeViewForFixedCamera!.TranslateInsteadOfPan;
        bool hasExistingFixedCameraSnapshot = useFixedCamera
            && hasFixedCameraSnapshot
            && ReferenceEquals(fixedCameraSnapshotView, activeViewForFixedCamera)
            && fixedCameraSnapshotTargetEntityId == chosenTarget.EntityId;

        if (!useFixedCamera) hasFixedCameraSnapshot = false;

        // Rotation is completely frozen in translate mode - the camera's
        // own facing never changes once the snapshot exists, regardless
        // of what configuration.VerticalRotation/HorizontalRotation (or
        // any Horizontal/Vertical pan advancing them in the background)
        // say. That's the entire point: "hold the camera facing forward"
        // while position moves, not the other way around.
        float vRotation2 = (useTranslateMode && hasExistingFixedCameraSnapshot)
            ? fixedCameraSnapshotVRotation
            : Math.Clamp(configuration.VerticalRotation, safeMinVRotation, safeMaxVRotation);

        // Vertical-angle jump detector. A sudden, large change here (a
        // "pop" rather than smooth motion) can come from two completely
        // different places: configuration.VerticalRotation itself
        // changing unexpectedly, or the CLAMP BOUNDS shifting under it
        // (safeMinVRotation/safeMaxVRotation, derived from the native
        // camera's own MinVRotation/MaxVRotation, which CamCam itself
        // also writes to above based on the active preset's
        // MaxAngleDegrees). Logging both halves separately here shows
        // which one actually moved, rather than guessing.
        if (hasPreviousVRotation2 && previousVRotationTargetEntityId == chosenTarget.EntityId)
        {
            float vJumpDegrees = MathF.Abs(vRotation2 - previousVRotation2) * (180f / MathF.PI);
            if (vJumpDegrees > 3f)
            {
                log.Warning(
                    $"[CamCam] vertical angle jump: {vJumpDegrees:0.0}deg in one frame. " +
                    $"previousVRotation2={previousVRotation2 * (180f / MathF.PI):0.0}deg newVRotation2={vRotation2 * (180f / MathF.PI):0.0}deg " +
                    $"configVerticalRotation={configuration.VerticalRotation * (180f / MathF.PI):0.0}deg " +
                    $"safeMinVRotation={safeMinVRotation * (180f / MathF.PI):0.0}deg safeMaxVRotation={safeMaxVRotation * (180f / MathF.PI):0.0}deg " +
                    $"cameraMinVRotation={camera->MinVRotation * (180f / MathF.PI):0.0}deg cameraMaxVRotation={camera->MaxVRotation * (180f / MathF.PI):0.0}deg " +
                    $"originalMinVRotation={(originalMinVRotation.HasValue ? (originalMinVRotation.Value * (180f / MathF.PI)).ToString("0.0") : "null")}deg " +
                    $"originalMaxVRotation={(originalMaxVRotation.HasValue ? (originalMaxVRotation.Value * (180f / MathF.PI)).ToString("0.0") : "null")}deg " +
                    $"followMaxAngleDegrees={configuration.FollowMaxAngleDegrees:0.0} target={chosenTarget.Name.TextValue}");
            }
        }
        previousVRotation2 = vRotation2;
        previousVRotationTargetEntityId = chosenTarget.EntityId;
        hasPreviousVRotation2 = true;
        float zoom = camera->MinZoom < camera->MaxZoom
            ? Math.Clamp(configuration.Zoom, camera->MinZoom + 0.001f, camera->MaxZoom)
            : camera->MaxZoom;

        // Relative to the target's own facing, not a fixed world-space
        // angle - configuration.HorizontalRotation is the offset FROM
        // whichever way they're currently facing, so a "Front" preset
        // tuned while looking at your own character's face reproduces
        // the same "looking at their face" framing on anyone else,
        // whichever direction they happen to be facing, instead of
        // landing at the same fixed compass direction regardless of who
        // that puts you looking at. This also means the camera turns
        // with a target as they rotate in place, not past them.
        //
        // NOTE: presets saved before this change stored a raw world
        // angle, not a target-relative offset - they'll look off now and
        // need re-saving once under this new math, since there's no way
        // to recover what the target's facing was at the moment they
        // were originally saved.
        // Reverted from GetStableTargetRotation back to the raw value -
        // /xllog data proved rotation jitter wasn't the cause of the
        // drift being chased (rawRotationDeltaDeg was a flat 0.000 the
        // entire time on a target still showing 1.4+ units of drift), so
        // the deadzone was solving a problem that didn't exist here,
        // while risking a real downside of its own: on a target that IS
        // naturally rotating, holding the last value until a ~1 degree
        // threshold is crossed produces a hold-then-snap step pattern
        // instead of smooth tracking, which can look and feel worse than
        // the raw signal it was meant to smooth. GetStableTargetRotation
        // itself is left in place (unused) in case a genuine jitter case
        // turns up later with actual data behind it.
        // Frozen exactly like vRotation2 above, and for the same reason,
        // when in translate mode.
        float effectiveHRotation = (useTranslateMode && hasExistingFixedCameraSnapshot)
            ? fixedCameraSnapshotHRotation
            : configuration.HorizontalRotation + (hasExistingFixedCameraSnapshot ? fixedCameraSnapshotTargetRotation : chosenTarget.Rotation);

        // HeightOffset shifts the look-at point itself, not the final
        // camera position after the fact - previously it only nudged
        // orbitPos.Y once the H/V/Zoom sphere position was already
        // computed, leaving X/Z untouched. That meant the camera drifted
        // off the sphere it was supposed to be orbiting on: the same
        // H/V/Zoom numbers no longer produced a consistent distance or
        // angle to the target once Height offset was nonzero. Folding it
        // into the look-at point keeps the whole sphere - X, Y, and Z -
        // shifting together, so the actual geometry stays consistent.
        // Height comes straight off the target's own GameObject rather
        // than a one-size-fits-all constant, so a lalafell and a
        // Roegadyn framed with the same preset both get a look-at point
        // that actually sits at a proportional spot on their body instead
        // of the same fixed world-space Y for everyone. See
        // HeightScaleFactor's comment above for how 1.4 was derived.
        float targetRealHeight = GetCharacterHeight(chosenTarget.Address);
        float heightOffset = targetRealHeight > 0f ? targetRealHeight * HeightScaleFactor : 1.3f;
        var targetSittingStateForLog = GetSittingState(chosenTarget.Address, out byte targetMode, out byte targetModeParam);

        // Raw vs stabilized target rotation, to directly confirm or rule
        // out whether the target's own facing is jittering frame to
        // frame - rawRotationDeltaDeg is the actual, un-filtered
        // frame-to-frame change; if that's bouncing around while shake is
        // visible, that's hard confirmation this is the real source
        // rather than a native system fighting the camera's position.
        float rawTargetRotationDeg = chosenTarget.Rotation * (180f / MathF.PI);
        float rawRotationDeltaDeg = hasPreviousRawTargetRotation && previousRawTargetRotationTargetEntityId == chosenTarget.EntityId
            ? ShortestAngleDeltaDegrees(previousRawTargetRotation, chosenTarget.Rotation) * (180f / MathF.PI)
            : 0f;
        previousRawTargetRotation = chosenTarget.Rotation;
        previousRawTargetRotationTargetEntityId = chosenTarget.EntityId;
        hasPreviousRawTargetRotation = true;

        // Same idea, for the target's own POSITION this time, not just
        // rotation. Motivated directly by a user observation: a
        // Translate-mode (strafe/dolly) preset, which snapshots position
        // once and never re-reads the target's live position again after
        // that, showed no shake at all through a staircase - while
        // normal orbit tracking, which re-reads chosenTarget.Position
        // fresh every single frame, keeps showing the small persistent
        // residual. If the target's own reported position has any
        // frame-to-frame jitter independent of rotation (network
        // interpolation correction on a character that isn't your own is
        // a plausible source), continuously re-reading it every frame -
        // which only orbit mode does, not translate mode - would explain
        // exactly that difference.
        float rawTargetPositionDelta = hasPreviousRawTargetPosition && previousRawTargetPositionEntityId == chosenTarget.EntityId
            ? Vector3.Distance(previousRawTargetPosition, chosenTarget.Position)
            : 0f;
        previousRawTargetPosition = chosenTarget.Position;
        previousRawTargetPositionEntityId = chosenTarget.EntityId;
        hasPreviousRawTargetPosition = true;

        log.Information($"[CamCam] height diag: target={chosenTarget.Name.TextValue} realHeight={targetRealHeight:0.000} heightOffset={heightOffset:0.000} mode={targetMode} modeParam={targetModeParam} sittingState={targetSittingStateForLog} rawTargetRotationDeg={rawTargetRotationDeg:0.000} rawRotationDeltaDeg={rawRotationDeltaDeg:0.000} stableTargetRotationDeg={stableTargetRotation * (180f / MathF.PI):0.000} rawTargetPositionDelta={rawTargetPositionDelta:0.0000}");

        equipmentIdsForLog = $"Head={GetEquippedItemId(chosenTarget.Address, 0)} Body={GetEquippedItemId(chosenTarget.Address, 1)} Hands={GetEquippedItemId(chosenTarget.Address, 2)} Legs={GetEquippedItemId(chosenTarget.Address, 3)} Feet={GetEquippedItemId(chosenTarget.Address, 4)} Ears={GetEquippedItemId(chosenTarget.Address, 5)} Neck={GetEquippedItemId(chosenTarget.Address, 6)} Wrists={GetEquippedItemId(chosenTarget.Address, 7)} RFinger={GetEquippedItemId(chosenTarget.Address, 8)} LFinger={GetEquippedItemId(chosenTarget.Address, 9)}";

        var lookAtPoint = chosenTarget.Position + new Vector3(0f, heightOffset + configuration.FollowHeightOffset, 0f);
        float cosV = MathF.Cos(vRotation2);
        var orbitPos = lookAtPoint + zoom * new Vector3(
            cosV * MathF.Sin(effectiveHRotation),
            MathF.Sin(vRotation2),
            cosV * MathF.Cos(effectiveHRotation));

        if (useFixedCamera)
        {
            if (hasExistingFixedCameraSnapshot)
            {
                // Held fixed - discard this frame's freshly-computed
                // sphere position entirely, even if the subject has
                // moved since the snapshot was taken. That's the whole
                // point: a tripod pan doesn't follow anyone.
                orbitPos = fixedCameraSnapshotPosition;
                fixedCameraSnapshotElapsedSeconds += deltaSeconds;

                if (useTranslateMode)
                {
                    // Advances the camera's OWN position along its OWN
                    // frozen right/up/forward vectors (captured at
                    // snapshot time, never recomputed) - "right" always
                    // means "right from where the camera started out
                    // facing," not wherever anything might be pointed
                    // later. Up is always true world-space vertical
                    // regardless of camera tilt - see
                    // TranslateStartUp/TranslateEndUp's comment in
                    // Configuration.cs. Explicit Start/End per axis (not
                    // a single "distance in one direction") so the
                    // camera's own H/V/Zoom/HeightOffset placement can
                    // serve as a true center/zero reference with
                    // independent control over both ends of the move.
                    var startOffset = new Vector3(
                        activeViewForFixedCamera!.TranslateStartRight,
                        activeViewForFixedCamera.TranslateStartUp,
                        activeViewForFixedCamera.TranslateStartForward);
                    var endOffset = new Vector3(
                        activeViewForFixedCamera.TranslateEndRight,
                        activeViewForFixedCamera.TranslateEndUp,
                        activeViewForFixedCamera.TranslateEndForward);
                    float totalTranslateDistance = Vector3.Distance(startOffset, endOffset);

                    // CyclePaused now genuinely halts this motion, same
                    // as every other pan already respected it - this one
                    // didn't check it at all before, so "Pause timers"
                    // had no effect on a translate-mode preset.
                    if (totalTranslateDistance > 0.001f && !translatePanMotionComplete && !CyclePaused
                        && fixedCameraSnapshotElapsedSeconds >= activeViewForFixedCamera.TranslateStartDelaySeconds)
                    {
                        translatePanCurrentDistance += translatePanDirection * activeViewForFixedCamera.TranslateSpeed * deltaSeconds;
                        bool justHitTranslateBound = false;
                        if (translatePanCurrentDistance >= totalTranslateDistance) { translatePanCurrentDistance = totalTranslateDistance; justHitTranslateBound = true; }
                        else if (translatePanCurrentDistance <= 0f) { translatePanCurrentDistance = 0f; justHitTranslateBound = true; }

                        if (justHitTranslateBound)
                        {
                            translatePanLegsCompleted++;
                            int legsNeeded = activeViewForFixedCamera.PanReturnBeforeAdvance ? 2 : 1;
                            if (translatePanLegsCompleted >= legsNeeded)
                                translatePanMotionComplete = true;
                            else
                                translatePanDirection = -translatePanDirection;
                        }
                    }

                    CheckTranslatePanAdvanceComplete(activeViewForFixedCamera!);

                    float translateProgress = totalTranslateDistance > 0.001f ? translatePanCurrentDistance / totalTranslateDistance : 0f;

                    // right/up/forward derived from the FROZEN H/V angles
                    // only - right stays purely horizontal (yaw-only, no
                    // roll) regardless of vertical tilt, matching how
                    // strafing works in virtually every camera system.
                    var rightVec = new Vector3(MathF.Cos(fixedCameraSnapshotHRotation), 0f, -MathF.Sin(fixedCameraSnapshotHRotation));
                    var upVec = new Vector3(0f, 1f, 0f);
                    var forwardVec = -new Vector3(
                        MathF.Cos(fixedCameraSnapshotVRotation) * MathF.Sin(fixedCameraSnapshotHRotation),
                        MathF.Sin(fixedCameraSnapshotVRotation),
                        MathF.Cos(fixedCameraSnapshotVRotation) * MathF.Cos(fixedCameraSnapshotHRotation));

                    var currentOffset = Vector3.Lerp(startOffset, endOffset, translateProgress);
                    var worldOffset = rightVec * currentOffset.X + upVec * currentOffset.Y + forwardVec * currentOffset.Z;

                    orbitPos = fixedCameraSnapshotPosition + worldOffset;
                }
            }
            else if (manualSliderCooldownTimer <= 0f && !CyclePaused)
            {
                // First frame for this (view, target) pairing - placed
                // using the exact same H/V/Zoom/HeightOffset sphere math
                // as any other preset, so setting one up feels identical
                // to normal, then frozen from here on. Gated on the same
                // condition ApplyCurrentSavedView itself uses before
                // copying this preset's own H/V/Zoom/HeightOffset into
                // configuration - without this, a snapshot taken while
                // that copy was still being skipped (e.g. the manual-
                // slider cooldown still running from tuning something a
                // moment earlier) could freeze whatever STALE values
                // happened to be left over from a previous preset/target
                // instead of this preset's own intended ones - exactly
                // the "close but not precise" / "looking down instead of
                // up" inconsistency reported. If this doesn't pass yet,
                // simply don't capture - orbitPos/effectiveHRotation/
                // vRotation2 keep recomputing live off whatever's current
                // every frame until it does, then the next frame captures
                // a stable, correct snapshot.
                fixedCameraSnapshotPosition = orbitPos;
                fixedCameraSnapshotTargetRotation = chosenTarget.Rotation;
                fixedCameraSnapshotHRotation = effectiveHRotation;
                fixedCameraSnapshotVRotation = vRotation2;
                fixedCameraSnapshotView = activeViewForFixedCamera;
                fixedCameraSnapshotTargetEntityId = chosenTarget.EntityId;
                hasFixedCameraSnapshot = true;
                fixedCameraSnapshotElapsedSeconds = 0f;
                translatePanCurrentDistance = 0f;
                translatePanDirection = 1;
                translatePanLegsCompleted = 0;
                translatePanMotionComplete = false;
                panAdvanceHasFired = false;

                if (useTranslateMode)
                {
                    // Apply the Start offset immediately, on this same
                    // snapshot frame - otherwise the camera would render
                    // one frame at the raw anchor with zero offset, then
                    // pop to anchor+Start on the very next frame once the
                    // branch above starts running. Same right/up/forward
                    // math as that branch, just evaluated once here too.
                    var rightVec0 = new Vector3(MathF.Cos(fixedCameraSnapshotHRotation), 0f, -MathF.Sin(fixedCameraSnapshotHRotation));
                    var upVec0 = new Vector3(0f, 1f, 0f);
                    var forwardVec0 = -new Vector3(
                        MathF.Cos(fixedCameraSnapshotVRotation) * MathF.Sin(fixedCameraSnapshotHRotation),
                        MathF.Sin(fixedCameraSnapshotVRotation),
                        MathF.Cos(fixedCameraSnapshotVRotation) * MathF.Cos(fixedCameraSnapshotHRotation));
                    var startOffset0 = new Vector3(activeViewForFixedCamera!.TranslateStartRight, activeViewForFixedCamera.TranslateStartUp, activeViewForFixedCamera.TranslateStartForward);
                    orbitPos = fixedCameraSnapshotPosition + rightVec0 * startOffset0.X + upVec0 * startOffset0.Y + forwardVec0 * startOffset0.Z;
                }
            }
        }

        if (configuration.FollowAvoidWallsAndObjects)
        {
            // Pulls the camera in along the same line if a wall or other
            // object sits between the subject and where the orbit math
            // wants the camera - see AvoidWallsAndObjects' comment. Runs
            // before the ground check below so a pulled-in position still
            // gets its own accurate ground height checked afterward.
            orbitPos = AvoidWallsAndObjects(lookAtPoint, orbitPos, configuration.FollowWallAvoidanceBuffer);
        }

        if (configuration.FollowHeightLockToGround)
        {
            // Real per-position ground height via raycast now, not the
            // target's own Y used as a stand-in for wherever the camera
            // actually ends up - see RaycastGroundHeight's comment for
            // why that mismatch was the original source of ground-lock's
            // own popping/shaking on uneven terrain. Starts the ray from
            // just above the higher of the camera's own candidate Y and
            // the target's Y (whichever is higher covers both "camera
            // dropped below ground" and "target is on a different level
            // than the camera" cases) with only a small buffer, rather
            // than a large fixed offset - a tall blind offset is exactly
            // what risked hitting a roof or upper floor above instead of
            // the real ground in an indoor/multi-story area. Falls back
            // to the old target-Y approximation only if the raycast finds
            // nothing at all (e.g. over a void).
            float raycastStartY = MathF.Max(orbitPos.Y, chosenTarget.Position.Y) + 3f;
            float groundY = RaycastGroundHeight(orbitPos.X, orbitPos.Z, raycastStartY) ?? chosenTarget.Position.Y;
            log.Information($"[CamCam] ground raycast: startY={raycastStartY:0.000} foundGroundY={groundY:0.000} targetY={chosenTarget.Position.Y:0.000} orbitYBeforeClamp={orbitPos.Y:0.000}");
            orbitPos.Y = MathF.Max(orbitPos.Y, groundY + configuration.FollowHeightGroundClearance);
        }

        Vector3 finalPos = orbitPos;
        // manualSliderCooldownTimer > 0 means something's being actively
        // tuned right now (or was within the last couple seconds) -
        // bypass smoothing entirely in that case, same reasoning as why
        // it gates ApplyCurrentSavedView's own auto-apply block. Without
        // this, dragging any preset's H/V/Z/etc field eased toward each
        // new value instead of tracking the drag directly, which felt
        // like the camera moving in slow motion while tuning - smoothing
        // is meant to soften ongoing/ambient camera motion (a pan sweep
        // fighting geometry), not something someone's actively adjusting
        // and wants to see respond immediately.
        if (configuration.FollowPositionSmoothingSeconds > 0f && manualSliderCooldownTimer <= 0f)
        {
            // Eases toward orbitPos instead of snapping straight to it -
            // see PositionSmoothingSeconds' comment in Configuration.cs
            // for why (softening the shake from whatever's still fighting
            // the camera near walls/uneven ground even after we override
            // its position - see the Ground lock comment above). Resets
            // to a hard snap whenever the actual target changes (a
            // different player in Cycle, or a FollowMode switch), not
            // just when smoothing is off - continuing to ease across a
            // genuine subject change would look like the camera gliding
            // across the map instead of cutting to the new subject.
            if (!hasSmoothedPosition || lastSmoothedTargetEntityId != chosenTarget.EntityId)
            {
                smoothedPosition = orbitPos;
                hasSmoothedPosition = true;
                lastSmoothedTargetEntityId = chosenTarget.EntityId;
            }
            else
            {
                float t = 1f - MathF.Exp(-deltaSeconds / MathF.Max(configuration.FollowPositionSmoothingSeconds, 0.001f));
                smoothedPosition = Vector3.Lerp(smoothedPosition, orbitPos, t);
            }
            finalPos = smoothedPosition;
        }
        else
        {
            hasSmoothedPosition = false;
        }

        positionHook.OverridePosition = finalPos;
        // No longer also writing camera->X/Y/Z directly here - that was a
        // now-superseded experiment (confirmed via /xllog: the hook
        // fires exactly once per frame with zero gaps, so it was never
        // being skipped). The actual source of the visible shake turned
        // out to be upstream of position writing entirely - see
        // GetStableTargetRotation's comment. Free Fly never does this
        // redundant write either, and this now matches that exactly:
        // positionHook.OverridePosition is the only thing driving
        // position, same as Free Fly.
        camera->CurrentHRotation = effectiveHRotation;
        camera->CurrentVRotation = vRotation2;
        // Deliberately NOT writing camera->CurrentZoom here anymore - Free
        // Fly never touches this field at all (only HRotation, VRotation,
        // and position), while orbit modes used to write it every frame.
        // zoom is already fully baked into the X/Y/Z position above
        // (orbitPos = lookAtPoint + zoom * direction), so this write was
        // never needed for actual positioning - its only effect was
        // whatever else downstream reads/reacts to CurrentZoom changing.
        // GetCameraMaxMaintainDistance (found via Hypostasis reference,
        // see CameraController's docs) is specifically about zoom/distance
        // limiting near geometry - if that's triggered by CurrentZoom
        // writes, this would explain visible shake that a position-only
        // diagnostic can't see, since the position data can look
        // perfectly clean while zoom/FOV is what's actually being nudged.
        // ReadWorldCameraStateForLog below still just READS CurrentZoom
        // (unchanged) for CurrentCameraZoom/diagnostics - now genuinely
        // interesting data, since it'll show whatever the native camera
        // decides on its own rather than reflecting our own write.

        // Shake detector. camera->X/Y/Z is the struct's own OUTPUT field
        // for the camera's current position - reading it right now, right
        // before we overwrite OverridePosition for THIS frame, shows
        // whatever it settled on as a RESULT of last frame's write having
        // been processed. If nothing touches the position after our hook
        // writes it, that should closely match what we wrote last frame
        // (previousFrameFinalPos). Both the target-hook-removal
        // experiment and turning Ground lock off failed to fix the
        // reported shake, ruling out the two leading theories so far -
        // rather than guess a third, this logs hard numbers exactly when
        // an unexpected jump happens, so the actual cause shows up in
        // data instead of another guess. driftFromLastWrite catches
        // something changing the position after our own write;
        // thisFrameJump catches OUR OWN computed position jumping
        // unexpectedly (e.g. from the target's own position being noisy).
        if (hasPreviousFrameWrite && previousFrameTargetEntityId == chosenTarget.EntityId)
        {
            var actualCameraPos = new Vector3(camera->X, camera->Y, camera->Z);
            float driftFromLastWrite = Vector3.Distance(actualCameraPos, previousFrameFinalPos);
            float thisFrameJump = Vector3.Distance(finalPos, previousFrameFinalPos);
            ulong detourCallsSinceLastCheck = positionHook.DetourCallCount - previousDetourCallCount;

            if (driftFromLastWrite > 0.05f || thisFrameJump > 1f)
            {
                // Fresh read here specifically, not relying on
                // ReadWorldCameraStateForLog's own call further below in
                // this same frame - that would still hold last frame's
                // values at this point, and correlating InterpDistance
                // against this frame's own drift is the whole point.
                ReadGameplayCameraStateForLog();

                log.Information(
                    $"[CamCam] shake detected: driftFromLastWrite={driftFromLastWrite:0.000} thisFrameJump={thisFrameJump:0.000} " +
                    $"detourCallsSinceLastCheck={detourCallsSinceLastCheck} " +
                    $"lastWrittenPos=({previousFrameFinalPos.X:0.000},{previousFrameFinalPos.Y:0.000},{previousFrameFinalPos.Z:0.000}) " +
                    $"actualCameraPos=({actualCameraPos.X:0.000},{actualCameraPos.Y:0.000},{actualCameraPos.Z:0.000}) " +
                    $"thisFrameOrbitPos=({orbitPos.X:0.000},{orbitPos.Y:0.000},{orbitPos.Z:0.000}) " +
                    $"thisFrameFinalPos=({finalPos.X:0.000},{finalPos.Y:0.000},{finalPos.Z:0.000}) " +
                    $"targetPos=({chosenTarget.Position.X:0.000},{chosenTarget.Position.Y:0.000},{chosenTarget.Position.Z:0.000}) " +
                    $"deltaSeconds={deltaSeconds:0.000} smoothingSec={configuration.FollowPositionSmoothingSeconds:0.00} " +
                    $"groundLock={configuration.FollowHeightLockToGround} bypassTargetHook={configuration.ExperimentalBypassTargetHook} " +
                    $"gameplayCam({(gameplayCameraAvailableForLog ? $"distance={gameplayCamDistanceForLog:0.000} minDistance={gameplayCamMinDistanceForLog:0.000} maxDistance={gameplayCamMaxDistanceForLog:0.000} interpDistance={gameplayCamInterpDistanceForLog:0.000} savedDistance={gameplayCamSavedDistanceForLog:0.000}" : "unavailable")})");
            }
        }
        previousFrameFinalPos = finalPos;
        previousFrameTargetEntityId = chosenTarget.EntityId;
        previousDetourCallCount = positionHook.DetourCallCount;
        hasPreviousFrameWrite = true;

        ReadWorldCameraStateForLog(camera);

        Status = configuration.FollowMode == CameraFollowMode.None ? "Orbiting you" : $"Orbiting {CurrentCycleName}";
    }

    /// <summary>
    /// The camera's actual current zoom distance and its live MinZoom/
    /// MaxZoom limits - read directly off the native struct, not just
    /// whatever the config sliders say, so it's possible to confirm
    /// whether a setting like Closest zoom is actually reaching the
    /// camera rather than just trusting the slider value.
    /// </summary>
    public float CurrentCameraZoom { get; private set; }
    public float CurrentCameraMinZoom { get; private set; }
    public float CurrentCameraMaxZoom { get; private set; }

    private int worldCamModeForLog;
    private float worldCamHRotationForLog, worldCamVRotationForLog, worldCamZoomForLog;

    private bool gameplayCameraAvailableForLog;
    private float gameplayCamDistanceForLog, gameplayCamMinDistanceForLog, gameplayCamMaxDistanceForLog;
    private float gameplayCamInterpDistanceForLog, gameplayCamSavedDistanceForLog;

    /// <summary>
    /// EXPERIMENTAL - unconfirmed whether FFXIV's native idle/AFK camera
    /// actually reads from this object, or whether it even matters given
    /// the existing position hook is resolved by function address (likely
    /// shared between WorldCamera and IdleCamera, since they're the same
    /// struct type) rather than per-instance. Always caches both cameras'
    /// current values for the periodic diagnostic log regardless of the
    /// toggle, so the comparison is visible even with syncing off - only
    /// the actual write to IdleCamera is gated behind the opt-in setting.
    /// </summary>
    /// <summary>
    /// Reads the game's own per-character Height field directly from the
    /// GameObject struct (confirmed offset via public FFXIVClientStructs
    /// source - not derived from race/gender, so it correctly reflects the
    /// individual height slider too). Feeds directly into the lookAt
    /// height offset via HeightScaleFactor - see that constant's comment
    /// for how the 1.4 multiplier was derived from real logged data
    /// across lalafell through the tallest races. Returns -1f for an
    /// invalid address, which callers treat as "fall back to the old
    /// fixed 1.3f" rather than producing a broken offset.
    /// </summary>
    private static unsafe float GetCharacterHeight(nint gameObjectAddress)
    {
        if (gameObjectAddress == nint.Zero) return -1f;
        return *(float*)(gameObjectAddress + 0xC8);
    }

    public enum CharacterSittingState
    {
        NotSitting,
        Ground,
        Furniture,
    }

    /// <summary>
    /// Reads the Character struct's Mode and ModeParam bytes directly
    /// (confirmed offsets via public FFXIVClientStructs source: Mode at
    /// 0x2364, ModeParam at 0x2365) and maps Mode to Ground/Furniture.
    /// Mode 11 (InPositionLoop) reliably means "anchored to furniture" -
    /// that's a real distinction the game itself makes - but it covers
    /// BOTH chairs and benches identically, since telling those apart
    /// would require identifying the specific furniture object involved,
    /// which isn't implemented here. Mode 3 (EmoteLoop) is treated as
    /// ground-sit, but that Mode also covers other looping emotes like
    /// /doze - the exact ModeParam sub-values that would rule those out
    /// aren't in the public struct docs. The per-target /xllog line
    /// includes the raw mode/modeParam numbers so this can be tightened
    /// with real data later if /doze (or similar) turns out to register
    /// as Ground incorrectly.
    /// </summary>
    private static unsafe CharacterSittingState GetSittingState(nint gameObjectAddress, out byte mode, out byte modeParam)
    {
        if (gameObjectAddress == nint.Zero) { mode = 0; modeParam = 0; return CharacterSittingState.NotSitting; }
        mode = *(byte*)(gameObjectAddress + 0x2364);
        modeParam = *(byte*)(gameObjectAddress + 0x2365);
        if (mode == 11) return CharacterSittingState.Furniture;
        if (mode == 3) return CharacterSittingState.Ground;
        return CharacterSittingState.NotSitting;
    }

    private static bool SatisfiesSittingRequirement(CharacterSittingState state, SittingRequirement requirement) => requirement switch
    {
        SittingRequirement.Ground => state == CharacterSittingState.Ground,
        SittingRequirement.Furniture => state == CharacterSittingState.Furniture,
        _ => state != CharacterSittingState.NotSitting, // Any
    };

    private static unsafe bool IsCharacterSitting(nint gameObjectAddress, out byte mode, out byte modeParam)
        => GetSittingState(gameObjectAddress, out mode, out modeParam) != CharacterSittingState.NotSitting;

    /// <summary>
    /// Reads the equipped item ID for one gear slot directly (confirmed
    /// via public FFXIVClientStructs source: DrawDataContainer starts at
    /// Character+0x6F8, its EquipmentModelId array starts at +0x1D0
    /// within that, 8 bytes per slot, Id as the first 2 bytes of each
    /// entry - so Character+0x8C8 plus slot*8). Slot order: Head=0,
    /// Body=1, Hands=2, Legs=3, Feet=4, Ears=5, Neck=6, Wrists=7,
    /// RFinger=8, LFinger=9. Returns 0 (no valid item ID) for an invalid
    /// address or out-of-range slot.
    /// </summary>
    private static unsafe ushort GetEquippedItemId(nint gameObjectAddress, int slot)
    {
        if (gameObjectAddress == nint.Zero || slot < 0 || slot > 9) return 0;
        return *(ushort*)(gameObjectAddress + 0x8C8 + slot * 8);
    }

    /// <summary>
    /// Reads the world camera's own current Mode/H/V/Zoom and live
    /// zoom bounds off the native struct for the periodic status log and
    /// CurrentCameraZoom/MinZoom/MaxZoom - not related to FFXIV's separate
    /// idling camera. This used to also try to keep that idling camera's
    /// own fields in sync (SyncIdleCameraExperimental), on the theory that
    /// FFXIV's native AFK cinematic reads from them - real testing showed
    /// it still initiated on its own and cycled through other people
    /// regardless, so that theory didn't hold up and the attempt was
    /// removed. The actual fix for FFXIV's own idling camera fighting
    /// CamCam is the "FFXIV's idling camera" toggle in Settings, which
    /// flips the real game setting (System Configuration > Other Settings
    /// > Auto-AFK Settings) via Dalamud's IGameConfig - not something this
    /// plugin can meaningfully override once it's already running.
    /// </summary>
    private void ReadWorldCameraStateForLog(RawGameCamera* camera)
    {
        worldCamModeForLog = camera->Mode;
        worldCamHRotationForLog = camera->CurrentHRotation;
        worldCamVRotationForLog = camera->CurrentVRotation;
        worldCamZoomForLog = camera->CurrentZoom;

        CurrentCameraZoom = camera->CurrentZoom;
        CurrentCameraMinZoom = camera->MinZoom;
        CurrentCameraMaxZoom = camera->MaxZoom;

        ReadGameplayCameraStateForLog();
    }

    /// <summary>
    /// Reads FFXIVClientStructs' own officially-maintained
    /// Client::Game::Camera - a completely separate, higher-level
    /// structure from RawGameCamera (which is Hypostasis's own
    /// reverse-engineered render-layer struct, used for everything else
    /// in this file). Accessed via CameraManager.Instance()->Camera - a
    /// direct field read/write through FFXIVClientStructs' own already-
    /// resolved static address system, same category of access as
    /// BGCollisionModule.RaycastMaterialFilter elsewhere in this file,
    /// not a custom hook.
    ///
    /// A user-captured /xllog line showed driftFromLastWrite=0.477 and
    /// gameplayCam's interpDistance=0.477 - identical to three decimal
    /// places. That match was real, but user-tested and DISPROVEN as an
    /// interception point: zeroing InterpDistance every frame fired
    /// constantly yet drift stayed completely unchanged (still 0.060,
    /// identical to before the fix) - the native engine recreates it
    /// faster than it can be cleared, so it was never actually preventing
    /// anything. Off by default now for that reason.
    ///
    /// Next experiment, on the same safe struct: forcing Distance itself
    /// (not InterpDistance) to match CamCam's own intended zoom every
    /// frame. DISPROVEN, and worse than InterpDistance-zeroing was - not
    /// just ineffective but confirmed actively harmful. A captured
    /// /xllog sequence showed this correcting camera->Distance by a real
    /// amount (1.530 -> 1.892), immediately followed on the SAME frame
    /// by a 15+ degree vertical-angle jump and a ~0.5 unit position jump
    /// - with no preset or target switch happening at that moment
    /// (confirmed directly, not assumed). Distance likely isn't an
    /// isolated value to the native camera - forcing a sudden, large
    /// change in it plausibly triggers a native recalculation of other
    /// parameters (interpreted like a big manual zoom or collision
    /// event), with vertical angle as collateral damage. Off by default
    /// for that reason.
    /// </summary>
    private void ReadGameplayCameraStateForLog()
    {
        var camera = FFXIVClientStructs.FFXIV.Client.Game.Control.CameraManager.Instance()->Camera;
        if (camera == null)
        {
            gameplayCameraAvailableForLog = false;
            return;
        }

        gameplayCameraAvailableForLog = true;
        gameplayCamDistanceForLog = camera->Distance;
        gameplayCamMinDistanceForLog = camera->MinDistance;
        gameplayCamMaxDistanceForLog = camera->MaxDistance;
        gameplayCamInterpDistanceForLog = camera->InterpDistance;
        gameplayCamSavedDistanceForLog = camera->SavedDistance;

        // User-found bug: this never had the !configuration.FreeFly guard
        // that ForceNativeDistanceToMatchZoom below it has - it was
        // running every frame during Free Fly too, and user testing
        // showed that's specifically why Free Fly could only "nudge" in
        // place instead of actually traveling anywhere: zeroing
        // InterpDistance every frame during Free Fly was fighting
        // whatever native distance/interpolation state the free camera
        // itself also depends on. Now scoped the same way Distance-
        // forcing already was, correctly, right from the start.
        if (configuration.ZeroOutNativeInterpDistance && !configuration.FreeFly && camera->InterpDistance != 0f)
        {
            log.Information($"[CamCam] Zeroing native InterpDistance (was {camera->InterpDistance:0.000}) - user-tested: this does not reduce position drift, left available only in case that's ever contradicted by new data.");
            camera->InterpDistance = 0f;
        }

        // Not meaningful during Free Fly - configuration.Zoom isn't the
        // value actually driving anything there, so only applied for the
        // orbit-based follow modes.
        if (configuration.ForceNativeDistanceToMatchZoom && !configuration.FreeFly
            && MathF.Abs(camera->Distance - configuration.Zoom) > 0.01f)
        {
            log.Warning($"[CamCam] Forcing native Distance to {configuration.Zoom:0.000} (was {camera->Distance:0.000}) - user-confirmed harmful side effect: this specific correction is known to sometimes trigger a large, unrelated vertical-angle jump on the same frame. Off by default - this only fires if manually re-enabled.");
            camera->Distance = configuration.Zoom;
        }
    }
}
