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
        cameraController = new CameraController(Configuration, TargetManager, ObjectTable, ClientState, targetHook, positionHook, freeCam, keyBlocker, KeyState, Log);
        settingsWindow = new SettingsWindow(Configuration, cameraController, GameConfig);
        WindowSystem.AddWindow(settingsWindow);

        // Same treatment, same reasoning: confirmed harmful (camera
        // teleports up to ~9 units, 50+ degree angle jumps), and asking
        // to manually uncheck it hasn't reliably worked across several
        // rounds of testing - it kept showing up still active in
        // captures. Forcing it off here removes the dependency on a
        // manual step entirely, rather than asking again.
        if (Configuration.ForceNativeDistanceToMatchZoom)
        {
            Configuration.ForceNativeDistanceToMatchZoom = false;
            Configuration.Save();
        }

        CommandManager.AddHandler(SettingsCommand, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open the CamCam settings window."
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleSettingsWindow;

        Framework.Update += cameraController.OnFrameworkUpdate;
    }

    private void OnCommand(string command, string args) => ToggleSettingsWindow();

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
