using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Game.ClientState.Objects.Types;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game.Control;
using FFXIVClientStructs.FFXIV.Common.Component.BGCollision;
using FFXIVClientStructs.FFXIV.Client.Game;
using GameCamera = FFXIVClientStructs.FFXIV.Client.Game.Camera;
using NativeCharacter = FFXIVClientStructs.FFXIV.Client.Game.Character.Character;
using NativeGameObject = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject;

namespace CamCam;

/// <summary>
/// Every frame, if CamCam is enabled, this drives the world camera one of
/// two ways:
///  - Free Fly: true 3D movement + turning via the configured keys, using
///    the getCameraPosition hook. Engages immediately.
///  - Orbit (Myself / My /target / Cycle): computes the camera position
///    from look-at point + H/V rotation + zoom + height offset, with H
///    relative to the subject's own facing. Engages once you've been idle
///    for IdleThresholdSeconds - the AFK-cinematic replacement.
///
/// Holding right/left mouse (outside CamCam's own window) hands control
/// back to the game for as long as it's held plus a short cooldown.
/// Cutscenes, zone transitions, gpose and (optionally) combat always hand
/// control back too.
/// </summary>
public unsafe class CameraController
{
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    private const int VK_RBUTTON = 0x02;
    private const int VK_LBUTTON = 0x01;
    private const float ManualInputCooldownSeconds = 2f;
    private const float ManualSliderCooldownSeconds = 2.5f;
    private const float StuckGrabTimeoutSeconds = 15f;
    private const float CandidateRefreshSeconds = 0.5f;
    private const float StatusLogIntervalSeconds = 2f;

    // Look-at height = character Height * this. Derived from real logs:
    // 1.3 looked right for a realHeight=0.93 character, 1.3/0.93 ~= 1.4.
    private const float HeightScaleFactor = 1.4f;

    private static readonly string[] NumpadKeyNames =
    {
        "Numpad0", "Numpad1", "Numpad2", "Numpad3", "Numpad4",
        "Numpad5", "Numpad6", "Numpad7", "Numpad8", "Numpad9",
        "Numpad+", "Numpad-", "Numpad*", "Numpad/", "Numpad.",
    };

    private enum PoseResult
    {
        NotEngaged,
        Yielding,
        Engaged,
    }

    /// <summary>
    /// One animated axis of a preset (horizontal, vertical, zoom, or
    /// strafe distance). Moves linearly from start to end (and optionally
    /// back), at a constant speed; Output() optionally applies
    /// ease-in/ease-out on top without changing the total duration.
    /// </summary>
    private sealed class PanAxis
    {
        public float Current;
        public bool Complete;
        public int Direction = 1;
        private int legs;

        public void Reset(float start, float end)
        {
            Current = start;
            Direction = end >= start ? 1 : -1;
            legs = 0;
            Complete = false;
        }

        public void MarkComplete() => Complete = true;

        public void Advance(float start, float end, float speed, float deltaSeconds, bool roundTrip)
        {
            if (Complete) return;
            Current += Direction * MathF.Max(speed, 0.01f) * deltaSeconds;
            float lo = MathF.Min(start, end);
            float hi = MathF.Max(start, end);

            bool hitBound = false;
            if (Current >= hi) { Current = hi; hitBound = true; }
            else if (Current <= lo) { Current = lo; hitBound = true; }
            if (!hitBound) return;

            legs++;
            if (legs >= (roundTrip ? 2 : 1))
                Complete = true;
            else
                Direction = -Direction;
        }

        public float Output(float start, float end, bool ease)
        {
            float range = end - start;
            if (!ease || MathF.Abs(range) < 1e-5f) return Current;
            float t = Math.Clamp((Current - start) / range, 0f, 1f);
            return start + range * (t * t * (3f - 2f * t));
        }
    }

    private readonly Configuration configuration;
    private readonly ITargetManager targetManager;
    private readonly IObjectTable objectTable;
    private readonly IClientState clientState;
    private readonly ICondition condition;
    private readonly WorldCameraTargetHook targetHook;
    private readonly WorldCameraPositionHook positionHook;
    private readonly FreeCamController freeCam;
    private readonly FlyKeyBlocker keyBlocker;
    private readonly IKeyState keyState;
    private readonly IPluginLog log;
    private readonly InputMonitor inputMonitor;
    private readonly Random random = new();

    // Cycle / preset selection
    private uint? cycleSelectedPlayerEntityId;
    private int cycleViewIndex;
    private float cycleTimer;
    private float presetCycleTimer;
    private int? previousRandomViewIndex;
    private CameraFollowMode? lastFollowMode;
    private List<IPlayerCharacter> cachedCandidates = new();
    private float candidateRefreshTimer = float.MaxValue;

    // Pan state for whichever preset is active
    private SavedView? activePanView;
    private readonly PanAxis hPan = new();
    private readonly PanAxis vPan = new();
    private readonly PanAxis zoomPan = new();
    private readonly PanAxis translatePan = new();
    private readonly PanAxis fovPan = new();

    // Native FOV before CamCam changed it (radians) - restored on disengage.
    private float? originalFov;
    private float? originalMinFov;
    private float? originalMaxFov;

    // Shot-to-shot transitions (see SavedView.TransitionSeconds).
    private bool hasLastPose;
    private SavedView? lastPoseView;
    private uint lastPoseTargetEntityId;
    private Vector3 lastPosePosition;
    private float lastPoseH, lastPoseV, lastPoseFov;
    private bool transitioning;
    private float transitionElapsed, transitionDuration;
    private Vector3 transitionFromPosition;
    private float transitionFromH, transitionFromV, transitionFromFov;

    // Path playback.
    private SavedView? pathView;
    private uint pathTargetEntityId;
    private float pathElapsed;
    private bool pathComplete;
    private Vector3 pathAnchor;
    private float pathAnchorRotation;

    // Path recording (Free Fly).
    private bool recordKeyWasHeld;
    private List<PathKeyframe>? recording;
    private float recordingElapsed;
    private float recordingSampleTimer;
    private Vector3 recordingAnchor;
    private float recordingAnchorRotation;
    private float recordingLastH;
    private float activePanElapsedSeconds;
    private bool panAdvanceHasFired;

    // Native camera limits before CamCam widened them - restored on disable.
    private float? originalMinZoom;
    private float? originalMaxVRotation;
    private float? originalMinVRotation;

    // Input / engagement
    private bool uiCurrentlyHiddenByUs;
    private float manualInputCooldownTimer;
    private float continuousGrabDurationSeconds;
    private float manualSliderCooldownTimer;
    private bool freeFlyToggleKeyWasHeld;
    private bool numpadBlockToggleKeyWasHeld;
    private bool numpadBlockManuallyDisabled;
    private bool toggleCamCamKeyWasHeld;
    private bool heightKeyWasHeld;
    private uint? lastFreeFlyTargetEntityId;
    private Vector3 lastPlayerPos;
    private bool hasLastPlayerPos;
    private float idleTimer;
    private float statusLogTimer;

    // Position smoothing - resets to a hard cut whenever the subject OR
    // the preset changes, so cuts stay cuts.
    private Vector3 smoothedPosition;
    private bool hasSmoothedPosition;
    private uint lastSmoothedTargetEntityId;
    private SavedView? lastSmoothedView;

    // Tripod / strafe snapshot, captured once per (view, target) pairing.
    private bool hasFixedCameraSnapshot;
    private SavedView? fixedCameraSnapshotView;
    private uint fixedCameraSnapshotTargetEntityId;
    private Vector3 fixedCameraSnapshotPosition;
    private float fixedCameraSnapshotTargetRotation;
    private float fixedCameraSnapshotHRotation;
    private float fixedCameraSnapshotVRotation;
    private float fixedCameraSnapshotElapsedSeconds;

    // Verbose-only diagnostics
    private Vector3 previousFrameFinalPos;
    private bool hasPreviousFrameWrite;
    private uint previousFrameTargetEntityId;
    private string equipmentIdsForLog = "";

    public string Status { get; private set; } = "Idle";
    public string CurrentCycleName { get; private set; } = "(none nearby)";

    /// <summary>True while CamCam is actively driving the camera this frame.</summary>
    public bool IsEngaged { get; private set; }

    /// <summary>Index into SavedViews of the active (most recently loaded or cycled-to) preset.</summary>
    public int CurrentViewIndex => cycleViewIndex;

    /// <summary>Whether Saved Views are what's driving the camera in the current mode.</summary>
    public bool SavedViewsAreDriving => configuration.SavedViews.Count > 0
        && (configuration.FollowMode != CameraFollowMode.Cycle || configuration.CycleUseSavedViews);

    /// <summary>The preset currently driving the camera, or null if presets aren't driving.</summary>
    public SavedView? ActiveView => SavedViewsAreDriving
        ? configuration.SavedViews[Math.Clamp(cycleViewIndex, 0, configuration.SavedViews.Count - 1)]
        : null;

    public bool IsRecording => recording != null;
    public float RecordingSeconds => recordingElapsed;
    public int RecordingKeyframes => recording?.Count ?? 0;

    /// <summary>The game's own FOV in degrees (before any CamCam override).</summary>
    public float GameFovDegrees { get; private set; } = 45f;

    /// <summary>Freezes the auto-cycle and auto-preset timers. Session-only.</summary>
    public bool CyclePaused { get; set; }

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

    /// <summary>Live values read off the native camera, to confirm settings actually reach it.</summary>
    public float CurrentCameraZoom { get; private set; }
    public float CurrentCameraMinZoom { get; private set; }
    public float CurrentCameraMaxZoom { get; private set; }

    public CameraController(
        Configuration configuration,
        ITargetManager targetManager,
        IObjectTable objectTable,
        IClientState clientState,
        ICondition condition,
        WorldCameraTargetHook targetHook,
        WorldCameraPositionHook positionHook,
        FreeCamController freeCam,
        FlyKeyBlocker keyBlocker,
        IKeyState keyState,
        IGamepadState gamepadState,
        IPluginLog log)
    {
        this.configuration = configuration;
        this.targetManager = targetManager;
        this.objectTable = objectTable;
        this.clientState = clientState;
        this.condition = condition;
        this.targetHook = targetHook;
        this.positionHook = positionHook;
        this.freeCam = freeCam;
        this.keyBlocker = keyBlocker;
        this.keyState = keyState;
        this.log = log;
        inputMonitor = new InputMonitor(gamepadState);
    }

    private void Verbose(string message)
    {
        if (configuration.VerboseLogging)
            log.Information(message);
    }

    // ------------------------------------------------------------------
    // Public controls
    // ------------------------------------------------------------------

    public void CycleNext() => StepCycle(+1);

    public void CyclePrevious() => StepCycle(-1);

    private void StepCycle(int step)
    {
        var players = GetCycleCandidates(forceRefresh: true);
        if (players.Count > 0)
        {
            int idx = ((ResolvePlayerIndex(players) + step) % players.Count + players.Count) % players.Count;
            cycleSelectedPlayerEntityId = players[idx].EntityId;
        }

        int count = configuration.SavedViews.Count;
        if (count > 0 && (configuration.FollowMode != CameraFollowMode.Cycle || configuration.CycleUseSavedViews))
            cycleViewIndex = ((cycleViewIndex + step) % count + count) % count;

        cycleTimer = 0f;
        presetCycleTimer = 0f;
    }

    /// <summary>Call while any camera/preset value is being actively edited - holds off auto-apply and smoothing so the edit shows immediately.</summary>
    public void NotifySliderAdjusted() => manualSliderCooldownTimer = ManualSliderCooldownSeconds;

    /// <summary>Makes a preset the active one and restarts its motion from the beginning.</summary>
    public void LoadView(SavedView view, int index, bool save = true)
    {
        hasSmoothedPosition = false;
        hasFixedCameraSnapshot = false;
        freeCam.ClearStartingPosition();

        ApplyViewToConfig(view);
        activePanView = null;
        SetActivePanView(view);
        SetCycleView(index);

        if (save) configuration.Save();
    }

    /// <summary>
    /// Called by the settings window while a preset's values are edited:
    /// shows the edited (static) pose immediately and restarts its motion
    /// once editing stops.
    /// </summary>
    public void PreviewEdit(SavedView view)
    {
        NotifySliderAdjusted();
        int index = configuration.SavedViews.IndexOf(view);
        if (index >= 0)
            LoadView(view, index, save: false);
    }

    /// <summary>Syncs the auto-cycle's index to a manually chosen preset and restarts both timers.</summary>
    public void SetCycleView(int index)
    {
        if (configuration.SavedViews.Count == 0) return;
        cycleViewIndex = Math.Clamp(index, 0, configuration.SavedViews.Count - 1);
        cycleTimer = 0f;
        presetCycleTimer = 0f;
    }

    public void Shutdown()
    {
        RestoreUiIfHidden();
        RestoreCameraLimits(writeBack: clientState.IsLoggedIn);
        keyBlocker.Remove();
        targetHook.Remove();
        positionHook.Remove();
    }

    // ------------------------------------------------------------------
    // Frame update
    // ------------------------------------------------------------------

    public void OnFrameworkUpdate(IFramework framework)
    {
        // Hard gate: hooks landing mid login/character-select transition
        // were traced to a crash. Fully unhook while logged out.
        if (!clientState.IsLoggedIn)
        {
            if (Status != "Not logged in")
                log.Information("[CamCam] Not logged in - removing all hooks.");
            FullDisengage("Not logged in", writeBackLimits: false);
            return;
        }

        // Clamped so a hitch (zone load, alt-tab) can't make pans jump.
        float deltaSeconds = MathF.Min((float)framework.UpdateDelta.TotalSeconds, 0.1f);

        if (CyclePaused)
        {
            cycleTimer = 0f;
            presetCycleTimer = 0f;
        }

        HandleHotkeys();

        if (!configuration.Enabled)
        {
            if (Status != "Idle")
                log.Information("[CamCam] Disabled - removing hooks.");
            FullDisengage("Idle", writeBackLimits: true);
            return;
        }

        // Situations where the game must own the camera.
        string? blockedReason = GetBlockedReason(out bool removeHooks, out bool restoreUi);
        if (blockedReason != null)
        {
            IsEngaged = false;
            if (removeHooks)
            {
                targetHook.Remove();
                positionHook.Remove();
            }
            else
            {
                targetHook.Release();
                positionHook.Release();
            }
            keyBlocker.Remove();
            freeCam.ClearStartingPosition();
            lastFreeFlyTargetEntityId = null;
            idleTimer = 0f;
            inputMonitor.Reset();
            hasSmoothedPosition = false;
            if (restoreUi) RestoreUiIfHidden();
            RestoreFov(writeBack: !removeHooks);
            StopRecording(save: true);
            hasLastPose = false;
            transitioning = false;
            Status = blockedReason;
            return;
        }

        if (manualSliderCooldownTimer > 0f)
            manualSliderCooldownTimer -= deltaSeconds;

        UpdateKeyBlocking();

        PoseResult result;
        try
        {
            result = WriteCameraPose(deltaSeconds);
        }
        catch (Exception ex)
        {
            result = PoseResult.NotEngaged;
            targetHook.Release();
            positionHook.Release();
            Status = $"Error: {ex.Message}";
            log.Warning(ex, "CamCam failed to write camera pose");
        }

        IsEngaged = result == PoseResult.Engaged;
        if (!IsEngaged)
        {
            hasLastPose = false;
            transitioning = false;
            RestoreFov(writeBack: true);
        }
        if (IsRecording && !(IsEngaged && configuration.FreeFly) && result != PoseResult.Yielding)
            StopRecording(save: true);
        UpdateAutoHideUi(result != PoseResult.NotEngaged);

        if (configuration.VerboseLogging)
        {
            statusLogTimer += deltaSeconds;
            if (statusLogTimer >= StatusLogIntervalSeconds)
            {
                statusLogTimer = 0f;
                log.Information(
                    $"[CamCam] status={Status} freeFly={configuration.FreeFly} followMode={configuration.FollowMode} " +
                    $"cycleTarget={CurrentCycleName} targetHook={targetHook.Status} positionHook={positionHook.Status} " +
                    $"keyBlocker={keyBlocker.Status} uiHidden={uiCurrentlyHiddenByUs} idle={idleTimer:0.0}/{configuration.IdleThresholdSeconds:0.0} " +
                    $"manualCooldown={manualInputCooldownTimer:0.0} numLock={(FreeCamController.IsNumLockOn ? "ON" : "OFF")} " +
                    $"pan(active={activePanView?.Name ?? "none"} h={hPan.Current:0.0}/{hPan.Complete} v={vPan.Current:0.0}/{vPan.Complete} " +
                    $"z={zoomPan.Current:0.00}/{zoomPan.Complete} t={translatePan.Current:0.00}/{translatePan.Complete}) " +
                    $"cycleTimer={cycleTimer:0.0}/{configuration.CycleIntervalSeconds:0.0} presetTimer={presetCycleTimer:0.0}/{configuration.PresetCycleIntervalSeconds:0.0} " +
                    $"equipment({equipmentIdsForLog})");
            }
        }
    }

    private void FullDisengage(string status, bool writeBackLimits)
    {
        IsEngaged = false;
        Status = status;
        if (writeBackLimits)
        {
            positionHook.Release();
            RestoreFov(writeBack: true);
        }
        else
        {
            RestoreFov(writeBack: false);
        }
        StopRecording(save: writeBackLimits);
        hasLastPose = false;
        targetHook.Remove();
        positionHook.Remove();
        keyBlocker.Remove();
        freeCam.ClearStartingPosition();
        lastFreeFlyTargetEntityId = null;
        hasSmoothedPosition = false;
        RestoreUiIfHidden();
        RestoreCameraLimits(writeBackLimits);
    }

    private string? GetBlockedReason(out bool removeHooks, out bool restoreUi)
    {
        removeHooks = false;
        restoreUi = true;

        if (condition[ConditionFlag.BetweenAreas] || condition[ConditionFlag.BetweenAreas51])
        {
            // The camera system is torn down/rebuilt during zone loads -
            // same reasoning as the logged-out gate.
            removeHooks = true;
            restoreUi = false;
            return "Paused: changing zones";
        }

        if (condition[ConditionFlag.WatchingCutscene] || condition[ConditionFlag.WatchingCutscene78]
            || condition[ConditionFlag.OccupiedInCutSceneEvent])
        {
            // The game hides/shows its own UI around cutscenes - toggling
            // here would desync our hidden/shown bookkeeping.
            restoreUi = false;
            return "Paused: cutscene";
        }

        if (clientState.IsGPosing)
        {
            restoreUi = false;
            return "Paused: gpose";
        }

        if (configuration.DisengageInCombat && condition[ConditionFlag.InCombat])
            return "Paused: in combat";

        return null;
    }

    private void HandleHotkeys()
    {
        if (!string.IsNullOrWhiteSpace(configuration.ToggleCamCamKeyName))
        {
            bool held = FreeCamController.IsHeld(configuration.ToggleCamCamKeyName);
            if (held && !toggleCamCamKeyWasHeld)
            {
                configuration.Enabled = !configuration.Enabled;
                configuration.Save();
                log.Information($"[CamCam] CamCam toggled to {configuration.Enabled} via keybind.");
            }
            toggleCamCamKeyWasHeld = held;
        }

        if (!string.IsNullOrWhiteSpace(configuration.FreeFlyToggleKeyName))
        {
            bool held = FreeCamController.IsHeld(configuration.FreeFlyToggleKeyName);
            if (held && !freeFlyToggleKeyWasHeld)
            {
                configuration.FreeFly = !configuration.FreeFly;
                if (configuration.FreeFly)
                    configuration.Enabled = true;
                configuration.Save();
                log.Information($"[CamCam] Free Fly toggled to {configuration.FreeFly} via keybind.");
            }
            freeFlyToggleKeyWasHeld = held;
        }

        if (!string.IsNullOrWhiteSpace(configuration.NumpadBlockToggleKeyName))
        {
            bool held = FreeCamController.IsHeld(configuration.NumpadBlockToggleKeyName);
            if (held && !numpadBlockToggleKeyWasHeld)
            {
                numpadBlockManuallyDisabled = !numpadBlockManuallyDisabled;
                log.Information($"[CamCam] Numpad key blocking manually {(numpadBlockManuallyDisabled ? "disabled" : "re-enabled")}.");
            }
            numpadBlockToggleKeyWasHeld = held;
        }

        if (!string.IsNullOrWhiteSpace(configuration.RecordToggleKeyName))
        {
            bool held = FreeCamController.IsHeld(configuration.RecordToggleKeyName);
            if (held && !recordKeyWasHeld)
                ToggleRecording();
            recordKeyWasHeld = held;
        }
    }

    /// <summary>Virtual keys that never count as "the player is back": CamCam's own keys and the UI-toggle key it presses itself.</summary>
    private HashSet<int> BuildIgnoredInputKeys()
    {
        var set = new HashSet<int>();
        void Add(string name)
        {
            int vk = FreeCamController.ResolveKey(name);
            if (vk != 0) set.Add(vk);
        }

        foreach (var name in NumpadKeyNames) Add(name);
        Add(configuration.FlyForwardKey);
        Add(configuration.FlyBackKey);
        Add(configuration.FlyLeftKey);
        Add(configuration.FlyRightKey);
        Add(configuration.FlyUpKey);
        Add(configuration.FlyDownKey);
        Add(configuration.FlyTurnLeftKey);
        Add(configuration.FlyTurnRightKey);
        Add(configuration.FlyLookUpKey);
        Add(configuration.FlyLookDownKey);
        Add(configuration.FlyFastModifierKey);
        Add(configuration.FlySlowModifierKey);
        Add(configuration.ToggleCamCamKeyName);
        Add(configuration.FreeFlyToggleKeyName);
        Add(configuration.NumpadBlockToggleKeyName);
        Add(configuration.RecordToggleKeyName);
        Add(configuration.UiToggleKeyName);
        set.Add(0x90); // Num Lock
        set.Add(0x91); // Scroll Lock
        return set;
    }

    private void UpdateKeyBlocking()
    {
        bool needsKeyBlocking = !numpadBlockManuallyDisabled
            && (configuration.FreeFly
                || configuration.FollowMode != CameraFollowMode.None
                || !string.IsNullOrWhiteSpace(configuration.FreeFlyToggleKeyName)
                || !string.IsNullOrWhiteSpace(configuration.ToggleCamCamKeyName));

        if (!needsKeyBlocking)
        {
            keyBlocker.Remove();
            return;
        }

        // The whole numpad, plus the toggle keys - not just the bound fly
        // keys, so unbound numpad keys don't leak through to hotbars.
        var names = new List<string>(NumpadKeyNames)
        {
            configuration.FreeFlyToggleKeyName,
            configuration.ToggleCamCamKeyName,
        };
        keyBlocker.Install();
        keyBlocker.SetBlockedKeys(names);

        // The OS-level hook can't stop the game's own key buffer - clear it
        // through Dalamud's IKeyState too. Only while the game is focused
        // and nobody is typing, same as the hook.
        if (GameWindow.AcceptsHotkeys)
            ClearGameKeyState(names);
    }

    private void ClearGameKeyState(IEnumerable<string> keyNames)
    {
        foreach (var name in keyNames)
        {
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

    private void UpdateAutoHideUi(bool engagedOrYielding)
    {
        // Hides the UI only once the camera actually takes over (like the
        // game's own AFK camera), not the moment CamCam is enabled.
        bool shouldHide = configuration.AutoHideUi && engagedOrYielding;

        // keybd_event goes to whatever window is focused - never send it to
        // another app or into the chat box. Retried next frame.
        if (!GameWindow.AcceptsHotkeys) return;
        if (shouldHide && !uiCurrentlyHiddenByUs)
        {
            UiToggleHelper.SimulateToggle(configuration.UiToggleKeyName);
            uiCurrentlyHiddenByUs = true;
        }
        else if (!shouldHide && uiCurrentlyHiddenByUs)
        {
            RestoreUiIfHidden();
        }
    }

    private void RestoreUiIfHidden()
    {
        if (!uiCurrentlyHiddenByUs || !GameWindow.AcceptsHotkeys) return;
        UiToggleHelper.SimulateToggle(configuration.UiToggleKeyName);
        uiCurrentlyHiddenByUs = false;
    }

    private void RestoreCameraLimits(bool writeBack)
    {
        if (writeBack && (originalMinZoom.HasValue || originalMaxVRotation.HasValue || originalMinVRotation.HasValue))
        {
            var camera = GetWorldCamera();
            if (camera != null)
            {
                if (originalMinZoom.HasValue) camera->MinDistance = originalMinZoom.Value;
                if (originalMaxVRotation.HasValue) camera->DirVMax = originalMaxVRotation.Value;
                if (originalMinVRotation.HasValue) camera->DirVMin = originalMinVRotation.Value;
            }
        }

        // Always forget them - they're re-captured fresh next time, so a
        // recreated camera object never gets stale values.
        originalMinZoom = null;
        originalMaxVRotation = null;
        originalMinVRotation = null;
    }

    private static GameCamera* GetWorldCamera()
    {
        var manager = CameraManager.Instance();
        return manager == null ? null : manager->Camera;
    }

    private bool IsUserManuallyControllingCamera()
    {
        if (!GameWindow.IsFocused) return false;

        bool rightHeld = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        bool leftHeld = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        if (!rightHeld && !leftHeld) return false;

        // Clicks on CamCam's own (or any ImGui) window aren't camera grabs.
        if (ImGui.GetIO().WantCaptureMouse) return false;

        return true;
    }

    private void UpdateIdleTimer(float deltaSeconds)
    {
        if (configuration.IdleDetection == IdleDetectionMode.AnyInput)
        {
            inputMonitor.Update(deltaSeconds, configuration, BuildIgnoredInputKeys());
            idleTimer = inputMonitor.SecondsSinceInput;
            return;
        }

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

    // ------------------------------------------------------------------
    // Camera pose
    // ------------------------------------------------------------------

    private PoseResult WriteCameraPose(float deltaSeconds)
    {
        var camera = GetWorldCamera();
        if (camera == null)
        {
            Status = "WorldCamera not ready";
            return PoseResult.NotEngaged;
        }

        if (camera->MaxDistance <= 0f)
        {
            Status = "WorldCamera not active";
            return PoseResult.NotEngaged;
        }

        bool grabbingCamera = IsUserManuallyControllingCamera();

        // GetAsyncKeyState can report a mouse button stuck down after
        // certain focus changes; no real drag lasts this long.
        if (grabbingCamera)
        {
            continuousGrabDurationSeconds += deltaSeconds;
            if (continuousGrabDurationSeconds > StuckGrabTimeoutSeconds)
            {
                log.Warning($"[CamCam] Mouse grab reported continuously for {StuckGrabTimeoutSeconds:0}s - assuming a stuck key read and taking control back.");
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
            idleTimer = 0f;
        }
        else if (manualInputCooldownTimer > 0f)
        {
            manualInputCooldownTimer -= deltaSeconds;
        }

        if (grabbingCamera || manualInputCooldownTimer > 0f)
        {
            targetHook.Release();
            positionHook.Release();
            hasSmoothedPosition = false;
            Status = "Yielding to your manual camera control";
            return PoseResult.Yielding;
        }

        if (!configuration.FreeFly)
        {
            // Reset to the game's own baseline every frame, then widen, so
            // each preset's limits apply on their own.
            originalMinZoom ??= camera->MinDistance;
            originalMaxVRotation ??= camera->DirVMax;
            originalMinVRotation ??= camera->DirVMin;

            float maxAngleRadians = configuration.FollowMaxAngleDegrees * (MathF.PI / 180f);
            camera->MinDistance = MathF.Min(originalMinZoom.Value, configuration.FollowMinZoom);
            camera->DirVMax = MathF.Max(originalMaxVRotation.Value, maxAngleRadians);
            camera->DirVMin = MathF.Min(originalMinVRotation.Value, -maxAngleRadians);
        }

        float safeMaxVRotation = camera->DirVMax - 0.02f;
        float safeMinVRotation = camera->DirVMin + 0.02f;

        if (configuration.FreeFly)
            return WriteFreeFlyPose(camera, deltaSeconds, safeMinVRotation, safeMaxVRotation);

        return WriteOrbitPose(camera, deltaSeconds, safeMinVRotation, safeMaxVRotation);
    }

    private PoseResult WriteFreeFlyPose(GameCamera* camera, float deltaSeconds, float safeMinVRotation, float safeMaxVRotation)
    {
        targetHook.Release();
        idleTimer = 0f;

        positionHook.Install(camera);
        if (!positionHook.IsInstalled)
        {
            Status = $"Free fly failed to install: {positionHook.Status}";
            return PoseResult.NotEngaged;
        }

        // Runs the same target/preset selection as orbit mode so cycling
        // keeps working while flying - but that writes the preset's H/V/
        // Zoom every frame. Keep those only when the target actually
        // changed (snap to the new subject) or a pan is animating them;
        // otherwise revert so manual turning isn't fought.
        float preSelectH = configuration.HorizontalRotation;
        float preSelectV = configuration.VerticalRotation;
        float preSelectZoom = configuration.Zoom;
        uint? previousTargetId = lastFreeFlyTargetEntityId;

        var chosenTarget = SelectChosenTarget(deltaSeconds);
        bool targetChanged = chosenTarget != null
            && (previousTargetId == null || previousTargetId.Value != chosenTarget.EntityId);
        lastFreeFlyTargetEntityId = chosenTarget?.EntityId;

        if (targetChanged)
        {
            freeCam.ClearStartingPosition();
        }
        else
        {
            var view = ActiveView;
            bool presetAnimating = view != null && (view.PanEnabled || view.VerticalPanEnabled || view.ZoomPanEnabled);
            if (!presetAnimating)
            {
                configuration.HorizontalRotation = preSelectH;
                configuration.VerticalRotation = preSelectV;
                configuration.Zoom = preSelectZoom;
            }
        }

        if (!freeCam.HasStartingPosition)
        {
            // Start at the same relative framing orbit mode would produce
            // on the current subject.
            var subject = chosenTarget ?? objectTable.LocalPlayer;
            var subjectPos = subject?.Position ?? (Vector3)camera->CameraBase.SceneCamera.Position;
            var lookAt = subjectPos + new Vector3(0f, GetLookAtHeight(subject) + configuration.FollowHeightOffset, 0f);
            float startV = Math.Clamp(configuration.VerticalRotation, safeMinVRotation, safeMaxVRotation);
            freeCam.ResetTo(lookAt + configuration.Zoom * OrbitDirection(configuration.HorizontalRotation, startV));
        }

        float hRotation = configuration.HorizontalRotation;
        float vRotation = Math.Clamp(configuration.VerticalRotation, safeMinVRotation, safeMaxVRotation);

        freeCam.Update(deltaSeconds, ref hRotation, ref vRotation, configuration.FreeFlySpeed, configuration.FreeFlyTurnSpeed, configuration);
        vRotation = Math.Clamp(vRotation, safeMinVRotation, safeMaxVRotation);

        if (configuration.FreeFlyLockToGround)
        {
            // Real terrain under the camera, falling back to your
            // character's height if the raycast finds nothing.
            var pos = freeCam.Position;
            float groundY = RaycastGroundHeight(pos.X, pos.Z, pos.Y + 3f)
                ?? objectTable.LocalPlayer?.Position.Y
                ?? pos.Y;
            freeCam.ClampMinimumY(groundY + configuration.FreeFlyGroundClearance);
        }

        configuration.HorizontalRotation = hRotation;
        configuration.VerticalRotation = vRotation;

        camera->ZoomMode = (CameraZoomMode)1; // third person
        camera->DirH = hRotation;
        camera->DirV = vRotation;
        positionHook.IsTrueFreeFly = true;
        positionHook.OverridePosition = freeCam.Position;
        positionHook.OverrideUpVector = null;
        ApplyFov(camera, configuration.FreeFlyFovDegrees);

        if (IsRecording)
            SampleRecording(freeCam.Position, hRotation, vRotation, deltaSeconds);

        ReadWorldCameraState(camera);
        Status = IsRecording ? $"Free flying - RECORDING {recordingElapsed:0.0}s" : "Free flying";
        return PoseResult.Engaged;
    }

    private PoseResult WriteOrbitPose(GameCamera* camera, float deltaSeconds, float safeMinVRotation, float safeMaxVRotation)
    {
        freeCam.ClearStartingPosition();
        lastFreeFlyTargetEntityId = null;

        // A pan from the previous mode must not keep running in the new one.
        if (lastFollowMode != configuration.FollowMode)
        {
            Verbose($"[CamCam] Orbit mode changed {lastFollowMode} -> {configuration.FollowMode}.");
            SetActivePanView(null);
            lastFollowMode = configuration.FollowMode;
        }

        // A manually Loaded panning view keeps animating here when saved
        // views aren't otherwise driving (Cycle without saved views).
        if (!SavedViewsAreDriving && !CyclePaused && manualSliderCooldownTimer <= 0f && activePanView != null)
        {
            activePanElapsedSeconds += deltaSeconds;
            AdvancePanAxes(activePanView, deltaSeconds);
            CheckPanAdvanceComplete();
        }

        UpdateIdleTimer(deltaSeconds);
        if (configuration.IdleThresholdSeconds > 0f && idleTimer < configuration.IdleThresholdSeconds)
        {
            targetHook.Release();
            positionHook.Release();
            hasSmoothedPosition = false;
            Status = $"Waiting to go idle ({idleTimer:0}/{configuration.IdleThresholdSeconds:0}s)";
            return PoseResult.NotEngaged;
        }

        IGameObject? chosenTarget = SelectChosenTarget(deltaSeconds);
        if (chosenTarget == null)
        {
            targetHook.Release();
            positionHook.Release();
            Status = configuration.FollowMode == CameraFollowMode.None
                ? "Nobody to orbit"
                : "Follow mode on, but nobody to look at";
            return PoseResult.NotEngaged;
        }

        if (configuration.ExperimentalBypassTargetHook)
        {
            targetHook.Release();
        }
        else
        {
            targetHook.Install(camera);
            targetHook.OverrideTargetAddress = chosenTarget.Address;
        }

        positionHook.Install(camera);
        if (!positionHook.IsInstalled)
        {
            Status = $"Position hook failed: {positionHook.Status}";
            return PoseResult.NotEngaged;
        }
        positionHook.IsTrueFreeFly = false;

        HandleHeightKeys(deltaSeconds);

        // Tripod / strafe: decided up front since rotation, zoom and
        // position all depend on whether a frozen snapshot is in use.
        SavedView? activeView = ActiveView;

        if (activeView != null && activeView.HasPath)
        {
            var (pathPos, pathH, pathV, pathFov) = AdvancePath(activeView, chosenTarget, deltaSeconds);
            return FinishOrbitPose(camera, chosenTarget, activeView, pathPos, pathH,
                Math.Clamp(pathV, safeMinVRotation, safeMaxVRotation), pathFov, deltaSeconds);
        }

        bool useFixedCamera = activeView != null && (activeView.FixedCameraPassBy || activeView.TranslateInsteadOfPan);
        bool useTranslateMode = useFixedCamera && activeView!.TranslateInsteadOfPan;
        bool hasSnapshot = useFixedCamera
            && hasFixedCameraSnapshot
            && ReferenceEquals(fixedCameraSnapshotView, activeView)
            && fixedCameraSnapshotTargetEntityId == chosenTarget.EntityId;

        if (!useFixedCamera) hasFixedCameraSnapshot = false;

        float vRotation = (useTranslateMode && hasSnapshot)
            ? fixedCameraSnapshotVRotation
            : Math.Clamp(configuration.VerticalRotation, safeMinVRotation, safeMaxVRotation);

        float zoom = camera->MinDistance < camera->MaxDistance
            ? Math.Clamp(configuration.Zoom, camera->MinDistance + 0.001f, camera->MaxDistance)
            : camera->MaxDistance;

        // H is relative to the subject's own facing, so a "front" preset
        // frames anyone's face regardless of which way they're turned.
        float hRotation = (useTranslateMode && hasSnapshot)
            ? fixedCameraSnapshotHRotation
            : configuration.HorizontalRotation + (hasSnapshot ? fixedCameraSnapshotTargetRotation : chosenTarget.Rotation);

        var lookAtPoint = chosenTarget.Position + new Vector3(0f, GetLookAtHeight(chosenTarget) + configuration.FollowHeightOffset, 0f);
        var orbitPos = lookAtPoint + zoom * OrbitDirection(hRotation, vRotation);

        if (configuration.VerboseLogging && chosenTarget is ICharacter)
        {
            equipmentIdsForLog = string.Join(" ", EquipmentSlotNames.Select((slot, i) => $"{slot}={GetEquippedModelId(chosenTarget, i)}"));
        }

        if (useFixedCamera)
        {
            if (hasSnapshot)
            {
                // Held fixed - a tripod doesn't follow anyone.
                orbitPos = fixedCameraSnapshotPosition;
                fixedCameraSnapshotElapsedSeconds += deltaSeconds;

                if (useTranslateMode)
                {
                    var startOffset = TranslateStart(activeView!);
                    var endOffset = TranslateEnd(activeView!);
                    float total = Vector3.Distance(startOffset, endOffset);

                    if (total > 0.001f && !CyclePaused
                        && fixedCameraSnapshotElapsedSeconds >= activeView!.TranslateStartDelaySeconds)
                    {
                        translatePan.Advance(0f, total, activeView.TranslateSpeed, deltaSeconds, activeView.PanReturnBeforeAdvance);
                    }
                    else if (total <= 0.001f)
                    {
                        translatePan.MarkComplete();
                    }

                    CheckTranslatePanAdvanceComplete(activeView!);

                    float distance = translatePan.Output(0f, total, activeView!.EaseInOut);
                    float progress = total > 0.001f ? distance / total : 0f;
                    orbitPos = fixedCameraSnapshotPosition + TranslateWorldOffset(Vector3.Lerp(startOffset, endOffset, progress));
                }
            }
            else if (manualSliderCooldownTimer <= 0f)
            {
                // First frame for this (view, target) pairing: placed with
                // the normal orbit math, then frozen. Skipped while an edit
                // is in progress so the snapshot never freezes stale values.
                fixedCameraSnapshotPosition = orbitPos;
                fixedCameraSnapshotTargetRotation = chosenTarget.Rotation;
                fixedCameraSnapshotHRotation = hRotation;
                fixedCameraSnapshotVRotation = vRotation;
                fixedCameraSnapshotView = activeView;
                fixedCameraSnapshotTargetEntityId = chosenTarget.EntityId;
                fixedCameraSnapshotElapsedSeconds = 0f;
                hasFixedCameraSnapshot = true;
                translatePan.Reset(0f, 1f);
                panAdvanceHasFired = false;

                // Apply the Start offset on this same frame to avoid a
                // one-frame pop at the raw anchor.
                if (useTranslateMode)
                    orbitPos = fixedCameraSnapshotPosition + TranslateWorldOffset(TranslateStart(activeView!));
            }
        }

        // Pull in if a wall sits between subject and camera. Not for a
        // tripod/strafe: its position is deliberately independent of the
        // moving subject.
        if (configuration.FollowAvoidWallsAndObjects && !useFixedCamera)
            orbitPos = AvoidWallsAndObjects(lookAtPoint, orbitPos, configuration.FollowWallAvoidanceBuffer);

        if (configuration.FollowHeightLockToGround)
        {
            // Real ground under the camera via raycast, starting just above
            // the higher of camera/subject so it doesn't hit a ceiling.
            float raycastStartY = MathF.Max(orbitPos.Y, chosenTarget.Position.Y) + 3f;
            float groundY = RaycastGroundHeight(orbitPos.X, orbitPos.Z, raycastStartY) ?? chosenTarget.Position.Y;
            orbitPos.Y = MathF.Max(orbitPos.Y, groundY + configuration.FollowHeightGroundClearance);
        }

        Vector3 finalPos = orbitPos;
        if (configuration.FollowPositionSmoothingSeconds > 0f && manualSliderCooldownTimer <= 0f)
        {
            // Cut (don't glide) whenever the subject or the preset changes.
            if (!hasSmoothedPosition
                || lastSmoothedTargetEntityId != chosenTarget.EntityId
                || !ReferenceEquals(lastSmoothedView, activeView))
            {
                smoothedPosition = orbitPos;
                hasSmoothedPosition = true;
                lastSmoothedTargetEntityId = chosenTarget.EntityId;
                lastSmoothedView = activeView;
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

        return FinishOrbitPose(camera, chosenTarget, activeView, finalPos, hRotation, vRotation, CurrentShotFov(activeView), deltaSeconds);
    }

    /// <summary>Shared tail of every orbit-style pose: shot transition, then the actual writes.</summary>
    private PoseResult FinishOrbitPose(GameCamera* camera, IGameObject chosenTarget, SavedView? activeView,
        Vector3 position, float hRotation, float vRotation, float fovDegrees, float deltaSeconds)
    {
        ApplyTransition(activeView, chosenTarget.EntityId, ref position, ref hRotation, ref vRotation, ref fovDegrees, deltaSeconds);

        if (configuration.VerboseLogging)
            LogShakeIfAny(camera, chosenTarget, position);

        camera->ZoomMode = (CameraZoomMode)1; // third person
        positionHook.OverridePosition = position;
        camera->DirH = hRotation;
        camera->DirV = vRotation;
        ApplyFov(camera, fovDegrees);
        positionHook.OverrideUpVector = RollUpVector(hRotation, vRotation, activeView?.RollDegrees ?? 0f);

        ReadWorldCameraState(camera);
        Status = configuration.FollowMode == CameraFollowMode.None ? "Orbiting you" : $"Orbiting {CurrentCycleName}";
        if (activeView != null && activeView.HasPath) Status += " (path)";
        return PoseResult.Engaged;
    }

    private void HandleHeightKeys(float deltaSeconds)
    {
        // Edits the active preset (or the live value when presets aren't
        // driving) - writing only the live value would be overwritten by
        // the preset on the very next frame.
        float delta = 0f;
        if (FreeCamController.IsHeld(configuration.FlyUpKey)) delta += configuration.FollowHeightAdjustSpeed * deltaSeconds;
        if (FreeCamController.IsHeld(configuration.FlyDownKey)) delta -= configuration.FollowHeightAdjustSpeed * deltaSeconds;

        if (delta != 0f)
        {
            configuration.FollowHeightOffset += delta;
            var view = ActiveView;
            if (view != null) view.HeightOffset = configuration.FollowHeightOffset;
            heightKeyWasHeld = true;
        }
        else if (heightKeyWasHeld)
        {
            heightKeyWasHeld = false;
            configuration.Save();
        }
    }

    private void LogShakeIfAny(GameCamera* camera, IGameObject chosenTarget, Vector3 finalPos)
    {
        if (hasPreviousFrameWrite && previousFrameTargetEntityId == chosenTarget.EntityId)
        {
            var actual = (Vector3)camera->CameraBase.SceneCamera.Position;
            float drift = Vector3.Distance(actual, previousFrameFinalPos);
            float jump = Vector3.Distance(finalPos, previousFrameFinalPos);
            if (drift > 0.05f || jump > 1f)
            {
                log.Information(
                    $"[CamCam] shake: driftFromLastWrite={drift:0.000} thisFrameJump={jump:0.000} " +
                    $"lastWritten=({previousFrameFinalPos.X:0.00},{previousFrameFinalPos.Y:0.00},{previousFrameFinalPos.Z:0.00}) " +
                    $"actual=({actual.X:0.00},{actual.Y:0.00},{actual.Z:0.00}) target={chosenTarget.Name.TextValue}");
            }
        }
        previousFrameFinalPos = finalPos;
        previousFrameTargetEntityId = chosenTarget.EntityId;
        hasPreviousFrameWrite = true;
    }

    private void ReadWorldCameraState(GameCamera* camera)
    {
        GameFovDegrees = (originalFov ?? camera->FoV) * (180f / MathF.PI);
        CurrentCameraZoom = camera->Distance;
        CurrentCameraMinZoom = camera->MinDistance;
        CurrentCameraMaxZoom = camera->MaxDistance;
    }

    // ------------------------------------------------------------------
    // Target + preset selection
    // ------------------------------------------------------------------

    private IGameObject? SelectChosenTarget(float deltaSeconds)
    {
        IGameObject? chosenTarget = null;

        if (configuration.FollowMode == CameraFollowMode.None)
        {
            chosenTarget = objectTable.LocalPlayer;
            CurrentCycleName = "(you)";
            ApplyCurrentSavedView(deltaSeconds, chosenTarget);
            return chosenTarget;
        }

        if (configuration.FollowMode == CameraFollowMode.CurrentTarget)
        {
            chosenTarget = targetManager.Target;
            CurrentCycleName = chosenTarget != null ? chosenTarget.Name.TextValue : "(nothing /targeted)";
            ApplyCurrentSavedView(deltaSeconds, chosenTarget);
            return chosenTarget;
        }

        var players = GetCycleCandidates();
        if (players.Count == 0)
        {
            CurrentCycleName = "(no players nearby)";
            return null;
        }

        int playerIdx = ResolvePlayerIndex(players);

        // Don't switch players while the active preset's motion is set to
        // drive the advance itself.
        bool holdForPan = configuration.CycleUseSavedViews && IsCurrentPanStillInProgress();
        if (configuration.CycleAutoAdvance && !CyclePaused && !holdForPan)
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

        if (configuration.CycleUseSavedViews && configuration.SavedViews.Count > 0)
        {
            ApplyCurrentSavedView(deltaSeconds, chosenTarget);
            var view = configuration.SavedViews[cycleViewIndex];
            CurrentCycleName = $"{chosenTarget.Name.TextValue} - {view.Name} ({playerIdx + 1}/{players.Count}, {cycleViewIndex + 1}/{configuration.SavedViews.Count})";
        }
        else
        {
            CurrentCycleName = $"{chosenTarget.Name.TextValue} ({playerIdx + 1}/{players.Count})";
        }

        return chosenTarget;
    }

    private int ResolvePlayerIndex(List<IPlayerCharacter> players)
    {
        if (cycleSelectedPlayerEntityId.HasValue)
        {
            int idx = players.FindIndex(p => p.EntityId == cycleSelectedPlayerEntityId.Value);
            if (idx >= 0) return idx;
            Verbose($"[CamCam] cycle target {cycleSelectedPlayerEntityId.Value} no longer a candidate - falling back to the first of {players.Count}.");
        }
        return 0;
    }

    /// <summary>
    /// Applies whichever preset cycleViewIndex points at (angles, zoom,
    /// height, limits, pan) to live config, advancing it on the preset
    /// timer. Runs in every mode where Saved Views are driving.
    /// </summary>
    private void ApplyCurrentSavedView(float deltaSeconds, IGameObject? chosenTarget)
    {
        if (configuration.SavedViews.Count == 0) return;

        var sittingState = GetSittingState(chosenTarget, out _, out _);

        cycleViewIndex = Math.Clamp(cycleViewIndex, 0, configuration.SavedViews.Count - 1);
        cycleViewIndex = FindEligibleViewIndex(cycleViewIndex, sittingState);

        if (configuration.PresetCycleEnabled && !CyclePaused && !IsCurrentPanStillInProgress())
        {
            presetCycleTimer += deltaSeconds;
            if (presetCycleTimer >= configuration.PresetCycleIntervalSeconds)
            {
                presetCycleTimer = 0f;
                if (configuration.PresetCycleRandom)
                {
                    int next = NextRandomViewIndex(cycleViewIndex, previousRandomViewIndex, sittingState);
                    previousRandomViewIndex = cycleViewIndex;
                    cycleViewIndex = next;
                }
                else
                {
                    cycleViewIndex = FindEligibleViewIndex((cycleViewIndex + 1) % configuration.SavedViews.Count, sittingState);
                }
            }
        }

        if (manualSliderCooldownTimer > 0f || CyclePaused) return;

        var view = configuration.SavedViews[cycleViewIndex];
        SetActivePanView(view);
        if (activePanView != null)
        {
            activePanElapsedSeconds += deltaSeconds;
            AdvancePanAxes(view, deltaSeconds);
        }

        ApplyViewToConfig(view, includeAnimatedAxes: false);
        CheckPanAdvanceComplete();
    }

    /// <summary>Copies a preset into the live settings. Axes with a pan enabled are left to the pan when includeAnimatedAxes is false.</summary>
    private void ApplyViewToConfig(SavedView view, bool includeAnimatedAxes = true)
    {
        if (includeAnimatedAxes || !view.PanEnabled) configuration.HorizontalRotation = view.HorizontalRotation;
        if (includeAnimatedAxes || !view.VerticalPanEnabled) configuration.VerticalRotation = view.VerticalRotation;
        if (includeAnimatedAxes || !view.ZoomPanEnabled) configuration.Zoom = view.Zoom;

        configuration.FollowHeightOffset = view.HeightOffset;
        configuration.FollowMinZoom = view.MinZoom;
        configuration.FollowMaxAngleDegrees = view.MaxAngleDegrees;
        configuration.FollowHeightLockToGround = view.HeightLockToGround;
        configuration.FollowHeightGroundClearance = view.HeightGroundClearance;
        configuration.FollowPositionSmoothingSeconds = view.PositionSmoothingSeconds;
        configuration.FollowAvoidWallsAndObjects = view.AvoidWallsAndObjects;
        configuration.FollowWallAvoidanceBuffer = view.WallAvoidanceBuffer;
    }

    private int FindEligibleViewIndex(int startIndex, CharacterSittingState sittingState)
    {
        int count = configuration.SavedViews.Count;
        if (count == 0) return 0;

        for (int i = 0; i < count; i++)
        {
            int idx = (startIndex + i) % count;
            if (IsViewEligible(configuration.SavedViews[idx], sittingState)) return idx;
        }
        return startIndex;
    }

    private static bool IsViewEligible(SavedView view, CharacterSittingState sittingState)
        => !view.RequireTargetSitting || SatisfiesSittingRequirement(sittingState, view.RequiredSittingType);

    /// <summary>Random eligible preset, never the current one and, when possible, not the one before it either.</summary>
    private int NextRandomViewIndex(int current, int? previous, CharacterSittingState sittingState)
    {
        int count = configuration.SavedViews.Count;
        var strict = new List<int>();
        var relaxed = new List<int>();
        for (int i = 0; i < count; i++)
        {
            if (i == current || !IsViewEligible(configuration.SavedViews[i], sittingState)) continue;
            relaxed.Add(i);
            if (previous != i) strict.Add(i);
        }

        var pool = strict.Count > 0 ? strict : relaxed;
        return pool.Count > 0 ? pool[random.Next(pool.Count)] : current;
    }

    // ------------------------------------------------------------------
    // Pan / motion
    // ------------------------------------------------------------------

    /// <summary>
    /// Declares which preset's motion is running. Resets progress only
    /// when the view actually changes; same view again is a no-op.
    /// </summary>
    private void SetActivePanView(SavedView? view)
    {
        bool anyPan = view != null && (view.PanEnabled || view.VerticalPanEnabled || view.ZoomPanEnabled || view.FovPanEnabled);
        if (!anyPan)
        {
            activePanView = null;
            return;
        }

        if (ReferenceEquals(activePanView, view)) return;

        activePanView = view;
        panAdvanceHasFired = false;
        activePanElapsedSeconds = 0f;

        if (view!.PanEnabled) hPan.Reset(HStartDegrees(view), HEndDegrees(view));
        else hPan.MarkComplete();

        if (view.VerticalPanEnabled) vPan.Reset(VStartDegrees(view), view.VerticalPanToDegrees);
        else vPan.MarkComplete();

        if (view.ZoomPanEnabled) zoomPan.Reset(view.Zoom, view.ZoomPanToValue);
        else zoomPan.MarkComplete();

        if (view.FovPanEnabled) fovPan.Reset(FovStartDegrees(view), view.FovPanToDegrees);
        else fovPan.MarkComplete();
    }

    private static float HStartDegrees(SavedView view) => view.HorizontalRotation * (180f / MathF.PI);

    /// <summary>Unwrapped end angle - start plus the shortest signed delta, so a sweep crosses +-180 the short way.</summary>
    private static float HEndDegrees(SavedView view)
    {
        float start = HStartDegrees(view);
        return start + ShortestAngleDeltaDegrees(start, view.PanToDegrees);
    }

    private static float VStartDegrees(SavedView view) => view.VerticalRotation * (180f / MathF.PI);

    private void AdvancePanAxes(SavedView view, float deltaSeconds)
    {
        bool roundTrip = view.PanReturnBeforeAdvance;

        if (view.PanEnabled)
        {
            float start = HStartDegrees(view), end = HEndDegrees(view);
            if (activePanElapsedSeconds >= view.HorizontalPanStartDelaySeconds)
                hPan.Advance(start, end, view.PanSpeedDegreesPerSecond, deltaSeconds, roundTrip);
            configuration.HorizontalRotation = hPan.Output(start, end, view.EaseInOut) * (MathF.PI / 180f);
        }

        if (view.VerticalPanEnabled)
        {
            float start = VStartDegrees(view), end = view.VerticalPanToDegrees;
            if (activePanElapsedSeconds >= view.VerticalPanStartDelaySeconds)
                vPan.Advance(start, end, view.VerticalPanSpeedDegreesPerSecond, deltaSeconds, roundTrip);
            configuration.VerticalRotation = vPan.Output(start, end, view.EaseInOut) * (MathF.PI / 180f);
        }

        if (view.ZoomPanEnabled)
        {
            if (activePanElapsedSeconds >= view.ZoomPanStartDelaySeconds)
                zoomPan.Advance(view.Zoom, view.ZoomPanToValue, view.ZoomPanSpeed, deltaSeconds, roundTrip);
            configuration.Zoom = zoomPan.Output(view.Zoom, view.ZoomPanToValue, view.EaseInOut);
        }

        if (view.FovPanEnabled && activePanElapsedSeconds >= view.FovPanStartDelaySeconds)
            fovPan.Advance(FovStartDegrees(view), view.FovPanToDegrees, view.FovPanSpeedDegreesPerSecond, deltaSeconds, roundTrip);
    }

    private float FovStartDegrees(SavedView view) => view.FieldOfViewDegrees > 0f ? view.FieldOfViewDegrees : GameFovDegrees;

    /// <summary>The FOV (degrees, 0 = game default) the shot wants this frame.</summary>
    private float CurrentShotFov(SavedView? view)
    {
        if (view == null) return 0f;
        if (view.FovPanEnabled && ReferenceEquals(activePanView, view))
            return fovPan.Output(FovStartDegrees(view), view.FovPanToDegrees, view.EaseInOut);
        return view.FieldOfViewDegrees;
    }

    /// <summary>True while the active preset is set to advance on completion and any of its motions (H, V, zoom, strafe) is still running.</summary>
    private bool IsCurrentPanStillInProgress()
    {
        var view = ActiveView;
        if (view == null || !view.PanAdvanceCycleOnComplete) return false;

        bool panRunning = ReferenceEquals(activePanView, view)
            && ((view.PanEnabled && !hPan.Complete)
                || (view.VerticalPanEnabled && !vPan.Complete)
                || (view.ZoomPanEnabled && !zoomPan.Complete)
                || (view.FovPanEnabled && !fovPan.Complete));

        bool pathRunning = view.HasPath && !view.PathLoop && !configuration.FreeFly
            && (!ReferenceEquals(pathView, view) || !pathComplete);
        if (pathRunning) return true;

        // Before the strafe snapshot exists the move hasn't even started.
        bool translateRunning = view.TranslateInsteadOfPan && !configuration.FreeFly
            && (!hasFixedCameraSnapshot || !ReferenceEquals(fixedCameraSnapshotView, view) || !translatePan.Complete);

        return panRunning || translateRunning;
    }

    private void CheckPanAdvanceComplete()
    {
        var view = activePanView;
        if (view == null || panAdvanceHasFired || !view.PanAdvanceCycleOnComplete) return;
        if (view.TranslateInsteadOfPan || view.HasPath) return; // strafe presets advance when the strafe finishes instead

        bool done = (!view.PanEnabled || hPan.Complete)
            && (!view.VerticalPanEnabled || vPan.Complete)
            && (!view.ZoomPanEnabled || zoomPan.Complete)
            && (!view.FovPanEnabled || fovPan.Complete);
        if (!done) return;

        panAdvanceHasFired = true;
        AdvanceAfterMotion();
    }

    private void CheckTranslatePanAdvanceComplete(SavedView view)
    {
        if (panAdvanceHasFired || !translatePan.Complete || !view.PanAdvanceCycleOnComplete) return;

        // If the preset also pans, wait for those axes too.
        if (ReferenceEquals(activePanView, view)
            && ((view.PanEnabled && !hPan.Complete) || (view.VerticalPanEnabled && !vPan.Complete)))
            return;

        panAdvanceHasFired = true;
        AdvanceAfterMotion();
    }

    /// <summary>"Advance after pan completes": Cycle-with-presets moves player and preset together, every other mode moves to the next preset.</summary>
    private void AdvanceAfterMotion()
    {
        if (configuration.FollowMode == CameraFollowMode.Cycle && configuration.CycleUseSavedViews)
        {
            CycleNext();
        }
        else if (SavedViewsAreDriving)
        {
            cycleViewIndex = (cycleViewIndex + 1) % configuration.SavedViews.Count;
            presetCycleTimer = 0f;
        }
    }

    private static Vector3 TranslateStart(SavedView v) => new(v.TranslateStartRight, v.TranslateStartUp, v.TranslateStartForward);

    private static Vector3 TranslateEnd(SavedView v) => new(v.TranslateEndRight, v.TranslateEndUp, v.TranslateEndForward);

    /// <summary>Right/forward relative to the frozen camera facing (yaw-only right), up always world-vertical.</summary>
    private Vector3 TranslateWorldOffset(Vector3 offset)
        => TranslateWorldOffset(offset, fixedCameraSnapshotHRotation, fixedCameraSnapshotVRotation);

    private static Vector3 TranslateWorldOffset(Vector3 offset, float hRotation, float vRotation)
    {
        var right = new Vector3(MathF.Cos(hRotation), 0f, -MathF.Sin(hRotation));
        var forward = -OrbitDirection(hRotation, vRotation);
        return right * offset.X + Vector3.UnitY * offset.Y + forward * offset.Z;
    }

    /// <summary>Unit vector from the look-at point out to the camera for the given orbit angles.</summary>
    private static Vector3 OrbitDirection(float hRotation, float vRotation)
    {
        float cosV = MathF.Cos(vRotation);
        return new Vector3(cosV * MathF.Sin(hRotation), MathF.Sin(vRotation), cosV * MathF.Cos(hRotation));
    }

    private static float ShortestAngleDeltaDegrees(float from, float to)
    {
        float delta = (to - from) % 360f;
        if (delta > 180f) delta -= 360f;
        else if (delta < -180f) delta += 360f;
        return delta;
    }

    // ------------------------------------------------------------------
    // FOV and roll
    // ------------------------------------------------------------------

    /// <summary>Sets the camera FOV in degrees (0 = put the game's own back), widening the game's FOV limits if needed.</summary>
    private void ApplyFov(GameCamera* camera, float degrees)
    {
        if (degrees <= 0f)
        {
            RestoreFov(writeBack: true);
            return;
        }

        originalFov ??= camera->FoV;
        originalMinFov ??= camera->MinFoV;
        originalMaxFov ??= camera->MaxFoV;

        float radians = Math.Clamp(degrees, 5f, 120f) * (MathF.PI / 180f);
        camera->MinFoV = MathF.Min(originalMinFov.Value, radians);
        camera->MaxFoV = MathF.Max(originalMaxFov.Value, radians);
        camera->FoV = radians;
    }

    private void RestoreFov(bool writeBack)
    {
        if (!originalFov.HasValue && !originalMinFov.HasValue && !originalMaxFov.HasValue) return;

        if (writeBack)
        {
            var camera = GetWorldCamera();
            if (camera != null)
            {
                if (originalMinFov.HasValue) camera->MinFoV = originalMinFov.Value;
                if (originalMaxFov.HasValue) camera->MaxFoV = originalMaxFov.Value;
                if (originalFov.HasValue) camera->FoV = originalFov.Value;
            }
        }

        originalFov = null;
        originalMinFov = null;
        originalMaxFov = null;
    }

    /// <summary>Up vector rolled around the view direction, or null for no roll.</summary>
    private static Vector3? RollUpVector(float hRotation, float vRotation, float rollDegrees)
    {
        if (MathF.Abs(rollDegrees) < 0.01f) return null;
        var forward = -OrbitDirection(hRotation, vRotation);
        var right = Vector3.Normalize(Vector3.Cross(forward, Vector3.UnitY));
        var up = Vector3.Cross(right, forward);
        var roll = Quaternion.CreateFromAxisAngle(forward, rollDegrees * (MathF.PI / 180f));
        return Vector3.Normalize(Vector3.Transform(up, roll));
    }

    // ------------------------------------------------------------------
    // Shot transitions
    // ------------------------------------------------------------------

    /// <summary>
    /// When the shot changes on the same subject and the new shot has a
    /// TransitionSeconds, eases from the last written pose to the new one
    /// instead of cutting. Records this frame's output as the next "last
    /// pose" either way.
    /// </summary>
    private void ApplyTransition(SavedView? view, uint targetId, ref Vector3 position, ref float h, ref float v, ref float fov, float deltaSeconds)
    {
        float fovResolved = fov > 0f ? fov : GameFovDegrees;
        bool sameSubject = hasLastPose && lastPoseTargetEntityId == targetId;

        if (!sameSubject)
        {
            transitioning = false;
        }
        else if (!ReferenceEquals(lastPoseView, view) && view != null && view.TransitionSeconds > 0f && manualSliderCooldownTimer <= 0f)
        {
            transitioning = true;
            transitionElapsed = 0f;
            transitionDuration = view.TransitionSeconds;
            transitionFromPosition = lastPosePosition;
            transitionFromH = lastPoseH;
            transitionFromV = lastPoseV;
            transitionFromFov = lastPoseFov;
        }

        if (transitioning)
        {
            transitionElapsed += deltaSeconds;
            float t = Math.Clamp(transitionElapsed / MathF.Max(transitionDuration, 0.001f), 0f, 1f);
            float e = t * t * (3f - 2f * t);
            position = Vector3.Lerp(transitionFromPosition, position, e);
            h = transitionFromH + ShortestAngleDeltaRadians(transitionFromH, h) * e;
            v = transitionFromV + (v - transitionFromV) * e;
            fovResolved = transitionFromFov + (fovResolved - transitionFromFov) * e;
            fov = fovResolved;
            if (t >= 1f) transitioning = false;
        }

        hasLastPose = true;
        lastPoseView = view;
        lastPoseTargetEntityId = targetId;
        lastPosePosition = position;
        lastPoseH = h;
        lastPoseV = v;
        lastPoseFov = fovResolved;
    }

    private static float ShortestAngleDeltaRadians(float from, float to)
    {
        float delta = (to - from) % (2f * MathF.PI);
        if (delta > MathF.PI) delta -= 2f * MathF.PI;
        else if (delta < -MathF.PI) delta += 2f * MathF.PI;
        return delta;
    }

    // ------------------------------------------------------------------
    // Path playback
    // ------------------------------------------------------------------

    /// <summary>Rotates a vector about world Y by the same convention as orbit H: RotateY(dir(h), a) == dir(h + a).</summary>
    private static Vector3 RotateY(Vector3 v, float angle)
    {
        float c = MathF.Cos(angle), s = MathF.Sin(angle);
        return new Vector3(v.X * c + v.Z * s, v.Y, v.Z * c - v.X * s);
    }

    private (Vector3 Anchor, float Rotation) PathAnchorFor(SavedView view, IGameObject subject)
        => view.PathRelativeToSubject
            ? (subject.Position, subject.Rotation)
            : (new Vector3(view.PathAnchorX, view.PathAnchorY, view.PathAnchorZ), view.PathAnchorRotation);

    private (Vector3 Position, float H, float V, float Fov) AdvancePath(SavedView view, IGameObject subject, float deltaSeconds)
    {
        if (!ReferenceEquals(pathView, view) || pathTargetEntityId != subject.EntityId)
        {
            // New shot or new subject: start over, anchored where the
            // subject is right now (the path doesn't chase a walking subject).
            pathView = view;
            pathTargetEntityId = subject.EntityId;
            pathElapsed = 0f;
            pathComplete = false;
            panAdvanceHasFired = false;
            (pathAnchor, pathAnchorRotation) = PathAnchorFor(view, subject);
        }

        float duration = MathF.Max(view.Path[^1].Time, 0.001f);
        if (!CyclePaused && manualSliderCooldownTimer <= 0f && !pathComplete)
            pathElapsed += deltaSeconds * MathF.Max(view.PathPlaybackSpeed, 0.05f);

        float t;
        if (view.PathLoop)
        {
            t = pathElapsed % duration;
        }
        else
        {
            if (pathElapsed >= duration)
            {
                pathElapsed = duration;
                pathComplete = true;
            }
            t = pathElapsed;
            if (view.EaseInOut)
            {
                float u = t / duration;
                t = duration * (u * u * (3f - 2f * u));
            }
        }

        if (pathComplete && view.PanAdvanceCycleOnComplete && !panAdvanceHasFired)
        {
            panAdvanceHasFired = true;
            AdvanceAfterMotion();
        }

        var k = SamplePath(view.Path, t);
        var position = pathAnchor + RotateY(new Vector3(k.X, k.Y, k.Z), pathAnchorRotation);
        return (position, k.H + pathAnchorRotation, k.V, k.FovDegrees);
    }

    /// <summary>Catmull-Rom through the keyframes (position and angles), linear FOV.</summary>
    private static PathKeyframe SamplePath(List<PathKeyframe> keys, float time)
    {
        if (time <= keys[0].Time) return keys[0];
        if (time >= keys[^1].Time) return keys[^1];

        int lo = 0, hi = keys.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (keys[mid].Time <= time) lo = mid;
            else hi = mid;
        }

        var k0 = keys[Math.Max(lo - 1, 0)];
        var k1 = keys[lo];
        var k2 = keys[hi];
        var k3 = keys[Math.Min(hi + 1, keys.Count - 1)];
        float span = MathF.Max(k2.Time - k1.Time, 1e-5f);
        float u = (time - k1.Time) / span;

        return new PathKeyframe
        {
            Time = time,
            X = CatmullRom(k0.X, k1.X, k2.X, k3.X, u),
            Y = CatmullRom(k0.Y, k1.Y, k2.Y, k3.Y, u),
            Z = CatmullRom(k0.Z, k1.Z, k2.Z, k3.Z, u),
            H = CatmullRom(k0.H, k1.H, k2.H, k3.H, u),
            V = CatmullRom(k0.V, k1.V, k2.V, k3.V, u),
            FovDegrees = k1.FovDegrees + (k2.FovDegrees - k1.FovDegrees) * u,
        };
    }

    private static float CatmullRom(float p0, float p1, float p2, float p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    // ------------------------------------------------------------------
    // Path recording
    // ------------------------------------------------------------------

    public void ToggleRecording()
    {
        if (IsRecording) StopRecording(save: true);
        else StartRecording();
    }

    /// <summary>Starts recording Free Fly. Anchored to the current subject (or you) so the path can replay around anyone.</summary>
    public void StartRecording()
    {
        if (IsRecording) return;
        if (!configuration.Enabled || !configuration.FreeFly)
        {
            Status = "Recording needs Free Fly on";
            log.Information("[CamCam] Recording ignored - turn on Free Fly first.");
            return;
        }

        var subject = ResolveSubject() ?? objectTable.LocalPlayer;
        if (subject == null) return;

        recording = new List<PathKeyframe>();
        recordingElapsed = 0f;
        recordingSampleTimer = float.MaxValue; // sample on the first frame
        recordingAnchor = subject.Position;
        recordingAnchorRotation = subject.Rotation;
        recordingLastH = float.NaN;
        log.Information($"[CamCam] Recording path around {subject.Name.TextValue}.");
    }

    private void SampleRecording(Vector3 position, float h, float v, float deltaSeconds)
    {
        if (recording == null) return;
        if (recording.Count > 0) recordingElapsed += deltaSeconds;
        recordingSampleTimer += deltaSeconds;
        if (recordingSampleTimer < 1f / MathF.Max(configuration.RecordSamplesPerSecond, 1f)) return;
        recordingSampleTimer = 0f;
        recording.Add(MakeKeyframe(position, h, v));
    }

    private PathKeyframe MakeKeyframe(Vector3 position, float h, float v)
    {
        // Unwrap H so interpolation never spins the long way round at +-180.
        float relH = h - recordingAnchorRotation;
        if (!float.IsNaN(recordingLastH))
            relH = recordingLastH + ShortestAngleDeltaRadians(recordingLastH, relH);
        recordingLastH = relH;

        var local = RotateY(position - recordingAnchor, -recordingAnchorRotation);
        return new PathKeyframe
        {
            Time = recordingElapsed,
            X = local.X,
            Y = local.Y,
            Z = local.Z,
            H = relH,
            V = v,
            FovDegrees = configuration.FreeFlyFovDegrees,
        };
    }

    /// <summary>Stops recording; with save, adds the path as a new shot (needs at least half a second of movement).</summary>
    public void StopRecording(bool save)
    {
        var keys = recording;
        recording = null;
        if (keys == null || !save) return;

        if (keys.Count >= 1)
            keys.Add(MakeKeyframe(freeCam.Position, configuration.HorizontalRotation, configuration.VerticalRotation));

        if (keys.Count < 2 || keys[^1].Time < 0.5f)
        {
            log.Information("[CamCam] Recording too short - discarded.");
            return;
        }

        var view = new SavedView
        {
            Name = $"Path {configuration.SavedViews.Count + 1}",
            Path = keys,
            PathAnchorX = recordingAnchor.X,
            PathAnchorY = recordingAnchor.Y,
            PathAnchorZ = recordingAnchor.Z,
            PathAnchorRotation = recordingAnchorRotation,
            HeightOffset = configuration.FollowHeightOffset,
            Zoom = configuration.Zoom,
            FieldOfViewDegrees = configuration.FreeFlyFovDegrees,
        };
        configuration.SavedViews.Add(view);
        cycleViewIndex = configuration.SavedViews.Count - 1;
        configuration.Save();
        log.Information($"[CamCam] Saved {keys.Count} keyframes ({keys[^1].Time:0.0}s) as \"{view.Name}\".");
    }

    // ------------------------------------------------------------------
    // Overlay preview
    // ------------------------------------------------------------------

    /// <summary>Who the camera is (or would be) filming right now, without side effects.</summary>
    public IGameObject? ResolveSubject() => configuration.FollowMode switch
    {
        CameraFollowMode.None => objectTable.LocalPlayer,
        CameraFollowMode.CurrentTarget => targetManager.Target,
        _ => (cycleSelectedPlayerEntityId.HasValue ? objectTable.SearchByEntityId(cycleSelectedPlayerEntityId.Value) : null)
             ?? objectTable.LocalPlayer,
    };

    public sealed class ShotPreview
    {
        public Vector3 LookAt;
        public Vector3 CameraStart;
        public List<Vector3> Motion = new();
        public bool IsPath;
    }

    /// <summary>Where the given shot puts the camera on the current subject, and the path its motion follows - for the in-world overlay.</summary>
    public ShotPreview? BuildPreview(SavedView view)
    {
        var subject = ResolveSubject();
        if (subject == null) return null;

        var preview = new ShotPreview
        {
            LookAt = subject.Position + new Vector3(0f, GetLookAtHeight(subject) + view.HeightOffset, 0f),
        };

        if (view.HasPath)
        {
            var (anchor, rotation) = ReferenceEquals(pathView, view) && IsEngaged
                ? (pathAnchor, pathAnchorRotation)
                : PathAnchorFor(view, subject);
            foreach (var k in view.Path)
                preview.Motion.Add(anchor + RotateY(new Vector3(k.X, k.Y, k.Z), rotation));
            preview.CameraStart = preview.Motion[0];
            preview.IsPath = true;
            return preview;
        }

        bool snapshotLive = hasFixedCameraSnapshot && ReferenceEquals(fixedCameraSnapshotView, view);
        float baseRotation = snapshotLive ? fixedCameraSnapshotTargetRotation : subject.Rotation;
        float h0 = view.HorizontalRotation + baseRotation;
        preview.CameraStart = snapshotLive ? fixedCameraSnapshotPosition : preview.LookAt + view.Zoom * OrbitDirection(h0, view.VerticalRotation);

        if (view.TranslateInsteadOfPan)
        {
            float h = snapshotLive ? fixedCameraSnapshotHRotation : h0;
            float v = snapshotLive ? fixedCameraSnapshotVRotation : view.VerticalRotation;
            preview.Motion.Add(preview.CameraStart + TranslateWorldOffset(TranslateStart(view), h, v));
            preview.Motion.Add(preview.CameraStart + TranslateWorldOffset(TranslateEnd(view), h, v));
            return preview;
        }

        if (!view.FixedCameraPassBy && (view.PanEnabled || view.VerticalPanEnabled || view.ZoomPanEnabled))
        {
            float hStart = HStartDegrees(view), hEnd = view.PanEnabled ? HEndDegrees(view) : hStart;
            float vStart = VStartDegrees(view), vEnd = view.VerticalPanEnabled ? view.VerticalPanToDegrees : vStart;
            float zEnd = view.ZoomPanEnabled ? view.ZoomPanToValue : view.Zoom;
            const int steps = 32;
            for (int i = 0; i <= steps; i++)
            {
                float t = i / (float)steps;
                float h = (hStart + (hEnd - hStart) * t) * (MathF.PI / 180f) + baseRotation;
                float v = (vStart + (vEnd - vStart) * t) * (MathF.PI / 180f);
                float z = view.Zoom + (zEnd - view.Zoom) * t;
                preview.Motion.Add(preview.LookAt + z * OrbitDirection(h, v));
            }
        }

        return preview;
    }

    // ------------------------------------------------------------------
    // Cycle candidates
    // ------------------------------------------------------------------

    /// <summary>
    /// Nearby players Cycle mode may pick, after filters. Rebuilt a few
    /// times a second rather than every frame; stale entries are dropped
    /// on every read. Sitting players stay in the pool whenever a preset
    /// requires sitting, otherwise that preset could never be reached.
    /// </summary>
    private List<IPlayerCharacter> GetCycleCandidates(bool forceRefresh = false)
    {
        candidateRefreshTimer += (float)Plugin.Framework.UpdateDelta.TotalSeconds;
        if (forceRefresh || candidateRefreshTimer >= CandidateRefreshSeconds)
        {
            candidateRefreshTimer = 0f;
            cachedCandidates = BuildCycleCandidates();
        }
        else
        {
            cachedCandidates.RemoveAll(p => !p.IsValid());
        }
        return cachedCandidates;
    }

    private List<IPlayerCharacter> BuildCycleCandidates()
    {
        bool anySavedViewNeedsSitting = false;
        foreach (var v in configuration.SavedViews)
        {
            if (v.RequireTargetSitting) { anySavedViewNeedsSitting = true; break; }
        }

        var localPlayer = objectTable.LocalPlayer;
        var list = new List<IPlayerCharacter>();
        foreach (var obj in objectTable)
        {
            if (obj is not IPlayerCharacter pc) continue;

            bool isSelf = localPlayer != null && pc.EntityId == localPlayer.EntityId;
            if (isSelf && configuration.CycleExcludeSelf) continue;

            if (!isSelf && localPlayer != null)
            {
                if (configuration.CycleMaxDistance > 0f
                    && Vector3.Distance(localPlayer.Position, pc.Position) > configuration.CycleMaxDistance)
                    continue;

                if (configuration.CycleRequireLineOfSight && !HasLineOfSight(localPlayer, pc))
                    continue;
            }

            var customize = pc.Customize;
            if (configuration.CycleGenderFilterMode != CycleGenderFilter.Any && customize.Length > 1)
            {
                bool isFemale = customize[1] == 1;
                if (configuration.CycleGenderFilterMode == CycleGenderFilter.MaleOnly && isFemale) continue;
                if (configuration.CycleGenderFilterMode == CycleGenderFilter.FemaleOnly && !isFemale) continue;
            }

            const byte lalafellRaceId = 3;
            if (configuration.ExcludeLalafells && customize.Length > 0 && customize[0] == lalafellRaceId) continue;

            if (!anySavedViewNeedsSitting && configuration.ExcludeSitting
                && GetSittingState(pc, out _, out _) != CharacterSittingState.NotSitting)
                continue;

            // Disciples of the Hand: ClassJob rows 8-15, stable since 2.0.
            if (configuration.ExcludeCrafters && pc.ClassJob.RowId is >= 8 and <= 15) continue;

            if (configuration.ExcludedEquipmentItemIds.Count > 0)
            {
                bool wearingExcluded = false;
                for (int slot = 0; slot < EquipmentSlotNames.Length; slot++)
                {
                    if (configuration.ExcludedEquipmentItemIds.Contains(GetEquippedModelId(pc, slot)))
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

    private static bool HasLineOfSight(IGameObject from, IGameObject to)
    {
        var origin = from.Position + new Vector3(0f, 1.5f, 0f);
        var dest = to.Position + new Vector3(0f, 1.5f, 0f);
        var offset = dest - origin;
        float distance = offset.Length();
        if (distance < 0.5f) return true;
        return !(BGCollisionModule.RaycastMaterialFilter(origin, offset / distance, out var hit, distance)
                 && hit.Distance < distance - 0.5f);
    }

    // ------------------------------------------------------------------
    // Collision helpers
    // ------------------------------------------------------------------

    /// <summary>Ground height at X/Z via the game's own collision raycast, cast straight down from startY. Null if nothing was hit.</summary>
    private static float? RaycastGroundHeight(float x, float z, float startY)
    {
        var origin = new Vector3(x, startY, z);
        if (BGCollisionModule.RaycastMaterialFilter(origin, new Vector3(0f, -1f, 0f), out var hit, 50f))
            return hit.Point.Y;
        return null;
    }

    /// <summary>Standard third-person collision: if geometry blocks look-at -> camera, stop just short of it.</summary>
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

    // ------------------------------------------------------------------
    // Raw character reads
    // ------------------------------------------------------------------

    private static readonly string[] EquipmentSlotNames =
        { "Head", "Body", "Hands", "Legs", "Feet", "Ears", "Neck", "Wrists", "RFinger", "LFinger" };

    /// <summary>Look-at height above the subject's feet, scaled by their own GameObject.Height (valid for any object).</summary>
    private static float GetLookAtHeight(IGameObject? obj)
    {
        if (obj == null || obj.Address == nint.Zero) return 1.3f;
        float height = ((NativeGameObject*)obj.Address)->Height;
        return height > 0f ? height * HeightScaleFactor : 1.3f;
    }

    public enum CharacterSittingState
    {
        NotSitting,
        Ground,
        Furniture,
    }

    /// <summary>
    /// Character.Mode/ModeParam. Mode 11
    /// (InPositionLoop) = furniture, Mode 3 (EmoteLoop) = ground-sit -
    /// though 3 also covers other looping emotes like /doze. Only read for
    /// actual characters: these offsets are past the end of smaller object
    /// types (event objects, treasure...), which a /target can be.
    /// </summary>
    private static CharacterSittingState GetSittingState(IGameObject? obj, out byte mode, out byte modeParam)
    {
        mode = 0;
        modeParam = 0;
        if (obj is not ICharacter || obj.Address == nint.Zero) return CharacterSittingState.NotSitting;

        var character = (NativeCharacter*)obj.Address;
        mode = (byte)character->Mode;
        modeParam = character->ModeParam;
        if (mode == 11) return CharacterSittingState.Furniture;
        if (mode == 3) return CharacterSittingState.Ground;
        return CharacterSittingState.NotSitting;
    }

    private static bool SatisfiesSittingRequirement(CharacterSittingState state, SittingRequirement requirement) => requirement switch
    {
        SittingRequirement.Ground => state == CharacterSittingState.Ground,
        SittingRequirement.Furniture => state == CharacterSittingState.Furniture,
        _ => state != CharacterSittingState.NotSitting,
    };

    /// <summary>
    /// Equipped gear MODEL id for one slot (Character.DrawData's equipment
    /// model ids, Head..LFinger). This is the appearance model,
    /// not the item id - visually identical items share it. Characters only.
    /// </summary>
    private static ushort GetEquippedModelId(IGameObject obj, int slot)
    {
        if (obj is not ICharacter || obj.Address == nint.Zero || slot < 0 || slot >= EquipmentSlotNames.Length) return 0;
        return ((NativeCharacter*)obj.Address)->DrawData.EquipmentModelIds[slot].Id;
    }

    /// <summary>For the settings window's "exclude what my target is wearing" helper.</summary>
    public IReadOnlyList<(string Slot, ushort ModelId)> GetTargetEquipment()
    {
        var target = targetManager.Target;
        var result = new List<(string, ushort)>();
        if (target is not ICharacter) return result;
        for (int i = 0; i < EquipmentSlotNames.Length; i++)
            result.Add((EquipmentSlotNames[i], GetEquippedModelId(target, i)));
        return result;
    }
}
