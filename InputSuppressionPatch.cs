using System;
using HarmonyLib;
using UnityEngine.InputSystem;

namespace AskaFirstPerson;

/// <summary>
/// Single-layer Harmony patch that hides the chord toggle button from the
/// game while the chord modifier is held.
///
/// We patch <c>UnityEngine.InputSystem.InputControl.EvaluateMagnitude()</c>,
/// the choke point InputActionState uses to decide whether a control's
/// pressed-ness has crossed the activation threshold. Returning 0 there
/// prevents action callbacks (<c>InputAction.performed</c> subscribers) from
/// firing at all — which is what Aska's combat/action bindings ultimately
/// listen on.
///
/// Empirical lineage: 1.0.2 shipped a defense-in-depth set of eight patches
/// spanning legacy <c>UnityEngine.Input.GetKey</c>, new-Input-System
/// <c>ButtonControl</c> getters, and <c>EvaluateMagnitude</c>. Live testing
/// on Aska confirmed ONLY this single layer fires — legacy and button-getter
/// layers were never observed triggering across the test session. They were
/// removed post-1.0.2 as dead code. See CLAUDE.md ("Game-specific input
/// architecture") for the empirical citation.
///
/// A <c>ReadValueAsButton</c> layer was also tried and REMOVED: the method
/// does NOT exist on <c>ButtonControl</c> in Aska's Unity.InputSystem interop
/// assembly (only on <c>InputActionState</c> / <c>InputBindingCompositeContext</c>
/// / <c>CallbackContext</c>), so <c>AccessTools.DeclaredMethod</c> returns null
/// and the patch class aborts registration.
///
/// Instance comparison is by control NAME (e.g. "rightStickPress"), not by
/// managed reference, because Il2CppInterop does NOT guarantee that two reads
/// of the same native control return the same managed wrapper instance —
/// reference equality fails intermittently on interop-wrapped objects.
///
/// Logging is throttled with a per-hold latch so we emit at most one line per
/// chord press, not one per frame. The latch is reset in
/// <see cref="FirstPersonBehaviour"/> when the modifier is released.
///
/// Patch registration is driven from <see cref="FirstPersonPlugin.Load"/> via
/// <c>CreateClassProcessor(typeof(X)).Patch()</c> inside a try/catch, so a
/// broken target does not abort the mod load. The list of patch classes lives
/// in <see cref="InputSuppressionPatch.PatchClasses"/>.
/// </summary>
internal static class InputSuppressionPatch
{
    // Throttle latch — set true after first suppression log of a hold,
    // reset by FirstPersonBehaviour on modifier release.
    internal static bool LoggedMagnitudeThisHold;

    /// <summary>
    /// Ordered list of patch classes that <see cref="FirstPersonPlugin"/>
    /// should register individually. Each entry is tagged with a short layer
    /// name so the load-time diagnostic log can report which patches survived
    /// target verification at runtime.
    /// </summary>
    internal static readonly (string Layer, Type PatchClass)[] PatchClasses =
    {
        ("Magnitude", typeof(EvaluateMagnitudePatch)),
    };

    // ------------------------------------------------------------------
    //  Shared helpers
    // ------------------------------------------------------------------

    /// <summary>
    /// True when we should suppress a read of the given button control.
    /// Name-based comparison avoids Il2CppInterop wrapper identity pitfalls.
    /// </summary>
    private static bool ShouldSuppressControl(InputControl instance)
    {
        if (instance == null) return false;
        if (FirstPersonBehaviour.InSelfRead) return false;
        if (!FirstPersonBehaviour.ChordModifierIsHeld) return false;

        string target = FirstPersonBehaviour.SuppressedToggleButtonName;
        if (string.IsNullOrEmpty(target)) return false;

        try
        {
            return string.Equals(instance.name, target, StringComparison.Ordinal);
        }
        catch { return false; }
    }

    /// <summary>
    /// Emits a diagnostic log line once per chord-hold.
    /// </summary>
    private static void LogOnce(ref bool latch, string layerTag, string detail)
    {
        if (latch) return;
        latch = true;
        try { FirstPersonPlugin.Log.LogInfo($"[Suppress.{layerTag}] {detail}"); }
        catch { }
    }

    // ------------------------------------------------------------------
    //  InputControl.EvaluateMagnitude() — the single working layer.
    //  This is the choke point InputActionState uses to decide if a
    //  control's pressed-ness has crossed the activation threshold.
    //  Returning 0f here prevents action callbacks from firing at all,
    //  which is what InputAction.performed subscribers ultimately receive.
    // ------------------------------------------------------------------

    [HarmonyPatch("UnityEngine.InputSystem.InputControl", "EvaluateMagnitude")]
    [HarmonyPatch(new Type[] { })]
    public static class EvaluateMagnitudePatch
    {
        [HarmonyPostfix]
        public static void Postfix(InputControl __instance, ref float __result)
        {
            if (__result <= 0f) return;
            if (!ShouldSuppressControl(__instance)) return;
            __result = 0f;
            LogOnce(ref LoggedMagnitudeThisHold, "Magnitude",
                $"InputControl.EvaluateMagnitude({__instance.name}) -> 0");
        }
    }
}
