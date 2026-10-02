using System;
using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.Config;
using Dalamud.Interface.Utility.Raii;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin.Services;
using Dalamud.Utility;

namespace CamCam;

/// <summary>
/// Tabbed settings window. The Shots tab edits saved presets directly -
/// the active preset drives the live camera every frame, so editing a
/// separate "live" value and pressing Update (the old workflow) lost the
/// edit as soon as the preset re-applied itself.
/// </summary>
public class SettingsWindow : Window
{
    private static readonly Vector4 DisabledTextColor = new(0.55f, 0.55f, 0.55f, 1f);
    private static readonly Vector4 ActiveColor = new(0.35f, 0.85f, 0.4f, 1f);
    private static readonly Vector4 WarningColor = new(1f, 0.45f, 0.35f, 1f);

    private static readonly float[] IdleThresholdOptions;
    private static readonly string[] IdleThresholdLabels;
    private static readonly float[] CycleIntervalOptions;
    private static readonly string[] CycleIntervalLabels;

    private readonly Configuration configuration;
    private readonly CameraController cameraController;
    private readonly IGameConfig gameConfig;

    private int selectedPresetIndex;
    private int lastSeenActiveIndex = -1;
    private List<SavedView>? undoSnapshot;
    private int pendingExcludedItemId;

    /// <summary>The shot shown in the editor - drawn in the world by ShotOverlay.</summary>
    public SavedView? SelectedView => selectedPresetIndex >= 0 && selectedPresetIndex < configuration.SavedViews.Count
        ? configuration.SavedViews[selectedPresetIndex]
        : null;

    static SettingsWindow()
    {
        var idle = new List<float> { 0f, 3f, 5f, 8f };
        for (int s = 15; s <= 900; s += 15) idle.Add(s);
        IdleThresholdOptions = idle.ToArray();
        IdleThresholdLabels = Array.ConvertAll(IdleThresholdOptions, FormatDuration);

        var cycle = new List<float>();
        for (int s = 1; s <= 14; s++) cycle.Add(s);
        for (int s = 15; s <= 120; s += 15) cycle.Add(s);
        for (int s = 180; s <= 600; s += 60) cycle.Add(s);
        CycleIntervalOptions = cycle.ToArray();
        CycleIntervalLabels = Array.ConvertAll(CycleIntervalOptions, FormatDuration);
    }

    public SettingsWindow(Configuration configuration, CameraController cameraController, IGameConfig gameConfig)
        : base("CamCam###CamCamSettings")
    {
        this.configuration = configuration;
        this.cameraController = cameraController;
        this.gameConfig = gameConfig;

        Size = new Vector2(560, 680);
        SizeCondition = ImGuiCond.FirstUseEver;
        SizeConstraints = new WindowSizeConstraints { MinimumSize = new Vector2(420, 300), MaximumSize = new Vector2(4000, 4000) };
    }

    // ------------------------------------------------------------------
    // Helpers
    // ------------------------------------------------------------------

    private static string FormatDuration(float seconds)
    {
        if (seconds <= 0f) return "Instant";
        int total = (int)seconds;
        int minutes = total / 60, secs = total % 60;
        if (minutes == 0) return $"{secs} sec";
        return secs == 0 ? $"{minutes} min" : $"{minutes} min {secs} sec";
    }

    private static void Hint(string text)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, DisabledTextColor);
        ImGui.TextWrapped(text);
        ImGui.PopStyleColor();
    }

    private static void HelpMarker(string text)
    {
        ImGui.SameLine();
        ImGui.TextDisabled("(?)");
        if (ImGui.IsItemHovered())
        {
            ImGui.BeginTooltip();
            ImGui.PushTextWrapPos(ImGui.GetFontSize() * 30f);
            ImGui.TextUnformatted(text);
            ImGui.PopTextWrapPos();
            ImGui.EndTooltip();
        }
    }

    /// <summary>Saves a global setting once a drag/typing gesture ends (or immediately for clicks) instead of every frame.</summary>
    private void SaveWhenDone(bool changed)
    {
        if (ImGui.IsItemDeactivatedAfterEdit() || (changed && !ImGui.IsItemActive()))
            configuration.Save();
    }

    private bool Checkbox(string label, bool value, Action<bool> set)
    {
        if (!ImGui.Checkbox(label, ref value)) return false;
        set(value);
        configuration.Save();
        return true;
    }

    private void IntervalCombo(string label, float[] options, string[] labels, float current, Action<float> set, float width = 140)
    {
        int index = 0;
        float best = float.MaxValue;
        for (int i = 0; i < options.Length; i++)
        {
            float diff = MathF.Abs(options[i] - current);
            if (diff < best) { best = diff; index = i; }
        }

        ImGui.SetNextItemWidth(width);
        if (ImGui.Combo(label, ref index, labels, labels.Length))
        {
            set(options[index]);
            configuration.Save();
        }
    }

    private void KeyDropdown(string label, string current, Action<string> set, float width = 130)
    {
        int index = 0;
        if (!string.IsNullOrWhiteSpace(current))
        {
            for (int i = 1; i < KeyCatalog.DisplayNames.Length; i++)
            {
                if (string.Equals(KeyCatalog.DisplayNames[i], current, StringComparison.OrdinalIgnoreCase))
                {
                    index = i;
                    break;
                }
            }
        }

        ImGui.SetNextItemWidth(width);
        if (ImGui.Combo(label, ref index, KeyCatalog.DisplayNames, KeyCatalog.DisplayNames.Length))
        {
            set(index == 0 ? "" : KeyCatalog.DisplayNames[index]);
            configuration.Save();
        }
    }

    private void SnapshotForUndo()
    {
        undoSnapshot = new List<SavedView>(configuration.SavedViews.Count);
        foreach (var v in configuration.SavedViews)
            undoSnapshot.Add(v.Clone());
    }

    // --- Preset field editing ------------------------------------------
    // Each edit: undo snapshot at the start of the gesture, live preview
    // while it's happening, save to disk when it ends.

    private void PresetFloat(SavedView view, string label, Func<float> get, Action<float> set, float speed, float min, float max, string format, float width = 110)
    {
        float value = get();
        ImGui.SetNextItemWidth(width);
        bool changed = ImGui.DragFloat(label, ref value, speed, min, max, format, ImGuiSliderFlags.AlwaysClamp);
        if (ImGui.IsItemActivated()) SnapshotForUndo();
        if (changed) set(value);
        if (changed) cameraController.PreviewEdit(view);
        else if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
        SaveWhenDone(changed);
    }

    private void PresetSlider(SavedView view, string label, Func<float> get, Action<float> set, float min, float max, string format)
    {
        float value = get();
        // Leave room for the longest slider label at the current font size.
        ImGui.SetNextItemWidth(-(ImGui.CalcTextSize("Horizontal angle").X + ImGui.GetStyle().ItemInnerSpacing.X * 2f + ImGui.GetStyle().ScrollbarSize));
        bool changed = ImGui.SliderFloat(label, ref value, min, max, format);
        if (ImGui.IsItemActivated()) SnapshotForUndo();
        if (changed) set(value);
        if (changed) cameraController.PreviewEdit(view);
        else if (ImGui.IsItemActive()) cameraController.NotifySliderAdjusted();
        SaveWhenDone(changed);
    }

    private void PresetDegrees(SavedView view, string label, Func<float> getRadians, Action<float> setRadians, float min, float max)
        => PresetSlider(view, label, () => getRadians() * (180f / MathF.PI), d => setRadians(d * (MathF.PI / 180f)), min, max, "%.0f deg");

    private bool PresetCheckbox(SavedView view, string label, bool value, Action<bool> set)
    {
        bool changed = ImGui.Checkbox(label, ref value);
        if (ImGui.IsItemActivated()) SnapshotForUndo();
        if (!changed) return false;
        set(value);
        cameraController.PreviewEdit(view);
        configuration.Save();
        return true;
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------

    public override void Draw()
    {
        FreeCamController.ResetVirtualButtons();

        DrawHeader();
        ImGui.Separator();

        if (!ImGui.BeginTabBar("##camcamtabs")) return;

        if (ImGui.BeginTabItem("Shots"))
        {
            DrawShotsTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Targeting"))
        {
            DrawTargetingTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Free Fly"))
        {
            DrawFreeFlyTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("General"))
        {
            DrawGeneralTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("Advanced"))
        {
            DrawAdvancedTab();
            ImGui.EndTabItem();
        }

        ImGui.EndTabBar();
        DrawFooter();
    }

    private static float FooterHeight => ImGui.GetFrameHeight() + ImGui.GetStyle().ItemSpacing.Y * 4f + 2f;

    private static void DrawFooter()
    {
        const string label = "Support on Ko-fi";
        var buttonSize = new Vector2(
            ImGui.CalcTextSize(label).X + (ImGui.GetStyle().FramePadding.X * 2f),
            ImGui.GetFrameHeight());

        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        // Right-aligned using the remaining content width (already accounts
        // for scrollbars/padding), so it never lands outside the window.
        var avail = ImGui.GetContentRegionAvail().X;
        if (avail > buttonSize.X)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + avail - buttonSize.X);

        // Ko-fi's own brand color, so it reads as a support link at a glance.
        using var buttonColor = ImRaii.PushColor(ImGuiCol.Button, new Vector4(1.0f, 0.369f, 0.357f, 1.0f));
        using var hoveredColor = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(1.0f, 0.45f, 0.44f, 1.0f));
        using var activeColor = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.85f, 0.28f, 0.27f, 1.0f));

        if (ImGui.Button(label, buttonSize))
            Util.OpenLink("https://ko-fi.com/grimmortaldread");
    }

    private void DrawHeader()
    {
        Checkbox("Enable CamCam", configuration.Enabled, v => configuration.Enabled = v);
        ImGui.SameLine(0, 20);
        Checkbox("Free Fly", configuration.FreeFly, v => configuration.FreeFly = v);

        ImGui.SameLine(0, 20);
        ImGui.TextUnformatted("Orbit:");
        var mode = configuration.FollowMode;
        ImGui.SameLine();
        if (ImGui.RadioButton("Myself", mode == CameraFollowMode.None)) SetFollowMode(CameraFollowMode.None);
        ImGui.SameLine();
        if (ImGui.RadioButton("/target", mode == CameraFollowMode.CurrentTarget)) SetFollowMode(CameraFollowMode.CurrentTarget);
        ImGui.SameLine();
        if (ImGui.RadioButton("Cycle", mode == CameraFollowMode.Cycle)) SetFollowMode(CameraFollowMode.Cycle);

        ImGui.TextColored(cameraController.IsEngaged ? ActiveColor : DisabledTextColor, cameraController.Status);

        if (ImGui.Button("< Prev")) cameraController.CyclePrevious();
        ImGui.SameLine();
        if (ImGui.Button("Next >")) cameraController.CycleNext();
        ImGui.SameLine();
        bool paused = cameraController.CyclePaused;
        ImGui.PushStyleColor(ImGuiCol.Button, paused ? new Vector4(0.7f, 0.3f, 0.3f, 1f) : new Vector4(0.25f, 0.5f, 0.3f, 1f));
        if (ImGui.Button(paused ? "Resume timers" : "Pause timers"))
            cameraController.CyclePaused = !paused;
        ImGui.PopStyleColor();
        ImGui.SameLine();
        ImGui.TextDisabled(cameraController.CurrentCycleName);
    }

    private void SetFollowMode(CameraFollowMode mode)
    {
        configuration.FollowMode = mode;
        configuration.Save();
    }

    // ------------------------------------------------------------------
    // Shots tab
    // ------------------------------------------------------------------

    private void DrawShotsTab()
    {
        // Playback
        if (configuration.FollowMode == CameraFollowMode.Cycle)
        {
            Checkbox("Use shots in Cycle mode", configuration.CycleUseSavedViews, v => configuration.CycleUseSavedViews = v);
            HelpMarker("Off: Cycle only changes who is filmed, using whatever shot was last loaded. On: Cycle also plays these shots. Myself and /target always play shots.");
        }

        Checkbox("Auto-advance shots", configuration.PresetCycleEnabled, v =>
        {
            configuration.PresetCycleEnabled = v;
            if (v) configuration.CycleUseSavedViews = true;
        });
        if (configuration.PresetCycleEnabled)
        {
            ImGui.SameLine();
            IntervalCombo("##presetinterval", CycleIntervalOptions, CycleIntervalLabels, configuration.PresetCycleIntervalSeconds, v => configuration.PresetCycleIntervalSeconds = v, 120);
            ImGui.SameLine();
            Checkbox("Random order", configuration.PresetCycleRandom, v => configuration.PresetCycleRandom = v);
        }

        Checkbox("Show the selected shot in the world", configuration.ShowShotOverlay, v => configuration.ShowShotOverlay = v);
        HelpMarker("While this window is open: yellow = where the camera looks, green = where it starts, blue = the route its motion takes. Easiest to see while holding a mouse button (camera handed back to you) or in Free Fly.");

        ImGui.Spacing();
        DrawPresetList();

        if (configuration.SavedViews.Count == 0)
        {
            Hint("No shots yet - click \"+ New\" to save the current camera as a shot.");
            return;
        }

        ImGui.Separator();
        ImGui.BeginChild("##preseteditor", new Vector2(0, -FooterHeight), false);
        DrawPresetEditor(configuration.SavedViews[selectedPresetIndex]);
        ImGui.EndChild();
    }

    private void DrawPresetList()
    {
        var views = configuration.SavedViews;
        int activeIndex = cameraController.CurrentViewIndex;

        // Follow the camera when it moves to another shot on its own.
        if (activeIndex != lastSeenActiveIndex)
        {
            lastSeenActiveIndex = activeIndex;
            if (activeIndex >= 0 && activeIndex < views.Count) selectedPresetIndex = activeIndex;
        }
        selectedPresetIndex = Math.Clamp(selectedPresetIndex, 0, Math.Max(0, views.Count - 1));

        // Button column sized from the widest label, so larger fonts/UI
        // scales don't clip the text; one button per row.
        string[] buttonLabels = { "+ New from live", "Duplicate", "Move up", "Move down", "Delete", "Undo", "Defaults" };
        float columnWidth = 0f;
        foreach (var label in buttonLabels)
            columnWidth = MathF.Max(columnWidth, ImGui.CalcTextSize(label).X);
        columnWidth += ImGui.GetStyle().FramePadding.X * 4f;
        var buttonSize = new Vector2(columnWidth, 0);

        float buttonsHeight = buttonLabels.Length * ImGui.GetFrameHeightWithSpacing() - ImGui.GetStyle().ItemSpacing.Y;
        float rowsHeight = Math.Clamp(views.Count, 3, 10) * ImGui.GetTextLineHeightWithSpacing() + ImGui.GetStyle().WindowPadding.Y * 2f;
        float listHeight = MathF.Max(buttonsHeight, rowsHeight);
        ImGui.BeginChild("##presetlist", new Vector2(-(columnWidth + ImGui.GetStyle().ItemSpacing.X), listHeight), true);
        for (int i = 0; i < views.Count; i++)
        {
            bool isActive = i == activeIndex && cameraController.ActiveView != null;
            var v = views[i];
            string tags = (v.FixedCameraPassBy ? " [tripod]" : "") + (v.TranslateInsteadOfPan ? " [strafe]" : "")
                + (v.PanEnabled || v.VerticalPanEnabled || v.ZoomPanEnabled || v.FovPanEnabled ? " [pan]" : "")
                + (v.HasPath ? " [path]" : "") + (v.RequireTargetSitting ? " [sitting]" : "");
            if (isActive) ImGui.PushStyleColor(ImGuiCol.Text, ActiveColor);
            if (ImGui.Selectable($"{i + 1}. {v.Name}{tags}##preset{i}", i == selectedPresetIndex))
            {
                selectedPresetIndex = i;
                cameraController.LoadView(v, i);
                lastSeenActiveIndex = i;
            }
            if (isActive) ImGui.PopStyleColor();
        }
        ImGui.EndChild();

        ImGui.SameLine();
        ImGui.BeginGroup();
        if (ImGui.Button("+ New from live", buttonSize))
        {
            SnapshotForUndo();
            views.Add(new SavedView
            {
                Name = $"Shot {views.Count + 1}",
                HorizontalRotation = configuration.HorizontalRotation,
                VerticalRotation = configuration.VerticalRotation,
                Zoom = configuration.Zoom,
                HeightOffset = configuration.FollowHeightOffset,
                MinZoom = configuration.FollowMinZoom,
                MaxAngleDegrees = configuration.FollowMaxAngleDegrees,
                HeightLockToGround = configuration.FollowHeightLockToGround,
                HeightGroundClearance = configuration.FollowHeightGroundClearance,
            });
            SelectAndLoad(views.Count - 1);
        }

        bool hasSelection = views.Count > 0;
        ImGui.BeginDisabled(!hasSelection);
        if (ImGui.Button("Duplicate", buttonSize))
        {
            SnapshotForUndo();
            var copy = views[selectedPresetIndex].Clone();
            copy.Name += " copy";
            views.Insert(selectedPresetIndex + 1, copy);
            SelectAndLoad(selectedPresetIndex + 1);
        }
        if (ImGui.Button("Move up", buttonSize) && selectedPresetIndex > 0)
            MovePreset(-1);
        if (ImGui.Button("Move down", buttonSize) && selectedPresetIndex < views.Count - 1)
            MovePreset(+1);
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.2f, 0.2f, 1f));
        if (ImGui.Button("Delete", buttonSize) && ImGui.GetIO().KeyShift)
        {
            SnapshotForUndo();
            views.RemoveAt(selectedPresetIndex);
            configuration.Save();
            if (views.Count > 0) SelectAndLoad(Math.Min(selectedPresetIndex, views.Count - 1));
        }
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shift+click to delete.");
        ImGui.EndDisabled();

        ImGui.BeginDisabled(undoSnapshot == null);
        if (ImGui.Button("Undo", buttonSize))
        {
            configuration.SavedViews.Clear();
            configuration.SavedViews.AddRange(undoSnapshot!);
            undoSnapshot = null;
            configuration.Save();
            if (views.Count > 0) SelectAndLoad(Math.Min(selectedPresetIndex, views.Count - 1));
        }
        ImGui.EndDisabled();
        if (ImGui.Button("Defaults", buttonSize) && ImGui.GetIO().KeyShift)
        {
            SnapshotForUndo();
            views.Clear();
            views.AddRange(Configuration.BuildDefaultSavedViews());
            SelectAndLoad(0);
        }
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shift+click to replace all shots with the shipped defaults (Undo works).");
        ImGui.EndGroup();
    }

    private void SelectAndLoad(int index)
    {
        selectedPresetIndex = index;
        lastSeenActiveIndex = index;
        cameraController.LoadView(configuration.SavedViews[index], index);
    }

    private void MovePreset(int direction)
    {
        SnapshotForUndo();
        var views = configuration.SavedViews;
        int target = selectedPresetIndex + direction;
        (views[selectedPresetIndex], views[target]) = (views[target], views[selectedPresetIndex]);
        SelectAndLoad(target);
    }

    private void DrawPresetEditor(SavedView view)
    {
        ImGui.PushID(selectedPresetIndex);

        string name = view.Name;
        ImGui.SetNextItemWidth(220);
        if (ImGui.InputText("Name", ref name, 64))
            view.Name = name;
        if (ImGui.IsItemActivated()) SnapshotForUndo();
        SaveWhenDone(false);

        ImGui.SameLine();
        if (ImGui.Button("Restart shot"))
            cameraController.LoadView(view, selectedPresetIndex);

        if (cameraController.ActiveView == null)
            ImGui.TextColored(WarningColor, "Shots aren't driving the camera in this mode - edits still update the live camera.");

        if (view.HasPath)
        {
            DrawPathEditor(view);
            DrawLensEditor(view);
            ImGui.PopID();
            return;
        }

        if (ImGui.CollapsingHeader("Framing", ImGuiTreeNodeFlags.DefaultOpen))
        {
            Hint("Horizontal is relative to the subject's facing: 0 = in front of them, 180 = behind. Ctrl+click a slider to type a value.");
            PresetDegrees(view, "Horizontal angle", () => view.HorizontalRotation, v => view.HorizontalRotation = v, -180f, 180f);
            PresetDegrees(view, "Vertical angle", () => view.VerticalRotation, v => view.VerticalRotation = v, -89f, 89f);
            PresetSlider(view, "Distance", () => view.Zoom, v => view.Zoom = v, 0.05f, 20f, "%.2f");
            PresetSlider(view, "Height offset", () => view.HeightOffset, v => view.HeightOffset = v, -5f, 5f, "%.2f");
            Hint("Height offset moves the point the camera looks at, relative to the subject's head height. Fly Up/Down keys adjust it live too.");
            PresetSlider(view, "Closest zoom", () => view.MinZoom, v => view.MinZoom = v, 0.01f, 2f, "%.2f");
            PresetSlider(view, "Steepest angle", () => view.MaxAngleDegrees, v => view.MaxAngleDegrees = v, 45f, 89f, "%.0f deg");
            Hint($"Live camera: zoom {cameraController.CurrentCameraZoom:0.00}, limits {cameraController.CurrentCameraMinZoom:0.00}-{cameraController.CurrentCameraMaxZoom:0.00}");
        }

        if (ImGui.CollapsingHeader("Collision and smoothing"))
        {
            PresetCheckbox(view, "Keep above ground", view.HeightLockToGround, v => view.HeightLockToGround = v);
            if (view.HeightLockToGround)
            {
                ImGui.SameLine();
                PresetFloat(view, "Clearance##ground", () => view.HeightGroundClearance, v => view.HeightGroundClearance = v, 0.05f, 0f, 5f, "%.2f", 70);
            }

            PresetCheckbox(view, "Avoid walls/objects", view.AvoidWallsAndObjects, v => view.AvoidWallsAndObjects = v);
            if (view.AvoidWallsAndObjects)
            {
                ImGui.SameLine();
                PresetFloat(view, "Buffer##walls", () => view.WallAvoidanceBuffer, v => view.WallAvoidanceBuffer = v, 0.02f, 0.05f, 3f, "%.2f", 70);
            }
            HelpMarker("Pulls the camera in front of any wall between it and the subject (game collision raycast). Not used for tripod/strafe shots.");

            PresetFloat(view, "Position smoothing (sec)", () => view.PositionSmoothingSeconds, v => view.PositionSmoothingSeconds = v, 0.02f, 0f, 2f, "%.2f", 90);
            HelpMarker("0 = exact. Above 0 eases toward the computed position to soften jitter near walls/uneven ground. Changing subject or shot is always a hard cut.");
        }

        if (ImGui.CollapsingHeader("When to use this shot"))
        {
            PresetCheckbox(view, "Only when the subject is sitting", view.RequireTargetSitting, v => view.RequireTargetSitting = v);
            if (view.RequireTargetSitting)
            {
                ImGui.SameLine();
                if (ImGui.RadioButton("Any##sit", view.RequiredSittingType == SittingRequirement.Any)) { SnapshotForUndo(); view.RequiredSittingType = SittingRequirement.Any; configuration.Save(); }
                ImGui.SameLine();
                if (ImGui.RadioButton("Ground##sit", view.RequiredSittingType == SittingRequirement.Ground)) { SnapshotForUndo(); view.RequiredSittingType = SittingRequirement.Ground; configuration.Save(); }
                ImGui.SameLine();
                if (ImGui.RadioButton("Chair/bench##sit", view.RequiredSittingType == SittingRequirement.Furniture)) { SnapshotForUndo(); view.RequiredSittingType = SittingRequirement.Furniture; configuration.Save(); }
            }
            Hint("Auto-advance skips this shot for anyone who doesn't match. \"Ground\" can also match other looping emotes such as /doze.");
        }

        if (ImGui.CollapsingHeader("Motion", ImGuiTreeNodeFlags.DefaultOpen))
            DrawMotionEditor(view);

        DrawLensEditor(view);

        ImGui.PopID();
    }

    private void DrawLensEditor(SavedView view)
    {
        if (!ImGui.CollapsingHeader("Lens and transition", ImGuiTreeNodeFlags.DefaultOpen)) return;

        bool customFov = view.FieldOfViewDegrees > 0f;
        if (PresetCheckbox(view, "Custom field of view", customFov, v => view.FieldOfViewDegrees = v ? MathF.Round(cameraController.GameFovDegrees) : 0f))
            customFov = view.FieldOfViewDegrees > 0f;
        if (customFov)
        {
            ImGui.SameLine();
            PresetFloat(view, "deg##fov", () => view.FieldOfViewDegrees, v => view.FieldOfViewDegrees = v, 0.2f, 5f, 120f, "%.1f", 90);
        }
        HelpMarker($"Lower = more zoomed-in, flatter look; higher = wider. The game's own is currently {cameraController.GameFovDegrees:0.0} deg.");

        PresetCheckbox(view, "FOV pan (zoom lens)", view.FovPanEnabled, v => view.FovPanEnabled = v);
        if (view.FovPanEnabled)
        {
            ImGui.Indent();
            PresetFloat(view, "To (deg)##fovto", () => view.FovPanToDegrees, v => view.FovPanToDegrees = v, 0.2f, 5f, 120f, "%.1f", 80);
            ImGui.SameLine();
            PresetFloat(view, "Speed (deg/s)##fovspeed", () => view.FovPanSpeedDegreesPerSecond, v => view.FovPanSpeedDegreesPerSecond = v, 0.1f, 0.1f, 60f, "%.1f", 80);
            ImGui.SameLine();
            PresetFloat(view, "Delay (s)##fovdelay", () => view.FovPanStartDelaySeconds, v => view.FovPanStartDelaySeconds = v, 0.1f, 0f, 30f, "%.1f", 60);
            float start = view.FieldOfViewDegrees > 0f ? view.FieldOfViewDegrees : cameraController.GameFovDegrees;
            Hint(DurationHint(view.FovPanToDegrees - start, view.FovPanSpeedDegreesPerSecond, view.FovPanStartDelaySeconds) + " Combine with a zoom pan the other way for a dolly-zoom.");
            ImGui.Unindent();
        }

        PresetFloat(view, "Roll (deg)##roll", () => view.RollDegrees, v => view.RollDegrees = v, 0.2f, -45f, 45f, "%.1f", 90);
        HelpMarker("Tilts the horizon (Dutch angle). Experimental - if the picture doesn't tilt in game, this game version doesn't honour it.");

        PresetFloat(view, "Transition into this shot (s)##transition", () => view.TransitionSeconds, v => view.TransitionSeconds = v, 0.05f, 0f, 10f, "%.2f", 90);
        HelpMarker("0 = hard cut. Above 0 = the camera glides from the previous shot over this many seconds when switching to this one on the same subject. A new subject is always a cut.");
    }

    private void DrawPathEditor(SavedView view)
    {
        if (!ImGui.CollapsingHeader("Recorded path", ImGuiTreeNodeFlags.DefaultOpen)) return;

        float duration = view.Path[^1].Time;
        Hint($"{view.Path.Count} keyframes, {duration:0.0}s recorded in Free Fly. Plays at {duration / MathF.Max(view.PathPlaybackSpeed, 0.05f):0.0}s.");

        PresetFloat(view, "Playback speed##pathspeed", () => view.PathPlaybackSpeed, v => view.PathPlaybackSpeed = v, 0.01f, 0.05f, 5f, "%.2fx", 90);
        PresetCheckbox(view, "Loop", view.PathLoop, v => view.PathLoop = v);
        PresetCheckbox(view, "Ease in/out", view.EaseInOut, v => view.EaseInOut = v);
        PresetCheckbox(view, "Replay around whoever is being filmed", view.PathRelativeToSubject, v => view.PathRelativeToSubject = v);
        HelpMarker("On: the same move relative to the current subject's position and facing. Off: replays at the exact spot in the world it was recorded.");
        PresetCheckbox(view, "Next shot when the path finishes", view.PanAdvanceCycleOnComplete, v => view.PanAdvanceCycleOnComplete = v);

        ImGui.Spacing();
        ImGui.PushStyleColor(ImGuiCol.Button, new Vector4(0.55f, 0.2f, 0.2f, 1f));
        if (ImGui.Button("Remove path (turn into a normal shot)") && ImGui.GetIO().KeyShift)
        {
            SnapshotForUndo();
            view.Path.Clear();
            cameraController.LoadView(view, selectedPresetIndex);
        }
        ImGui.PopStyleColor();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip("Shift+click. Undo works.");
    }

    private void DrawMotionEditor(SavedView view)
    {
        PresetCheckbox(view, "Tripod (camera stays put, only rotation moves)", view.FixedCameraPassBy, v => view.FixedCameraPassBy = v);
        HelpMarker("The camera is placed once using the framing above, then stays fixed while the subject moves. Combine with horizontal/vertical pan to sweep. Zoom pan has no effect here.");

        PresetCheckbox(view, "Strafe / dolly (camera moves, facing locked)", view.TranslateInsteadOfPan, v => view.TranslateInsteadOfPan = v);
        if (view.TranslateInsteadOfPan)
        {
            ImGui.Indent();
            DrawStartEnd(view, "Right", () => view.TranslateStartRight, v => view.TranslateStartRight = v, () => view.TranslateEndRight, v => view.TranslateEndRight = v);
            DrawStartEnd(view, "Up", () => view.TranslateStartUp, v => view.TranslateStartUp = v, () => view.TranslateEndUp, v => view.TranslateEndUp = v);
            DrawStartEnd(view, "Forward", () => view.TranslateStartForward, v => view.TranslateStartForward = v, () => view.TranslateEndForward, v => view.TranslateEndForward = v);
            PresetFloat(view, "Speed (units/s)##tr", () => view.TranslateSpeed, v => view.TranslateSpeed = v, 0.1f, 0.1f, 30f, "%.1f", 80);
            ImGui.SameLine();
            PresetFloat(view, "Delay (s)##tr", () => view.TranslateStartDelaySeconds, v => view.TranslateStartDelaySeconds = v, 0.1f, 0f, 30f, "%.1f", 70);
            Hint("Offsets are relative to the placed camera: Right/Forward follow its facing, Up is always straight up. Rotation pans are ignored while this is on.");
            ImGui.Unindent();
        }

        ImGui.Spacing();
        PresetCheckbox(view, "Horizontal pan", view.PanEnabled, v => view.PanEnabled = v);
        if (view.PanEnabled)
        {
            ImGui.Indent();
            PresetFloat(view, "To (deg)##h", () => view.PanToDegrees, v => view.PanToDegrees = v, 1f, -180f, 180f, "%.0f", 80);
            ImGui.SameLine();
            PresetFloat(view, "Speed (deg/s)##h", () => view.PanSpeedDegreesPerSecond, v => view.PanSpeedDegreesPerSecond = v, 0.5f, 0.5f, 60f, "%.1f", 80);
            ImGui.SameLine();
            PresetFloat(view, "Delay (s)##h", () => view.HorizontalPanStartDelaySeconds, v => view.HorizontalPanStartDelaySeconds = v, 0.1f, 0f, 30f, "%.1f", 60);
            Hint(DurationHint(ShortestDelta(view.HorizontalRotation * 180f / MathF.PI, view.PanToDegrees), view.PanSpeedDegreesPerSecond, view.HorizontalPanStartDelaySeconds));
            ImGui.Unindent();
        }

        PresetCheckbox(view, "Vertical pan", view.VerticalPanEnabled, v => view.VerticalPanEnabled = v);
        if (view.VerticalPanEnabled)
        {
            ImGui.Indent();
            PresetFloat(view, "To (deg)##v", () => view.VerticalPanToDegrees, v => view.VerticalPanToDegrees = v, 1f, -89f, 89f, "%.0f", 80);
            ImGui.SameLine();
            PresetFloat(view, "Speed (deg/s)##v", () => view.VerticalPanSpeedDegreesPerSecond, v => view.VerticalPanSpeedDegreesPerSecond = v, 0.5f, 0.5f, 60f, "%.1f", 80);
            ImGui.SameLine();
            PresetFloat(view, "Delay (s)##v", () => view.VerticalPanStartDelaySeconds, v => view.VerticalPanStartDelaySeconds = v, 0.1f, 0f, 30f, "%.1f", 60);
            Hint(DurationHint(view.VerticalPanToDegrees - view.VerticalRotation * 180f / MathF.PI, view.VerticalPanSpeedDegreesPerSecond, view.VerticalPanStartDelaySeconds));
            ImGui.Unindent();
        }

        PresetCheckbox(view, "Zoom pan (dolly in/out)", view.ZoomPanEnabled, v => view.ZoomPanEnabled = v);
        if (view.ZoomPanEnabled)
        {
            ImGui.Indent();
            PresetFloat(view, "To##z", () => view.ZoomPanToValue, v => view.ZoomPanToValue = v, 0.05f, 0.05f, 20f, "%.2f", 80);
            ImGui.SameLine();
            PresetFloat(view, "Speed (/s)##z", () => view.ZoomPanSpeed, v => view.ZoomPanSpeed = v, 0.05f, 0.05f, 20f, "%.2f", 80);
            ImGui.SameLine();
            PresetFloat(view, "Delay (s)##z", () => view.ZoomPanStartDelaySeconds, v => view.ZoomPanStartDelaySeconds = v, 0.1f, 0f, 30f, "%.1f", 60);
            Hint(DurationHint(view.ZoomPanToValue - view.Zoom, view.ZoomPanSpeed, view.ZoomPanStartDelaySeconds));
            ImGui.Unindent();
        }

        bool anyMotion = view.PanEnabled || view.VerticalPanEnabled || view.ZoomPanEnabled || view.TranslateInsteadOfPan;
        if (!anyMotion) return;

        ImGui.Spacing();
        PresetCheckbox(view, "Ease in/out", view.EaseInOut, v => view.EaseInOut = v);
        HelpMarker("Accelerates and decelerates smoothly instead of starting and stopping abruptly. Total duration is unchanged.");
        PresetCheckbox(view, "Go back to the start afterwards", view.PanReturnBeforeAdvance, v => view.PanReturnBeforeAdvance = v);
        PresetCheckbox(view, "Next shot when the motion finishes", view.PanAdvanceCycleOnComplete, v => view.PanAdvanceCycleOnComplete = v);
        HelpMarker("Waits for every enabled motion to finish, then advances (in Cycle with shots, the subject changes too). The auto-advance timers hold off while it's running.");
    }

    private void DrawStartEnd(SavedView view, string axis, Func<float> getStart, Action<float> setStart, Func<float> getEnd, Action<float> setEnd)
    {
        PresetFloat(view, $"##start{axis}", getStart, setStart, 0.1f, -50f, 50f, "%.1f", 70);
        ImGui.SameLine();
        ImGui.TextUnformatted("->");
        ImGui.SameLine();
        PresetFloat(view, $"{axis}##end{axis}", getEnd, setEnd, 0.1f, -50f, 50f, "%.1f", 70);
    }

    private static float ShortestDelta(float from, float to)
    {
        float d = (to - from) % 360f;
        if (d > 180f) d -= 360f;
        else if (d < -180f) d += 360f;
        return d;
    }

    private static string DurationHint(float range, float speed, float delay)
    {
        float seconds = speed > 0f ? MathF.Abs(range) / speed : 0f;
        return delay > 0f ? $"Takes {seconds:0.0}s after a {delay:0.0}s delay." : $"Takes {seconds:0.0}s.";
    }

    // ------------------------------------------------------------------
    // Targeting tab
    // ------------------------------------------------------------------

    private void DrawTargetingTab()
    {
        Hint("These settings only affect Cycle mode.");

        Checkbox("Auto-advance subject", configuration.CycleAutoAdvance, v => configuration.CycleAutoAdvance = v);
        if (configuration.CycleAutoAdvance)
        {
            ImGui.SameLine();
            IntervalCombo("##cycleinterval", CycleIntervalOptions, CycleIntervalLabels, configuration.CycleIntervalSeconds, v => configuration.CycleIntervalSeconds = v, 120);
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Who can be filmed");

        ImGui.TextUnformatted("Gender:");
        var gender = configuration.CycleGenderFilterMode;
        ImGui.SameLine();
        if (ImGui.RadioButton("Any##g", gender == CycleGenderFilter.Any)) { configuration.CycleGenderFilterMode = CycleGenderFilter.Any; configuration.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("Male##g", gender == CycleGenderFilter.MaleOnly)) { configuration.CycleGenderFilterMode = CycleGenderFilter.MaleOnly; configuration.Save(); }
        ImGui.SameLine();
        if (ImGui.RadioButton("Female##g", gender == CycleGenderFilter.FemaleOnly)) { configuration.CycleGenderFilterMode = CycleGenderFilter.FemaleOnly; configuration.Save(); }

        Checkbox("Exclude myself", configuration.CycleExcludeSelf, v => configuration.CycleExcludeSelf = v);
        Checkbox("Exclude lalafells", configuration.ExcludeLalafells, v => configuration.ExcludeLalafells = v);
        Checkbox("Exclude sitting players", configuration.ExcludeSitting, v => configuration.ExcludeSitting = v);
        HelpMarker("Ignored while any shot is set to \"Only when the subject is sitting\" - otherwise that shot could never be used.");
        Checkbox("Exclude crafters", configuration.ExcludeCrafters, v => configuration.ExcludeCrafters = v);
        Checkbox("Only players I can see (no walls in between)", configuration.CycleRequireLineOfSight, v => configuration.CycleRequireLineOfSight = v);

        float maxDistance = configuration.CycleMaxDistance;
        ImGui.SetNextItemWidth(200);
        bool changed = ImGui.SliderFloat("Max distance", ref maxDistance, 0f, 100f, maxDistance <= 0f ? "Unlimited" : "%.0f yalms");
        if (changed) configuration.CycleMaxDistance = maxDistance;
        SaveWhenDone(changed);

        ImGui.Separator();
        ImGui.TextUnformatted("Exclude anyone wearing...");
        Hint("Matches gear by its MODEL id (what the game stores for appearance), not the item id on Garland Tools. Easiest: /target someone wearing the piece and click its slot below.");

        var equipment = cameraController.GetTargetEquipment();
        if (equipment.Count == 0)
        {
            ImGui.TextDisabled("(target a player to pick from their gear)");
        }
        else
        {
            foreach (var (slot, modelId) in equipment)
            {
                if (modelId == 0) continue;
                bool already = configuration.ExcludedEquipmentItemIds.Contains(modelId);
                ImGui.BeginDisabled(already);
                if (ImGui.SmallButton($"{slot}: {modelId}##eq{slot}"))
                {
                    configuration.ExcludedEquipmentItemIds.Add(modelId);
                    configuration.Save();
                }
                ImGui.EndDisabled();
                ImGui.SameLine();
            }
            ImGui.NewLine();
        }

        ImGui.SetNextItemWidth(120);
        ImGui.InputInt("##newExcludedId", ref pendingExcludedItemId, 0, 0);
        ImGui.SameLine();
        if (ImGui.Button("Add id") && pendingExcludedItemId is > 0 and <= ushort.MaxValue)
        {
            ushort id = (ushort)pendingExcludedItemId;
            if (!configuration.ExcludedEquipmentItemIds.Contains(id))
            {
                configuration.ExcludedEquipmentItemIds.Add(id);
                configuration.Save();
            }
            pendingExcludedItemId = 0;
        }

        int remove = -1;
        for (int i = 0; i < configuration.ExcludedEquipmentItemIds.Count; i++)
        {
            if (ImGui.SmallButton($"x##rm{i}")) remove = i;
            ImGui.SameLine();
            ImGui.TextUnformatted(configuration.ExcludedEquipmentItemIds[i].ToString());
        }
        if (remove >= 0)
        {
            configuration.ExcludedEquipmentItemIds.RemoveAt(remove);
            configuration.Save();
        }
    }

    // ------------------------------------------------------------------
    // Free Fly tab
    // ------------------------------------------------------------------

    private void DrawFreeFlyTab()
    {
        bool numLockOn = FreeCamController.IsNumLockOn;
        ImGui.TextColored(numLockOn ? ActiveColor : WarningColor,
            numLockOn ? "Num Lock: ON" : "Num Lock: OFF - numpad keys won't work as configured.");

        float speed = configuration.FreeFlySpeed;
        ImGui.SetNextItemWidth(200);
        bool changed = ImGui.SliderFloat("Fly speed", ref speed, 0.5f, 20f);
        if (changed) configuration.FreeFlySpeed = speed;
        SaveWhenDone(changed);

        float turnDeg = configuration.FreeFlyTurnSpeed * (180f / MathF.PI);
        ImGui.SetNextItemWidth(200);
        changed = ImGui.SliderFloat("Turn speed", ref turnDeg, 30f, 360f, "%.0f deg/s");
        if (changed) configuration.FreeFlyTurnSpeed = turnDeg * (MathF.PI / 180f);
        SaveWhenDone(changed);

        KeyDropdown("Fast (hold)", configuration.FlyFastModifierKey, v => configuration.FlyFastModifierKey = v, 100);
        ImGui.SameLine();
        float fast = configuration.FlyFastMultiplier;
        ImGui.SetNextItemWidth(70);
        changed = ImGui.DragFloat("x##fast", ref fast, 0.1f, 1f, 10f, "%.1f");
        if (changed) configuration.FlyFastMultiplier = fast;
        SaveWhenDone(changed);

        KeyDropdown("Slow (hold)", configuration.FlySlowModifierKey, v => configuration.FlySlowModifierKey = v, 100);
        ImGui.SameLine();
        float slow = configuration.FlySlowMultiplier;
        ImGui.SetNextItemWidth(70);
        changed = ImGui.DragFloat("x##slow", ref slow, 0.01f, 0.05f, 1f, "%.2f");
        if (changed) configuration.FlySlowMultiplier = slow;
        SaveWhenDone(changed);

        float ffFov = configuration.FreeFlyFovDegrees;
        ImGui.SetNextItemWidth(200);
        changed = ImGui.SliderFloat("Field of view", ref ffFov, 0f, 120f, ffFov <= 0f ? "Game default" : "%.1f deg");
        if (changed) configuration.FreeFlyFovDegrees = ffFov < 5f ? 0f : ffFov;
        SaveWhenDone(changed);

        ImGui.Separator();
        ImGui.TextUnformatted("Record a camera path");
        if (cameraController.IsRecording)
        {
            ImGui.TextColored(WarningColor, $"Recording... {cameraController.RecordingSeconds:0.0}s, {cameraController.RecordingKeyframes} keyframes");
            if (ImGui.Button("Stop and save as a shot")) cameraController.StopRecording(save: true);
            ImGui.SameLine();
            if (ImGui.Button("Discard")) cameraController.StopRecording(save: false);
        }
        else
        {
            ImGui.BeginDisabled(!configuration.FreeFly || !configuration.Enabled);
            if (ImGui.Button("Start recording")) cameraController.StartRecording();
            ImGui.EndDisabled();
            ImGui.SameLine();
            KeyDropdown("Record key", configuration.RecordToggleKeyName, v => configuration.RecordToggleKeyName = v, 100);
        }
        Hint("Fly the move you want, then stop: it's saved as a new \"Path\" shot that replays smoothly - around whoever is being filmed, relative to where the subject stood when you started. Works in Free Fly only; /camcam record also toggles it.");
        ImGui.Separator();

        KeyDropdown("Toggle Free Fly key", configuration.FreeFlyToggleKeyName, v => configuration.FreeFlyToggleKeyName = v);
        HelpMarker("Also turns CamCam on.");

        if (ImGui.CollapsingHeader("Movement keys"))
        {
            KeyDropdown("Forward", configuration.FlyForwardKey, v => configuration.FlyForwardKey = v, 100);
            ImGui.SameLine();
            KeyDropdown("Back", configuration.FlyBackKey, v => configuration.FlyBackKey = v, 100);
            KeyDropdown("Strafe L", configuration.FlyLeftKey, v => configuration.FlyLeftKey = v, 100);
            ImGui.SameLine();
            KeyDropdown("Strafe R", configuration.FlyRightKey, v => configuration.FlyRightKey = v, 100);
            KeyDropdown("Up", configuration.FlyUpKey, v => configuration.FlyUpKey = v, 100);
            ImGui.SameLine();
            KeyDropdown("Down", configuration.FlyDownKey, v => configuration.FlyDownKey = v, 100);
            KeyDropdown("Turn L", configuration.FlyTurnLeftKey, v => configuration.FlyTurnLeftKey = v, 100);
            ImGui.SameLine();
            KeyDropdown("Turn R", configuration.FlyTurnRightKey, v => configuration.FlyTurnRightKey = v, 100);
            KeyDropdown("Look up", configuration.FlyLookUpKey, v => configuration.FlyLookUpKey = v, 100);
            ImGui.SameLine();
            KeyDropdown("Look down", configuration.FlyLookDownKey, v => configuration.FlyLookDownKey = v, 100);
        }

        Checkbox("Don't fly below the ground", configuration.FreeFlyLockToGround, v => configuration.FreeFlyLockToGround = v);
        if (configuration.FreeFlyLockToGround)
        {
            ImGui.SameLine();
            float clearance = configuration.FreeFlyGroundClearance;
            ImGui.SetNextItemWidth(80);
            changed = ImGui.DragFloat("Clearance##ff", ref clearance, 0.05f, 0f, 3f, "%.2f");
            if (changed) configuration.FreeFlyGroundClearance = clearance;
            SaveWhenDone(changed);
        }

        ImGui.Separator();
        DrawFreeFlyDPad();

        float x = cameraController.FreeFlyPositionX, y = cameraController.FreeFlyPositionY, z = cameraController.FreeFlyPositionZ;
        ImGui.SetNextItemWidth(200);
        if (ImGui.DragFloat("X", ref x, 0.1f)) cameraController.FreeFlyPositionX = x;
        ImGui.SetNextItemWidth(200);
        if (ImGui.DragFloat("Y (height)", ref y, 0.1f)) cameraController.FreeFlyPositionY = y;
        ImGui.SetNextItemWidth(200);
        if (ImGui.DragFloat("Z", ref z, 0.1f)) cameraController.FreeFlyPositionZ = z;
        Hint("Free Fly's raw world position.");
    }

    /// <summary>On-screen equivalents of the fly keys - hold a button to move.</summary>
    private static void DrawFreeFlyDPad()
    {
        var size = new Vector2(26, 26);

        ImGui.BeginGroup();
        ImGui.TextDisabled("Move");
        ImGui.Dummy(size); ImGui.SameLine();
        ImGui.Button("^##fwd", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualForwardHeld = true;
        ImGui.Button("<##sl", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualStrafeLeftHeld = true;
        ImGui.SameLine(); ImGui.Dummy(size); ImGui.SameLine();
        ImGui.Button(">##sr", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualStrafeRightHeld = true;
        ImGui.Dummy(size); ImGui.SameLine();
        ImGui.Button("v##back", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualBackHeld = true;
        ImGui.EndGroup();

        ImGui.SameLine(0, 24);
        ImGui.BeginGroup();
        ImGui.TextDisabled("Up / Turn");
        ImGui.Dummy(size); ImGui.SameLine();
        ImGui.Button("^##up", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualUpHeld = true;
        ImGui.Button("<##tl", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualTurnLeftHeld = true;
        ImGui.SameLine(); ImGui.Dummy(size); ImGui.SameLine();
        ImGui.Button(">##tr", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualTurnRightHeld = true;
        ImGui.Dummy(size); ImGui.SameLine();
        ImGui.Button("v##down", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualDownHeld = true;
        ImGui.EndGroup();

        ImGui.SameLine(0, 24);
        ImGui.BeginGroup();
        ImGui.TextDisabled("Look");
        ImGui.Button("^##lu", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualLookUpHeld = true;
        ImGui.Dummy(size);
        ImGui.Button("v##ld", size);
        if (ImGui.IsItemActive()) FreeCamController.VirtualLookDownHeld = true;
        ImGui.EndGroup();
    }

    // ------------------------------------------------------------------
    // General tab
    // ------------------------------------------------------------------

    private void DrawGeneralTab()
    {
        ImGui.TextUnformatted("When the camera takes over");

        IntervalCombo("Wait for idle", IdleThresholdOptions, IdleThresholdLabels, configuration.IdleThresholdSeconds, v => configuration.IdleThresholdSeconds = v, 160);
        HelpMarker("Orbit modes only engage after this long idle. Free Fly engages immediately.");

        ImGui.TextUnformatted("Idle means:");
        ImGui.SameLine();
        if (ImGui.RadioButton("My character hasn't moved", configuration.IdleDetection == IdleDetectionMode.CharacterMovement))
        {
            configuration.IdleDetection = IdleDetectionMode.CharacterMovement;
            configuration.Save();
        }
        ImGui.SameLine();
        if (ImGui.RadioButton("No keyboard/mouse input", configuration.IdleDetection == IdleDetectionMode.AnyInput))
        {
            configuration.IdleDetection = IdleDetectionMode.AnyInput;
            configuration.Save();
        }
        HelpMarker("\"No input\" matches how the game's own AFK camera decides. CamCam's own keys, its window, and the UI-toggle key it presses never count.");
        if (configuration.IdleDetection == IdleDetectionMode.AnyInput)
        {
            ImGui.Indent();
            Checkbox("Gamepad counts as input", configuration.IdleGamepadCounts, v => configuration.IdleGamepadCounts = v);
            Checkbox("Moving the mouse counts as input", configuration.IdleMouseMovementCounts, v => configuration.IdleMouseMovementCounts = v);
            if (configuration.IdleMouseMovementCounts)
            {
                ImGui.SameLine();
                float threshold = configuration.IdleMouseMovementThresholdPixels;
                ImGui.SetNextItemWidth(120);
                bool moved = ImGui.SliderFloat("##mousethreshold", ref threshold, 5f, 400f, "after %.0f px");
                if (moved) configuration.IdleMouseMovementThresholdPixels = threshold;
                SaveWhenDone(moved);
            }
            Hint("Keys, mouse clicks and the scroll wheel always count. Off by default, so nudging the mouse doesn't end the shot.");
            ImGui.Unindent();
        }

        Checkbox("Hand the camera back during combat", configuration.DisengageInCombat, v => configuration.DisengageInCombat = v);
        Hint("Cutscenes, zone changes and gpose always hand the camera back.");

        if (gameConfig.TryGet(SystemConfigOption.IdlingCameraAFK, out uint idlingCameraAfkRaw))
        {
            bool blocked = idlingCameraAfkRaw == 0;
            if (ImGui.Checkbox("Turn off FFXIV's own AFK camera", ref blocked))
                gameConfig.Set(SystemConfigOption.IdlingCameraAFK, blocked ? 0u : 1u);
            HelpMarker("Same as the Auto-AFK idling camera option in System Configuration. Recommended - otherwise the game's camera fights CamCam's.");
        }

        ImGui.Separator();
        Checkbox("Hide the game UI while CamCam has the camera", configuration.AutoHideUi, v => configuration.AutoHideUi = v);
        if (configuration.AutoHideUi)
        {
            KeyDropdown("Your \"Toggle UI Display Mode\" key", configuration.UiToggleKeyName, v => configuration.UiToggleKeyName = v);
            Hint("Must match System > Keybind > System > Toggle UI Display Mode in-game (default Scroll Lock). Only single keys are supported - no modifiers.");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Keybinds");
        KeyDropdown("Toggle CamCam", configuration.ToggleCamCamKeyName, v => configuration.ToggleCamCamKeyName = v);
        KeyDropdown("Toggle numpad blocking", configuration.NumpadBlockToggleKeyName, v => configuration.NumpadBlockToggleKeyName = v);
        HelpMarker("While CamCam is using the numpad, it's blocked from reaching the game (hotbars). This lets numpad through temporarily.");
        Hint("Keys only work while the game window is focused and you aren't typing. Also available: /camcam on|off|toggle|next|prev|pause|fly|record.");
    }

    // ------------------------------------------------------------------
    // Advanced tab
    // ------------------------------------------------------------------

    private void DrawAdvancedTab()
    {
        Checkbox("Drop native camera target (recommended)", configuration.ExperimentalBypassTargetHook, v => configuration.ExperimentalBypassTargetHook = v);
        HelpMarker("On: CamCam computes position/rotation itself and leaves the game's camera target alone, like Free Fly. Off: also redirects the game's camera target to the subject.");

        Checkbox("Verbose logging", configuration.VerboseLogging, v => configuration.VerboseLogging = v);
        HelpMarker("Writes diagnostics (status every 2s, shake detection, cycle details, target equipment) to /xllog. Leave off normally.");

        ImGui.Separator();
        ImGui.TextUnformatted("Live values (what the active shot is writing right now)");
        Hint($"H {configuration.HorizontalRotation * 180f / MathF.PI:0.0} deg, V {configuration.VerticalRotation * 180f / MathF.PI:0.0} deg, distance {configuration.Zoom:0.00}, height {configuration.FollowHeightOffset:0.00}");
        Hint($"Native camera: zoom {cameraController.CurrentCameraZoom:0.00}, limits {cameraController.CurrentCameraMinZoom:0.00}-{cameraController.CurrentCameraMaxZoom:0.00}");
    }
}
