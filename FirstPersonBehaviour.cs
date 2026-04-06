using System;
using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AskaFirstPerson;

/// <summary>
/// Core MonoBehaviour that drives first-person camera positioning, mouse look,
/// and player model visibility. Attached to a persistent BepInEx GameObject.
///
/// Aska architecture (discovered via runtime inspection):
///   Camera:  CinemachineCamera(Clone) → CinemachineBrain drives multiple
///            CinemachineVirtualCamera instances (TPFCameraNormal, TPFCameraAim, etc.)
///   Player:  CharacterRagnar(Clone) tagged "Player", non-Humanoid Animator
///   Head:    Bip001 Head at GeometryParent/ragnar/master/Bip001/.../Bip001 Neck/Bip001 Head
///   Body meshes: direct children of GeometryParent/ragnar (ragnar_base_*, beard*, hair*)
///   Weapons:     attached to skeleton bones inside ragnar/master/Bip001/.../Hand/
///
/// Strategy: We do NOT disable CinemachineBrain (that breaks game state).
/// Instead, a Harmony prefix on CinemachineBrain.LateUpdate skips only
/// the camera positioning logic. All other game systems continue normally.
/// </summary>
public class FirstPersonBehaviour : MonoBehaviour
{
    // ------------------------------------------------------------------
    //  Public state — read by CameraControllerPatch and InputSuppressionPatch
    // ------------------------------------------------------------------
    public static bool IsFirstPerson;

    // Set every frame from Update() to drive the InputSuppressionPatch
    // postfix that hides the chord toggle button from the game while the
    // modifier is held. String-name comparison is used because Il2CppInterop
    // does not guarantee stable managed wrapper identity for native controls,
    // so reference equality fails intermittently.
    internal static string SuppressedToggleButtonName;   // e.g. "rightStickPress"
    internal static bool ChordModifierIsHeld;
    internal static bool InSelfRead; // reentrancy guard for our own button reads

    // ------------------------------------------------------------------
    //  Cached references
    // ------------------------------------------------------------------
    private Camera _cam;
    private Transform _headBone;
    private Transform _playerRoot;
    private Transform _characterRoot;  // GeometryParent/<character> (e.g. ragnar, aska)
    private Transform _skeletonRoot;   // _characterRoot/master — main skeleton, NOT equipment sub-skeletons
    private Transform _leftHand;
    private Transform _rightHand;

    // Spine bones for upper-body rotation
    private Transform _spine;
    private Transform _spine1;

    // Gamepad
    private Gamepad _gamepad;

    // ------------------------------------------------------------------
    //  Mouse-look state
    // ------------------------------------------------------------------
    private float _pitch;
    private float _yaw;

    // ------------------------------------------------------------------
    //  Saved originals for restoration
    // ------------------------------------------------------------------
    private float _savedFOV;
    private float _savedNearClip;

    // ------------------------------------------------------------------
    //  Smoothing / motion dampening
    // ------------------------------------------------------------------
    private Vector3 _smoothVelocity;
    private Vector3 _stableBasePos; // Dampened base position (reduces combat shake)

    // ------------------------------------------------------------------
    //  Renderers set to shadow-only (body meshes that still cast shadows)
    // ------------------------------------------------------------------
    private readonly List<Renderer> _shadowOnlyRenderers = new();
    private float _rendererScanTimer;
    private const float RendererScanInterval = 0.5f;

    // ==================================================================
    //  Unity callbacks
    // ==================================================================

    private void Update()
    {
        // Only allow first-person in gameplay (StreamingWorld scene).
        // Auto-disable if player returns to main menu while FP is on.
        if (!IsInGameplay())
        {
            if (IsFirstPerson)
            {
                IsFirstPerson = false;
                DisableFirstPerson();
                ClearCachedReferences();
                FirstPersonPlugin.Log.LogInfo("Left gameplay — first-person auto-disabled");
            }
            return;
        }

        // Fetch gamepad once. Try/catch is load-bearing under Il2CppInterop —
        // Gamepad.current can throw during scene transitions.
        try { _gamepad = Gamepad.current; }
        catch { _gamepad = null; }

        // --- Toggle detection ---
        bool togglePressed = Input.GetKeyDown(FirstPersonPlugin.CfgToggleKey.Value)
                             || TryReadGamepadToggle();

        if (togglePressed)
        {
            IsFirstPerson = !IsFirstPerson;
            if (IsFirstPerson) EnableFirstPerson();
            else DisableFirstPerson();
            FirstPersonPlugin.Log.LogInfo($"First-person mode: {(IsFirstPerson ? "ON" : "OFF")}");
        }

        if (!IsFirstPerson || _cam == null)
            return;

        // Don't process camera input when the game is paused or a menu is open.
        if (IsGamePaused())
            return;

        // --- Mouse look (legacy Input) ---
        float sens = FirstPersonPlugin.CfgSensitivity.Value;
        _yaw += Input.GetAxis("Mouse X") * sens;
        _pitch -= Input.GetAxis("Mouse Y") * sens;

        // --- Gamepad right stick (new Input System) ---
        // Separate try/catch from the fetch above: a successful Gamepad.current
        // does not guarantee subsequent property reads can't throw under interop.
        if (_gamepad != null)
        {
            try
            {
                Vector2 stick = _gamepad.rightStick.ReadValue();
                float gpSens = sens * 3f; // Gamepad needs higher multiplier
                _yaw += stick.x * gpSens * Time.deltaTime * 60f;
                _pitch -= stick.y * gpSens * Time.deltaTime * 60f;
            }
            catch { }
        }

        _pitch = Mathf.Clamp(_pitch, -85f, 85f);
    }

    /// <summary>
    /// Republishes the suppression state read by InputSuppressionPatch every
    /// frame and reports whether the gamepad chord toggled this frame.
    /// InSelfRead bypasses the patch so chord detection sees the real button.
    ///
    /// Also performs device-state clamping: while the modifier is held, we
    /// forcibly write 0 to the toggle button's device state via
    /// InputState.Change. This catches InputAction.performed callbacks that
    /// read state from the device buffer rather than through property getters.
    /// Clamping runs every frame the modifier is held; the first frame of a
    /// press may still leak through (event-queue race), but subsequent frames
    /// are clean and any is-pressed polling by game code sees 0.
    ///
    /// Outer try/catch is load-bearing under Il2CppInterop.
    /// </summary>
    private bool TryReadGamepadToggle()
    {
        if (_gamepad == null)
        {
            ResetSuppressionState();
            return false;
        }

        try
        {
            InSelfRead = true;
            try
            {
                string toggleName = FirstPersonPlugin.CfgGamepadToggleButton.Value;
                var toggleBtn = GetGamepadButton(_gamepad, toggleName);
                string modName = FirstPersonPlugin.CfgGamepadModifierButton.Value;
                bool noModifier = string.Equals(modName, "None", StringComparison.OrdinalIgnoreCase);
                bool modHeld = false;
                if (!noModifier)
                {
                    var modBtn = GetGamepadButton(_gamepad, modName);
                    modHeld = modBtn != null && modBtn.isPressed;
                }

                bool wasHeld = ChordModifierIsHeld;

                // Publish suppression state for the patch postfix.
                // Control name is what the Harmony postfix compares against
                // (not the managed wrapper reference — it's unstable under interop).
                SuppressedToggleButtonName = (noModifier || toggleBtn == null)
                    ? null
                    : toggleBtn.name;
                ChordModifierIsHeld = modHeld;

                // Reset the per-hold logging latch when the modifier
                // transitions state, so the next press re-emits the
                // diagnostic line.
                if (wasHeld != modHeld)
                    InputSuppressionPatch.LoggedMagnitudeThisHold = false;

                // Clamp device state to 0 every frame the modifier is
                // held. The very first frame of an R3 press may still
                // reach the game (it was queued before we clamped), but
                // from frame 2 onward the state is 0 and any action-callback
                // path that reads from device state sees "not pressed".
                if (modHeld && toggleBtn != null)
                {
                    try { InputState.Change(toggleBtn, 0f); }
                    catch { }
                }

                return toggleBtn != null
                       && toggleBtn.wasPressedThisFrame
                       && (noModifier || modHeld);
            }
            finally { InSelfRead = false; }
        }
        catch { return false; }
    }

    /// <summary>
    /// Clears all suppression-state fields. Called on gamepad loss,
    /// scene exit, and reference reset.
    /// </summary>
    private static void ResetSuppressionState()
    {
        SuppressedToggleButtonName = null;
        ChordModifierIsHeld = false;
        InputSuppressionPatch.LoggedMagnitudeThisHold = false;
    }

    private void LateUpdate()
    {
        if (!IsFirstPerson)
            return;

        if (_cam == null || _headBone == null)
        {
            TryFindReferences();
            return;
        }

        // Don't move camera when paused
        if (IsGamePaused())
            return;

        // --- Motion dampening ---
        // The camera tracks the head bone for XZ movement (responsive)
        // but dampens the Y axis to reduce head bob and combat jitter.
        // This keeps lateral movement feeling snappy while reducing
        // the vertical shake that causes motion sickness.
        float dampening = FirstPersonPlugin.CfgMotionDampening.Value;

        // Horizontal: always follow head bone directly (no lag)
        float targetX = _headBone.position.x;
        float targetZ = _headBone.position.z;

        // Vertical: blend between raw head Y and a smoothed Y
        // This filters out rapid vertical changes (hits, bobs, rolls)
        // while keeping the correct base height.
        float rawY = _headBone.position.y;
        float smoothLerpSpeed = Mathf.Lerp(30f, 6f, dampening);
        _stableBasePos.y = Mathf.Lerp(_stableBasePos.y, rawY,
            Time.deltaTime * smoothLerpSpeed);
        float targetY = Mathf.Lerp(rawY, _stableBasePos.y, dampening);

        Vector3 basePos = new Vector3(targetX, targetY, targetZ);

        // Apply offsets
        Vector3 forward = _cam.transform.forward;
        Vector3 targetPos = basePos
            + Vector3.up * FirstPersonPlugin.CfgVerticalOffset.Value
            + forward * FirstPersonPlugin.CfgForwardOffset.Value;

        // Final smoothing pass
        _cam.transform.position = Vector3.SmoothDamp(
            _cam.transform.position,
            targetPos,
            ref _smoothVelocity,
            1f / FirstPersonPlugin.CfgSmoothSpeed.Value);

        // Rotation: purely input-driven, decoupled from animations
        _cam.transform.rotation = Quaternion.Euler(_pitch, _yaw, 0f);

        // --- Upper body rotation via spine bones ---
        // Rotate spine bones so the upper body (arms, hands, held items)
        // faces the camera direction. This NEVER touches _playerRoot,
        // so the game's CharacterMovement, Rigidbody, and network sync
        // are completely unaffected. Legs continue facing the movement
        // direction naturally.
        //
        // Applied in LateUpdate AFTER the Animator has written bone
        // transforms, so we add our rotation on top of the animation.
        if (_spine != null && _playerRoot != null)
        {
            float bodyYaw = _playerRoot.eulerAngles.y;
            float deltaYaw = Mathf.DeltaAngle(bodyYaw, _yaw);

            // Clamp so the upper body doesn't twist unnaturally
            deltaYaw = Mathf.Clamp(deltaYaw, -80f, 80f);

            // Distribute rotation across spine bones for a natural look
            float perBone = deltaYaw * 0.5f; // split between 2 bones
            _spine.localRotation = _spine.localRotation
                * Quaternion.Euler(0f, perBone, 0f);

            if (_spine1 != null)
                _spine1.localRotation = _spine1.localRotation
                    * Quaternion.Euler(0f, perBone, 0f);
        }

        // Periodically re-scan for new renderers (items picked up,
        // equipment changed, etc.) and hide them too.
        _rendererScanTimer += Time.deltaTime;
        if (_rendererScanTimer >= RendererScanInterval)
        {
            _rendererScanTimer = 0f;
            HidePlayerRenderers(rebuildCache: false);
        }
    }

    // ==================================================================
    //  Mode transitions
    // ==================================================================

    private void EnableFirstPerson()
    {
        TryFindReferences();

        if (_cam == null)
        {
            FirstPersonPlugin.Log.LogWarning(
                "Cannot enable first-person — main camera not found. " +
                "Ensure you are in-game (not a loading screen).");
            IsFirstPerson = false;
            return;
        }

        // Save originals
        _savedFOV = _cam.fieldOfView;
        _savedNearClip = _cam.nearClipPlane;

        // Apply first-person overrides
        _cam.fieldOfView = FirstPersonPlugin.CfgFOV.Value;
        _cam.nearClipPlane = FirstPersonPlugin.CfgNearClip.Value;

        // Seed yaw/pitch from current camera orientation
        Vector3 euler = _cam.transform.eulerAngles;
        _yaw = euler.y;
        _pitch = euler.x > 180f ? euler.x - 360f : euler.x;

        // Initialize stable base position at head
        _stableBasePos = _headBone != null ? _headBone.position : _cam.transform.position;

        // Diagnostic log — helps debug camera height issues
        if (_headBone != null && _playerRoot != null)
        {
            float rootY = _playerRoot.position.y;
            float headY = _headBone.position.y;
            FirstPersonPlugin.Log.LogInfo(
                $"[Diagnostics] PlayerRoot Y={rootY:F2}, HeadBone Y={headY:F2}, " +
                $"HeadHeight={headY - rootY:F2}, Camera Y={_cam.transform.position.y:F2}, " +
                $"Player={_playerRoot.name}");
        }

        HidePlayerRenderers(rebuildCache: true);
    }

    private void DisableFirstPerson()
    {
        if (_cam != null)
        {
            _cam.fieldOfView = _savedFOV;
            _cam.nearClipPlane = _savedNearClip;
        }

        ShowPlayerModel();
    }

    // ==================================================================
    //  Reference discovery
    // ==================================================================

    private void TryFindReferences()
    {
        if (_cam == null)
            _cam = Camera.main;

        // Player: find the LOCAL player (important in multiplayer).
        // In Photon Fusion, the local player has InputAuthority.
        // Falls back to closest-to-camera if Fusion check fails.
        if (_playerRoot == null)
        {
            GameObject localPlayer = FindLocalPlayer();
            if (localPlayer == null)
                return;

            _playerRoot = localPlayer.transform;

            // Find the character geometry root (ragnar, aska, freya, etc.)
            // It's the child of GeometryParent that contains a "master"
            // skeleton transform — works for any character model.
            var geometryParent = _playerRoot.Find("GeometryParent");
            if (geometryParent != null)
            {
                for (int i = 0; i < geometryParent.childCount; i++)
                {
                    var child = geometryParent.GetChild(i);
                    var master = child.Find("master");
                    if (master != null)
                    {
                        _characterRoot = child;
                        _skeletonRoot = master;
                        FirstPersonPlugin.Log.LogInfo(
                            $"Character geometry root: {child.name}, skeleton: {master.name}");
                        break;
                    }
                }
            }

            FirstPersonPlugin.Log.LogInfo($"Local player: {localPlayer.name}");
        }

        // All bone lookups use EXPLICIT PATHS from _skeletonRoot (master)
        // to avoid matching duplicate skeletons inside equipment meshes.
        // Equipment like Leather0_Gloves_Ragnar contains a full Bip001
        // skeleton — depth-first search finds its bones before the real ones.
        // Transform.Find("path/to/bone") is deterministic and unambiguous.

        if (_skeletonRoot != null)
        {
            const string bipedBase = "Bip001/Bip001 Pelvis/Bip001 Spine";

            if (_headBone == null)
            {
                _headBone = _skeletonRoot.Find(
                    bipedBase + "/Bip001 Spine1/Bip001 Neck/Bip001 Head");

                if (_headBone != null)
                    FirstPersonPlugin.Log.LogInfo($"Head bone resolved: {_headBone.name}");
                else
                    FirstPersonPlugin.Log.LogWarning(
                        "Could not find head bone via explicit path. " +
                        "The skeleton hierarchy may have changed.");
            }

            if (_leftHand == null)
                _leftHand = _skeletonRoot.Find(
                    bipedBase + "/Bip001 Spine1/Bip001 L Clavicle/Bip001 L UpperArm/Bip001 L Forearm/Bip001 L Hand");

            if (_rightHand == null)
                _rightHand = _skeletonRoot.Find(
                    bipedBase + "/Bip001 Spine1/Bip001 R Clavicle/Bip001 R UpperArm/Bip001 R Forearm/Bip001 R Hand");

            if (_spine == null)
                _spine = _skeletonRoot.Find(bipedBase);

            if (_spine1 == null)
                _spine1 = _skeletonRoot.Find(bipedBase + "/Bip001 Spine1");
        }
    }

    // ==================================================================
    //  Player model visibility
    //
    //  Hide EVERYTHING on the player (body, hair, equipment, backpack,
    //  cape, quiver, stowed weapons — all of it) as shadow-only.
    //  Then selectively re-enable renderers under the hand bones so
    //  held weapons/tools remain visible.
    //
    //  This catches all accessories/equipment regardless of where
    //  they're attached (skeleton bones, item containers, etc.)
    //  and prevents anything from clipping into the camera during
    //  rolls, sprints, or looking around.
    //
    //  Config ShowLowerBody = true: also keep forearm/hand/leg body
    //  meshes visible (experimental, has mesh artifacts).
    // ==================================================================

    // Body meshes safe to show in experimental mode (far from camera).
    private static readonly string[] LowerBodyPrefixes =
    {
        "ragnar_base_forearm",
        "ragnar_base_hand",
        "ragnar_base_pants",
        "ragnar_base_boots",
        "ragnar_base_belt",
        "ragnar_base_glove",
    };

    private static bool IsLowerBody(string name)
    {
        foreach (string prefix in LowerBodyPrefixes)
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Checks if a transform is a descendant of the given ancestor.
    /// </summary>
    private static bool IsChildOf(Transform t, Transform ancestor)
    {
        if (ancestor == null) return false;
        Transform current = t.parent;
        while (current != null)
        {
            if (current == ancestor) return true;
            current = current.parent;
        }
        return false;
    }

    /// <summary>
    /// Walks every renderer under the player and sets it to ShadowsOnly,
    /// except for held items (descendants of Bip001 L/R Hand) and — when the
    /// experimental ShowLowerBody flag is on — lower-body meshes parented
    /// directly under the character root.
    ///
    /// rebuildCache=true: initial hide on FP enable. Clears the restore list,
    /// processes all renderers, logs the count.
    /// rebuildCache=false: periodic re-scan. Catches newly equipped items
    /// without disturbing the existing restore list.
    /// </summary>
    private void HidePlayerRenderers(bool rebuildCache)
    {
        if (_playerRoot == null) return;
        if (rebuildCache) _shadowOnlyRenderers.Clear();

        bool showBody = FirstPersonPlugin.CfgShowBody.Value;
        foreach (var renderer in _playerRoot.GetComponentsInChildren<Renderer>())
        {
            if (renderer == null || !renderer.enabled) continue;
            if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly) continue;

            // Keep hand-held items visible (weapons, tools, shields).
            if (IsChildOf(renderer.transform, _leftHand) ||
                IsChildOf(renderer.transform, _rightHand))
                continue;

            // Experimental: keep lower-body meshes visible.
            if (showBody && _characterRoot != null &&
                renderer.transform.parent == _characterRoot &&
                IsLowerBody(renderer.gameObject.name))
                continue;

            renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            _shadowOnlyRenderers.Add(renderer);
        }

        if (rebuildCache)
            FirstPersonPlugin.Log.LogInfo(
                $"Set {_shadowOnlyRenderers.Count} renderers to shadow-only " +
                "(body + equipment + accessories; hands kept visible)");
    }

    private void ShowPlayerModel()
    {
        foreach (var renderer in _shadowOnlyRenderers)
        {
            if (renderer != null)
                renderer.shadowCastingMode = ShadowCastingMode.On;
        }
        _shadowOnlyRenderers.Clear();
    }

    // ==================================================================
    //  Utility
    // ==================================================================

    /// <summary>
    /// Finds the local player in singleplayer or multiplayer.
    /// Uses Photon Fusion's InputAuthority to identify the local player
    /// among multiple "Player"-tagged objects. Falls back to the player
    /// closest to the camera if Fusion check fails.
    /// </summary>
    private GameObject FindLocalPlayer()
    {
        var players = GameObject.FindGameObjectsWithTag("Player");
        if (players == null || players.Length == 0)
            return null;

        // Single player — only one candidate
        if (players.Length == 1)
            return players[0];

        // Multiplayer — try Fusion InputAuthority first
        try
        {
            foreach (var p in players)
            {
                var netObj = p.GetComponent<NetworkObject>();
                if (netObj != null && netObj.HasInputAuthority)
                    return p;
            }
        }
        catch
        {
            // Fusion API not available or errored — fall through
        }

        // Fallback — pick the player closest to the camera
        if (_cam == null) _cam = Camera.main;
        if (_cam == null) return players[0];

        GameObject closest = null;
        float closestDist = float.MaxValue;
        foreach (var p in players)
        {
            float dist = Vector3.Distance(
                p.transform.position, _cam.transform.position);
            if (dist < closestDist)
            {
                closestDist = dist;
                closest = p;
            }
        }

        return closest;
    }

    /// <summary>
    /// Returns true only when the player is in actual gameplay
    /// (not main menu, loading screen, etc.).
    /// The gameplay scene in Aska is "StreamingWorld".
    /// </summary>
    private static bool IsInGameplay()
    {
        return SceneManager.GetActiveScene().name == "StreamingWorld";
    }

    /// <summary>
    /// Clears all cached references so they're re-discovered when
    /// entering a new game session (different character, multiplayer, etc.).
    /// </summary>
    private void ClearCachedReferences()
    {
        _cam = null;
        _headBone = null;
        _playerRoot = null;
        _characterRoot = null;
        _skeletonRoot = null;
        _leftHand = null;
        _rightHand = null;
        _spine = null;
        _spine1 = null;

        // Drop input suppression so the patch postfixes do nothing
        // until the next gameplay session re-publishes the state.
        ResetSuppressionState();
    }

    /// <summary>
    /// Detects if the game is paused or a menu is open.
    /// Checks multiple signals since different situations use different mechanisms.
    /// </summary>
    private static bool IsGamePaused()
    {
        // Time stopped (common pause mechanism)
        if (Time.timeScale == 0f)
            return true;

        // Cursor visible or unlocked (menu/inventory/map open)
        if (Cursor.visible || Cursor.lockState != CursorLockMode.Locked)
            return true;

        return false;
    }

    /// <summary>
    /// Maps a config string to a gamepad ButtonControl.
    /// Returns null if the name is unrecognized.
    /// </summary>
    private static ButtonControl GetGamepadButton(Gamepad pad, string name)
    {
        return name switch
        {
            "North" or "ButtonNorth" => pad.buttonNorth,
            "South" or "ButtonSouth" => pad.buttonSouth,
            "East" or "ButtonEast" => pad.buttonEast,
            "West" or "ButtonWest" => pad.buttonWest,
            "LeftShoulder" or "LeftBumper" => pad.leftShoulder,
            "RightShoulder" or "RightBumper" => pad.rightShoulder,
            "LeftTrigger" => pad.leftTrigger,
            "RightTrigger" => pad.rightTrigger,
            "LeftStickButton" => pad.leftStickButton,
            "RightStickButton" => pad.rightStickButton,
            "Start" or "StartButton" => pad.startButton,
            "Select" or "SelectButton" => pad.selectButton,
            "DpadUp" => pad.dpad.up,
            "DpadDown" => pad.dpad.down,
            "DpadLeft" => pad.dpad.left,
            "DpadRight" => pad.dpad.right,
            _ => null,
        };
    }

}
