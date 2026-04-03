# Aska First Person Camera

A toggleable first-person camera mod for Aska. Look through your character's eyes, see your weapons in hand, and explore the world from a whole new perspective.

## Requirements

- [BepInEx 6 IL2CPP (BE #755 or newer)](https://builds.bepinex.dev/projects/bepinex_be)
  - **Easiest method:** Install [r2modman](https://thunderstore.io/package/ebkr/r2modman/), select Aska, and install **BepInExPack_IL2CPP** from the mod list. Then install this mod through r2modman as well.

## Manual Installation

1. **Install BepInEx 6 IL2CPP** if you haven't already:
   - Download `BepInEx_Unity.IL2CPP-win-x64-6.0.0-be.755.zip` from the link above
   - Find your Aska folder: Steam > right-click Aska > Manage > Browse Local Files
   - Extract the zip contents into this folder (next to `ASKA.exe`)
   - Launch Aska once and close it (BepInEx generates required files on first run)

2. **Install the mod:**
   - Download `AskaFirstPerson.dll` from the Files tab
   - Place it in `<Aska folder>/BepInEx/plugins/`
   - That's it — launch the game

## How to Use

| Action | Keyboard | Gamepad |
|--------|----------|---------|
| Toggle first/third person | **F5** | **R3** (right stick click) |

Toggle only works while in-game (not on menus or loading screens). The mod auto-disables when returning to the main menu.

## Features

- Smooth camera anchored to the player's head bone with configurable offsets
- **Motion dampening** reduces vertical camera shake from combat, rolls, and head bob
- **Shadow-only body** -- your character is hidden but still casts a realistic shadow
- **Weapons and tools stay visible** in your hands
- **Upper body spine rotation** -- arms and held items follow where you look
- **Equipment hidden cleanly** -- backpack, cape, quiver, stowed weapons, hair, and beard are all handled. Newly equipped items are caught automatically
- **Multiplayer compatible** -- client-side only, other players are unaffected and don't need the mod
- **Pause-aware** -- camera stops moving when the game is paused or a menu is open
- Works with both male (Ragnar) and female (Aska) characters

## Configuration

After first launch, a config file is created at:
```
BepInEx/config/com.community.askafirstperson.cfg
```

You can edit it with any text editor, or press **F1** in-game if you have [BepInEx ConfigManager](https://thunderstore.io/c/aska/) installed.

### Key Settings

| Setting | Default | Description |
|---------|---------|-------------|
| FOV | 80 | First-person field of view (60-120) |
| MouseSensitivity | 2.0 | Mouse look sensitivity |
| MotionDampening | 0.4 | Reduces vertical shake. 0 = raw, 1 = max smooth. Try 0.3-0.5 |
| SmoothSpeed | 15 | Camera position smoothing (higher = snappier) |
| VerticalOffset | 0.1 | Height offset above head bone (metres) |
| ForwardOffset | 0.12 | Forward offset from head bone (metres) |
| NearClipPlane | 0.05 | Lower values prevent seeing inside geometry |
| ToggleKey | F5 | Keyboard toggle key |
| ShowLowerBody | false | Experimental: show forearms/hands/legs (may have visual artifacts) |

## Compatibility

- Works in singleplayer and co-op multiplayer
- Compatible with other BepInEx IL2CPP mods
- After major Aska updates, delete `BepInEx/interop/` and relaunch to regenerate assemblies

## Troubleshooting

- **Mod not loading:** Make sure you have BepInEx **6** IL2CPP (not BepInEx 5). The DLL must be in `BepInEx/plugins/`, not the game root.
- **F5 doesn't work:** You must be in-game, not on the main menu or a loading screen.
- **Camera too low or jittery:** Delete `BepInEx/config/com.community.askafirstperson.cfg` and relaunch to reset to defaults. Adjust MotionDampening and SmoothSpeed to taste.
- **Equipment still visible after equipping:** New items are detected every 0.5 seconds. If something persists, toggle FP off and on.

## Uninstallation

Delete `AskaFirstPerson.dll` from `BepInEx/plugins/` and optionally delete the config file at `BepInEx/config/com.community.askafirstperson.cfg`.
