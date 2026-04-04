using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using UnityEngine;

namespace AskaFirstPerson;

[BepInPlugin(PluginGuid, PluginName, PluginVersion)]
public class FirstPersonPlugin : BasePlugin
{
    public const string PluginGuid = "com.community.askafirstperson";
    public const string PluginName = "Aska First Person Camera";
    public const string PluginVersion = "1.0.1";

    internal static new ManualLogSource Log;

    // Camera settings
    public static ConfigEntry<float> CfgFOV;
    public static ConfigEntry<float> CfgSensitivity;
    public static ConfigEntry<float> CfgNearClip;
    public static ConfigEntry<float> CfgVerticalOffset;
    public static ConfigEntry<float> CfgForwardOffset;
    public static ConfigEntry<float> CfgSmoothSpeed;
    public static ConfigEntry<float> CfgMotionDampening;

    // Player settings
    public static ConfigEntry<string> CfgHeadBoneName;

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

        // Player — Aska uses 3ds Max Biped skeleton naming
        CfgHeadBoneName = Config.Bind("Player", "HeadBoneName", "Bip001 Head",
            "Name of the head bone in the player skeleton hierarchy");

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

        // Apply Harmony patches (suppresses CinemachineBrain in FP mode)
        var harmony = new Harmony(PluginGuid);
        harmony.PatchAll();

        // Register the MonoBehaviour that drives camera logic each frame
        AddComponent<FirstPersonBehaviour>();

        string modKey = CfgGamepadModifierButton.Value;
        string padToggle = CfgGamepadToggleButton.Value;
        string padDesc = string.Equals(modKey, "None", System.StringComparison.OrdinalIgnoreCase)
            ? padToggle
            : $"{modKey}+{padToggle}";
        Log.LogInfo($"{PluginName} v{PluginVersion} loaded — press [{CfgToggleKey.Value}] or gamepad [{padDesc}] to toggle");
    }
}
