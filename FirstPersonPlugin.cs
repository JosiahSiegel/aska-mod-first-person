using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using HarmonyLogger = HarmonyLib.Tools.Logger;
using UnityEngine;

namespace AskaFirstPerson;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class FirstPersonPlugin : BasePlugin
{
    public const string PluginGuid = "com.community.askafirstperson";
    public const string PluginName = "Aska First Person Camera";
    public const string PluginVersion = "1.0.2";

    internal static new ManualLogSource Log;

    // Camera settings
    public static ConfigEntry<float> CfgFOV;
    public static ConfigEntry<float> CfgSensitivity;
    public static ConfigEntry<float> CfgNearClip;
    public static ConfigEntry<float> CfgVerticalOffset;
    public static ConfigEntry<float> CfgForwardOffset;
    public static ConfigEntry<float> CfgSmoothSpeed;
    public static ConfigEntry<float> CfgMotionDampening;

    // Visibility settings
    public static ConfigEntry<bool> CfgShowBody;
    // Control settings
    public static ConfigEntry<KeyCode> CfgToggleKey;
    public static ConfigEntry<string> CfgGamepadToggleButton;
    public static ConfigEntry<string> CfgGamepadModifierButton;

    public override void Load()
    {
        Log = base.Log;

        // Camera
        CfgFOV = Config.Bind("Camera", "FOV", 80f,
            new ConfigDescription("First-person field of view",
                new AcceptableValueRange<float>(60f, 120f)));

        CfgSensitivity = Config.Bind("Camera", "MouseSensitivity", 2.0f,
            new ConfigDescription("Mouse look sensitivity",
                new AcceptableValueRange<float>(0.1f, 10f)));

        CfgNearClip = Config.Bind("Camera", "NearClipPlane", 0.05f,
            "Near clip plane distance — lower values prevent seeing inside geometry");

        CfgVerticalOffset = Config.Bind("Camera", "VerticalOffset", 0.1f,
            "Vertical offset above the head bone (metres)");

        CfgForwardOffset = Config.Bind("Camera", "ForwardOffset", 0.12f,
            "Forward offset from the head bone (metres)");

        CfgSmoothSpeed = Config.Bind("Camera", "SmoothSpeed", 15f,
            new ConfigDescription("Position smoothing factor — higher = less smoothing",
                new AcceptableValueRange<float>(1f, 100f)));

        CfgMotionDampening = Config.Bind("Camera", "MotionDampening", 0.4f,
            new ConfigDescription(
                "Reduces vertical camera shake from combat, rolls, and head bob. " +
                "Only affects up/down movement — lateral tracking stays responsive. " +
                "0 = no dampening (raw head tracking), 1 = maximum dampening. " +
                "Recommended 0.3-0.5.",
                new AcceptableValueRange<float>(0f, 1f)));

        // Visibility
        CfgShowBody = Config.Bind("Visibility", "ShowLowerBody", false,
            "Show lower body (forearms, hands, legs) in first-person. " +
            "Off = cleaner look (shadow-only body, weapons visible). " +
            "On = see arms/legs but may have visual artifacts (mesh seams, backface culling).");

        // Controls
        CfgToggleKey = Config.Bind("Controls", "ToggleKey", KeyCode.F5,
            "Key to toggle between first-person and third-person camera");

        CfgGamepadToggleButton = Config.Bind("Controls", "GamepadToggleButton", "RightStickButton",
            "Gamepad button to toggle first-person mode. " +
            "Valid: North, South, East, West, LeftShoulder, RightShoulder, " +
            "LeftTrigger, RightTrigger, LeftStickButton, RightStickButton, " +
            "Start, Select, DpadUp, DpadDown, DpadLeft, DpadRight");

        CfgGamepadModifierButton = Config.Bind("Controls", "GamepadModifierButton", "LeftShoulder",
            "Modifier button that must be held while pressing the toggle button. " +
            "Set to \"None\" to disable the modifier (bare button press). " +
            "Default: LeftShoulder (LB + R3 chord avoids conflicting with game actions). " +
            "Valid: None, North, South, East, West, LeftShoulder, RightShoulder, " +
            "LeftTrigger, RightTrigger, LeftStickButton, RightStickButton, " +
            "Start, Select, DpadUp, DpadDown, DpadLeft, DpadRight");

        // Apply Harmony patches per-class so a single failed target (e.g. a
        // method that doesn't exist on this machine's Il2CppInterop assembly)
        // does not abort the entire plugin load. Harmony.PatchAll() is all-or-
        // nothing — one broken patch class and nothing else runs.
        var harmony = new Harmony(PluginGuid);
        var active = new List<string>();

        // Temporarily drop the HarmonyX Warn channel during patch registration.
        // Each CreateClassProcessor().Patch() call triggers an internal
        // AccessTools.GetTypesFromAssembly scan of every loaded assembly;
        // UnityEngine.CoreModule in the Il2Cpp interop bundle has three types
        // (DirectChildrenEnumerable, <>c, IdentityAttributes) that fail to
        // load, producing ~15 lines of stack trace warnings per patch. They
        // are cosmetic and affect no behavior — but they swamp the log and
        // hide real warnings. BepInEx defaults HarmonyLogger.ChannelFilter to
        // Warn | Error; we narrow to Error for the registration loop only
        // and restore it afterward so real post-load Warn entries still surface.
        var savedHarmonyChannels = HarmonyLogger.ChannelFilter;
        HarmonyLogger.ChannelFilter = savedHarmonyChannels & ~HarmonyLogger.LogChannel.Warn;
        try
        {
            // CinemachineBrain suppression — required for first-person to work at
            // all, but still wrapped so a theoretical failure degrades gracefully.
            if (TryPatchClass(harmony, typeof(CinemachineBrainPatch)))
                active.Add("Cinemachine");

            // Input suppression — list lives in InputSuppressionPatch so the
            // DRY mapping between layer name and patch class stays in one place.
            foreach (var (layer, type) in InputSuppressionPatch.PatchClasses)
            {
                if (TryPatchClass(harmony, type))
                    active.Add(layer);
            }
        }
        finally
        {
            HarmonyLogger.ChannelFilter = savedHarmonyChannels;
        }

        Log.LogInfo($"Harmony patches active: [{string.Join(", ", active)}]");

        // Register the MonoBehaviour that drives camera logic each frame
        AddComponent<FirstPersonBehaviour>();

        string modKey = CfgGamepadModifierButton.Value;
        string padToggle = CfgGamepadToggleButton.Value;
        string padDesc = string.Equals(modKey, "None", System.StringComparison.OrdinalIgnoreCase)
            ? padToggle
            : $"{modKey}+{padToggle}";
        Log.LogInfo($"{PluginName} v{PluginVersion} loaded — press [{CfgToggleKey.Value}] or gamepad [{padDesc}] to toggle");
    }

    /// <summary>
    /// Registers a single Harmony patch class, swallowing (and logging) any
    /// exception so other patch classes can still apply. Returns true if the
    /// class was patched successfully.
    /// </summary>
    private static bool TryPatchClass(Harmony harmony, Type type)
    {
        try
        {
            harmony.CreateClassProcessor(type).Patch();
            return true;
        }
        catch (Exception ex)
        {
            Log.LogWarning($"Skipping Harmony patch {type.Name}: {ex.Message}");
            return false;
        }
    }
}
