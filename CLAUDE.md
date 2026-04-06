# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

A BepInEx 6 IL2CPP plugin for **Aska** (Unity 6, IL2CPP backend) that adds a toggleable first-person camera. Targets `net6.0`. Single DLL output: `AskaFirstPerson.dll`.

## Build & package

Project references BepInEx/interop DLLs from the local r2modman profile via the `R2ModManDir` MSBuild property (default: `%APPDATA%\r2modmanPlus-local\ASKA\profiles\Default\BepInEx`). **CI cannot build** — interop assemblies are generated per-machine at first game launch.

```bash
dotnet build -c Release                              # build plugin
dotnet build -c Release -p:R2ModManDir="C:\path"     # override BepInEx location
./package.sh                                         # build + zip for Thunderstore/Nexus/GitHub
./package.sh 1.2.3                                   # override version
```

Fast iterate: build, then copy `bin/Release/net6.0/AskaFirstPerson.dll` into `$APPDATA/r2modmanPlus-local/ASKA/profiles/Default/BepInEx/plugins/` and relaunch via r2modman. Logs land in `BepInEx/LogOutput.log`.

The `Discovery/` folder is a separate dev-only inspection plugin (F8 dumps scene hierarchy); it's excluded from the main csproj's compilation and shipped only as a debugging aid.

## Architecture

Four files do all the work:

- **`FirstPersonPlugin.cs`** — `BasePlugin.Load()` binds config entries, runs `Harmony.PatchAll()`, and calls `AddComponent<FirstPersonBehaviour>()` to get Unity lifecycle callbacks on a persistent BepInEx GameObject.
- **`CameraControllerPatch.cs`** — Harmony prefix on `Cinemachine.CinemachineBrain.LateUpdate` (string-based patch for IL2CPP interop) that returns `false` while FP is active. This skips *only* Cinemachine's camera positioning; brain state, input routing, and other game systems keep running.
- **`InputSuppressionPatch.cs`** — Harmony postfix on `UnityEngine.InputSystem.InputControl.EvaluateMagnitude()` (string-based) that returns `0` while the chord modifier (e.g. LB) is held and the `__instance` name matches the configured toggle button (e.g. `rightStickPress`). `EvaluateMagnitude` is the choke point `InputActionState` uses to decide whether a press has crossed the activation threshold — returning 0 prevents `InputAction.performed` callbacks from firing. Paired with device-state clamping in `FirstPersonBehaviour.TryReadGamepadToggle()` (`InputState.Change(btn, 0f)`) that catches action-callback paths reading directly from the device buffer. The `FirstPersonBehaviour.InSelfRead` reentrancy flag bypasses suppression for our own reads inside `Update()`.
- **`FirstPersonBehaviour.cs`** — the `MonoBehaviour` that does everything else: input toggle, mouse/gamepad look, `LateUpdate` camera positioning with motion dampening, spine-bone upper-body rotation, renderer visibility, and local-player discovery.

### Critical invariants (these will break things if violated)

- **Never touch `_playerRoot.position` or `_playerRoot.rotation`.** `CharacterMovement`, Rigidbody, and Photon Fusion networking own that transform. Visual body rotation is applied by rotating `Bip001 Spine` / `Bip001 Spine1` **in `LateUpdate`, after the Animator writes bone poses**, so our rotation layers on top.
- **Resolve bones via explicit `Transform.Find("Bip001/Bip001 Pelvis/.../Bip001 Head")` paths from `_skeletonRoot` (= `GeometryParent/<character>/master`).** Depth-first/name search will match duplicate Biped skeletons embedded inside equipment meshes (gloves, armor) and return the wrong bone. Character geometry root is discovered by scanning `GeometryParent` children for one that contains a `master` child — works for Ragnar, Aska, and any future character.
- **Local player in multiplayer** is found via `FindLocalPlayer()`: iterate `"Player"`-tagged objects and pick the one with `NetworkObject.HasInputAuthority` (Photon Fusion). Falls back to closest-to-camera. Never assume a single Player object exists.
- **Cache everything.** No `FindObjectOfType`, `Camera.main`, or `GetComponent` in per-frame hot paths. `TryFindReferences()` resolves once; `ClearCachedReferences()` is called when leaving the `StreamingWorld` scene so a new session re-discovers them.
- **Gate all camera work** on `IsInGameplay()` (active scene == `"StreamingWorld"`) and `IsGamePaused()`. Aska additively streams world chunks so the active scene name stays `StreamingWorld` throughout gameplay — don't switch to `sceneLoaded` events (would fire constantly). The pause check needs **both** `Time.timeScale == 0` **and** `Cursor.visible || lockState != Locked`, because inventory/map screens don't freeze time.
- **Spine rotation must be re-applied as `localRotation *= Euler(...)` every `LateUpdate`, never cached.** The Animator overwrites bone local rotations each frame before our LateUpdate runs, so our delta naturally layers on top of fresh animation data. A "cache the target rotation" optimization will silently break.
- **`Camera.main` is safe here** because Aska tags exactly one camera `MainCamera`, and we cache into `_cam` so the internal tag scan runs once per session. Don't replace with `FindObjectOfType<Camera>()` — Cinemachine spawns several virtual cameras that would match.
- **`GameObject.FindGameObjectsWithTag("Player")` can return null** on the first scene tick under Il2CppInterop, not just an empty array. Both the null check and the `Length == 0` short-circuit in `FindLocalPlayer()` are required.
- **`InputSuppressionPatch` postfix is stateless — it reads `SuppressedToggleButtonName` and `ChordModifierIsHeld` from `FirstPersonBehaviour` every call.** Those two fields **must be republished every frame from `Update()`**, including the "no gamepad" path (clear them) and the "modifier released" path (set `ChordModifierIsHeld = false`). Stale state would suppress R3 forever after the user releases LB. `ClearCachedReferences()` also clears them when leaving gameplay. Name comparison (not managed reference equality) is required because Il2CppInterop does not guarantee stable wrapper identity for native controls.
- **`FirstPersonBehaviour.InSelfRead = true` must wrap every one of our own reads of the toggle/modifier buttons inside `Update()`.** The `EvaluateMagnitude` postfix looks at this flag as a reentrancy guard — new-Input-System `ButtonControl` property getters internally route through `EvaluateMagnitude`, so our own chord-detection read of `wasPressedThisFrame` would otherwise see the suppressed `0` magnitude and the chord would never fire. Wrap reads in `try/finally` so the flag is always cleared even if `Gamepad` access throws.
- **Modifier-first ordering is intentional and not a bug.** The chord only fires when the modifier (LB) is *already held* at the moment the toggle (R3) is pressed. This matches universal chord-input UX (Ctrl+C, Cmd+S, etc.) and means users who press R3 first then LB get the normal R3 game action. Do **not** add a "lookahead buffer" config that fires the toggle if the modifier arrives within N ms after the toggle press — that adds input lag worse than the limitation it solves.

### IL2CPP / interop notes

- **Harmony patches use the string overload**, not `typeof(CinemachineBrain)`. The Il2CppInterop-wrapped type lives under an `Il2Cpp` namespace prefix and attribute-time `typeof` resolution is unreliable; `[HarmonyPatch("Cinemachine.CinemachineBrain", "LateUpdate")]` targets the original CLR name and works. `CameraControllerPatch.cs` deliberately omits `__instance` (it suppresses **all** brains globally, fine for Aska's single-brain setup). `InputSuppressionPatch.cs` shows the opposite pattern: string-based patch **with** `__instance` typed as the unhollowed `InputControl` — Harmony resolves the parameter via the patched method's declaring type at PatchAll time, so this works as long as the parameter type is the actual unhollowed type from the interop assembly (not a wrapper or `object`).
- **Aska's gamepad input flows exclusively through `InputAction.performed` callbacks that route via `InputControl.EvaluateMagnitude()`.** Confirmed empirically by 1.0.2 testing: a defense-in-depth suppression set spanning legacy `UnityEngine.Input.GetKey`, new-Input-System `ButtonControl.wasPressedThisFrame`/`isPressed` getters, and `EvaluateMagnitude` was deployed — only the `EvaluateMagnitude` postfix ever fired across many chord presses. The legacy and button-getter layers were trimmed post-1.0.2. If future Aska updates change input wiring, re-add the defense-in-depth layers; for now, a single `EvaluateMagnitude` postfix plus `InputState.Change` device-state clamping is sufficient.
- **Mixed input stacks are intentional.** Mouse/keyboard uses legacy `UnityEngine.Input` (`InputLegacyModule`); gamepad uses the new Input System (`UnityEngine.InputSystem.Gamepad.current`). Aska ships both modules and the csproj references both on purpose — do not unify.
- **`try/catch` around Fusion and Gamepad access is load-bearing, not defensive.** Under Il2CppInterop, `NetworkObject.HasInputAuthority` throws before the Fusion runner spawns, and `Gamepad.current` can throw during scene transitions. Don't remove the catches.
- **`AddComponent<FirstPersonBehaviour>()` from `BasePlugin.Load`** relies on BepInEx 6 IL2CPP's automatic `ClassInjector` registration for the generic type. Do **not** add a manual `ClassInjector.RegisterTypeInIl2Cpp<FirstPersonBehaviour>()` — that double-registers and crashes.
- **`shadowCastingMode = ShadowsOnly` is URP-safe.** Aska uses URP and URP honors the flag per-renderer with no material/shader changes. Don't try to "improve" this by toggling layers on the main camera — Aska's URP renderer-feature stack (outlines, post) reads specific layers and layer-swapping will break those effects.

### Visibility strategy

`HidePlayerRenderers(bool rebuildCache)` walks every `Renderer` under `_playerRoot` and sets `shadowCastingMode = ShadowsOnly`, **except** renderers that are descendants of `Bip001 L Hand` / `Bip001 R Hand` (so held weapons/tools stay visible). This cleanly handles body, hair, beard, backpack, cape, quiver, and stowed weapons without per-item allowlists. It is called with `rebuildCache: true` on FP enable, and with `rebuildCache: false` every 0.5s from `LateUpdate` to catch newly equipped items. `ShowPlayerModel()` restores `ShadowCastingMode.On` on exit. The `ShowLowerBody` config flag (experimental) additionally keeps `ragnar_base_forearm/hand/pants/boots/belt/glove*` visible.

### Motion dampening

`LateUpdate` tracks the head bone's XZ directly (responsive lateral movement) but blends Y between raw head-Y and a Lerp-smoothed Y based on `CfgMotionDampening` — filters combat jitter, rolls, and head bob without adding overall input lag. `Vector3.SmoothDamp` applies a final position smoothing pass. Rotation is purely input-driven (`Quaternion.Euler(_pitch, _yaw, 0)`), fully decoupled from animation.

## Releasing

Version lives in **two** places that must match: `FirstPersonPlugin.cs` → `PluginVersion`, and `thunderstore/manifest.json` → `version_number`. `package.sh` only **warns** on mismatch — it does not fail — so double-check before tagging. When controls/features change, also update the manifest `description` — it's what users see on Thunderstore. Tag `vX.Y.Z` and push; GitHub release workflow runs; manually upload `dist/AskaFirstPerson-X.Y.Z.zip` to Thunderstore and Nexus.

## Further reading in-repo

- `README.md` — end-user install, config reference, Sunshine/Moonlight workflow
- `CONTRIBUTING.md` — dev setup, PR workflow, code guidelines
