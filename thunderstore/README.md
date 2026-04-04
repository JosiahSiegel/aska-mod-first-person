# Aska First Person Camera

A BepInEx 6 IL2CPP plugin that adds a fully-featured first-person camera to Aska.

## Features

- **Toggle camera** with **F5** (keyboard) or **LB + R3** (gamepad) -- both fully configurable
- Smooth camera positioning anchored to the player's head bone with configurable offsets
- **Motion dampening** reduces vertical camera shake from combat, rolls, and head bob without adding lateral lag
- **Shadow-only body** -- player model is hidden but still casts shadows for immersion
- **Held items stay visible** -- weapons, tools, and shields in either hand remain rendered
- **Upper body spine rotation** -- arms and held items follow the camera direction naturally, split across two spine bones for a smooth look
- **Equipment hiding** -- all gear (backpack, cape, quiver, stowed weapons, hair, beard) is hidden cleanly; periodic re-scan catches newly equipped items
- **Multiplayer aware** -- correctly identifies the local player via Photon Fusion InputAuthority; other players are unaffected
- **Pause detection** -- camera input stops when the game is paused, a menu is open, or the cursor is unlocked
- **Scene-aware** -- auto-disables when returning to the main menu and re-discovers references on new sessions
- **Experimental lower body mode** -- optional config to show forearms, hands, and legs (may have mesh artifacts)
- Configurable FOV, mouse sensitivity, near clip plane, smoothing, and more
- All settings editable via `BepInEx/config/com.community.askafirstperson.cfg` or in-game with BepInEx ConfigManager (F1)

## Installation

### With r2modman (recommended)

1. Install [r2modman](https://thunderstore.io/package/ebkr/r2modman/)
2. Select Aska as your game
3. Search for **AskaFirstPerson** and install

### Manual

1. Install [BepInEx 6 IL2CPP (BE #755+)](https://builds.bepinex.dev/projects/bepinex_be) for Aska
2. Copy `AskaFirstPerson.dll` into `BepInEx/plugins/` inside your Aska game folder
3. Launch the game

## Configuration

After first launch, edit `BepInEx/config/com.community.askafirstperson.cfg`:

### Camera

| Setting | Default | Range | Description |
|---------|---------|-------|-------------|
| FOV | 80 | 60 -- 120 | First-person field of view |
| MouseSensitivity | 2.0 | 0.1 -- 10 | Mouse look sensitivity |
| NearClipPlane | 0.05 | -- | Near clip plane distance (lower prevents seeing inside geometry) |
| VerticalOffset | 0.1 | -- | Vertical offset above the head bone (metres) |
| ForwardOffset | 0.12 | -- | Forward offset from the head bone (metres) |
| SmoothSpeed | 15 | 1 -- 100 | Position smoothing factor (higher = less smoothing) |
| MotionDampening | 0.4 | 0 -- 1 | Reduces vertical camera shake from combat and head bob. 0 = raw tracking, 1 = maximum dampening. Recommended 0.3 -- 0.5 |

### Player

| Setting | Default | Description |
|---------|---------|-------------|
| HeadBoneName | Bip001 Head | Name of the head bone in the player skeleton (Aska uses 3ds Max Biped naming) |

### Visibility

| Setting | Default | Description |
|---------|---------|-------------|
| ShowLowerBody | false | Show forearms, hands, and legs in first-person. Off = cleaner shadow-only body. On = experimental, may have mesh artifacts |

### Controls

| Setting | Default | Description |
|---------|---------|-------------|
| ToggleKey | F5 | Keyboard key to toggle first-person and third-person |
| GamepadToggleButton | RightStickButton | Gamepad button to toggle first-person mode |
| GamepadModifierButton | LeftShoulder | Modifier button held with the toggle button (LB + R3 by default). Set to "None" for a bare button press |

## How It Works

- A Harmony prefix on `CinemachineBrain.LateUpdate` suppresses Cinemachine's camera positioning while in first-person mode, without disabling CinemachineBrain itself (which would break game state)
- Camera position tracks the head bone with configurable vertical/forward offsets and motion dampening
- Spine bones (`Bip001 Spine` and `Bip001 Spine1`) are rotated in LateUpdate after the Animator writes, so the upper body follows the camera direction while legs face the movement direction
- All renderers on the player hierarchy are set to `ShadowCastingMode.ShadowsOnly`, except items attached to hand bones
- Bone lookups use explicit skeleton paths from the `master` transform to avoid matching duplicate skeletons inside equipment meshes

## Requirements

- Aska (Steam)
- BepInEx 6 Bleeding Edge (IL2CPP build #755 or newer)

## Compatibility

- Client-side only -- does not affect network state in co-op
- Works in both singleplayer and multiplayer sessions
- Compatible with other BepInEx IL2CPP mods
- May need updates after major Aska patches (delete `BepInEx/interop/` and relaunch to regenerate)

## Sunshine / Moonlight (Remote Play / Steam Deck)

If you stream Aska to a Steam Deck or another device via [Sunshine](https://github.com/LizardByte/Sunshine) + [Moonlight](https://moonlight-stream.org/), you need mods installed directly in the game folder (r2modman's "Start modded" won't work over a stream).

Copy your r2modman profile to the game folder:

```bash
# Git Bash / MSYS2
cp -r "$APPDATA/r2modmanPlus-local/ASKA/profiles/Default/"* \
  "C:/Program Files (x86)/Steam/steamapps/common/ASKA/"
```

```powershell
# PowerShell
Copy-Item -Recurse -Force `
  "$env:APPDATA\r2modmanPlus-local\ASKA\profiles\Default\*" `
  "C:\Program Files (x86)\Steam\steamapps\common\ASKA\"
```

Then launch Aska normally from Steam -- mods load automatically. Re-run the copy after updating mods in r2modman. To revert, delete `winhttp.dll` and `doorstop_config.ini` from the game folder.

## Troubleshooting

- **Plugin not loading:** Ensure you have BepInEx 6 IL2CPP (not BepInEx 5), and the DLL is in `BepInEx/plugins/`
- **Camera not switching:** Make sure you are in-game (not on a menu or loading screen) when pressing the toggle key
- **Seeing inside player model:** Decrease `NearClipPlane` or increase `ForwardOffset`
- **Jittery camera:** Decrease `SmoothSpeed` for more smoothing, or increase `MotionDampening` to reduce vertical shake
- **Camera moves in menus:** This should not happen -- pause detection stops input when the cursor is visible or the game is paused. If it does, please report the issue
- **Equipment still visible:** New equipment is detected every 0.5 seconds. If something persists, toggle first-person off and on again
