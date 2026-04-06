using HarmonyLib;

namespace AskaFirstPerson;

/// <summary>
/// Harmony patch that suppresses CinemachineBrain.LateUpdate while
/// first-person mode is active. This prevents Cinemachine from overriding
/// our camera position/rotation each frame, while keeping all other
/// CinemachineBrain functionality (input routing, state management) intact.
///
/// Uses string-based patching for IL2CPP interop compatibility.
/// </summary>
[HarmonyPatch("Cinemachine.CinemachineBrain", "LateUpdate")]
public static class CinemachineBrainPatch
{
    // Return false = skip original method (first-person active)
    // Return true  = let original run (third-person)
    [HarmonyPrefix]
    public static bool Prefix() => !FirstPersonBehaviour.IsFirstPerson;
}
