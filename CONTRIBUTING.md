# Contributing to Aska First Person Camera

Thanks for your interest in contributing! This guide covers everything you need to get the mod building, tested, and ready for a pull request.

## Prerequisites

| Tool | Version | Purpose |
|------|---------|---------|
| [.NET SDK](https://dotnet.microsoft.com/download) | 6.0+ | Build the plugin |
| [r2modman](https://thunderstore.io/package/ebkr/r2modman/) | Latest | Manage BepInEx and launch modded Aska |
| [Aska](https://store.steampowered.com/app/1898300/ASKA/) | Steam | The game |
| Git | Any | Source control |

Optional but recommended:
- **Visual Studio 2022+** or **Rider** for IDE support
- **dnSpyEx** for inspecting interop assemblies

## Initial Setup

### 1. Install BepInEx via r2modman

1. Open r2modman, select **Aska**
2. Install **BepInExPack_IL2CPP** from the online mod list
3. Click **Start modded** once and close the game — this generates interop assemblies

### 2. Clone and build

```bash
git clone https://github.com/YOUR_USERNAME/aska-first-person.git
cd aska-first-person
dotnet build -c Release
```

The project references BepInEx DLLs from the default r2modman profile path:

```
%APPDATA%\r2modmanPlus-local\ASKA\profiles\Default\BepInEx\
```

If your r2modman installation is elsewhere, override the path:

```bash
dotnet build -c Release -p:R2ModManDir="C:\path\to\BepInEx"
```

### 3. Install the built DLL

Copy `bin\Release\net6.0\AskaFirstPerson.dll` to your r2modman plugins folder:

```
%APPDATA%\r2modmanPlus-local\ASKA\profiles\Default\BepInEx\plugins\
```

Launch via r2modman's **Start modded** button.

## Project Structure

```
aska-first-person/
  AskaFirstPerson.csproj     # Project file (references BepInEx/interop DLLs)
  FirstPersonPlugin.cs       # BepInEx BasePlugin entry point, config bindings
  FirstPersonBehaviour.cs    # MonoBehaviour: camera, input, visibility, spine rotation
  CameraControllerPatch.cs   # Harmony patch on CinemachineBrain.LateUpdate
  Discovery/                 # Dev-only scene inspection plugin (not part of the release)
    AskaDiscovery.csproj
    DiscoveryPlugin.cs
  thunderstore/              # Thunderstore package files
    manifest.json
    README.md
    icon.png
  .github/workflows/
    release.yml              # CI: builds, packages, and creates GitHub releases
  CONTRIBUTING.md            # This file (you are here)
```

## Architecture Overview

### How the mod works

1. **`FirstPersonPlugin.Load()`** registers config entries, applies Harmony patches, and adds `FirstPersonBehaviour` to a persistent GameObject.

2. **`CameraControllerPatch`** is a Harmony prefix on `CinemachineBrain.LateUpdate` that returns `false` (skips the original) when first-person mode is active. This prevents Cinemachine from overriding our camera position.

3. **`FirstPersonBehaviour.Update()`** handles toggle input (keyboard F5 / gamepad R3), mouse look, and gamepad right stick. It gates all input on `IsInGameplay()` and `IsGamePaused()`.

4. **`FirstPersonBehaviour.LateUpdate()`** positions the camera at the head bone with motion dampening, applies mouse-driven rotation, rotates spine bones for upper-body aiming, and periodically re-scans for new renderers to hide.

### Key design decisions

- **Never touch `_playerRoot.rotation`** — the game's `CharacterMovement` and Photon Fusion networking own this transform. We rotate spine bones instead.
- **Explicit bone paths** via `Transform.Find("Bip001/Bip001 Pelvis/.../Bip001 Head")` — equipment meshes (gloves, armor) contain duplicate Biped skeletons that break depth-first search.
- **Shadow-only renderers** instead of disabling — the player still casts a shadow.
- **Hand bone exception** — renderers under `Bip001 L Hand` / `Bip001 R Hand` stay visible so you can see held weapons and tools.
- **Multiplayer** — `FindLocalPlayer()` checks Photon Fusion `NetworkObject.HasInputAuthority` to identify the local player among multiple "Player"-tagged objects.

## Development Workflow

### Quick iteration

```bash
dotnet build -c Release && cp bin/Release/net6.0/AskaFirstPerson.dll "$APPDATA/r2modmanPlus-local/ASKA/profiles/Default/BepInEx/plugins/"
```

Then launch via r2modman. Check `BepInEx/LogOutput.log` for your plugin's log messages (prefixed with `Aska First Person Camera`).

### Using the Discovery plugin

The `Discovery/` folder contains a runtime inspection plugin that dumps scene hierarchy, bone paths, and component lists to the log. Useful when investigating new game updates or character models.

```bash
cd Discovery
dotnet build -c Release
cp bin/Release/net6.0/AskaDiscovery.dll "$APPDATA/r2modmanPlus-local/ASKA/profiles/Default/BepInEx/plugins/"
```

Press **F8** in-game to trigger a dump, then check `LogOutput.log`.

### Debugging tips

- **Diagnostic logging** is built into the mod — look for `[Diag]` and `[Diagnostics]` lines in `LogOutput.log` while first-person mode is active.
- **Delete the config file** (`BepInEx/config/com.community.askafirstperson.cfg`) to reset all settings to defaults.
- **Delete `BepInEx/interop/`** after game updates — BepInEx regenerates interop assemblies on next launch.

## How to Contribute

### Reporting issues

- Include your `BepInEx/LogOutput.log` (the full file, not just errors)
- Note whether the issue occurs on a new game or an established save
- Mention any other mods installed

### Submitting changes

**All contributions must be submitted via Pull Request.** Direct pushes to `main` are not accepted — the `main` branch is protected. This ensures every change is reviewed and tested before merging.

1. **Fork** the repository on GitHub
2. **Clone** your fork: `git clone https://github.com/YOUR_USERNAME/aska-first-person.git`
3. **Create a feature branch:** `git checkout -b feature/my-change`
4. Make your changes
5. **Test** on both a **new game** and an **established save** (equipment sub-skeletons and character state differ significantly)
6. **Verify the build:** `dotnet build -c Release`
7. **Commit** with a clear message describing **what** and **why**
8. **Push** to your fork: `git push origin feature/my-change`
9. **Open a Pull Request** against `main` on the upstream repository
10. Respond to any review feedback

### Code guidelines

- **Target .NET 6.0** — BepInEx 6 IL2CPP plugins use this framework
- **Cache everything** — no `FindObjectOfType`, `Camera.main`, or `GetComponent` calls in `Update`/`LateUpdate` hot paths. Resolve references once in `TryFindReferences()`.
- **Guard all references** — IL2CPP objects can be destroyed at any time. Null-check before every access.
- **Use explicit bone paths** — never use depth-first search for bones. Equipment meshes contain duplicate skeletons that will match first.
- **Don't touch `_playerRoot`** — rotation, position, or any transform modification on the player root will break `CharacterMovement` and network sync. Use spine bones for visual rotation.
- **Test multiplayer** — use `FindLocalPlayer()` to identify the local player. Never assume there is only one "Player"-tagged object.

### Areas where help is welcome

- **Female character (Aska) testing** — verify bone paths and equipment hiding work identically
- **Multiplayer testing** — verify no visual glitches or desync for other players in a session
- **New equipment types** — if new game updates add equipment that clips through the camera, the renderer hiding system may need updates
- **Performance profiling** — the periodic renderer re-scan and spine rotation run every frame. Profile impact on lower-end hardware.
- **Controller support** — test with different gamepad models. The Input System integration may need tweaks for non-Xbox controllers.
- **Game updates** — Aska updates periodically. Class names, bone hierarchies, or Cinemachine setup may change.

## Releasing

> **Note:** The project cannot be built in CI because it depends on BepInEx interop DLLs
> generated at runtime on each machine. Builds are done locally.

To publish a new version:

1. Update the version in `FirstPersonPlugin.cs` (`PluginVersion`) and `thunderstore/manifest.json` (`version_number`)
2. Build and package locally:
   ```bash
   ./package.sh
   ```
   This produces `dist/AskaFirstPerson-X.Y.Z.zip`.
3. Commit and tag:
   ```bash
   git commit -am "Bump version to X.Y.Z"
   git tag vX.Y.Z
   git push origin main --tags
   ```
4. A GitHub Release is auto-created by the workflow. Upload the zip from `dist/` to it.
5. Upload `dist/AskaFirstPerson-X.Y.Z.zip` to [Thunderstore](https://thunderstore.io/c/aska/)
6. Upload `dist/AskaFirstPerson-X.Y.Z.zip` to [Nexus Mods](https://www.nexusmods.com/aska/)

## License

By contributing, you agree that your contributions will be licensed under the same license as the project.
