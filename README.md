# CamCam

Manual camera control for FFXIV, built as a Dalamud plugin. Open settings with `/camcam`.

## How it works

Instead of hooking the game's internal camera-update function (fragile, needs
re-scanning every patch), CamCam takes the approach the working Cammy plugin
uses for its free-cam: every frame, if enabled, it overwrites the world
camera's position/angle/zoom with whatever's set in the settings window,
via a `Framework.Update` hook. Normal targeting is left completely alone,
so you can point the camera anywhere while still tab-targeting or
clicking other players.

## How it actually works now

Two separate mechanisms:

1. **Rotation and zoom** - `CameraController.cs` writes WorldCamera's
   `CurrentHRotation`/`CurrentVRotation`/`CurrentZoom` directly, every
   frame. This is the same technique Cammy's own FreeCam feature uses,
   and it's proven to stick.
2. **Who the camera orbits** - `WorldCameraTargetHook.cs` hooks the
   camera's `getCameraTarget` function and, when "Follow my current
   target" is on, hands back whatever you have `/target`ed instead of
   yourself. This is the real Cammy plugin's spectate mechanism - the
   engine's own native position/collision math then runs on that target
   instead of us reimplementing it.

Earlier versions of this tried writing WorldCamera's Position/LookAt
directly (doesn't stick - the engine recomputes it every frame from
`getCameraTarget()`), and then tried the target-redirect hook using a
hardcoded module address copied from CamIdleHijack's own unconfirmed
attempt at this same idea. Both were wrong in different ways.

This version instead pulled the real source of `Hypostasis`
(github.com/UnknownX7/Hypostasis) - the library the actual, currently-used
Cammy plugin depends on for this exact hook - directly off GitHub via its
commit history. Two things came out of that:

- Every offset already in `RawCamera.cs` (position, look-at, zoom,
  rotation, mode) matches Hypostasis's maintained struct exactly, field
  for field. CamIdleHijack's own reverse-engineering was correct here;
  it was never the problem.
- `getCameraTarget` isn't at some fixed address at all - it's **vtable
  slot 18**, read live off the camera object's own vtable pointer at
  runtime. That's a meaningfully different, more robust technique than a
  hardcoded address: slot indices only break if FFXIV's own code
  reorders that class's virtual methods, which is far rarer than a raw
  address moving. `WorldCameraTargetHook.cs` now reads
  `camera->VTable[18]` live instead of guessing a fixed number.

## The real risk in this version

This still installs a live function hook - real risk, same category as
before, just a more solidly-sourced address. It's only installed while
"Follow my current target" is on, and fully removed the moment you turn
CamCam off. Same guidance as always with live hooks: if the camera, or
anything else, looks wrong after enabling it, restart the game.

## Height offset and empty Follow Mode Options - same root cause

Both traced to one thing: still on the default "Myself" orbit mode.

- **Height offset "not working"** - it genuinely only applies in a
  follow mode (My /target or Cycle); orbiting yourself computes the
  camera position an entirely different way that never involves it. This
  was already noted in small gray text, easy to miss. Height offset,
  Closest zoom, and Steepest angle now show a loud colored warning
  instead, exactly when they currently have no effect - not a static
  note you have to remember.
- **Follow Mode Options "opens but stays collapsed"** - it wasn't
  actually failing to expand. With Orbit set to "Myself," that section
  has nothing to show, since none of its content applies until you pick
  "My /target" or "Cycle" - an empty expanded section looks identical to
  one that didn't open. It now always shows something, including a
  direct note on what to do when there's genuinely nothing to configure.

## Horizontal angle made absolute - this was the real cause of three separate reports

All three things reported together turned out to be one bug: "loading
presets doesn't work the same when Cycle targets myself," "myself vs
cycle angles don't match," and "loading a preset isn't in the same
position it was Updated to be once the Cycle target switches." There
are not separate preset lists for Myself/target/Cycle - it's always the
same one list. The actual cause: `effectiveHRotation` added whoever was
currently being looked at's own facing rotation into the angle
(`targetFacing + configuration.HorizontalRotation`). Since every
character faces a different direction at any given moment, the exact
same saved number produced a different absolute camera angle depending
on who it was pointed at - including your own character versus someone
else, and including the same preset applied to two different Cycle
targets in a row.

Changed Horizontal angle to be a true absolute world angle - target
facing no longer factors in at all. The same preset now produces the
same camera angle regardless of who or what it's applied to, matching
what was asked for directly. Worth knowing as a tradeoff: this also
means presets no longer auto-reframe to stay "behind" or "beside"
someone regardless of which way they're facing the way the old relative
behavior did - if that consistency-across-facing-directions turns out
to matter for some presets, that's a real, opposite need from what was
asked for here, and would need its own toggle rather than a global
change. Say so if that comes up.

## More room for preset names

The name field was 110px in a row already sharing space with stats text
and three buttons - widening it there would've just pushed things off
screen. Restructured instead: name (now 220px) sits on its own line with
Load/Update/Remove, and the H/V/Z/Height/MinZoom/MaxAngle stats moved to
their own line below.

## Pan no longer jumps to a separate "From" value - starts from the preset's own saved angle

Real design flaw, not a setup mistake. `PanFromDegrees` was its own
independent field with no requirement to match the preset's actual
saved `HorizontalRotation` - if you'd set up the preset's angle, then
separately captured a starting point for the pan (or never touched it,
leaving it at whatever default), the pan would jump straight to that
possibly-unrelated `From` value the instant it started, throwing off
the shot you'd actually saved.

Removed `PanFromDegrees` entirely. The pan now always starts from the
preset's own saved Horizontal angle - the same value shown as `H:` next
to the preset's name, the one Load/Update already manage - and sweeps
from there to `PanToDegrees`. Nothing else to keep in sync anymore; the
"From" field and its "Set" button are gone from the UI, since there's
no longer a separate value to set.

## Pause froze the timers instead of resetting them - the real cause of "everything broke on unpause"

Confirmed directly in the code: `cycleTimer` and `presetCycleTimer` only
ever get zeroed by an explicit advance event (a threshold being hit,
Next/Previous, loading a view). Pausing never touched them - they just
sat frozen at whatever progress they'd already made before pause was
engaged. If cycling had been running for a while before you paused to
set up presets, both timers could've been sitting seconds away from
firing. The instant you unpaused, they resumed from that stale,
near-expired progress and could fire almost immediately - possibly both
of them in quick succession - snapping the player and/or preset to
something else entirely right when everything was supposed to stay
exactly where you'd left it. That matches "set up correctly under
pause, broke the second I unpaused" precisely.

Fixed: both timers are now continuously pinned at zero for the entire
duration the pause is on, not just frozen wherever they happened to be.
Unpausing now always starts from a full, fresh interval - no stale
progress can carry over and fire early.

## Found the actual cause: pan silently carried over across Orbit mode switches

Did a full sweep of every write path touching Horizontal angle, Vertical
angle, Zoom, Height offset, Closest zoom, and Steepest angle - all of
them are clean, only written by Load, the live sliders, or the
Cycle+SavedViews application block as expected. One exception:
`AdvanceActivePan` writes to Horizontal angle completely unconditionally
by mode, and `activePanView` was never cleared when Orbit mode changed.

That means loading a panning preset even once - even just while testing
Cycle - left it silently driving Horizontal angle in the background in
*every other mode afterward*, including "Myself," until it either
finished its sweep or a different non-panning view happened to get
loaded. Switch from Cycle back to Myself, and whatever preset you
thought you were looking at could still have a leftover pan from Cycle
quietly overwriting it. This lines up exactly with "set up and save all
6 presets, switch to Cycle, they don't stay the same, switch back to
Myself, still not how I saved them."

Fixed: any active pan now clears the moment Orbit mode changes, so it
can no longer bleed from one mode's context into another's. If this
turns out not to fully explain what you're seeing, the periodic
`/xllog` line still has the pan diagnostic from last round
(`pan(active=... deg=... dir=... complete=... legs=...)`) - checking
whether `active` shows anything other than `none` while sitting in
Myself mode would confirm or rule this theory out directly.

## Load-doesn't-reload fix, vertical pan, diagnostic logging for the Myself-to-Cycle report

- **Fixed: Load didn't reload an already-active panning preset** -
  confirmed cause: `SetActivePanView` only resets pan progress when the
  view *reference* changes, so re-clicking Load on the currently-active
  panning preset was a silent no-op since it was already the same
  object. Load now force-resets pan state regardless of whether the
  view was already selected.
- **Vertical pan added** - same design as horizontal (starts from the
  preset's own saved Vertical angle, sweeps to its own To, independent
  speed). Can run alongside horizontal pan on the same preset for a
  diagonal sweep - each axis tracks its own completion separately, and
  "Advance to next preset after pan completes" now waits for *every*
  enabled axis to finish, not just whichever one happens to complete
  first.
- **"Horizontal off specifically going Myself to Cycle"** - genuinely
  could not pin this down through code review with confidence, so
  rather than guess at a change that might be wrong, added logging right
  at the transition point: `/xllog`, look for `[CamCam] Orbit mode
  changed None -> Cycle...` - it logs `cycleViewIndex`, the live
  Horizontal angle, and that index's saved Horizontal angle side by
  side at the exact moment of the switch. That comparison should show
  directly whether the value is wrong immediately at the transition, or
  drifts wrong sometime after.

## Height offset was breaking the orbit sphere - real cause of ongoing angle inconsistency

You called this one correctly. `orbitPos.Y += FollowHeightOffset` shifted
only the final Y coordinate *after* the X/Z position was already
computed from the Horizontal/Vertical/Zoom sphere formula - meaning the
camera drifted off the sphere it was supposed to be orbiting on. The
same numbers no longer produced a consistent distance or angle to the
target once Height offset was nonzero, and this is very likely what was
actually behind "horizontal angle still off" too, since a Y-axis drift
visually distorts the whole framing, not just the vertical component.
Fixed by folding Height offset into the look-at point itself, so the
entire sphere - not just Y - shifts together.

The ground-lock clamp is a separate, narrower story: it's an
intentional hard safety floor, not part of the intended framing, so it
still only touches Y after the sphere position is computed - and
whether it actually triggers depends on the target's exact geometry
(steep V-angle, their ground elevation), which genuinely does vary. That
part wasn't changed; a full fix would mean recomputing X/Z to stay on
the sphere even when the floor clamp kicks in, which is real added
complexity I didn't want to risk without more confidence first.

One more known, harder limitation worth naming honestly: the look-at
point uses a fixed "+1.3 units above the target's feet" regardless of
race - a Lalafell and a Roegadyn don't actually get looked at the same
relative body position. Not fixed here; would need reliable access to
each character's actual model height, which isn't something I have a
safe way to read right now.

## Pan resetting mid-sweep, zoom slider, random preset cycle

- **Found the pan-resetting bug** - when both "Auto-advance saved
  views" (the fixed timer) and a view's own "Advance to next preset
  after pan completes" were active together, the fixed timer could
  advance the view *before* the pan finished its sweep, restarting a
  brand new pan on the next view before the first one ever completed -
  exactly the "pans, then snaps back, then pans again" pattern
  described. Fixed: the fixed timer now waits for a pan that's meant to
  drive its own advance to actually finish first, instead of the two
  fighting each other.
- **Zoom / distance is a real slider now**, not a drag-float.
- **Random preset cycle** - "Random order" checkbox next to the preset
  timer, only affects that timer specifically (manual Next/Previous and
  pan-completion advances stay sequential and predictable).

## Sticky zoom/angle widening fix, idle-timer unification, and reorganizing

- **Found the real cause of "positions still not lined up."** The
  zoom/angle widening only ever narrowed the camera's own limits further
  (`MathF.Min`/`MathF.Max` against whatever was already there each
  frame) and never reset - so once one preset's MinZoom/MaxAngle widened
  the camera, it stayed that wide permanently, and a *different*
  preset's own MinZoom/MaxAngle values never actually took effect
  independently the way they should have since presets started carrying
  their own copies of those fields. Fixed by capturing the game's true
  original limits once and resetting to that baseline every frame before
  re-widening, so each preset's own limits are properly, independently
  respected now regardless of what was active before it.
- **"Wait for idle" now applies to Myself too**, not just My /target and
  Cycle - previously Myself explicitly bypassed it. All three modes now
  go through the exact same idle-wait gate.
- **Removed "[Experimental]" from the idle-camera-sync toggle** since
  the worldCam/idleCam log comparison confirmed it works. Renamed
  `ExperimentalIdleCameraSync` to `IdleCameraSync`, defaulted to on.
- **Cycle timer options now include 1-14 seconds** individually, in
  addition to the existing 15-second increments up to 2 minutes - shared
  across every timer dropdown that uses this option set.
- **"Also cycle through Saved Views" moved to the top of Saved Views**
  and renamed to "Cycle through saved views," with "Auto preset cycle"
  (renamed "Auto-advance saved views") and its timer (renamed "Cycle
  saved views timer") moved up right alongside it - both now sit before
  Save/Reload/Pause rather than being split across two different
  sections. This timer already ties in with each preset's own "Advance
  to next preset after pan completes" option correctly (both funnel
  through the same underlying advance call, which resets this timer so
  they don't double up) - that connection already existed, it just
  wasn't visible with the pieces living in two different places.
- **Pan not working when targeting self** - genuinely couldn't find the
  bug through code review; the logic looks like it should work
  regardless of mode. Added pan state (`pan(active=... deg=... dir=...
  complete=... legs=...)`) to the periodic `/xllog` line instead of
  guessing at a fix - that'll show definitively whether the pan is
  advancing at all while orbiting yourself, or stuck, or already marked
  complete.

## Presets now capture all of Camera Angle and Zoom, zoom pushed closer with a live confirmation display

- **Saved Views now capture Closest zoom, Steepest angle, and both
  height ground-lock settings**, not just angle/zoom/height/pan - these
  previously only existed as global settings shared across every preset.
  Different shots often want different close-up range or ground-lock
  behavior, so each preset now carries its own. Wired through Save
  Current, Update, Load, and auto-cycling the same way the original four
  fields already were.
- **Zoom floor pushed lower again** - the clamp epsilon, Closest zoom's
  slider minimum, and Zoom/distance's minimum all dropped by another
  order of magnitude (0.01 -> 0.001), and the default for Closest zoom
  dropped from 0.05 to 0.01.
- **Added a live confirmation display** right under Closest zoom,
  reading the actual current zoom and MinZoom/MaxZoom limits directly
  off the camera struct rather than trusting the config slider - this is
  after three rounds of "still not close enough" despite loosening the
  numbers each time, so before assuming the floor still isn't low
  enough, this reading will show definitively whether the setting is
  actually reaching the camera or whether something else is the real
  constraint.

## Pan was oscillating forever, and Pause didn't actually freeze anything

Two real bugs, both making preset-editing harder than it should've been.

- **The pan never stopped.** I'd only ever gated the *side effect*
  (advancing to the next preset) behind the completion check - the
  actual back-and-forth motion itself was never gated by anything and
  ran forever, bouncing between From and To indefinitely regardless of
  any setting. A cinematic pan should be a one-time move, not a
  perpetual scan. Rewrote it: sweeps From -> To once and then holds
  still at To (default), or does the full round trip From -> To -> From
  once and holds at From if "Pan back to From before advancing" is on -
  either way, it now actually stops.
- **Pause didn't touch the pan, or the currently-applied preset's
  values, at all.** It only ever gated the two auto-advance timers
  (which player/preset gets *selected*), never the pan motion or the
  per-frame reapplication of whichever preset is currently active. Both
  are now gated behind Pause too - with Pause on, the pan freezes in
  place and the live sliders stop getting overwritten by the cycling
  preset entirely, which is what "make it pause pan as well... update
  presets easier without it moving/changing" actually needed.

## Experimental fix attempt for the native idle camera

Added something to actually test the hypothesis rather than just talk
about it. New toggle under General: "[Experimental] Sync FFXIV's native
idle camera" - forces the game's separate `IdleCamera` object to mirror
whatever angle/zoom/mode CamCam is actively maintaining on `WorldCamera`,
every frame. Unconfirmed whether this does anything; explicitly opt-in
and easy to turn back off.

More useful than the toggle itself: the periodic `/xllog` line now logs
both cameras' Mode/H/V/Zoom side by side (`worldCam(...)` vs
`idleCam(...)`), regardless of whether syncing is on. If `idleCam`'s
values start diverging from `worldCam`'s right around when the override
happens, that confirms the game is driving it independently. Worth
checking that log with the toggle *off* first, to see the raw divergence
before judging whether the sync toggle actually changes anything.

## Pan-completion bug fix, timer pause, window-resize wrapping, reorganizing

- **Found and fixed the actual "pans back to start" bug.** The pan's
  starting direction was hardcoded to always move upward first, but
  whether that means "toward To" or "toward From" depends on which of
  the two is numerically larger. When From > To, the pan would
  instantly register as "at its bound" on the very first frame - before
  visibly moving at all - firing any advance-on-complete trigger
  immediately. Now the first leg always heads toward To regardless of
  which value is bigger, so "advance after pan completes" correctly
  means "after going from one direction to the next," not misfiring on
  frame one.
- Added **"Pan back to From before advancing"** as an explicit option
  (only shown once "Advance to next preset after pan completes" is on)
  - off (default) advances right after reaching To; on waits for the
  full round trip back to From first.
- Renamed that checkbox to **"Advance to next preset after pan
  completes."**
- **Visual separator** between each Saved View row.
- **"Auto preset cycle" and its timer moved to the Saved Views
  section**, alongside the rest of the preset controls, out of Follow
  Mode Options.
- **"Also cycle through Saved Views" isn't redundant with Auto preset
  cycle** - one controls *whether* a Saved View's angle/zoom/height gets
  applied at all while cycling, the other controls whether *which* view
  is applied advances on its own timer. Kept both, but turning on Auto
  preset cycle now automatically turns this on too, since auto-advancing
  a preset that isn't even being applied wouldn't do anything. Hint text
  rewritten to directly explain what it does rather than just restating
  its own name.
- **Every hint/status text in the window now wraps to the current width**
  instead of running off as one long line - previously only the Follow
  Mode Options intro did this. A `TextDisabledWrapped` helper replaced
  every `ImGui.TextDisabled` call throughout the file.
- **"Pause timers" button** in Saved Views - freezes both Auto cycle and
  Auto preset cycle in place until pressed again (manual Next/Previous
  still works), so adjusting and saving a preset doesn't get interrupted
  by the camera jumping to something else mid-edit. Session-only, resets
  off on restart so it can't get stuck.

## Active-view highlighting, decoupled preset timer, renames, reorganizing

- **Active view highlighting** - each Saved View row now shows a green
  `>` when it's the one currently loaded/cycled to (tracked the same way
  Load and auto-cycling already did internally, just exposed to the UI
  now), so it's obvious which one Update would actually resave.
- **"Wait for idle" moved to General.**
- **"Auto-advance" renamed to "Auto cycle"**, its interval dropdown
  renamed to **"Auto cycle timer."**
- **New: "Auto preset cycle" + "Preset cycle timer"** - the preset/angle
  can now advance on its own independent schedule, decoupled from how
  often the player changes. Previously both moved together on one
  timer; now you can, for example, stay on the same player for a while
  but cycle through several saved angles on them before moving on.
  Next/Previous still moves both together, matching the old behavior for
  manual stepping.
- General cleanup pass on hint text for consistency with the renames and
  the "always visible regardless of mode" change from last round.

## Follow Mode Options always visible, wider pan sliders, advance-after-pan

- **Follow Mode Options no longer hides content based on Orbit mode** -
  idle threshold, gender filter, cycling controls, everything is
  configurable regardless of which mode is currently selected. Hint text
  now clarifies what only takes effect in "My /target" or "Cycle"
  instead of the section just disappearing.
- **Pan sliders widened** - From/To went from 60px to 100px, deg/s from
  70px to 110px.
- **"Advance to next after pan completes"** - new per-view checkbox
  under the pan controls. When on, finishing the pan's first sweep
  (reaching either bound for the first time) immediately advances Cycle
  mode to the next player/view, instead of waiting for the fixed
  auto-advance timer. Only fires once per view activation, and only
  matters during Cycle mode with "Also cycle through Saved Views" on.

## Height offset cycle-lock bug, layout reorder, confirming what already exists

- **Height offset really was broken in Cycle+SavedViews mode** - it was
  the one slider (Horizontal angle, Vertical angle, and Zoom all already
  had this) that never got the "pause auto-overwrite for a couple
  seconds while you're dragging it" protection added a few rounds back.
  Dragging it got reverted almost instantly, every time. Fixed - now
  matches the other three sliders exactly.
- **General section moved to the top** of the collapsible list, right
  after the always-visible top controls.
- **Ground-lock for Height offset and the idle/cycle dropdowns are
  already in the code** - verified directly by reading the file, not
  assumed. If they're not showing up in-game, the running build predates
  the rounds that added them; a clean rebuild should bring both back.
- Also cleaned up a doc-comment mixup from an earlier edit that had
  landed on the wrong method.

Native FFXIV idle/AFK camera possibly overriding CamCam once it kicks in
- this is a different, deeper thing than CamCam's own "Wait for idle"
  setting, and I don't have a confirmed mechanism or a safe fix for it
  yet. Leading theory: the game may switch which camera object is
  actually active (there's a separate `IdleCamera` pointer alongside
  `WorldCamera` in the camera manager struct) once its own native idle
  trigger fires - CamCam only ever hooks `WorldCamera`, so if that's
  what's happening, the hooks would still be running but pointed at an
  object the game isn't using for rendering anymore. Worth checking
  in-game System/Character Configuration first for anything mentioning
  an idle or auto camera - that would be a zero-risk fix if it exists.
  If not, and this is worth chasing further, I'd want to add read-only
  diagnostic logging first to confirm the theory before attempting
  anything at the memory level, rather than guessing blind so soon after
  the last crash.

## Cinematic pan for Saved Views

Each saved view now has a "Cinematic pan" checkbox. When on, the view
sweeps its Horizontal angle back and forth between two bounds (From/To,
in degrees) at a set speed (degrees/second) instead of holding a fixed
angle - the view's own saved H value is ignored while panning is active.
"Set" buttons next to From and To capture your current live Horizontal
angle, so the workflow is: rotate to one extreme in-game, click Set on
From; rotate to the other extreme, click Set on To - no manual degree
math needed.

Under the hood, one shared pan mechanism drives this regardless of how
the view became active - manually clicking Load, or Cycle mode
auto-cycling onto it (works with "Also cycle through Saved Views" too,
panning independently on whichever view is currently selected).
Dragging the Horizontal angle slider yourself still pauses it briefly
via the same manual-override cooldown already used elsewhere, rather
than fighting your input.

## Simplified: 3 presets, Zoom no longer required in code

Fewer things to type numbers for blind. `BuildDefaultSavedViews()` now
has three rows instead of six, and the tuple dropped Zoom entirely -
each row is just `(Name, H degrees, V degrees, Height)`. Every default
preset starts at a fixed medium zoom (3.0) automatically. To set your
own distance for a preset: load it in-game, drag the Zoom/distance
slider to where you want it, hit Update on that view - no code editing
needed for zoom at all.

## Height was never part of Saved Views - now it is, plus a ground lock for it

Real bug, not a mistake on your end: `SavedView` never had a height
field at all - Save/Load/Update and the six default preset tuples only
ever touched Horizontal angle, Vertical angle, and Zoom. Whatever height
number got written into a preset had nowhere to go, so Load could never
have changed it. Added `HeightOffset` to `SavedView` itself, threaded it
through Save/Load/Update, the list display (now shows `Height:` too),
and auto-cycling through saved views. `BuildDefaultSavedViews()`'s tuple
now has a fifth field - fill in the real height number as the last
value in each row, e.g. `("Preset 1", 0f, 0f, 3f, -2.5f)`.

Also added a ground-lock toggle for Height offset, next to the slider -
"Don't let height offset go below ground level (approximate)" plus a
clearance slider when it's on, same pattern as Free Fly's ground lock.
Uses whoever you're currently orbiting as the ground reference (works
for yourself too, since that's unified with follow-mode positioning now).

## Closest zoom and Steepest angle now work in every mode too

Same story as Height offset last round: the widening logic that lets
zoom get closer and the vertical angle get steeper was still gated to
skip "orbiting yourself" specifically, left over from before that mode
went through the same computation as an actual follow mode. Dropped the
mode check - now applies whenever CamCam is actively controlling the
camera at all (i.e. whenever Free Fly isn't on), matching Height offset.
Removed the now-dead conditional-warning helper these three used to
share, since nothing is follow-mode-only anymore.

Cycle occasionally stopping after switching Myself -> target -> Cycle,
independent of auto-advance: traced through idle-timer handling and
cycle-state persistence across mode switches and didn't find a bug -
"My /target" mode doesn't write to anything Cycle mode reads, and the
idle timer isn't reset by switching modes. Need the Status line (or the
periodic `/xllog` line) at the exact moment it happens to know which of
several different possible causes this actually is before changing more
memory-adjacent code without solid footing.

## Why editing BuildDefaultSavedViews() wasn't showing up

Seeding only fires on a genuinely empty `SavedViews` list. Nathan's own
install already had a saved view from earlier testing before this
feature existed, so the seed condition was already false - editing the
six placeholder rows in `Configuration.cs` had no effect on his current
list no matter how many rebuilds. Real config lives at
`%AppData%\XIVLauncher\pluginConfigs\CamCam.json` - `Configuration.cs`
only defines defaults, not saved data.

Added a **"Reload shipped defaults"** button next to "Save Current
Angle/Zoom as View" - clears the current list and repopulates it from
`BuildDefaultSavedViews()` on demand, regardless of what's currently
saved. Meant for iterating on the six preset values without needing an
actually-empty install each time to see a change.

## Height offset now works in every mode, including orbiting yourself

Fair pushback - there wasn't a good reason for it to be follow-mode-only.
The real cause: "orbiting yourself" and the actual follow modes used two
completely different ways of positioning the camera. Follow modes
compute position manually (look-at point + spherical offset, which is
where Height offset gets added in). Orbiting yourself instead wrote
straight to the game's own native camera fields and let the game handle
positioning - a path that never touched Height offset at all, structurally.

Unified them: orbiting yourself now goes through the same manual
computation as a follow mode, using the local player as the "target."
Height offset, and everything else that comes from that computation,
now applies universally. Still engages instantly with no idle wait,
unlike a real follow mode. One side effect worth knowing: Horizontal
angle while orbiting yourself is now relative to which way your
character is facing, matching how it already works for every other
follow target, rather than a fixed world direction like before.

## Crash traced to camera reinitialization during login/character transitions

Crash dump showed an access violation inside the game's own
`Client::Game::Camera.vf1` (its native camera Init function), called from
`AgentLobby.Update` - the login/character-select screen. That's the game
tearing down and reinitializing its own camera system, most likely during
a relog or character switch. CamCam had no concept of "am I actually in
the game world right now" - hooks and per-frame camera writes kept
running as long as Enabled was on, with zero awareness of this kind of
transition, which is exactly the situation where the camera object itself
is mid-rebuild.

Fixed with a hard gate using `IClientState.IsLoggedIn` - a standard,
safe Dalamud API, not a memory-level guess. The very first thing checked
every frame now, before Enabled or any toggle key: if not logged in
(title screen, character select, or mid-transition), CamCam fully
disengages - every hook removed, no writes at all - no exceptions. Also
now in the periodic `/xllog` line (`loggedIn=True/False`) for future
reference if anything like this comes up again.

## Auto-advance dropdown, and the real reason "off" still cycled

- **Auto-advance interval is now a dropdown too** - 15 seconds up to 2
  minutes, same 15-second-increment pattern and label style as the idle
  timer.
- **Found the actual bug behind "toggling auto-advance off still
  cycles through players."** Cycle mode was tracking "who's selected" as
  a raw position in the nearby-player list - but that list gets rebuilt
  from scratch every frame, and its order/contents shift naturally as
  players walk in and out of render range. So even with the index frozen
  and auto-advance off, position N in the list could quietly become a
  *different person* from one frame to the next - not because anything
  was actively cycling, but because the list itself kept changing shape
  underneath a fixed index. Rebuilt around tracking the actual selected
  player (by `EntityId`, a stable per-object identifier) instead of a
  list position - Cycle mode now only changes who it's looking at when
  you explicitly Next/Previous or when auto-advance is genuinely on and
  its timer fires. Applies to both plain player cycling and the
  saved-views-combined mode.

## Idle timer as a dropdown, not a free slider

"Wait for idle" under Follow Mode Options is now a dropdown - Instant,
then every 15 seconds up to 15 minutes (61 options total), instead of a
free-drag slider that could land on any number. Labels read naturally
("1 min 30 sec" rather than "90"). If your saved value predates this
change and isn't a clean 15-second multiple, it snaps to the closest
option rather than defaulting to Instant.

## Default presets, layout consolidation, closer zoom

- **Six default presets ship with the plugin now.** `Configuration.BuildDefaultSavedViews()`
  has six clearly-marked placeholder rows (currently all `0deg / 0deg /
  3.0` zoom) - edit those six lines with your real H/V/Z numbers in
  plain degrees, no radian math needed. `Plugin.cs` seeds them into
  `SavedViews` only on a genuinely fresh install (empty list) - it will
  never touch or overwrite views you've already customized on your own
  install, and only fires once per fresh config.
- **Camera Angle and Zoom now holds everything angle/zoom-related** -
  Height offset, Closest zoom, and Steepest angle moved up from Follow
  Mode Options, using the same default bar width as Horizontal/Vertical
  angle and Zoom/distance instead of a custom wider one.
- **Zoom can get closer** - the floor was `MinZoom + 0.05`; now `+0.01`.
  Closest zoom's slider minimum dropped from 0.05 to 0.01, its default
  from 0.3 to 0.05, and the main Zoom/distance drag's minimum from 0.1
  to 0.01. Still only reaches those depths in a follow mode (Closest
  zoom only widens the camera's limits there) - orbiting yourself keeps
  the game's own normal minimum, unchanged from before.

## Live Num Lock check - a real update to the numpad theory

Nothing bound to the numpad keys in FFXIV's own keybind list is
important new information - it means the "unbind the conflicting FFXIV
action" advice from the last couple of rounds doesn't actually apply
here, since there's no conflict there to unbind. The more likely
explanation now: **Num Lock is off**, so the physical numpad keys are
sending arrow/Page Up/Page Down/Home/End/Insert/Delete codes instead of
numpad codes - and if FFXIV has anything bound to *those* keys, that's
what fires, invisibly, since searching "Numpad" wouldn't surface it.

Rather than keep asking you to check this manually, `/camcam` now shows
Num Lock's actual live state directly under Free Fly Options, in red or
green, updating in real time - and it's also in the periodic `/xllog`
line now (`numLock=ON` or `numLock=OFF`). No more guessing about this
one specific fact.

## Gender filter, Cycle blocking manual changes, toggle-timing diagnostics

- **Manual slider changes blocked during saved-views cycling** -
  auto-cycling wrote the current view's angle/zoom over yours every
  frame, so any manual drag got reverted almost immediately. Dragging
  the Horizontal angle, Vertical angle, or Zoom sliders now sets a
  2.5-second window (`CameraController.NotifySliderAdjusted`) during
  which saved-views auto-cycling won't overwrite them - long enough to
  actually adjust and then hit Update.
- **Male/Female target filter** - new radio buttons under Cycle mode
  (Any / Male / Female), filtering the nearby-player pool using the
  standard FFXIV customize byte array (`ICharacter.Customize[1]`).
- **Free Fly toggle timing** - genuinely uncertain of the exact cause
  this round. Added logging that captures the manual-mouse-cooldown and
  idle-timer state at the precise moment the toggle fires
  (`/xllog`, look for `[CamCam] Free Fly toggled...`) - if
  `manualInputCooldown` shows above 0 at that moment, that's the
  explanation (right-click/left-click held shortly before pressing the
  toggle); if it's 0 and engagement still lagged, that's a different,
  real bug still to find.
- **Numpad in hotbars** - no new code change here without new
  information; still need to know whether unbinding the conflicting
  FFXIV keybind resolved it, and whether the hook-callback log line
  ever actually appears.

## Key dropdowns, separate toggle keybinds, layout reorder

- **All keybind fields are now dropdowns, not free text.** `KeyCatalog.cs`
  is a single alphabetical list of every key CamCam understands, used
  both to populate every dropdown and to resolve a saved key to a
  virtual-key code - the two can no longer drift apart, and a typo'd or
  unrecognized key name is no longer possible to select. This is the
  most likely real explanation for the Free Fly toggle "not working" -
  if what was typed in didn't exactly match what the old resolver
  expected, it silently did nothing.
- **Toggle CamCam now has its own keybind**, separate from Toggle Free
  Fly - both live under their respective sections (General, Free Fly
  Options) and both work regardless of whether CamCam is currently on.
- **Saved Views moved next to Camera Angle and Zoom**, both open by
  default, so Load/tweak/Update is all in one place without switching
  sections.
- **Numpad still triggering hotbars** - no new information since last
  round, so no new hook-side change here. The one action guaranteed to
  fix it either way is unbinding the conflicting FFXIV action directly
  (System > Keybinds, search "Numpad").

## Load/Update fix, toggle-key diagnostics, and an honest read on the numpad issue

- **"Load" got instantly overwritten during Cycle mode** - auto-cycling
  writes the angle/zoom every frame from whatever it's currently on, so
  a manual Load was corrected right back a moment later. `Load` now also
  syncs the auto-cycle's internal index (`CameraController.SetCycleView`)
  to match what you just loaded, so it actually sticks.
- **Adjusting and resaving views** - each saved view now has an
  "Update" button alongside Load/Remove: load a view, tweak it with the
  angle/zoom sliders, click Update to resave those changes into the same
  entry instead of creating a new one.
- **Free Fly toggle key** - added real logging (`/xllog`, look for
  `[CamCam] Free Fly toggled...`) plus the key resolver now also
  understands Home/End/Insert/Delete/Backspace/Caps Lock/Escape/Alt,
  in case the chosen key just wasn't recognized before. The periodic
  status line also now shows what VK code your typed key name resolves
  to - `0x00` means it's not being recognized at all, which would fully
  explain nothing happening when you press it.
- **Numpad still triggering hotbars - changing my assessment.** I
  looked through your log for `[CamCam] FlyKeyBlocker's hook callback
  fired` and don't see it anywhere, including across a 40-second Free
  Fly stretch where those keys would have been held continuously. That,
  plus three rounds of this not resolving despite a hook that's
  implemented the way Windows documents it, points toward FFXIV reading
  keyboard input through DirectInput - which polls the hardware directly
  and can bypass the exact interception point a `WH_KEYBOARD_LL` hook
  sits at. If that's the case, no hook-side fix solves this; only
  removing the conflict at its source does. The one guaranteed fix:
  open FFXIV's own **System > Keybinds**, search "Numpad," and unbind
  whatever's currently on the keys CamCam uses (or pick different CamCam
  keys that have nothing bound in-game at all). The hook stays in the
  code since it's harmless and may still help in other scenarios, but I'm
  not presenting it as the actual fix for this anymore.

## Settings window redesign, saved-views targeting fix, toggle key fix

- **Settings window reorganized** - Enable/Free Fly/Follow Mode are now
  always visible at the top with nothing else competing for space.
  Everything else lives in collapsible sections (Camera Angle and Zoom,
  Follow Mode Options open by default; Free Fly Options, Saved Views,
  General collapsed) so you only see what you're actually using. Wider
  scrollbar (22px) and noticeably more spacing between every element,
  scoped to just this window via `PushStyleVar`/`PopStyleVar` so it
  doesn't affect other plugins' windows.
- **"Cycle saved views" only targeted yourself** - it was falling back
  to your own character whenever nothing was `/target`ed, which is what
  was happening the whole time it was tested. Rebuilt to pull from the
  same nearby-player list normal Cycle mode uses - it now advances
  through players and saved views together, one step of each per
  Next/Previous or auto-advance tick.
- **Free Fly toggle key didn't work** - it was gated behind the "Enable
  CamCam" checkbox, so it did nothing if CamCam wasn't already on.
  Fixed to work regardless, and it now turns "Enable CamCam" on
  automatically the first time it flips Free Fly on.
- **Numpad still leaking into hotbars** - the low-level hook is
  installed correctly per how Windows documents it, which points toward
  a real technical limit rather than a bug in this code: some games read
  keyboard input via DirectInput or Raw Input, which can bypass the
  exact point a `WH_KEYBOARD_LL` hook intercepts at. If that's what's
  happening here, no hook-side code change fixes it - only picking keys
  with nothing bound to them in FFXIV's own keybind list would. Need to
  know whether `/xllog` shows `[CamCam] FlyKeyBlocker's hook callback
  fired for the first time` before doing anything further here - that
  one fact tells us which category of problem this actually is.

## Zoom range bug, saved-views cycling

- **Zoom couldn't actually get closer** - the "Zoom / distance" slider's
  minimum was hardcoded at 1.5, left over from before follow-mode zoom
  widening existed. The camera itself was already allowed down to 0.3 in
  follow modes, but the slider physically couldn't produce a number below
  1.5, so "closer" was never reachable regardless of the follow-mode zoom
  settings. Slider now goes down to 0.1.
- **Cycle through saved views** - new checkbox under Cycle mode: instead
  of stepping through nearby players, it steps through your Saved Views
  list (angle/zoom presets), keeping who it's looking at fixed (current
  `/target` if set, otherwise yourself). Next/Previous and auto-advance
  both work the same way, just over presets instead of players. If this
  isn't what "cycle with saved positions" meant, say so and I'll adjust -
  the other plausible reading was a curated/favorites player list instead
  of the full nearby-player pool, which is a different feature.

## Mouse control back, idle timer, Free Fly toggle key

- **Camera locking you out of normal mouse control** - CamCam was
  overwriting rotation/zoom every single frame regardless of what you
  were doing, fighting your own right/left-click camera input. Fixed by
  yielding completely (removing both hooks, writing nothing) whenever
  either mouse button is held, plus a short cooldown after release so it
  doesn't snap back mid-adjustment - the exact same fix CamIdleHijack
  already uses for this. Applies in every mode, including Free Fly.
- **Idle start timer** - Follow/orbit modes now wait until you've stood
  still for a configurable number of seconds (default 8, slider goes to
  0 for instant) before engaging, matching CamIdleHijack's own "cinematic
  idle camera" framing. Free Fly is exempt - flying is already something
  you're actively choosing to do, not something that should require
  standing still first.
- **Free Fly toggle keybind** - type any key in the new field to flip
  Free Fly on/off with one press, no need to open this window.
- **Height slider too finicky** - widened considerably, and the live
  Up/Down key adjustment speed was cut roughly in half.

**The numpad-still-triggering-hotbars report needs one more round of
data before I can fix it correctly.** `FlyKeyBlocker.cs` now logs
explicitly whether the hook actually installed (with the Win32 error
code if not) and whether Windows ever actually calls it - check
`/xllog` for lines starting with `[CamCam] FlyKeyBlocker` after this
build. If it never says "hook callback fired for the first time," the
hook isn't intercepting input at all for some reason on your system, and
that's the real thing to chase next, rather than guessing at a second
fix on top of an unconfirmed first one.

## Follow-mode rewrite, key blocker, turn fix, cycle timer

- **Reset on cycle, fixed properly**: horizontal angle was an absolute
  world-space number, so the same 40° looked completely different
  depending on which way each player happened to be facing - not a
  reset, just relative to nothing. Follow modes now compute position
  manually (look-at point + rotation + zoom, same trig Free Fly uses)
  with the angle relative to the target's own facing - `target.Rotation +
  offset`, the exact same fix CamIdleHijack already uses for this. This
  also means Follow modes now use the position hook instead of relying
  on the game's own `getCameraPosition`, which is what makes the height
  offset and cycle timer below possible at all.
- **4/6 turn direction**: genuine sign bug in the rotation math, not a
  preference - swapped.
- **Numpad still triggering hotbars**: `GetAsyncKeyState` (used
  everywhere in this project) only *reads* key state - it can't stop the
  game from also seeing the same keypress. `FlyKeyBlocker.cs` adds a
  standard Windows low-level keyboard hook (`SetWindowsHookEx` +
  `WH_KEYBOARD_LL`) that swallows CamCam's specific configured keys at
  the OS level before anything else sees them. This is a genuinely
  different mechanism from the memory write that crashed the game
  earlier - it never touches process memory at all, it's the same
  category of technique tools like AutoHotkey use. Only active while
  Free Fly or a follow mode is on, and only the specific configured keys
  are affected - everything else passes through untouched.
- **Height while following + cycle timer**: both fall out of the
  follow-mode rewrite above. Up/Down (your fly keys) now adjust an
  independent height offset while following - there's also a slider with
  a reset button. Cycle mode has an "Auto-advance" checkbox with an
  adjustable interval.

## Two root causes behind almost everything reported this round

1. **Stale saved config.** Dalamud persists your settings to disk. When
   I change a *default* value in code, that only affects brand-new
   installs - an existing saved file keeps last round's key names and
   overrides the new defaults every time the plugin loads. This is why
   arrow keys/brackets/`=`/`-` were still active even after the numpad
   defaults shipped. Fixed properly this time: `Configuration.Version`
   bumped to 2, with a one-time migration in `Plugin.cs` that resets the
   fly-key fields to current defaults for anyone still on an older saved
   config. Should not recur for future key changes if the version gets
   bumped again alongside them.
2. **Num Lock changes what a numpad key sends.** With Num Lock off,
   physically pressing Numpad-8 sends the same code as the Up arrow -
   not a numpad code at all - and Numpad-9/Numpad-3 send Page Up/Page
   Down. This explains "9 increases height" (it was secretly Page Up,
   already bound to Up) and the general shakiness (sometimes an old
   stale binding matched, sometimes nothing did). **Num Lock must be ON**
   for the numpad bindings to register as intended - flagged prominently
   in the settings window now.

## This round's specific changes

- Movement remapped to your exact numbers: 8/2 forward/back, 7/9 strafe
  left/right, 4/6 turn left/right, Numpad 0 up, Numpad `.` (Del) down,
  Numpad +/- look up/down.
- **Auto-hide UI**: new toggle + keybind field (defaults to `R`, same as
  your CamIdleHijack setup) that simulates your "Toggle UI Display Mode"
  key when CamCam turns on/off - pure keypress simulation via Windows'
  standard input API, same category as CamIdleHijack's own UI-hide
  logic, no game memory involved at all.
- **Ground clamp for Free Fly**: optional toggle that stops the camera
  going below your character's current height plus a clearance amount.
  This is an approximation (character height, not real terrain
  collision) - it'll feel right near flat ground close to you and won't
  help on stairs or multi-level areas, since there's no actual raycast
  happening.
- **Wider zoom/angle while following a target**: when orbiting your
  current target or a cycled player, the camera's own zoom/angle limits
  are temporarily widened (adjustable sliders, default 0.3 closest zoom /
  80° steepest angle) - the same "zoom hack" category of write Cammy
  uses, applied to fields (`MinZoom`/`MaxVRotation`/`MinVRotation`)
  already read and written successfully elsewhere in this project. Only
  active while actively following someone; orbiting yourself keeps the
  game's normal limits untouched.

## Two more fixes from hands-on testing

1. **Free Fly started far away** - it was seeding its starting position
   from `camera->X/Y/Z`, which is an engine-recomputed output, not
   reliable to read at the exact instant Free Fly switches on. Now it
   computes a starting position near your character directly (same
   look-at + rotation + zoom math used elsewhere), so it opens close to
   where the normal camera already was instead of somewhere far off.
2. **Numpad instead of arrows/brackets** - 8/2 forward/back, 4/6 turn
   left/right, 7/9 strafe left/right, Numpad +/- look up/down. Up/Down
   for vertical flight stayed on Page Up/Page Down since that wasn't part
   of the numpad request. Numpad keys needed explicit handling in the key
   resolver - Windows treats "Numpad8" as a different key from "8", so
   typing a bare digit wouldn't have picked up the numpad key at all.

## Three real gaps, addressed this round

Free Fly itself confirmed working (no crash, movement as designed). Three
things it was actually missing, based on hands-on testing:

1. **No way to turn while flying** - only the settings sliders, which
   meant tabbing out of the flight to adjust. Added dedicated turn/look
   keys (`[`/`]` to turn, `=`/`-` to look up/down by default, all
   retypeable) that rotate continuously while held, fast enough for a
   full 180 in about a second and a half at the default turn speed.
2. **No target cycling, only "current /target"** - added a proper
   `CameraFollowMode`: orbit yourself, orbit your current `/target`, or
   **Cycle nearby players** with Next/Previous buttons that step through
   everyone in render range without needing to leave the CamCam window at
   all.
3. **No way to save an angle** - added a Saved Views list (horizontal
   angle, vertical angle, zoom), same pattern CamIdleHijack already uses
   for its own pose list: name it, Load it later, Remove it when done.
   Position isn't part of a saved view - "camera angle" is what got
   asked for, and position is Free Fly-specific state that doesn't mean
   much divorced from wherever you were flying at the time.

Free Fly and the follow modes remain mutually exclusive (Free Fly wins if
both are set) - flying to an arbitrary position and orbiting a fixed
target are different position models, and combining them didn't seem
like what "an option for free roam, and an option to target players" was
asking for. Let me know if that reading's wrong.

## The game crashed - what happened and what changed

`MovementLock.cs` (added last round to stop WASD from also walking your
character while flying) has been removed entirely after it crashed the
game. Most likely cause: it wrote (`++`/`--`) to a memory address found
by scanning the whole game binary for a short, fairly generic byte
pattern - and even Cammy's own source comment on that exact signature
expresses uncertainty about it. A bad *read* gives wrong behavior; a bad
*write* can corrupt whatever else happens to live at that address, which
is a plausible, maybe likely, explanation for a crash. Nothing here
touches your account or save data either way - FFXIV keeps that
server-side - so a crash just means relaunch the game, no lasting harm.

Replaced with something that avoids the risk instead of trying to get the
risky version right: Free Fly now uses configurable, non-WASD keys
(arrows + Page Up/Down by default, all typeable in `/camcam` the same way
CamIdleHijack lets you type your own UI-toggle key) instead of trying to
force the game's movement off. Since the fly keys and the game's own
movement keys no longer overlap, there's nothing to fight and no memory
write involved at all.

## What changed the round before that

Your report was "just looking at my character - not targeting, no camera
freedom." Two real gaps, addressed directly:

1. **No actual free movement existed before** - only orbit angle/zoom
   around a fixed point. `FreeCamController.cs` + `WorldCameraPositionHook.cs`
   add real WASD/QE 3D flight, using the getCameraPosition hook (vtable
   slot 16, confirmed via the same Hypostasis source as slot 18) to hand
   the engine a fully free-flown position - the actual mechanism Cammy's
   own FreeCam uses, not a variation on the orbit code.
2. **No way to tell why target-follow wasn't working** - `CameraController.cs`
   now logs status to `/xllog` every 2 seconds while CamCam is on: which
   mode is active, whether each hook actually installed, and what (if
   anything) you currently have targeted. If "Follow my current target"
   still doesn't work after this, that log output is what to paste back -
   it'll say plainly whether the hook failed to install, installed but no
   target was set, or something else.

Free Fly and Follow Target are mutually exclusive - Free Fly wins if both
are on. Rotation sliders always work in both modes; zoom only applies
when Free Fly is off, since position is fully manual otherwise.

## How to actually test this

1. `/camcam`, enable CamCam.
2. Try Free Fly first - hold W and see if the camera moves. This doesn't
   depend on targeting anything, so it's the simplest way to confirm the
   plugin is doing anything at all.
3. Turn Free Fly off, `/target` a nearby player, turn on "Follow my
   current target," see if the camera reorients onto them.
4. Either way, check `/xllog` for lines starting with `[CamCam]` - that's
   the ground truth for what actually happened, not a guess.

## Before you build

Everything through the manifest, SDK, and `IObjectTable` issues from
earlier rounds is fixed and unchanged. This round's changes haven't been
built or run in-game from this side either - only checked against
Hypostasis's actual maintained source, which is a stronger basis than
anything used so far in this project, but still not the same as it
running in your client.

## Build

```
dotnet build -c Debug
```

Load it in-game the same way you load CamIdleHijack: `/xlsettings` ->
Experimental -> Dev Plugin Locations -> point at the built `CamCam.dll`.

## One heads-up

This reads and writes your own client's memory, same category as Cammy and
similar plugins - it doesn't touch other players or their systems. That
said, any third-party memory modification is outside FFXIV's terms of
service, same as it is for CamIdleHijack or any other Dalamud plugin - your
call to weigh, just flagging it plainly.
