using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace AskaDiscovery;

[BepInPlugin("com.community.askadiscovery", "Aska Discovery", "1.0.0")]
public class DiscoveryPlugin : BasePlugin
{
    internal static new ManualLogSource Log;

    public override void Load()
    {
        Log = base.Log;
        AddComponent<DiscoveryBehaviour>();
        Log.LogInfo("Discovery plugin loaded — press F8 in-game to dump scene info");
    }
}

public class DiscoveryBehaviour : MonoBehaviour
{
    void Update()
    {
        if (Input.GetKeyDown(KeyCode.F8))
        {
            DiscoveryPlugin.Log.LogWarning("=== DISCOVERY V2 DUMP START ===");
            DumpSpineChain();
            DumpVirtualCameras();
            DumpTPFCamPoint();
            DiscoveryPlugin.Log.LogWarning("=== DISCOVERY V2 DUMP END ===");
        }
    }

    private void DumpSpineChain()
    {
        DiscoveryPlugin.Log.LogWarning("--- SPINE → HEAD CHAIN ---");

        var player = GameObject.FindWithTag("Player");
        if (player == null)
        {
            DiscoveryPlugin.Log.LogError("Player not found");
            return;
        }

        // Find "Bip001 Spine" and trace every child down to leaves
        var spine = FindChildByName(player.transform, "Bip001 Spine");
        if (spine != null)
        {
            DiscoveryPlugin.Log.LogWarning($"Found spine: {GetPath(spine)}");
            DumpHierarchy(spine, 0, 15);
        }
        else
        {
            DiscoveryPlugin.Log.LogWarning("Bip001 Spine not found — dumping full Bip001 tree");
            var bip = FindChildByName(player.transform, "Bip001");
            if (bip != null)
                DumpHierarchy(bip, 0, 15);
        }

        // Also search for anything with "head" in the name
        DiscoveryPlugin.Log.LogWarning("--- ALL TRANSFORMS CONTAINING 'head' ---");
        SearchTransformsByName(player.transform, "head");

        DiscoveryPlugin.Log.LogWarning("--- ALL TRANSFORMS CONTAINING 'neck' ---");
        SearchTransformsByName(player.transform, "neck");
    }

    private void DumpVirtualCameras()
    {
        DiscoveryPlugin.Log.LogWarning("--- CINEMACHINE VIRTUAL CAMERAS ---");

        // Search for all MonoBehaviours with "Cinemachine" in the type name
        foreach (var mb in FindObjectsOfType<MonoBehaviour>())
        {
            try
            {
                var typeName = mb.GetIl2CppType()?.FullName ?? mb.GetType().FullName;
                if (typeName.Contains("Cinemachine"))
                {
                    DiscoveryPlugin.Log.LogWarning($"  {typeName} on '{mb.gameObject.name}' path={GetPath(mb.transform)}");
                }
            }
            catch { }
        }

        // Dump components on TPFCameraNormal specifically
        var tpfNormal = GameObject.Find("TPFCameraNormal(Clone)");
        if (tpfNormal != null)
        {
            DiscoveryPlugin.Log.LogWarning("--- TPFCameraNormal(Clone) COMPONENTS ---");
            foreach (var c in tpfNormal.GetComponents<Component>())
            {
                if (c != null)
                {
                    var typeName = c.GetIl2CppType()?.FullName ?? c.GetType().FullName;
                    DiscoveryPlugin.Log.LogWarning($"  {typeName}");
                }
            }
            DumpHierarchy(tpfNormal.transform, 0, 3);
        }
    }

    private void DumpTPFCamPoint()
    {
        DiscoveryPlugin.Log.LogWarning("--- TPFCamPoint ---");
        var camPoint = GameObject.Find("TPFCamPoint");
        if (camPoint != null)
        {
            DiscoveryPlugin.Log.LogWarning($"TPFCamPoint path: {GetPath(camPoint.transform)}");
            foreach (var c in camPoint.GetComponents<Component>())
            {
                if (c != null)
                {
                    var typeName = c.GetIl2CppType()?.FullName ?? c.GetType().FullName;
                    DiscoveryPlugin.Log.LogWarning($"  {typeName}");
                }
            }
            DumpHierarchy(camPoint.transform, 0, 3);
        }
    }

    private void SearchTransformsByName(Transform root, string partialName)
    {
        string lower = partialName.ToLowerInvariant();
        SearchTransformsRecursive(root, lower);
    }

    private void SearchTransformsRecursive(Transform parent, string lower)
    {
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name.ToLowerInvariant().Contains(lower))
                DiscoveryPlugin.Log.LogWarning($"  FOUND: {child.name} path={GetPath(child)}");
            SearchTransformsRecursive(child, lower);
        }
    }

    private void DumpHierarchy(Transform root, int indent, int maxDepth)
    {
        if (maxDepth <= 0) return;
        string prefix = new string(' ', indent * 2);

        for (int i = 0; i < root.childCount; i++)
        {
            var child = root.GetChild(i);
            DiscoveryPlugin.Log.LogWarning($"{prefix}- {child.name} (children: {child.childCount})");
            DumpHierarchy(child, indent + 1, maxDepth - 1);
        }
    }

    private static Transform FindChildByName(Transform parent, string name)
    {
        string lower = name.ToLowerInvariant();
        for (int i = 0; i < parent.childCount; i++)
        {
            var child = parent.GetChild(i);
            if (child.name.ToLowerInvariant() == lower)
                return child;
            var result = FindChildByName(child, name);
            if (result != null)
                return result;
        }
        return null;
    }

    private static string GetPath(Transform t)
    {
        string path = t.name;
        var current = t;
        while (current.parent != null)
        {
            current = current.parent;
            path = current.name + "/" + path;
        }
        return path;
    }
}
