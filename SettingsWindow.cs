using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Game.Config;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Bindings.ImGui;

namespace CamCam;

public class SettingsWindow : Window
{
    private readonly Configuration configuration;
    private readonly CameraController cameraController;
    private readonly IGameConfig gameConfig;

    // Holds the in-progress typed value for the "add excluded item ID"
    // input under Follow Mode Options - needs to persist across frames
    // while the person is typing, unlike everything else in this file
    // which reads straight from configuration each frame.
    private int pendingExcludedItemId;

    // Single-level undo for anything done to the Saved Views list - value
    // edits, name changes, toggles, Add, Remove, and Reload shipped
    // defaults all go through SnapshotForUndo() before they touch
    // configuration.SavedViews, so Undo always reverts whatever the most
    // recent one of those was, regardless of which kind it was.
    private List<SavedView>? undoSnapshot;
    // Which preset's full editing panel is shown below the numbered
    // button row - a UI-only selection, independent of whichever preset
    // is actually active in the camera right now (cameraController.
    // CurrentViewIndex). You can look at/edit preset 5 while preset 2 is
    // the one actually playing.
    private int selectedPresetIndex;
    private DateTime lastUndoSnapshotAt = DateTime.MinValue;

    // Instant, then every 15 seconds up to 15 minutes - computed once,
    // not per-frame, since the option list never changes.
    private static readonly float[] IdleThresholdOptions;
    private static readonly string[] IdleThresholdLabels;

    // Every 15 seconds up to 2 minutes, for the auto-advance interval.
    private static readonly float[] CycleIntervalOptions;
    private static readonly string[] CycleIntervalLabels;

    static SettingsWindow()
    {
        var idleOptions = new System.Collections.Generic.List<float> { 0f };
        for (int seconds = 15; seconds <= 900; seconds += 15)
            idleOptions.Add(seconds);
        IdleThresholdOptions = idleOptions.ToArray();

        IdleThresholdLabels = new string[IdleThresholdOptions.Length];
        for (int i = 0; i < IdleThresholdOptions.Length; i++)
            IdleThresholdLabels[i] = FormatDuration(IdleThresholdOptions[i]);

        var cycleOptions = new System.Collections.Generic.List<float>();
        for (int seconds = 1; seconds <= 14; seconds++)
            cycleOptions.Add(seconds);
        for (int seconds = 15; seconds <= 120; seconds += 15)
            cycleOptions.Add(seconds);
        CycleIntervalOptions = cycleOptions.ToArray();

        CycleIntervalLabels = new string[CycleIntervalOptions.Length];
        for (int i = 0; i < CycleIntervalOptions.Length; i++)
            CycleIntervalLabels[i] = FormatDuration(CycleIntervalOptions[i]);
    }

    private static readonly Vector4 DisabledTextColor = new(0.5f, 0.5f, 0.5f, 1f);

    /// <summary>Same gray look as ImGui.TextDisabled, but reflows to the window's current width like ImGui.TextWrapped instead of running off as one long unbroken line.</summary>
    private static void TextDisabledWrapped(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, DisabledTextColor);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static SavedView CloneSavedView(SavedView v) => new()
    {
        Name = v.Name,
        HorizontalRotation = v.HorizontalRotation,
        VerticalRotation = v.VerticalRotation,
        Zoom = v.Zoom,
        HeightOffset = v.HeightOffset,
        MinZoom = v.MinZoom,
        MaxAngleDegrees = v.MaxAngleDegrees,
        HeightLockToGround = v.HeightLockToGround,
        HeightGroundClearance = v.HeightGroundClearance,
        PanEnabled = v.PanEnabled,
        PanToDegrees = v.PanToDegrees,
        PanSpeedDegreesPerSecond = v.PanSpeedDegreesPerSecond,
        VerticalPanEnabled = v.VerticalPanEnabled,
        VerticalPanToDegrees = v.VerticalPanToDegrees,
        VerticalPanSpeedDegreesPerSecond = v.VerticalPanSpeedDegreesPerSecond,
        ZoomPanEnabled = v.ZoomPanEnabled,
        ZoomPanToValue = v.ZoomPanToValue,
        ZoomPanSpeed = v.ZoomPanSpeed,
        HorizontalPanStartDelaySeconds = v.HorizontalPanStartDelaySeconds,
        VerticalPanStartDelaySeconds = v.VerticalPanStartDelaySeconds,
        ZoomPanStartDelaySeconds = v.ZoomPanStartDelaySeconds,
        PanAdvanceCycleOnComplete = v.PanAdvanceCycleOnComplete,
        PanReturnBeforeAdvance = v.PanReturnBeforeAdvance,
        RequireTargetSitting = v.RequireTargetSitting,
        RequiredSittingType = v.RequiredSittingType,
        PositionSmoothingSeconds = v.PositionSmoothingSeconds,
        AvoidWallsAndObjects = v.AvoidWallsAndObjects,
        WallAvoidanceBuffer = v.WallAvoidanceBuffer,
        FixedCameraPassBy = v.FixedCameraPassBy,
        TranslateInsteadOfPan = v.TranslateInsteadOfPan,
        TranslateStartRight = v.TranslateStartRight,
        TranslateEndRight = v.TranslateEndRight,
        TranslateStartUp = v.TranslateStartUp,
        TranslateEndUp = v.TranslateEndUp,
        TranslateStartForward = v.TranslateStartForward,
        TranslateEndForward = v.TranslateEndForward,
        TranslateSpeed = v.TranslateSpeed,
        TranslateStartDelaySeconds = v.TranslateStartDelaySeconds,
    };

    /// <summary>
    /// Captures the Saved Views list as it stood BEFORE whatever change
    /// is about to happen, so Undo can get back to it. Debounced to once
    /// per ~600ms: a drag gesture fires a change every frame while it's
    /// held, and without debouncing, each of those frames would overwrite
    /// the snapshot with what was true a moment ago instead of what was
    /// true before the whole drag started - Undo would only ever revert
    /// the last frame of movement instead of the whole gesture. The
    /// tradeoff is that a single very slow drag spanning more than 600ms
    /// could split into two undo steps - acceptable for a one-level undo
    /// meant to catch a mistake, not a full history.
    /// </summary>
    private void SnapshotForUndo()
    {
        if ((DateTime.UtcNow - lastUndoSnapshotAt).TotalMilliseconds < 600) return;
        undoSnapshot = new List<SavedView>(configuration.SavedViews.Count);
        foreach (var v in configuration.SavedViews)
            undoSnapshot.Add(CloneSavedView(v));
        lastUndoSnapshotAt = DateTime.UtcNow;
    }

    private static string FormatDuration(float seconds)
    {
        if (seconds <= 0f) return "Instant";
        int total = (int)seconds;
        int minutes = total / 60;
        int secs = total % 60;
        if (minutes == 0) return $"{secs} sec";
        if (secs == 0) return $"{minutes} min";
        return $"{minutes} min {secs} sec";
    }

    public SettingsWindow(Configuration configuration, CameraController cameraController, IGameConfig gameConfig)
        : base("CamCam Settings###CamCamSettings")
    {
        this.configuration = configuration;
        this.cameraController = cameraController;
        this.gameConfig = gameConfig;

        Size = new Vector2(480, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        FreeCamController.ResetVirtualButtons();

        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 22f);
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(8f, 14f));
        ImGui.PushStyleVar(ImGuiStyleVar.FramePadding, new Vector2(6f, 5f));

        DrawTopControls();
        ImGui.Spacing();

        if (ImGui.CollapsingHeader("General"))
        {
            ImGui.Indent();
            DrawGeneralSection();
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Camera Angle and Zoom", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawRotationZoomSection();
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Saved Views", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawSavedViewsSection();
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Follow Mode Options", ImGuiTreeNodeFlags.DefaultOpen))
        {
            ImGui.Indent();
            DrawFollowModeSection();
            ImGui.Unindent();
        }

        if (ImGui.CollapsingHeader("Free Fly Options"))
        {
            ImGui.Indent();
            DrawFreeFlySection();
            ImGui.Unindent();
        }

        ImGui.PopStyleVar(3);
    }

    private void DrawTopControls()
    {
        var enabled = configuration.Enabled;
        if (ImGui.Checkbox("Enable CamCam", ref enabled))
        {
            configuration.Enabled = enabled;
            configuration.Save();
        }

        TextDisabledWrapped(cameraController.Status);
        TextDisabledWrapped("Holding right or left mouse always hands control back to you.");

        ImGui.Spacing();

        var freeFly = configuration.FreeFly;
        if (ImGui.Checkbox("Free Fly", ref freeFly))
        {
            configuration.FreeFly = freeFly;
            configuration.Save();
        }

        ImGui.SameLine(0, 24);
        ImGui.Text("Orbit:");
        ImGui.SameLine();
        var mode = configuration.FollowMode;
        if (ImGui.RadioButton("Myself", mode == CameraFollowMode.None))
        {
            configuration.FollowMode = CameraFollowMode.None;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("My /target", mode == CameraFollowMode.CurrentTarget))
        {
            configuration.FollowMode = CameraFollowMode.CurrentTarget;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Cycle", mode == CameraFollowMode.Cycle))
        {
            configuration.FollowMode = CameraFollowMode.Cycle;
            configuration.Save();
        }
    }

    private void DrawGeneralSection()
    {
        DrawKeyDropdown("Toggle CamCam keybind", () => configuration.ToggleCamCamKeyName, v => configuration.ToggleCamCamKeyName = v);
        TextDisabledWrapped("Flips \"Enable CamCam\" on/off from anywhere - separate from the Free Fly toggle under Free Fly Options.");

        ImGui.Spacing();
        DrawKeyDropdown("Block numpad keybind", () => configuration.NumpadBlockToggleKeyName, v => configuration.NumpadBlockToggleKeyName = v);
        TextDisabledWrapped("Toggles whether numpad fly keys are blocked from reaching the game. Lets you temporarily let numpad through (e.g. to use a numpad-bound hotbar slot) without leaving Free Fly.");

        ImGui.Spacing();
        DrawKeyDropdown("Start/stop recording keybind", () => configuration.RecordToggleKeyName, v => configuration.RecordToggleKeyName = v);
        TextDisabledWrapped("Reserved for the upcoming record-a-Free-Fly-path-as-a-preset feature - not wired up to actual recording yet, just claiming the keybind now.");

        ImGui.Spacing();
        var autoHide = configuration.AutoHideUi;
        if (ImGui.Checkbox("Auto-hide UI while CamCam is active", ref autoHide))
        {
            configuration.AutoHideUi = autoHide;
            configuration.Save();
        }
        if (autoHide)
        {
            DrawKeyDropdown("Your \"Toggle UI Display Mode\" key", () => configuration.UiToggleKeyName, v => configuration.UiToggleKeyName = v);
            TextDisabledWrapped("Check System > Keybinds > System > \"Toggle UI Display Mode\" in-game for what to pick here.");
        }

        ImGui.Spacing();
        DrawIdleThresholdDropdown();
        TextDisabledWrapped("Stand still this long before the camera engages - applies to Myself, My /target, and Cycle alike now, for consistency across all three.");

        ImGui.Spacing();
        if (gameConfig.TryGet(SystemConfigOption.IdlingCameraAFK, out uint idlingCameraAfkRaw))
        {
            bool blocked = idlingCameraAfkRaw == 0;
            if (ImGui.Checkbox("Block FFXIV's own idling camera (Auto-AFK)", ref blocked))
                gameConfig.Set(SystemConfigOption.IdlingCameraAFK, blocked ? 0u : 1u);
            ImGui.SameLine();
            TextDisabledWrapped($"(raw value: {idlingCameraAfkRaw})");
            TextDisabledWrapped("Same as toggling Auto-AFK Settings in System Configuration yourself. If it doesn't seem to work, check that menu directly - a relog may be needed for the change to take effect.");
        }
        else
        {
            TextDisabledWrapped("Couldn't read FFXIV's Auto-AFK setting - try again once fully logged in.");
        }
    }

    private void DrawFreeFlySection()
    {
        ImGui.TextWrapped("True 3D movement using the numpad keys below - not WASD, since those are your normal movement keys.");

        bool numLockOn = FreeCamController.IsNumLockOn;
        ImGui.TextColored(
            numLockOn ? new Vector4(0.3f, 1f, 0.3f, 1f) : new Vector4(1f, 0.3f, 0.3f, 1f),
            numLockOn ? "Num Lock: ON" : "Num Lock: OFF - numpad keys won't work as configured right now.");

        var freeFlySpeed = configuration.FreeFlySpeed;
        if (ImGui.SliderFloat("Fly speed", ref freeFlySpeed, 0.5f, 20f))
        {
            configuration.FreeFlySpeed = freeFlySpeed;
            configuration.Save();
        }

        var turnSpeedDeg = configuration.FreeFlyTurnSpeed * (180f / MathF.PI);
        if (ImGui.SliderFloat("Turn speed", ref turnSpeedDeg, 30f, 360f, "%.0f deg/s"))
        {
            configuration.FreeFlyTurnSpeed = turnSpeedDeg * (MathF.PI / 180f);
            configuration.Save();
        }

        DrawKeyDropdown("Toggle Free Fly keybind", () => configuration.FreeFlyToggleKeyName, v => configuration.FreeFlyToggleKeyName = v);
        TextDisabledWrapped("Press once to flip Free Fly on/off - also turns on \"Enable CamCam\" automatically.");

        ImGui.Spacing();
        ImGui.Text("Move:");
        DrawKeyDropdown("Forward", () => configuration.FlyForwardKey, v => configuration.FlyForwardKey = v);
        ImGui.SameLine();
        DrawKeyDropdown("Back", () => configuration.FlyBackKey, v => configuration.FlyBackKey = v);
        DrawKeyDropdown("Strafe left", () => configuration.FlyLeftKey, v => configuration.FlyLeftKey = v);
        ImGui.SameLine();
        DrawKeyDropdown("Strafe right", () => configuration.FlyRightKey, v => configuration.FlyRightKey = v);
        DrawKeyDropdown("Up", () => configuration.FlyUpKey, v => configuration.FlyUpKey = v);
        ImGui.SameLine();
        DrawKeyDropdown("Down", () => configuration.FlyDownKey, v => configuration.FlyDownKey = v);

        ImGui.Spacing();
        ImGui.Text("Turn / look:");
        DrawKeyDropdown("Turn left", () => configuration.FlyTurnLeftKey, v => configuration.FlyTurnLeftKey = v);
        ImGui.SameLine();
        DrawKeyDropdown("Turn right", () => configuration.FlyTurnRightKey, v => configuration.FlyTurnRightKey = v);
        DrawKeyDropdown("Look up", () => configuration.FlyLookUpKey, v => configuration.FlyLookUpKey = v);
        ImGui.SameLine();
        DrawKeyDropdown("Look down", () => configuration.FlyLookDownKey, v => configuration.FlyLookDownKey = v);

        ImGui.Spacing();
        var lockGround = configuration.FreeFlyLockToGround;
        if (ImGui.Checkbox("Don't fly below ground level (approximate)", ref lockGround))
        {
            configuration.FreeFlyLockToGround = lockGround;
            configuration.Save();
        }
        if (lockGround)
        {
            var clearance = configuration.FreeFlyGroundClearance;
            if (ImGui.SliderFloat("Ground clearance", ref clearance, 0f, 3f))
            {
                configuration.FreeFlyGroundClearance = clearance;
                configuration.Save();
            }
        }
        TextDisabledWrapped("Uses your character's current height as \"ground\" - not real terrain collision, so it works best near flat areas close to you.");
    }

    private void DrawFollowModeSection()
    {
        var autoCycle = configuration.CycleAutoAdvance;
        if (ImGui.Checkbox("Auto cycle", ref autoCycle))
        {
            configuration.CycleAutoAdvance = autoCycle;
            configuration.Save();
        }
        if (autoCycle)
        {
            ImGui.SameLine();
            DrawCycleIntervalDropdown();
        }
        TextDisabledWrapped("Moves to the next nearby player on its own. Only affects Cycle mode. Next/Previous buttons moved to the Saved Views section.");

        ImGui.Spacing();
        ImGui.TextWrapped("These four only take effect once Orbit is set to \"My /target\" or \"Cycle\".");

        ImGui.Spacing();
        var bypassTargetHook = configuration.ExperimentalBypassTargetHook;
        if (ImGui.Checkbox("Drop native camera target (matches Free Fly)", ref bypassTargetHook))
        {
            configuration.ExperimentalBypassTargetHook = bypassTargetHook;
            configuration.Save();
        }
        TextDisabledWrapped("On by default - tested extensively with no downside found.");

        ImGui.Spacing();
        var zeroInterpDistance = configuration.ZeroOutNativeInterpDistance;
        if (ImGui.Checkbox("Zero out native interpolation lag", ref zeroInterpDistance))
        {
            configuration.ZeroOutNativeInterpDistance = zeroInterpDistance;
            configuration.Save();
        }
        TextDisabledWrapped("Off by default - tested, doesn't reduce shake. Was also found to freeze Free Fly movement if left on - now automatically skipped during Free Fly regardless of this setting.");

        ImGui.Spacing();
        var forceDistance = configuration.ForceNativeDistanceToMatchZoom;
        if (ImGui.Checkbox("Force native Distance to match zoom", ref forceDistance))
        {
            configuration.ForceNativeDistanceToMatchZoom = forceDistance;
            configuration.Save();
        }
        TextDisabledWrapped("Off by default - confirmed harmful (can cause sudden camera jumps).");

        ImGui.Spacing();
        var genderFilter = configuration.CycleGenderFilterMode;
        ImGui.Text("Target:");
        ImGui.SameLine();
        if (ImGui.RadioButton("Any", genderFilter == CycleGenderFilter.Any))
        {
            configuration.CycleGenderFilterMode = CycleGenderFilter.Any;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Male", genderFilter == CycleGenderFilter.MaleOnly))
        {
            configuration.CycleGenderFilterMode = CycleGenderFilter.MaleOnly;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("Female", genderFilter == CycleGenderFilter.FemaleOnly))
        {
            configuration.CycleGenderFilterMode = CycleGenderFilter.FemaleOnly;
            configuration.Save();
        }
        TextDisabledWrapped("Only affects Cycle mode.");

        ImGui.Spacing();
        var excludeLalafells = configuration.ExcludeLalafells;
        if (ImGui.Checkbox("Exclude lalafells", ref excludeLalafells))
        {
            configuration.ExcludeLalafells = excludeLalafells;
            configuration.Save();
        }
        TextDisabledWrapped("Skips lalafells when picking who to cycle through - presets tuned for taller races tend to frame them oddly. Only affects Cycle mode.");

        ImGui.Spacing();
        var excludeSitting = configuration.ExcludeSitting;
        if (ImGui.Checkbox("Exclude sitting", ref excludeSitting))
        {
            configuration.ExcludeSitting = excludeSitting;
            configuration.Save();
        }
        TextDisabledWrapped("Skips anyone currently sitting (ground-sit or chair) when picking who to cycle through. Only affects Cycle mode.");

        ImGui.Spacing();
        var excludeCrafters = configuration.ExcludeCrafters;
        if (ImGui.Checkbox("Exclude crafters", ref excludeCrafters))
        {
            configuration.ExcludeCrafters = excludeCrafters;
            configuration.Save();
        }
        TextDisabledWrapped("Skips anyone in one of the 8 crafting jobs when picking who to cycle through. Only affects Cycle mode.");

        ImGui.Spacing();
        ImGui.Text("Don't target if wearing:");
        TextDisabledWrapped("The game doesn't expose \"clothing types\" as a concept - only exact item IDs per gear slot. Add the specific IDs you want to exclude below; the periodic /xllog status line shows the current target's equipped IDs per slot so you can identify which number is which piece of gear on them, then add it here.");

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("##newExcludedItemId", ref pendingExcludedItemId, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Add##addExcludedItem"))
        {
            if (pendingExcludedItemId is > 0 and <= ushort.MaxValue)
            {
                ushort id = (ushort)pendingExcludedItemId;
                if (!configuration.ExcludedEquipmentItemIds.Contains(id))
                {
                    configuration.ExcludedEquipmentItemIds.Add(id);
                    configuration.Save();
                }
                pendingExcludedItemId = 0;
            }
        }

        int removeItemIdIndex = -1;
        for (int i = 0; i < configuration.ExcludedEquipmentItemIds.Count; i++)
        {
            ImGui.PushID($"excludedItem{i}");
            ImGui.Text(configuration.ExcludedEquipmentItemIds[i].ToString());
            ImGui.SameLine();
            if (ImGui.Button("Remove"))
                removeItemIdIndex = i;
            ImGui.PopID();
        }
        if (removeItemIdIndex >= 0)
        {
            configuration.ExcludedEquipmentItemIds.RemoveAt(removeItemIdIndex);
            configuration.Save();
        }
        TextDisabledWrapped("Excludes a candidate if the ID above is equipped in ANY gear slot. Only affects Cycle mode.");

        ImGui.Spacing();
        TextDisabledWrapped("\"Cycle through saved views\" (whether Cycle applies your Saved Views' angle/zoom/height at all) and its timer moved to the top of the Saved Views section.");

        ImGui.Spacing();
        ImGui.TextWrapped("Height offset, Closest zoom, and Steepest angle all live under Camera Angle and Zoom above. Horizontal angle there is an absolute world angle now, not relative to whoever's being looked at - the same saved angle looks identical regardless of target, including yourself.");
    }

    private void DrawRotationZoomSection()
    {
        TextDisabledWrapped("Ctrl+Click any slider below to type an exact number instead of dragging.");

        var hRotDegrees = configuration.HorizontalRotation * (180f / MathF.PI);
        if (ImGui.SliderFloat("Horizontal angle", ref hRotDegrees, -180f, 180f, "%.0f deg"))
        {
            configuration.HorizontalRotation = hRotDegrees * (MathF.PI / 180f);
            configuration.Save();
        }
        if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();

        var vRotDegrees = configuration.VerticalRotation * (180f / MathF.PI);
        if (ImGui.SliderFloat("Vertical angle", ref vRotDegrees, -89f, 89f, "%.0f deg"))
        {
            configuration.VerticalRotation = vRotDegrees * (MathF.PI / 180f);
            configuration.Save();
        }
        if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();

        var zoom = configuration.Zoom;
        if (ImGui.SliderFloat("Zoom / distance", ref zoom, 0.001f, 20f, "%.3f"))
        {
            configuration.Zoom = zoom;
            configuration.Save();
        }
        if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
        TextDisabledWrapped("Zoom only applies when Free Fly is off. How close it can get in a follow mode depends on Closest zoom below.");

        ImGui.Spacing();
        DrawFreeFlyDPad();
        ImGui.Spacing();

        var freeFlyX = cameraController.FreeFlyPositionX;
        if (ImGui.DragFloat("Free Fly X (left/right)", ref freeFlyX, 0.1f))
            cameraController.FreeFlyPositionX = freeFlyX;
        if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();

        var freeFlyY = cameraController.FreeFlyPositionY;
        if (ImGui.DragFloat("Free Fly Y (up/down)", ref freeFlyY, 0.1f))
            cameraController.FreeFlyPositionY = freeFlyY;
        if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();

        var freeFlyZ = cameraController.FreeFlyPositionZ;
        if (ImGui.DragFloat("Free Fly Z (forward/back)", ref freeFlyZ, 0.1f))
            cameraController.FreeFlyPositionZ = freeFlyZ;
        if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
        TextDisabledWrapped("Direct access to what the arrow keys/D-pad actually move - Free Fly's raw position isn't expressed as H/V/Zoom the way orbit mode is, since it's a free point in space, not an orbit around a target. Only affects Free Fly; no fixed range since world coordinates aren't naturally bounded.");

        var heightOffset = configuration.FollowHeightOffset;
        if (ImGui.SliderFloat("Height offset", ref heightOffset, -10f, 10f))
        {
            configuration.FollowHeightOffset = heightOffset;
            configuration.Save();
        }
        if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
        ImGui.SameLine();
        if (ImGui.Button("Reset##height"))
        {
            configuration.FollowHeightOffset = 0f;
            configuration.Save();
        }
        TextDisabledWrapped("Works in every mode, including orbiting yourself. Also adjustable live with the Up/Down fly keys.");

        var heightLockToGround = configuration.FollowHeightLockToGround;
        if (ImGui.Checkbox("Don't let height offset go below ground level (approximate)", ref heightLockToGround))
        {
            configuration.FollowHeightLockToGround = heightLockToGround;
            configuration.Save();
        }
        if (heightLockToGround)
        {
            var heightGroundClearance = configuration.FollowHeightGroundClearance;
            if (ImGui.SliderFloat("Ground clearance##height", ref heightGroundClearance, 0f, 3f))
            {
                configuration.FollowHeightGroundClearance = heightGroundClearance;
                configuration.Save();
            }
        }
        TextDisabledWrapped("Uses whoever you're orbiting as \"ground\" - not real terrain collision, same approximation Free Fly's ground lock uses.");

        var minZoom = configuration.FollowMinZoom;
        if (ImGui.SliderFloat("Closest zoom", ref minZoom, 0.001f, 2f))
        {
            configuration.FollowMinZoom = minZoom;
            configuration.Save();
        }
        TextDisabledWrapped("Widens the camera's own zoom limit past the game's normal minimum, same technique Cammy uses. Works in every mode, including orbiting yourself.");
        TextDisabledWrapped($"Live camera right now: zoom={cameraController.CurrentCameraZoom:0.000}, limit range={cameraController.CurrentCameraMinZoom:0.000} to {cameraController.CurrentCameraMaxZoom:0.000} - read directly off the camera, not the slider above, so this confirms what's actually reaching it.");

        var maxAngle = configuration.FollowMaxAngleDegrees;
        if (ImGui.SliderFloat("Steepest angle", ref maxAngle, 45f, 89f, "%.0f deg"))
        {
            configuration.FollowMaxAngleDegrees = maxAngle;
            configuration.Save();
        }
        TextDisabledWrapped("Works in every mode, including orbiting yourself.");
    }

    private void DrawSavedViewsSection()
    {
        var useSavedViews = configuration.CycleUseSavedViews;
        if (ImGui.Checkbox("Cycle through saved views", ref useSavedViews))
        {
            configuration.CycleUseSavedViews = useSavedViews;
            configuration.Save();
        }
        TextDisabledWrapped(useSavedViews
            ? "Cycle applies these presets' angle/zoom/height. Only affects Cycle mode."
            : "Off: only who's looked at changes. On: preset angle/zoom/height applies too.");

        var presetCycle = configuration.PresetCycleEnabled;
        if (ImGui.Checkbox("Auto-advance saved views", ref presetCycle))
        {
            configuration.PresetCycleEnabled = presetCycle;
            if (presetCycle) configuration.CycleUseSavedViews = true; // a preset can't advance on its own if it's not being applied at all
            configuration.Save();
        }
        if (presetCycle)
        {
            ImGui.SameLine();
            DrawPresetCycleIntervalDropdown();

            var random = configuration.PresetCycleRandom;
            if (ImGui.Checkbox("Random order", ref random))
            {
                configuration.PresetCycleRandom = random;
                configuration.Save();
            }
        }
        TextDisabledWrapped("Moves to the next preset on its own. Only in Cycle mode.");

        ImGui.Spacing();

        if (ImGui.Button("Reload defaults"))
        {
            SnapshotForUndo();
            configuration.SavedViews.Clear();
            configuration.SavedViews.AddRange(Configuration.BuildDefaultSavedViews());
            selectedPresetIndex = 0;
            configuration.Save();
        }

        ImGui.SameLine();
        ImGui.BeginDisabled(undoSnapshot == null);
        if (ImGui.Button("Undo"))
        {
            configuration.SavedViews.Clear();
            configuration.SavedViews.AddRange(undoSnapshot!);
            configuration.Save();
            undoSnapshot = null;
            if (selectedPresetIndex >= configuration.SavedViews.Count)
                selectedPresetIndex = Math.Max(0, configuration.SavedViews.Count - 1);
        }
        ImGui.EndDisabled();

        ImGui.SameLine();
        var paused = cameraController.CyclePaused;
        Vector4 pauseColor = paused ? new Vector4(0.75f, 0.3f, 0.3f, 1f) : new Vector4(0.25f, 0.55f, 0.3f, 1f);
        ImGui.PushStyleColor(ImGuiCol.Button, pauseColor);
        if (ImGui.Button(paused ? "Resume timers" : "Pause timers"))
            cameraController.CyclePaused = !paused;
        ImGui.PopStyleColor();
        TextDisabledWrapped("Reload/Undo affect all presets. Pause freezes auto-cycling - manual Next/Previous still works.");

        ImGui.Spacing();
        if (ImGui.Button("<- Previous"))
            cameraController.CyclePrevious();
        ImGui.SameLine();
        if (ImGui.Button("Next ->"))
            cameraController.CycleNext();
        ImGui.SameLine();
        TextDisabledWrapped(cameraController.CurrentCycleName);
        TextDisabledWrapped("Moves both the player and preset together, regardless of timers. Only affects Cycle mode. Moved here from Follow Mode Options - handy to cycle players while setting up presets.");

        ImGui.Spacing();
        ImGui.Text($"Presets ({configuration.SavedViews.Count}):");

        for (int i = 0; i < configuration.SavedViews.Count; i++)
        {
            if (i > 0) ImGui.SameLine();
            bool isSelected = i == selectedPresetIndex;
            bool isActive = i == cameraController.CurrentViewIndex;

            bool pushedColor = false;
            if (isActive)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.2f, 0.55f, 0.25f, 1f));
                pushedColor = true;
            }
            else if (isSelected)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.25f, 0.4f, 0.7f, 1f));
                pushedColor = true;
            }

            if (ImGui.Button($"{i + 1}##presetbtn{i}", new Vector2(32, 0)))
                selectedPresetIndex = i;

            if (pushedColor) ImGui.PopStyleColor();
        }

        if (configuration.SavedViews.Count > 0) ImGui.SameLine();
        if (ImGui.Button("+##addpreset"))
        {
            SnapshotForUndo();
            var newView = new SavedView
            {
                Name = $"View {configuration.SavedViews.Count + 1}",
                HorizontalRotation = configuration.HorizontalRotation,
                VerticalRotation = configuration.VerticalRotation,
                Zoom = configuration.Zoom,
                HeightOffset = configuration.FollowHeightOffset,
                MinZoom = configuration.FollowMinZoom,
                MaxAngleDegrees = configuration.FollowMaxAngleDegrees,
                HeightLockToGround = configuration.FollowHeightLockToGround,
                HeightGroundClearance = configuration.FollowHeightGroundClearance,
            };
            configuration.SavedViews.Add(newView);
            selectedPresetIndex = configuration.SavedViews.Count - 1;
            configuration.Save();
        }
        TextDisabledWrapped("Green = active in-camera now. Blue = selected for editing below. + adds a new preset from your current live angle/zoom.");

        if (configuration.SavedViews.Count == 0)
        {
            TextDisabledWrapped("No views saved yet - click + to add one.");
            return;
        }

        selectedPresetIndex = Math.Clamp(selectedPresetIndex, 0, configuration.SavedViews.Count - 1);
        var view = configuration.SavedViews[selectedPresetIndex];

        ImGui.Separator();
        ImGui.PushID(selectedPresetIndex);

        string name = view.Name;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("##name", ref name, 32))
        {
            SnapshotForUndo();
            view.Name = name;
            configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Load"))
        {
            cameraController.LoadView(view, selectedPresetIndex);
        }

        ImGui.SameLine();
        if (ImGui.Button("Update"))
        {
            SnapshotForUndo();
            view.HorizontalRotation = configuration.HorizontalRotation;
            view.VerticalRotation = configuration.VerticalRotation;
            view.Zoom = configuration.Zoom;
            view.HeightOffset = configuration.FollowHeightOffset;
            view.MinZoom = configuration.FollowMinZoom;
            view.MaxAngleDegrees = configuration.FollowMaxAngleDegrees;
            view.HeightLockToGround = configuration.FollowHeightLockToGround;
            view.HeightGroundClearance = configuration.FollowHeightGroundClearance;
            configuration.Save();
        }

        ImGui.SameLine();
        if (ImGui.Button("Remove"))
        {
            SnapshotForUndo();
            configuration.SavedViews.RemoveAt(selectedPresetIndex);
            configuration.Save();
            selectedPresetIndex = Math.Clamp(selectedPresetIndex, 0, Math.Max(0, configuration.SavedViews.Count - 1));
            ImGui.PopID();
            return; // view is now stale - bail out cleanly this frame rather than keep using it
        }
        TextDisabledWrapped("Load a preset, tweak the Camera Angle and Zoom sliders above, Update to resave. Green number above = active in-camera. While flying, this reproduces the same H/V/Zoom framing on whoever the current target is, same as orbit mode.");

        ImGui.Spacing();

        var editGroundLock = view.HeightLockToGround;
            if (ImGui.Checkbox("Ground lock##groundlock", ref editGroundLock))
            {
                SnapshotForUndo();
                view.HeightLockToGround = editGroundLock;
                configuration.Save();
            }
            if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
            if (editGroundLock)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(65);
                var editGroundClearance = view.HeightGroundClearance;
                if (ImGui.DragFloat("Clearance##groundclearance", ref editGroundClearance, 0.05f, 0f, 5f, "%.2f"))
                {
                    SnapshotForUndo();
                    view.HeightGroundClearance = editGroundClearance;
                    configuration.Save();
                }
                if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
            }
            TextDisabledWrapped(editGroundLock
                ? "Stops the camera dropping below real ground height (plus Clearance) at wherever the camera actually ends up - found via the game's own collision raycast, not approximated from the target's own height anymore. Previously this used a single fixed reference for the whole shot, which caused visible popping/shaking on a pan that crossed uneven terrain; this should track the real terrain instead."
                : "Camera follows the pure orbit math with no floor - it can go below real ground height if the angle/zoom take it there.");

            var editAvoidWalls = view.AvoidWallsAndObjects;
            if (ImGui.Checkbox("Avoid walls/objects##avoidwalls", ref editAvoidWalls))
            {
                SnapshotForUndo();
                view.AvoidWallsAndObjects = editAvoidWalls;
                configuration.Save();
            }
            if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
            if (editAvoidWalls)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(65);
                var editWallBuffer = view.WallAvoidanceBuffer;
                if (ImGui.DragFloat("Buffer##wallbuffer", ref editWallBuffer, 0.02f, 0.05f, 3f, "%.2f"))
                {
                    SnapshotForUndo();
                    view.WallAvoidanceBuffer = editWallBuffer;
                    configuration.Save();
                }
                if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
            }
            TextDisabledWrapped(editAvoidWalls
                ? "Pulls the camera in along its own line of sight if a wall or object sits between it and the subject, found via the game's own collision raycast - the same basic technique every third-person camera uses, done deterministically here instead of relying on (and fighting) whatever the game's native camera does. Buffer is how far short of the wall to stop - raise it if the camera needs to back off further to clear whatever's causing shake near that boundary. Genuinely new - test on presets that pan near walls or objects specifically."
                : "Camera can clip through walls and objects between it and the subject if the angle/zoom put it there.");

            ImGui.SetNextItemWidth(65);
            var editSmoothing = view.PositionSmoothingSeconds;
            if (ImGui.DragFloat("Smoothing (sec)##smoothing", ref editSmoothing, 0.02f, 0f, 2f, "%.2f"))
            {
                SnapshotForUndo();
                view.PositionSmoothingSeconds = MathF.Max(editSmoothing, 0f);
                configuration.Save();
            }
            TextDisabledWrapped(editSmoothing > 0f
                ? "Eases toward the target position instead of snapping straight to it every frame - softens shake from walls or uneven ground the camera is still fighting with, at the cost of a slight lag behind sudden changes (including your own pan). Resets to a clean cut whenever the actual target changes (Cycle swapping players, or a FollowMode switch) - it only smooths continuous motion on the same subject."
                : "0 = instant, snaps straight to the computed position every frame - the default, and how every preset behaved before this field existed. Raise it if a specific pan is visibly shaking near a wall or uneven ground.");

            TextDisabledWrapped("Drag or Ctrl+Click to edit exactly - changes this preset directly and apply live if it's active.");

            var requireSitting = view.RequireTargetSitting;
            if (ImGui.Checkbox("Only apply when target is sitting##requiresitting", ref requireSitting))
            {
                SnapshotForUndo();
                view.RequireTargetSitting = requireSitting;
                configuration.Save();
            }
            if (requireSitting)
            {
                ImGui.SameLine();
                var sittingType = view.RequiredSittingType;
                ImGui.SetNextItemWidth(150);
                if (ImGui.RadioButton("Any##sitAny", sittingType == SittingRequirement.Any))
                {
                    SnapshotForUndo();
                    view.RequiredSittingType = SittingRequirement.Any;
                    configuration.Save();
                }
                ImGui.SameLine();
                if (ImGui.RadioButton("Ground##sitGround", sittingType == SittingRequirement.Ground))
                {
                    SnapshotForUndo();
                    view.RequiredSittingType = SittingRequirement.Ground;
                    configuration.Save();
                }
                ImGui.SameLine();
                if (ImGui.RadioButton("Furniture##sitFurniture", sittingType == SittingRequirement.Furniture))
                {
                    SnapshotForUndo();
                    view.RequiredSittingType = SittingRequirement.Furniture;
                    configuration.Save();
                }
            }
            TextDisabledWrapped(requireSitting
                ? "Cycle skips straight past this preset for anyone who doesn't match the sitting type selected above - it's only ever shown for a target who does. \"Furniture\" covers chairs and benches alike (the game doesn't distinguish those two at the level this reads). Only affects Cycle mode."
                : "Cycle skips straight past this preset for anyone who isn't currently sitting - it's only ever shown for a target who is. Only affects Cycle mode.");

            var fixedCameraPassBy = view.FixedCameraPassBy;
            if (ImGui.Checkbox("Tripod (fixed position)##fixedcamera", ref fixedCameraPassBy))
            {
                SnapshotForUndo();
                view.FixedCameraPassBy = fixedCameraPassBy;
                configuration.Save();
            }
            TextDisabledWrapped(fixedCameraPassBy
                ? "Camera position is placed once (using this preset's own H/V/Zoom/Height above, same as any other preset) and then held completely fixed for the rest of the shot - a tripod, not an orbit. The subject can walk anywhere without the camera following. Combine with Horizontal and/or Vertical pan below so the camera's own rotation still sweeps from a fixed spot - that's what lets the subject drift into frame and back out, rather than the camera tracking them. Zoom pan doesn't apply here (there's no orbit radius once position is frozen)."
                : "Camera continuously tracks the subject like every other preset - turn this on for a fixed tripod-style pan instead, where the camera plants itself once and only its rotation moves.");

            ImGui.Spacing();
            var translateInsteadOfPan = view.TranslateInsteadOfPan;
            if (ImGui.Checkbox("Strafe / dolly / pedestal (moving camera, fixed facing)##translatemode", ref translateInsteadOfPan))
            {
                SnapshotForUndo();
                view.TranslateInsteadOfPan = translateInsteadOfPan;
                configuration.Save();
            }

            if (translateInsteadOfPan)
            {
                ImGui.TextWrapped("Right/Left:");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70);
                var translateStartRight = view.TranslateStartRight;
                if (ImGui.DragFloat("Start##translatestartright", ref translateStartRight, 0.1f, -50f, 50f, "%.1f"))
                {
                    SnapshotForUndo();
                    view.TranslateStartRight = translateStartRight;
                    configuration.Save();
                }
                if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70);
                var translateEndRight = view.TranslateEndRight;
                if (ImGui.DragFloat("End##translateendright", ref translateEndRight, 0.1f, -50f, 50f, "%.1f"))
                {
                    SnapshotForUndo();
                    view.TranslateEndRight = translateEndRight;
                    configuration.Save();
                }
                if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();

                ImGui.TextWrapped("Up/Down:  ");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70);
                var translateStartUp = view.TranslateStartUp;
                if (ImGui.DragFloat("Start##translatestartup", ref translateStartUp, 0.1f, -50f, 50f, "%.1f"))
                {
                    SnapshotForUndo();
                    view.TranslateStartUp = translateStartUp;
                    configuration.Save();
                }
                if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70);
                var translateEndUp = view.TranslateEndUp;
                if (ImGui.DragFloat("End##translateendup", ref translateEndUp, 0.1f, -50f, 50f, "%.1f"))
                {
                    SnapshotForUndo();
                    view.TranslateEndUp = translateEndUp;
                    configuration.Save();
                }
                if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();

                ImGui.TextWrapped("Fwd/Back: ");
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70);
                var translateStartForward = view.TranslateStartForward;
                if (ImGui.DragFloat("Start##translatestartforward", ref translateStartForward, 0.1f, -50f, 50f, "%.1f"))
                {
                    SnapshotForUndo();
                    view.TranslateStartForward = translateStartForward;
                    configuration.Save();
                }
                if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
                ImGui.SameLine();
                ImGui.SetNextItemWidth(70);
                var translateEndForward = view.TranslateEndForward;
                if (ImGui.DragFloat("End##translateendforward", ref translateEndForward, 0.1f, -50f, 50f, "%.1f"))
                {
                    SnapshotForUndo();
                    view.TranslateEndForward = translateEndForward;
                    configuration.Save();
                }
                if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();

                ImGui.SetNextItemWidth(90);
                var translateSpeed = view.TranslateSpeed;
                if (ImGui.DragFloat("units/s##translatespeed", ref translateSpeed, 0.1f, 0.1f, 30f))
                {
                    SnapshotForUndo();
                    view.TranslateSpeed = translateSpeed;
                    configuration.Save();
                }
                if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();

                ImGui.SameLine();
                ImGui.SetNextItemWidth(90);
                var translateDelay = view.TranslateStartDelaySeconds;
                if (ImGui.DragFloat("Start delay (sec)##translatedelay", ref translateDelay, 0.1f, 0f, 30f, "%.1f"))
                {
                    SnapshotForUndo();
                    view.TranslateStartDelaySeconds = MathF.Max(translateDelay, 0f);
                    configuration.Save();
                }

                TextDisabledWrapped("Works independently of Tripod. Start/End per axis define the move relative to where you placed the camera - Right/Forward relative to its facing, Up/Down always true vertical. Facing stays locked the whole time.");
            }

            var panEnabled = view.PanEnabled;
            if (ImGui.Checkbox("Cinematic pan (horizontal)", ref panEnabled))
            {
                SnapshotForUndo();
                view.PanEnabled = panEnabled;
                configuration.Save();
            }

            if (panEnabled)
            {
                ImGui.SameLine();
                TextDisabledWrapped($"Starts from this preset's own H:{view.HorizontalRotation * 180f / MathF.PI:0} deg");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                var panTo = view.PanToDegrees;
                if (ImGui.DragFloat("To##panto", ref panTo, 1f, -180f, 180f, "%.0f deg"))
                {
                    SnapshotForUndo();
                    view.PanToDegrees = panTo;
                    configuration.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button("Set##setto"))
                {
                    SnapshotForUndo();
                    view.PanToDegrees = configuration.HorizontalRotation * (180f / MathF.PI);
                    configuration.Save();
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(110);
                var panSpeed = view.PanSpeedDegreesPerSecond;
                if (ImGui.DragFloat("deg/s##panspeed", ref panSpeed, 0.5f, 0.5f, 60f))
                {
                    SnapshotForUndo();
                    view.PanSpeedDegreesPerSecond = panSpeed;
                    configuration.Save();
                }

                ImGui.SetNextItemWidth(90);
                var panDelay = view.HorizontalPanStartDelaySeconds;
                if (ImGui.DragFloat("Start delay (sec)##pandelay", ref panDelay, 0.1f, 0f, 30f, "%.1f"))
                {
                    SnapshotForUndo();
                    view.HorizontalPanStartDelaySeconds = MathF.Max(panDelay, 0f);
                    configuration.Save();
                }

                TextDisabledWrapped("Sweeps from this preset's own saved Horizontal angle to To while this view is active. \"Set\" captures your current live Horizontal angle - rotate to where you want the sweep to end in-game, then click Set. Start delay holds this axis still for that many seconds after the preset activates before it begins moving - useful for staging one axis (e.g. Zoom) to move first.");
            }

            var vPanEnabled = view.VerticalPanEnabled;
            if (ImGui.Checkbox("Cinematic pan (vertical)", ref vPanEnabled))
            {
                SnapshotForUndo();
                view.VerticalPanEnabled = vPanEnabled;
                configuration.Save();
            }

            if (vPanEnabled)
            {
                ImGui.SameLine();
                TextDisabledWrapped($"Starts from this preset's own V:{view.VerticalRotation * 180f / MathF.PI:0} deg");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                var vPanTo = view.VerticalPanToDegrees;
                if (ImGui.DragFloat("To##vpanto", ref vPanTo, 1f, -89f, 89f, "%.0f deg"))
                {
                    SnapshotForUndo();
                    view.VerticalPanToDegrees = vPanTo;
                    configuration.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button("Set##vsetto"))
                {
                    SnapshotForUndo();
                    view.VerticalPanToDegrees = configuration.VerticalRotation * (180f / MathF.PI);
                    configuration.Save();
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(110);
                var vPanSpeed = view.VerticalPanSpeedDegreesPerSecond;
                if (ImGui.DragFloat("deg/s##vpanspeed", ref vPanSpeed, 0.5f, 0.5f, 60f))
                {
                    SnapshotForUndo();
                    view.VerticalPanSpeedDegreesPerSecond = vPanSpeed;
                    configuration.Save();
                }

                ImGui.SetNextItemWidth(90);
                var vPanDelay = view.VerticalPanStartDelaySeconds;
                if (ImGui.DragFloat("Start delay (sec)##vpandelay", ref vPanDelay, 0.1f, 0f, 30f, "%.1f"))
                {
                    SnapshotForUndo();
                    view.VerticalPanStartDelaySeconds = MathF.Max(vPanDelay, 0f);
                    configuration.Save();
                }

                TextDisabledWrapped("Sweeps from this preset's own saved Vertical angle to To while this view is active. \"Set\" captures your current live Vertical angle. Can run at the same time as horizontal/zoom pan for a combined sweep - each axis tracks its own completion and start delay independently, so e.g. Vertical can hold still while Zoom moves in first, then start once Vertical's own delay elapses.");
            }

            var zoomPanEnabled = view.ZoomPanEnabled;
            if (ImGui.Checkbox("Cinematic pan (zoom)", ref zoomPanEnabled))
            {
                SnapshotForUndo();
                view.ZoomPanEnabled = zoomPanEnabled;
                configuration.Save();
            }

            if (zoomPanEnabled)
            {
                ImGui.SameLine();
                TextDisabledWrapped($"Starts from this preset's own Z:{view.Zoom:0.000}");

                ImGui.SameLine();
                ImGui.SetNextItemWidth(100);
                var zoomPanTo = view.ZoomPanToValue;
                if (ImGui.DragFloat("To##zoompanto", ref zoomPanTo, 0.05f, 0.001f, 20f, "%.3f"))
                {
                    SnapshotForUndo();
                    view.ZoomPanToValue = zoomPanTo;
                    configuration.Save();
                }
                ImGui.SameLine();
                if (ImGui.Button("Set##zoomsetto"))
                {
                    SnapshotForUndo();
                    view.ZoomPanToValue = configuration.Zoom;
                    configuration.Save();
                }

                ImGui.SameLine();
                ImGui.SetNextItemWidth(110);
                var zoomPanSpeed = view.ZoomPanSpeed;
                if (ImGui.DragFloat("units/s##zoompanspeed", ref zoomPanSpeed, 0.05f, 0.05f, 20f))
                {
                    SnapshotForUndo();
                    view.ZoomPanSpeed = zoomPanSpeed;
                    configuration.Save();
                }

                ImGui.SetNextItemWidth(90);
                var zoomPanDelay = view.ZoomPanStartDelaySeconds;
                if (ImGui.DragFloat("Start delay (sec)##zoompandelay", ref zoomPanDelay, 0.1f, 0f, 30f, "%.1f"))
                {
                    SnapshotForUndo();
                    view.ZoomPanStartDelaySeconds = MathF.Max(zoomPanDelay, 0f);
                    configuration.Save();
                }

                TextDisabledWrapped("Sweeps camera distance from this preset's own saved Zoom to To while this view is active - a dolly in/out. \"Set\" captures your current live Zoom. Combine with Start delay on Horizontal/Vertical above to hold an angle fixed while zooming in first, then start the angle change once its own delay elapses.");
            }

            if (panEnabled || vPanEnabled || zoomPanEnabled)
            {
                var advanceOnComplete = view.PanAdvanceCycleOnComplete;
                if (ImGui.Checkbox("Advance to next preset after pan completes##panadvance", ref advanceOnComplete))
                {
                    SnapshotForUndo();
                    view.PanAdvanceCycleOnComplete = advanceOnComplete;
                    configuration.Save();
                }

                var returnFirst = view.PanReturnBeforeAdvance;
                if (ImGui.Checkbox("Pan back to start before advancing##panreturn", ref returnFirst))
                {
                    SnapshotForUndo();
                    view.PanReturnBeforeAdvance = returnFirst;
                    configuration.Save();
                }
                TextDisabledWrapped(advanceOnComplete
                    ? (returnFirst
                        ? "Waits for the full round trip (saved angle -> To -> back to saved angle) on every enabled axis before advancing."
                        : "Advances as soon as every enabled axis reaches its own To - doesn't wait for any of them to pan back to their starting angle first.")
                    : "Only takes effect once \"Advance to next preset after pan completes\" above is also on for this preset - fine to set ahead of time either way.");
                TextDisabledWrapped("Advances once every enabled pan axis finishes, instead of waiting for the timer. Cycle mode with Saved Views only.");
            }

            ImGui.PopID();
    }

    /// <summary>Finds the closest matching option to whatever's currently stored - handles old values from before this was a dropdown (the previous free slider allowed any number, not just 15-second steps).</summary>
    private void DrawIdleThresholdDropdown()
    {
        int currentIndex = 0;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < IdleThresholdOptions.Length; i++)
        {
            float diff = MathF.Abs(IdleThresholdOptions[i] - configuration.IdleThresholdSeconds);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                currentIndex = i;
            }
        }

        ImGui.SetNextItemWidth(160);
        if (ImGui.Combo("Wait for idle", ref currentIndex, IdleThresholdLabels, IdleThresholdLabels.Length))
        {
            configuration.IdleThresholdSeconds = IdleThresholdOptions[currentIndex];
            configuration.Save();
        }
    }

    private void DrawCycleIntervalDropdown()
    {
        int currentIndex = 0;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < CycleIntervalOptions.Length; i++)
        {
            float diff = MathF.Abs(CycleIntervalOptions[i] - configuration.CycleIntervalSeconds);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                currentIndex = i;
            }
        }

        ImGui.SetNextItemWidth(140);
        if (ImGui.Combo("Auto cycle timer", ref currentIndex, CycleIntervalLabels, CycleIntervalLabels.Length))
        {
            configuration.CycleIntervalSeconds = CycleIntervalOptions[currentIndex];
            configuration.Save();
        }
    }

    private void DrawPresetCycleIntervalDropdown()
    {
        int currentIndex = 0;
        float bestDiff = float.MaxValue;
        for (int i = 0; i < CycleIntervalOptions.Length; i++)
        {
            float diff = MathF.Abs(CycleIntervalOptions[i] - configuration.PresetCycleIntervalSeconds);
            if (diff < bestDiff)
            {
                bestDiff = diff;
                currentIndex = i;
            }
        }

        ImGui.SetNextItemWidth(140);
        if (ImGui.Combo("Cycle saved views timer", ref currentIndex, CycleIntervalLabels, CycleIntervalLabels.Length))
        {
            configuration.PresetCycleIntervalSeconds = CycleIntervalOptions[currentIndex];
            configuration.Save();
        }
    }

    /// <summary>Alphabetical dropdown backed by KeyCatalog - replaces free-text key entry so an unrecognized/typo'd key name can no longer be picked.</summary>
    /// <summary>
    /// One shared D-pad, not duplicated per preset - controls the single
    /// global Free Fly camera regardless of which preset is currently
    /// selected above. Two plus-sign shaped button groups: Move
    /// (forward/back/strafe) and Up-Down/Turn (vertical + yaw). Buttons
    /// set FreeCamController's virtual-held flags while the mouse is down
    /// on them (ImGui.IsItemActive), which Update() checks alongside the
    /// real keyboard state - so these work interchangeably with the
    /// actual numpad keys, not as a separate input path.
    /// </summary>
    private void DrawFreeFlyDPad()
    {
        var size = new Vector2(24, 24);

        ImGui.BeginGroup();
        ImGui.TextDisabled("Move");
        ImGui.Dummy(size); ImGui.SameLine();
        ImGui.Button("^##dpadfwd", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualForwardHeld = true;

        ImGui.Button("<##dpadstrafeleft", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualStrafeLeftHeld = true;
        ImGui.SameLine();
        ImGui.Dummy(size);
        ImGui.SameLine();
        ImGui.Button(">##dpadstraferight", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualStrafeRightHeld = true;

        ImGui.Dummy(size); ImGui.SameLine();
        ImGui.Button("v##dpadback", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualBackHeld = true;
        ImGui.EndGroup();

        ImGui.SameLine();
        ImGui.Spacing();
        ImGui.SameLine();

        ImGui.BeginGroup();
        ImGui.TextDisabled("Up/Turn");
        ImGui.Dummy(size); ImGui.SameLine();
        ImGui.Button("^##dpadup", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualUpHeld = true;

        ImGui.Button("<##dpadturnleft", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualTurnLeftHeld = true;
        ImGui.SameLine();
        ImGui.Dummy(size);
        ImGui.SameLine();
        ImGui.Button(">##dpadturnright", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualTurnRightHeld = true;

        ImGui.Dummy(size); ImGui.SameLine();
        ImGui.Button("v##dpaddown", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualDownHeld = true;
        ImGui.EndGroup();

        ImGui.SameLine();
        TextDisabledWrapped("Controls Free Fly directly, same as the numpad keys - hold any button.");
    }

    private void DrawKeyDropdown(string label, Func<string> get, Action<string> set)
    {
        string current = get();
        int currentIndex = 0;
        if (!string.IsNullOrWhiteSpace(current))
        {
            for (int i = 1; i < KeyCatalog.DisplayNames.Length; i++)
            {
                if (string.Equals(KeyCatalog.DisplayNames[i], current, StringComparison.OrdinalIgnoreCase))
                {
                    currentIndex = i;
                    break;
                }
            }
        }

        ImGui.SetNextItemWidth(130);
        if (ImGui.Combo(label, ref currentIndex, KeyCatalog.DisplayNames, KeyCatalog.DisplayNames.Length))
        {
            set(currentIndex == 0 ? "" : KeyCatalog.DisplayNames[currentIndex]);
            configuration.Save();
        }
    }
}
