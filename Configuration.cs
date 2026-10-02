using System;
using System.Collections.Generic;
using Dalamud.Configuration;
using Dalamud.Plugin;

namespace CamCam;

public enum CameraFollowMode
{
    None,
    CurrentTarget,
    Cycle,
}

public enum CycleGenderFilter
{
    Any,
    MaleOnly,
    FemaleOnly,
}

[Serializable]
public class SavedView
{
    public string Name { get; set; } = "View";

    public float HorizontalRotation { get; set; }
    public float VerticalRotation { get; set; }
    public float Zoom { get; set; }
    public float HeightOffset { get; set; }

    // The rest of Camera Angle and Zoom, now captured per-preset instead
    // of only living as global settings - different shots often want
    // different close-up/steepness/ground-lock behavior.
    // No longer shown in the UI: CamCam widens the game's distance/angle
    // limits automatically to whatever the shot needs. Kept so older
    // configs still deserialize.
    public float MinZoom { get; set; } = 0.01f;
    public float MaxAngleDegrees { get; set; } = 80f;
    public bool HeightLockToGround { get; set; } = false;
    public float HeightGroundClearance { get; set; } = 0.5f;

    // Cinematic pan - sweeps Horizontal angle from this preset's own
    // saved HorizontalRotation to PanToDegrees while this view is
    // active, instead of holding still. Always starts from the preset's
    // actual saved angle - there's deliberately no separate "From" value
    // that could drift out of sync with what's actually saved.
    public bool PanEnabled { get; set; } = false;
    public float PanToDegrees { get; set; } = 30f;
    public float PanSpeedDegreesPerSecond { get; set; } = 10f;

    // Vertical pan - same idea as horizontal, independent axis. Can be
    // used on its own or together with horizontal pan for a diagonal
    // sweep; each axis tracks its own completion separately since they
    // can finish at different times if their ranges/speeds differ.
    public bool VerticalPanEnabled { get; set; } = false;
    public float VerticalPanToDegrees { get; set; } = 30f;
    public float VerticalPanSpeedDegreesPerSecond { get; set; } = 10f;

    // Zoom pan - same idea as horizontal/vertical, but for camera
    // distance instead of an angle. No wrap-around concerns here since
    // zoom is a plain linear distance, not an angle.
    public bool ZoomPanEnabled { get; set; } = false;
    public float ZoomPanToValue { get; set; } = 3f;
    public float ZoomPanSpeed { get; set; } = 1f;

    // Delays how long after this preset becomes active before each axis
    // actually starts moving - 0 (default) means it starts immediately,
    // same as every preset before this existed. Lets one axis hold still
    // while another moves first, e.g. holding Vertical fixed while Zoom
    // pans in, only starting the vertical tilt once it's reached a
    // deliberately-timed point rather than all enabled axes starting
    // together. Each axis's own delay is independent of the others.
    public float HorizontalPanStartDelaySeconds { get; set; } = 0f;
    public float VerticalPanStartDelaySeconds { get; set; } = 0f;
    public float ZoomPanStartDelaySeconds { get; set; } = 0f;

    // When true, reaching either pan bound for the first time (i.e. the
    // pan finishes its initial sweep) advances Cycle mode to the next
    // player/view, instead of waiting for the fixed auto-advance timer.
    public bool PanAdvanceCycleOnComplete { get; set; } = false;

    // Only matters when PanAdvanceCycleOnComplete is on. False (default):
    // advance as soon as the sweep reaches the far bound (From -> To).
    // True: wait for the full round trip (From -> To -> From) first.
    public bool PanReturnBeforeAdvance { get; set; } = false;

    // When true, Cycle mode skips over this preset entirely unless the
    // current target is sitting (ground-sit or chair). Checked every time
    // the preset selection would otherwise land here - a target who isn't
    // sitting never sees this preset applied at all.
    public bool RequireTargetSitting { get; set; } = false;

    // Only read when RequireTargetSitting is true. Defaults to Any so
    // existing presets saved before this field existed keep their old
    // "any kind of sitting counts" behavior unchanged.
    public SittingRequirement RequiredSittingType { get; set; } = SittingRequirement.Any;

    // 0 (default) = instant, exactly the old behavior - the written camera
    // position snaps straight to the computed orbit position every frame,
    // same as every preset saved before this field existed. Above 0, it's
    // a time constant in seconds: the position eases toward the target
    // instead of snapping, which softens (but doesn't eliminate) visible
    // shake from whatever's still fighting the camera near walls or
    // uneven ground - see the Ground lock comment above for why that
    // fight exists in the first place. Bigger number = slower, laggier
    // catch-up; keep it small (0.1-0.3) unless a pan is being genuinely
    // violent.
    public float PositionSmoothingSeconds { get; set; } = 0f;

    // Off by default (matches every preset saved before this existed).
    // Uses the game's own real collision raycast system to keep the
    // camera from clipping through walls or objects between it and the
    // subject - see AvoidWallsAndObjects' comment in CameraController.cs.
    // Genuinely new capability, not an upgrade to something that existed
    // before - test on presets that pan near walls/objects specifically.
    public bool AvoidWallsAndObjects { get; set; } = false;

    // How far short of a detected wall/object to stop, in world units.
    // 0.35 was the original hardcoded value - exposed here so it can be
    // tuned per-preset without a code change, particularly for keeping
    // the camera further back from geometry if a smaller buffer isn't
    // enough to clear whatever's causing shake near that boundary.
    public float WallAvoidanceBuffer { get; set; } = 0.35f;

    // When true, this preset's camera position is captured ONCE (a
    // snapshot at the moment this preset becomes active) and held
    // completely fixed for the rest of the shot, instead of continuously
    // recomputed from wherever the subject currently is - a tripod pan,
    // not an orbit. The snapshot uses this preset's own H/V/Zoom/
    // HeightOffset (the normal orbit sphere math) to place the camera
    // exactly once, so setting one up feels the same as any other preset
    // - it just freezes there instead of tracking. Horizontal/Vertical
    // pan above still work normally on top of this - that's what lets
    // the subject drift through frame and back out, since the camera's
    // own rotation keeps sweeping even though its position doesn't move.
    // Zoom pan is NOT meaningful here (there's no orbit radius once
    // position is frozen) and is ignored if this is on. UI label is
    // "Tripod (fixed position)" - field name kept as-is so existing
    // saved presets don't lose this setting.
    public bool FixedCameraPassBy { get; set; } = false;

    // A fully independent alternative to FixedCameraPassBy above, not a
    // sub-option of it - shown as its own separate toggle, and turning
    // this on by itself is enough (no need to also enable Tripod). A
    // true dolly/strafe/pedestal move: the camera's FACING stays
    // completely locked at whatever it was at the snapshot moment, and
    // instead its POSITION translates through space between the Start
    // and End values below. This is what produces "hold the camera
    // facing forward and simply strafe sideways" or "drop straight down
    // while still facing forward." When this is on, Horizontal/Vertical
    // pan settings are ignored entirely (rotation is locked, there's
    // nothing for them to sweep). If both this and FixedCameraPassBy are
    // somehow on at once, this one wins (position moves, rotation locked).
    public bool TranslateInsteadOfPan { get; set; } = false;

    // Explicit start and end points, not a single "distance in one
    // direction" - lets the camera's own H/V/Zoom/HeightOffset placement
    // serve as a true zero/center reference, with independent control
    // over where the move begins and ends on each side. E.g. Start
    // Right = -10, End Right = 10 sweeps a full 20 units, centered on
    // the placed position; Start Right = 0, End Right = 15 starts
    // exactly at the placed position and moves only rightward from
    // there. Right/Forward are relative to the camera's OWN facing
    // direction at the snapshot moment (not world-space compass
    // directions) - positive Right is to the right, positive Forward is
    // further into the shot, both perpendicular/parallel to wherever the
    // camera happened to be facing when it froze. Up is the one
    // exception: positive is always straight up in world space
    // regardless of camera tilt, since "drop 15 feet" should mean an
    // actual vertical drop, not something relative to a tilted camera's
    // own up vector.
    public float TranslateStartRight { get; set; } = -10f;
    public float TranslateEndRight { get; set; } = 10f;
    public float TranslateStartUp { get; set; } = 0f;
    public float TranslateEndUp { get; set; } = 0f;
    public float TranslateStartForward { get; set; } = 0f;
    public float TranslateEndForward { get; set; } = 0f;

    // Units per second along the combined 3D path length between Start
    // and End (not per-axis) - same "total distance / speed = duration"
    // idea as every other pan.
    public float TranslateSpeed { get; set; } = 2f;
    public float TranslateStartDelaySeconds { get; set; } = 0f;

    // Smoothstep ease-in/ease-out on every pan/strafe axis of this preset
    // instead of constant speed with hard starts/stops. The configured
    // speed then becomes the AVERAGE speed - total duration is unchanged.
    public bool EaseInOut { get; set; } = false;

    // Field of view in degrees. 0 = leave the game's own FOV alone.
    // Shifts where the subject sits in frame, in world units at the subject
    // (positive = subject right of center). Rule-of-thirds framing.
    public float SideOffset { get; set; } = 0f;

    // Scales Distance, Height offset and Side offset by the subject's
    // character height, so one shot frames a lalafell and a roegadyn alike.
    public bool ScaleWithSubjectSize { get; set; } = false;

    public float FieldOfViewDegrees { get; set; } = 0f;

    // FOV pan - same idea as the other pan axes. Starts from
    // FieldOfViewDegrees (or the game's FOV when that's 0).
    public bool FovPanEnabled { get; set; } = false;
    public float FovPanToDegrees { get; set; } = 30f;
    public float FovPanSpeedDegreesPerSecond { get; set; } = 3f;
    public float FovPanStartDelaySeconds { get; set; } = 0f;

    // Camera roll (Dutch angle), degrees. Experimental - written to the
    // render camera's up vector.
    public float RollDegrees { get; set; } = 0f;

    // How this shot is entered when the camera switches to it on the SAME
    // subject: 0 = hard cut, above 0 = eased move from the previous shot
    // over this many seconds. Changing subject is always a cut.
    public float TransitionSeconds { get; set; } = 0f;

    // Recorded camera path (Free Fly recording). When this has 2+
    // keyframes the shot plays the path instead of the orbit/pan settings.
    // Keyframes are stored relative to the subject at record time
    // (position rotated into the subject's facing, H relative to facing).
    public List<PathKeyframe> Path { get; set; } = new();

    // True: replay around whoever the current subject is (same relative
    // move on anyone). False: replay at the exact world spot it was
    // recorded, using PathAnchor* below.
    public bool PathRelativeToSubject { get; set; } = true;
    public float PathAnchorX { get; set; }
    public float PathAnchorY { get; set; }
    public float PathAnchorZ { get; set; }
    public float PathAnchorRotation { get; set; }
    public float PathPlaybackSpeed { get; set; } = 1f;
    public bool PathLoop { get; set; } = false;

    public bool HasPath => Path.Count >= 2;

    public SavedView Clone()
    {
        var copy = (SavedView)MemberwiseClone();
        copy.Path = new List<PathKeyframe>(Path.Count);
        foreach (var k in Path) copy.Path.Add(k.Clone());
        return copy;
    }
}

[Serializable]
public class PathKeyframe
{
    public float Time { get; set; }
    // Camera position relative to the anchor, rotated into the anchor's
    // own yaw frame (see CameraController.RotateY).
    public float X { get; set; }
    public float Y { get; set; }
    public float Z { get; set; }
    // Radians; H relative to the subject's facing at record time.
    public float H { get; set; }
    public float V { get; set; }
    // Degrees; 0 = game default.
    public float FovDegrees { get; set; }

    public PathKeyframe Clone() => (PathKeyframe)MemberwiseClone();
}

public enum IdleDetectionMode
{
    // No keyboard/mouse input at all - how FFXIV's own AFK camera decides.
    AnyInput,
    // Your character hasn't moved - the original CamCam behavior.
    CharacterMovement,
}

// Ground vs Furniture is a real distinction the game's Character struct
// makes itself (different Mode values) - Chair vs Bench is NOT: both are
// "attached to furniture" as far as that struct is concerned, so this
// can't tell them apart without identifying the specific furniture
// object involved, which isn't implemented here.
public enum SittingRequirement
{
    Any,
    Ground,
    Furniture,
}

[Serializable]
public class Configuration : IPluginConfiguration
{
    // Bumped 1 -> 2 when fly keys moved to numpad defaults. See Plugin.cs
    // constructor: existing saved configs get their fly-key fields reset
    // to current defaults on first load after the update, since a saved
    // file on disk always overrides these C# defaults otherwise.
    // v2 -> v3: UI toggle default moved from "R" (autorun in FFXIV's
    // default keybinds) to Scroll Lock, the game's real default for
    // Toggle UI Display Mode.
    public int Version { get; set; } = 3;

    /// <summary>Master switch. While false, CamCam never touches the camera and both hooks stay uninstalled.</summary>
    public bool Enabled = false;

    public bool AutoHideUi { get; set; } = false;

    public string UiToggleKeyName { get; set; } = "Scroll Lock";

    // Empty = no keybind. Separate from FreeFlyToggleKeyName - this one
    // flips the master "Enable CamCam" switch itself.
    public string ToggleCamCamKeyName { get; set; } = "";

    /// <summary>
    /// True 3D free-fly movement, using the getCameraPosition hook.
    /// Takes priority over FollowMode if both are set.
    /// </summary>
    public bool FreeFly = false;

    public float FreeFlySpeed = 4f;
    public float FreeFlyTurnSpeed = 2.5f; // radians/sec while a turn/look key is held

    public bool FreeFlyLockToGround { get; set; } = false;
    // Degrees, 0 = game default. Also what Free Fly recordings capture.
    public float FreeFlyFovDegrees { get; set; } = 0f;
    public float FreeFlyGroundClearance { get; set; } = 0.5f;

    // Orbit modes (Myself/Target/Cycle) fully compute and override the
    // camera's position and rotation every frame, same as Free Fly - but
    // unlike Free Fly, they used to also keep the getCameraTarget hook
    // actively telling the engine "the camera's target is this specific
    // person," the whole time. Free Fly instead removes that hook
    // entirely. This toggle makes orbit modes drop it too, matching Free
    // Fly's approach. Extensive /xllog-verified testing (orbiting several
    // different players, not just self) with this on found no reliability
    // regression - the originally-suspected downside (orbiting someone
    // other than yourself being unreliable without the hook) didn't
    // reproduce, likely because the position/rotation computation has
    // changed substantially since that concern was first written. On by
    // default now on that basis. The actual cause of the wall/ground
    // shake turned out to be unrelated to this hook (see
    // GetStableTargetRotation's comment) - kept available as a toggle in
    // case a regression does turn up for a case testing hasn't covered.
    public bool ExperimentalBypassTargetHook { get; set; } = true;

    // Developer diagnostics (per-frame height/raycast/shake logs and the
    // 2-second status line). Off by default - they flood /xllog otherwise.
    public bool VerboseLogging { get; set; } = false;

    // Hands the camera back to the game while in combat - the game's own
    // AFK camera never runs mid-fight either. Cutscenes, zone loads and
    // gpose always disengage regardless of this.
    public bool DisengageInCombat { get; set; } = true;

    // CharacterMovement stays the default: with AnyInput, every mouse move
    // (including reaching for the settings window) counts as "back".
    public IdleDetectionMode IdleDetection { get; set; } = IdleDetectionMode.CharacterMovement;

    // Hold while flying to scale speed. Not Shift/Ctrl: with Num Lock on,
    // Shift+numpad makes Windows send arrow/navigation codes instead.
    public string FlyFastModifierKey { get; set; } = "Numpad3";
    public string FlySlowModifierKey { get; set; } = "Numpad1";
    public float FlyFastMultiplier { get; set; } = 3f;
    public float FlySlowMultiplier { get; set; } = 0.25f;

    // "No input" idle detection details.
    public bool IdleMouseMovementCounts { get; set; } = false;
    public float IdleMouseMovementThresholdPixels { get; set; } = 40f;
    public bool IdleGamepadCounts { get; set; } = true;

    // Draws the selected shot's camera position/motion in the world while
    // the settings window is open.
    public bool ShowShotOverlay { get; set; } = true;

    // Free Fly recording sample rate.
    public float RecordSamplesPerSecond { get; set; } = 20f;

    // Cycle pool filters beyond appearance/job.
    public bool CycleExcludeSelf { get; set; } = false;
    // 0 = unlimited (whole object table, ~100 yalms).
    public float CycleMaxDistance { get; set; } = 0f;
    // Skips anyone with a wall/object between them and you.
    public bool CycleRequireLineOfSight { get; set; } = false;

    // Deliberately NOT WASD - those double as FFXIV's own movement keys.
    // Numpad keys require Num Lock ON - with it off, Windows sends arrow/
    // paging/navigation codes instead of numpad codes for the same
    // physical keys, and these bindings won't register at all.
    public string FlyForwardKey { get; set; } = "Numpad8";
    public string FlyBackKey { get; set; } = "Numpad2";
    public string FlyLeftKey { get; set; } = "Numpad7";  // strafe left
    public string FlyRightKey { get; set; } = "Numpad9"; // strafe right
    public string FlyUpKey { get; set; } = "Numpad0";
    public string FlyDownKey { get; set; } = "Numpad.";  // numpad Del/Decimal key
    public string FlyTurnLeftKey { get; set; } = "Numpad4";
    public string FlyTurnRightKey { get; set; } = "Numpad6";
    public string FlyLookUpKey { get; set; } = "Numpad+";
    public string FlyLookDownKey { get; set; } = "Numpad-";

    /// <summary>
    /// Who the camera orbits while Free Fly is off. None = yourself,
    /// same as the base game. CurrentTarget = whatever you have
    /// /targeted. Cycle = step through nearby players with Next/Previous.
    /// </summary>
    public CameraFollowMode FollowMode { get; set; } = CameraFollowMode.None;

    // While actively following a target (CurrentTarget or Cycle), the
    // camera's own zoom/angle limits are temporarily widened to these
    // values - same "zoom hack" category of technique Cammy uses - so
    // orbiting someone else can get as close/steep as Free Fly can,
    // instead of being capped by the game's normal third-person limits.
    public float FollowMinZoom { get; set; } = 0.01f;
    public float FollowMaxAngleDegrees { get; set; } = 80f;

    // Independent vertical offset while following a target - the fly
    // Up/Down keys adjust this live, same as they move Free Fly's height.
    public float FollowHeightOffset { get; set; } = 0f;
    public float FollowHeightAdjustSpeed { get; set; } = 1.5f;
    public bool FollowHeightLockToGround { get; set; } = false;
    public float FollowHeightGroundClearance { get; set; } = 0.5f;
    public float FollowPositionSmoothingSeconds { get; set; } = 0f;
    public bool FollowAvoidWallsAndObjects { get; set; } = false;
    public float FollowWallAvoidanceBuffer { get; set; } = 0.35f;

    public bool CycleAutoAdvance { get; set; } = false;
    public float CycleIntervalSeconds { get; set; } = 15f;

    // Separate from CycleAutoAdvance/CycleIntervalSeconds (which player
    // Cycle mode advances to) - lets the preset/angle change on its own
    // schedule, independent of how often the target changes.
    public bool PresetCycleEnabled { get; set; } = false;
    public float PresetCycleIntervalSeconds { get; set; } = 15f;

    // When true, the preset-cycle timer above picks a random Saved View
    // (never repeating the current one) instead of stepping through them
    // in order. Only affects the timer - manual Next/Previous and the
    // pan-completion trigger stay sequential.
    public bool PresetCycleRandom { get; set; } = false;

    // When true, Cycle mode steps through SavedViews (angle/zoom presets)
    // instead of nearby players - the "who" stays fixed (your current
    // /target if set, otherwise yourself), only the preset advances.
    public bool CycleUseSavedViews { get; set; } = false;

    // Filters the nearby-player pool Cycle mode steps through. Uses the
    // standard FFXIV appearance-customize byte array (index 1 = gender,
    // 0 = male, 1 = female) exposed via ICharacter.Customize.
    public CycleGenderFilter CycleGenderFilterMode { get; set; } = CycleGenderFilter.Any;

    // Skips lalafell characters when building the nearby-player pool for
    // Cycle mode. Lalafells' realHeight (~0.6, vs. ~0.9-1.2 for other
    // races) makes preset framing tuned on a taller race sit noticeably
    // differently on them even with height-scaled offsets - this sidesteps
    // that rather than trying to compensate for it. Uses the same
    // Customize array (index 0 = race; lalafell = 3).
    public bool ExcludeLalafells { get; set; } = false;

    // Skips characters currently sitting (ground-sit or chair) when
    // building the nearby-player pool for Cycle mode. Detected via the
    // Character struct's own Mode field (EmoteLoop or InPositionLoop) -
    // see IsCharacterSitting's comment for the caveat on what else that
    // can catch.
    public bool ExcludeSitting { get; set; } = false;

    // Skips anyone currently classed as one of the 8 Disciples of the
    // Hand (Carpenter, Blacksmith, Armorer, Goldsmith, Leatherworker,
    // Weaver, Alchemist, Culinarian) when building the nearby-player pool
    // for Cycle mode. Uses the character's current ClassJob via Dalamud's
    // own safe wrapper (ICharacter.ClassJob.RowId) - not a raw memory
    // read - checked against the standard ClassJob sheet row IDs for
    // those 8 jobs, stable since 2.0 launch.
    public bool ExcludeCrafters { get; set; } = false;

    // NOTE: these are the gear's MODEL ids (what the character struct
    // actually stores per slot - shared between visually identical items),
    // not the item ids shown on Garland Tools/Teamcraft. Field name kept
    // for config compatibility.
    // Model IDs that disqualify a candidate from Cycle mode if equipped in
    // ANY gear slot (Head/Body/Hands/Legs/Feet/Ears/Neck/Wrists/either
    // Finger). There's no "clothing type/category" concept exposed by the
    // game's data - only exact per-item IDs - so this is a manually built
    // list rather than anything more categorical. The periodic /xllog
    // status line includes the current target's equipped IDs per slot to
    // help identify which number is which piece of gear.
    public List<ushort> ExcludedEquipmentItemIds { get; set; } = new();

    // How long you must stand still before Follow/orbit modes actually
    // engage (Free Fly is exempt - flying is already a deliberate,
    // active action, not something that should require standing still
    // first). Grabbing the camera manually resets this back to 0.
    public float IdleThresholdSeconds { get; set; } = 8f;

    // Empty = no keybind. Press-and-release toggles Free Fly on/off
    // without opening the settings window.
    public string FreeFlyToggleKeyName { get; set; } = "";

    // Press-and-release toggles whether the numpad fly keys are actively
    // blocked from reaching the game (see FlyKeyBlocker/ClearGameKeyState
    // in CameraController.cs) without needing the settings window open.
    // Independent of whether Free Fly/keys-in-use blocking would
    // otherwise be active - lets you deliberately let numpad through to
    // the game (e.g. to actually use a numpad-bound hotbar slot) without
    // having to stop flying first.
    public string NumpadBlockToggleKeyName { get; set; } = "Numpad/";

    // Free Fly only: press to start recording a camera path, press again
    // to stop and save it as a new path shot.
    public string RecordToggleKeyName { get; set; } = "Numpad*";

    /// <summary>Radians.</summary>
    public float HorizontalRotation = 0f;

    /// <summary>Radians.</summary>
    public float VerticalRotation = 0.5f;

    public float Zoom = 3f;

    public List<SavedView> SavedViews { get; set; } = new();

    /// <summary>
    /// The three presets a fresh install ships with. EDIT THE THREE ROWS
    /// BELOW with your own H (horizontal angle) / V (vertical angle) /
    /// Height numbers - H and V are plain degrees here for readability
    /// (matching what the settings window's sliders show), converted to
    /// radians automatically since that's how SavedView actually stores
    /// them. Zoom isn't set here - see the comment inside the method for
    /// how to set it per-preset via the live UI instead. Only used to seed
    /// SavedViews on a completely fresh install (see Plugin.cs) - never
    /// overwrites views you've already customized.
    /// </summary>
    public static List<SavedView> BuildDefaultSavedViews()
    {
        // Name, H (degrees), V (degrees), Height offset - replace these
        // three rows. Zoom isn't set here - every preset starts at a fixed
        // medium distance (3.0). To set your own zoom for a preset: load
        // it in-game, drag the Zoom/distance slider to where you want it,
        // then hit Update on that view to save the distance you dialed in.
        (string Name, float HDeg, float VDeg, float Height)[] presets =
        {
            ("Preset 1", 0f, 0f, 0f),
            ("Preset 2", 0f, 0f, 0f),
            ("Preset 3", 0f, 0f, 0f),
        };

        const float defaultZoom = 3f;

        var list = new List<SavedView>();
        foreach (var p in presets)
        {
            list.Add(new SavedView
            {
                Name = p.Name,
                HorizontalRotation = p.HDeg * (MathF.PI / 180f),
                VerticalRotation = p.VDeg * (MathF.PI / 180f),
                Zoom = defaultZoom,
                HeightOffset = p.Height,
            });
        }
        return list;
    }

    [NonSerialized]
    private IDalamudPluginInterface? pluginInterface;

    public void Initialize(IDalamudPluginInterface pi) => pluginInterface = pi;

    public void Save() => pluginInterface?.SavePluginConfig(this);
}
