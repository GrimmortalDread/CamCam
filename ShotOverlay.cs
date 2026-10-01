using System.Collections.Generic;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Plugin.Services;

namespace CamCam;

/// <summary>
/// Draws the shot being edited into the game world while the settings
/// window is open: the point the camera looks at, where the camera starts,
/// and the route its motion (pan sweep, strafe, recorded path) takes.
/// Most useful while the camera is handed back to you (hold a mouse
/// button) or in Free Fly, where you can look at the shot from outside.
/// </summary>
public sealed class ShotOverlay
{
    private static readonly uint LookAtColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.85f, 0.2f, 1f));
    private static readonly uint CameraColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.35f, 0.9f, 0.4f, 1f));
    private static readonly uint MotionColor = ImGui.ColorConvertFloat4ToU32(new Vector4(0.3f, 0.75f, 1f, 0.9f));
    private static readonly uint SightColor = ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 1f, 1f, 0.35f));

    private readonly Configuration configuration;
    private readonly CameraController cameraController;
    private readonly SettingsWindow settingsWindow;
    private readonly IGameGui gameGui;

    public ShotOverlay(Configuration configuration, CameraController cameraController, SettingsWindow settingsWindow, IGameGui gameGui)
    {
        this.configuration = configuration;
        this.cameraController = cameraController;
        this.settingsWindow = settingsWindow;
        this.gameGui = gameGui;
    }

    public void Draw()
    {
        if (!configuration.ShowShotOverlay || !settingsWindow.IsOpen) return;

        var view = settingsWindow.SelectedView;
        if (view == null) return;

        var preview = cameraController.BuildPreview(view);
        if (preview == null) return;

        var draw = ImGui.GetBackgroundDrawList();

        // Motion route, segment by segment so off-screen parts are skipped.
        Vector2? previous = null;
        foreach (var point in preview.Motion)
        {
            Vector2? current = gameGui.WorldToScreen(point, out var screen) ? screen : null;
            if (previous.HasValue && current.HasValue)
                draw.AddLine(previous.Value, current.Value, MotionColor, 2.5f);
            previous = current;
        }

        if (preview.Motion.Count > 0 && gameGui.WorldToScreen(preview.Motion[^1], out var end))
            draw.AddCircleFilled(end, 5f, MotionColor);

        bool lookAtVisible = gameGui.WorldToScreen(preview.LookAt, out var lookAt);
        bool cameraVisible = gameGui.WorldToScreen(preview.CameraStart, out var camera);

        if (lookAtVisible && cameraVisible)
            draw.AddLine(camera, lookAt, SightColor, 1.5f);

        if (lookAtVisible)
            draw.AddCircleFilled(lookAt, 5f, LookAtColor);

        if (cameraVisible)
        {
            draw.AddCircle(camera, 9f, CameraColor, 0, 2.5f);
            draw.AddText(camera + new Vector2(12f, -8f), CameraColor, view.Name);
        }
    }
}
