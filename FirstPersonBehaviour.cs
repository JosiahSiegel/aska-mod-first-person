using System.Collections.Generic;
using Fusion;
using UnityEngine;
using UnityEngine.InputSystem;
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
    //  Public state — read by CameraControllerPatch
    // ------------------------------------------------------------------
    public static bool IsFirstPerson;

    // ------------------------------------------------------------------
    //  Cached references
    // ------------------------------------------------------------------
    private Camera _cam;
    private Transform _headBone;
    private Transform _playerRoot;
    private Transform _geometryParent;
    private Transform _ragnarRoot;
    private Transform _skeletonRoot; // ragnarRoot/master — the MAIN skeleton (not equipment sub-skeletons)
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

        // --- Toggle: keyboard ---
        bool togglePressed = Input.GetKeyDown(FirstPersonPlugin.CfgToggleKey.Value);

        // --- Toggle: gamepad right stick click (R3) ---
        if (!togglePressed)
        {
            try
            {
                _gamepad = Gamepad.current;
                if (_gamepad != null && _gamepad.rightStickButton.wasPressedThisFrame)
                    togglePressed = true;
            }
            catch { }
        }

        if (togglePressed)
        {
            IsFirstPerson = !IsFirstPerson;

            if (IsFirstPerson)
                EnableFirstPerson();
            else
                DisableFirstPerson();

            FirstPersonPlugin.Log.LogInfo($"First-person mode: {(IsFirstPerson ? "ON" : "OFF")}");
        }

        if (!IsFirstPerson || _cam == null)
            return;

        // Don't process camera input when the game is paused or a
        // menu is open. Check multiple signals since different games
        // use different pause mechanisms.
        if (IsGamePaused())
            return;

        // --- Mouse input (legacy Input) ---
        float sens = FirstPersonPlugin.CfgSensitivity.Value;
        _yaw += Input.GetAxis("Mouse X") * sens;
        _pitch -= Input.GetAxis("Mouse Y") * sens;

        // --- Gamepad right stick (new Input System) ---
        try
        {
            if (_gamepad == null)
                _gamepad = Gamepad.current;
            if (_gamepad != null)
            {
                Vector2 stick = _gamepad.rightStick.ReadValue();
                float gpSens = sens * 3f; // Gamepad needs higher multiplier
                _yaw += stick.x * gpSens * Time.deltaTime * 60f;
                _pitch -= stick.y * gpSens * Time.deltaTime * 60f;
            }
        }
        catch { }

        _pitch = Mathf.Clamp(_pitch, -85f, 85f);
    }

    // Timer for periodic renderer re-scan (catches newly picked up items)
    private float _rendererScanTimer;
    private const float RendererScanInterval = 0.5f;

    // Diagnostic timer
    private float _diagTimer;

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
            HideNewRenderers();
        }

        // Diagnostic logging every 2 seconds (helps debug camera issues)
        _diagTimer += Time.deltaTime;
        if (_diagTimer >= 2f)
        {
            _diagTimer = 0f;
            float rootY = _playerRoot != null ? _playerRoot.position.y : -999f;
            float headY = _headBone != null ? _headBone.position.y : -999f;
            float camY = _cam.transform.position.y;
            string headPath = _headBone != null ? GetHierarchyPath(_headBone) : "NULL";
            string playerName = _playerRoot != null ? _playerRoot.name : "NULL";
            FirstPersonPlugin.Log.LogInfo(
                $"[Diag] player={playerName} rootY={rootY:F2} headY={headY:F2} " +
                $"headHeight={headY - rootY:F2} camY={camY:F2} " +
                $"headBone={headPath} " +
                $"spine={(_spine != null ? _spine.name : "NULL")} " +
                $"timeScale={Time.timeScale:F2}");
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

        HidePlayerModel();
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
            if (localPlayer != null)
            {
                _playerRoot = localPlayer.transform;
                _geometryParent = _playerRoot.Find("GeometryParent");

                // Find the character geometry root (ragnar, aska, freya, etc.)
                // It's the child of GeometryParent that contains a "master"
                // skeleton transform — works for any character model.
                if (_geometryParent != null)
                {
                    for (int i = 0; i < _geometryParent.childCount; i++)
                    {
                        var child = _geometryParent.GetChild(i);
                        var master = child.Find("master");
                        if (master != null)
                        {
                            _ragnarRoot = child;
                            _skeletonRoot = master;
                            FirstPersonPlugin.Log.LogInfo(
                                $"Character geometry root: {child.name}, skeleton: {master.name}");
                            break;
                        }
                    }
                }

                FirstPersonPlugin.Log.LogInfo(
                    $"Local player: {localPlayer.name}");
            }
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
                    FirstPersonPlugin.Log.LogInfo($"Head bone resolved: {GetHierarchyPath(_headBone)}");
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
        string lower = name.ToLowerInvariant();
        foreach (string prefix in LowerBodyPrefixes)
        {
            if (lower.StartsWith(prefix))
                return true;
        }
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

    private void SetRendererShadowOnly(Renderer renderer)
    {
        if (renderer != null && renderer.enabled &&
            renderer.shadowCastingMode != ShadowCastingMode.ShadowsOnly)
        {
            renderer.shadowCastingMode = ShadowCastingMode.ShadowsOnly;
            _shadowOnlyRenderers.Add(renderer);
        }
    }

    private void HidePlayerModel()
    {
        if (_playerRoot == null) return;

        _shadowOnlyRenderers.Clear();
        bool showBody = FirstPersonPlugin.CfgShowBody.Value;

        // Get ALL renderers on the entire player hierarchy
        foreach (var renderer in _playerRoot.GetComponentsInChildren<Renderer>())
        {
            if (renderer == null || !renderer.enabled) continue;

            // Always keep hand-held items visible (weapons, tools, shields)
            if (IsChildOf(renderer.transform, _leftHand) ||
                IsChildOf(renderer.transform, _rightHand))
                continue;

            // In experimental mode, also keep lower body meshes visible
            if (showBody && _ragnarRoot != null &&
                renderer.transform.parent == _ragnarRoot &&
                IsLowerBody(renderer.gameObject.name))
                continue;

            SetRendererShadowOnly(renderer);
        }

        FirstPersonPlugin.Log.LogInfo(
            $"Set {_shadowOnlyRenderers.Count} renderers to shadow-only " +
            "(body + equipment + accessories; hands kept visible)");
    }

    /// <summary>
    /// Catches renderers that appeared after the initial hide (items
    /// picked up, equipment changed, etc.) and sets them to shadow-only.
    /// Runs periodically from LateUpdate, not every frame.
    /// </summary>
    private void HideNewRenderers()
    {
        if (_playerRoot == null) return;
        bool showBody = FirstPersonPlugin.CfgShowBody.Value;

        foreach (var renderer in _playerRoot.GetComponentsInChildren<Renderer>())
        {
            if (renderer == null || !renderer.enabled) continue;

            // Already hidden
            if (renderer.shadowCastingMode == ShadowCastingMode.ShadowsOnly)
                continue;

            // Keep hand-held items visible
            if (IsChildOf(renderer.transform, _leftHand) ||
                IsChildOf(renderer.transform, _rightHand))
                continue;

            // In experimental mode, keep lower body visible
            if (showBody && _ragnarRoot != null &&
                renderer.transform.parent == _ragnarRoot &&
                IsLowerBody(renderer.gameObject.name))
                continue;

            SetRendererShadowOnly(renderer);
        }
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
        _geometryParent = null;
        _ragnarRoot = null;
        _skeletonRoot = null;
        _leftHand = null;
        _rightHand = null;
        _spine = null;
        _spine1 = null;
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

    private static string GetHierarchyPath(Transform t)
    {
        string path = t.name;
        Transform current = t;

        while (current.parent != null)
        {
            current = current.parent;
            path = current.name + "/" + path;
        }

        return path;
    }
}
