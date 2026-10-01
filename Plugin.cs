using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;

namespace CamCam;

public sealed class Plugin : IDalamudPlugin
{
    public string Name => "CamCam";

    private const string SettingsCommand = "/camcam";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static ITargetManager TargetManager { get; private set; } = null!;
    [PluginService] internal static IObjectTable ObjectTable { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IGameInteropProvider GameInteropProvider { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameConfig GameConfig { get; private set; } = null!;
    [PluginService] internal static IKeyState KeyState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    public Configuration Configuration { get; }
    public WindowSystem WindowSystem { get; } = new("CamCam");

    private readonly SettingsWindow settingsWindow;
    private readonly WorldCameraTargetHook targetHook;
    private readonly WorldCameraPositionHook positionHook;
    private readonly FreeCamController freeCam;
    private readonly FlyKeyBlocker keyBlocker;
    private readonly CameraController cameraController;

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        // v1 -> v2: fly keys moved from arrows/brackets to numpad defaults.
        // A saved config always overrides the C# default values above, so
        // without this, everyone who already had CamCam configured would
        // stay on the old key names forever.
        if (Configuration.Version < 2)
        {
            Configuration.FlyForwardKey = "Numpad8";
            Configuration.FlyBackKey = "Numpad2";
            Configuration.FlyLeftKey = "Numpad7";
            Configuration.FlyRightKey = "Numpad9";
            Configuration.FlyUpKey = "Numpad0";
            Configuration.FlyDownKey = "Numpad.";
            Configuration.FlyTurnLeftKey = "Numpad4";
            Configuration.FlyTurnRightKey = "Numpad6";
            Configuration.FlyLookUpKey = "Numpad+";
            Configuration.FlyLookDownKey = "Numpad-";
            Configuration.Version = 2;
            Configuration.Save();
            Log.Information("[CamCam] Migrated fly-key bindings to numpad defaults (v1 -> v2).");
        }

        if (Configuration.Version < 3)
        {
            if (Configuration.UiToggleKeyName == "R")
                Configuration.UiToggleKeyName = "Scroll Lock";
            Configuration.Version = 3;
            Configuration.Save();
        }

        // Only seeds on a genuinely fresh install - never touches
        // SavedViews once you've added or removed anything yourself.
        if (Configuration.SavedViews.Count == 0)
        {
            Configuration.SavedViews.AddRange(Configuration.BuildDefaultSavedViews());
            Configuration.Save();
        }

        targetHook = new WorldCameraTargetHook(GameInteropProvider, Log);
        positionHook = new WorldCameraPositionHook(GameInteropProvider, Log);
        freeCam = new FreeCamController();
        keyBlocker = new FlyKeyBlocker(Log);
        cameraController = new CameraController(Configuration, TargetManager, ObjectTable, ClientState, Condition, targetHook, positionHook, freeCam, keyBlocker, KeyState, Log);
        settingsWindow = new SettingsWindow(Configuration, cameraController, GameConfig);
        WindowSystem.AddWindow(settingsWindow);

        CommandManager.AddHandler(SettingsCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open settings. Also: /camcam on|off|toggle, /camcam next|prev, /camcam pause, /camcam fly."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleSettingsWindow;

        Framework.Update += cameraController.OnFrameworkUpdate;
    }

    private void OnCommand(string command, string args)
    {
        switch (args.Trim().ToLowerInvariant())
        {
            case "on": Configuration.Enabled = true; Configuration.Save(); break;
            case "off": Configuration.Enabled = false; Configuration.Save(); break;
            case "toggle": Configuration.Enabled = !Configuration.Enabled; Configuration.Save(); break;
            case "next": cameraController.CycleNext(); break;
            case "prev": case "previous": cameraController.CyclePrevious(); break;
            case "pause": cameraController.CyclePaused = !cameraController.CyclePaused; break;
            case "fly":
                Configuration.FreeFly = !Configuration.FreeFly;
                if (Configuration.FreeFly) Configuration.Enabled = true;
                Configuration.Save();
                break;
            default: ToggleSettingsWindow(); break;
        }
    }

    private void ToggleSettingsWindow() => settingsWindow.Toggle();

    public void Dispose()
    {
        Framework.Update -= cameraController.OnFrameworkUpdate;
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleSettingsWindow;
        CommandManager.RemoveHandler(SettingsCommand);
        WindowSystem.RemoveAllWindows();
        cameraController.Shutdown();
        keyBlocker.Dispose();
        targetHook.Dispose();
        positionHook.Dispose();
    }
}
