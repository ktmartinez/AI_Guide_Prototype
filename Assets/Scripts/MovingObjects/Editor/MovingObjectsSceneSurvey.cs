using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Moving Objects > Survey Themed Scenes: writes a text report per themed scene to Logs/MoverSurvey/ listing the player spawn,
// terrains, ambient audio, roads/paths and candidate objects (with world bounds, animators, colliders, static flags) and the guide's
// Key Items. Used to plan mover routes that follow real roads and avoid buildings and task objects. Changes nothing in the scenes.
public static class MovingObjectsSceneSurvey
{
    public static readonly string[] ThemedScenes =
    {
        "Monster Pet Shop", "Flower Shop", "Alien Spaceship Repair Shop", "Witch Cottage", "Kitchen", "Pharmacy"
    };

    private const string OutputFolder = "Logs/MoverSurvey";
    private const int MaxLinesPerSection = 400;

    private static readonly Regex Interesting = new Regex(
        @"road|street|path|sidewalk|lane|track|bridge|parking|crosswalk|cart|wagon|horse|cow|pig|sheep|truck|rock|pod|potion|flask|" +
        @"cauldron|lily|skull|broom|avatar|spawn|\bcar\b|bus|boat|dragon|slime|\bcat\b|\bdog\b|beholder|mimic|turtler|goleling|squidle|" +
        @"meswirl|dogar|bell|lantern|\bkey|lock|goblet|missile|rocket|launch|\bpad\b|tower|pier|port|pond|lake|market|well|stable|" +
        @"fence|\bpen\b|field|bench|plaza|bounds|ghost|robot|astronaut",
        RegexOptions.IgnoreCase);

    [MenuItem("Tools/Moving Objects/Survey Themed Scenes")]
    public static void SurveyAllScenes()
    {
        if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        Directory.CreateDirectory(OutputFolder);
        foreach (string sceneName in ThemedScenes)
        {
            EditorSceneManager.OpenScene($"Assets/Scenes/{sceneName}.unity", OpenSceneMode.Single);
            File.WriteAllText(Path.Combine(OutputFolder, sceneName + ".txt"), SurveyOpenScene());
            Debug.Log($"[Mover Survey] Wrote {OutputFolder}/{sceneName}.txt");
        }
    }

    private static string SurveyOpenScene()
    {
        StringBuilder report = new StringBuilder();
        int keyItemsLayer = LayerMask.NameToLayer("Key Items");

        report.AppendLine("== Player rigs ==");
        foreach (XROrigin rig in Object.FindObjectsOfType<XROrigin>(true))
            report.AppendLine($"{GetPath(rig.transform)} pos={Format(rig.transform.position)} fwd={Format(rig.transform.forward)} active={rig.gameObject.activeInHierarchy}");

        report.AppendLine("== Terrains ==");
        foreach (Terrain terrain in Object.FindObjectsOfType<Terrain>(true))
            report.AppendLine($"{GetPath(terrain.transform)} pos={Format(terrain.transform.position)} size={Format(terrain.terrainData.size)}");

        report.AppendLine("== Audio sources ==");
        foreach (AudioSource source in Object.FindObjectsOfType<AudioSource>(true))
            report.AppendLine($"{GetPath(source.transform)} clip={(source.clip != null ? source.clip.name : "-")} pos={Format(source.transform.position)} " +
                              $"loop={source.loop} playOnAwake={source.playOnAwake} blend={source.spatialBlend:0.#} max={source.maxDistance:0}");

        List<GameObject> all = new List<GameObject>();
        foreach (GameObject root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
                all.Add(t.gameObject);

        report.AppendLine("== Hierarchy (depth <= 2) ==");
        int lines = 0;
        foreach (GameObject root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
            DumpHierarchy(root.transform, 0, report, ref lines);

        report.AppendLine("== Name matches ==");
        lines = 0;
        foreach (GameObject go in all)
        {
            if (!Interesting.IsMatch(go.name)) continue;
            if (lines++ >= MaxLinesPerSection) { report.AppendLine("... (truncated)"); break; }
            report.AppendLine(Describe(go, keyItemsLayer));
        }

        report.AppendLine("== Key Items (named colliders the guide perceives) ==");
        lines = 0;
        foreach (GameObject go in all)
        {
            if (go.layer != keyItemsLayer || go.GetComponent<Collider>() == null) continue;
            if (lines++ >= MaxLinesPerSection) { report.AppendLine("... (truncated)"); break; }
            Bounds b = GetBounds(go);
            report.AppendLine($"{go.name} | center={Format(b.center)} size={Format(b.size)}");
        }
        return report.ToString();
    }

    private static void DumpHierarchy(Transform t, int depth, StringBuilder report, ref int lines)
    {
        if (lines++ >= MaxLinesPerSection) return;
        Bounds b = GetBounds(t.gameObject);
        report.AppendLine($"{new string(' ', depth * 2)}{t.name} [{t.childCount}] center={Format(b.center)} size={Format(b.size)}{(t.gameObject.activeInHierarchy ? "" : " (inactive)")}");
        if (depth >= 2) return;
        foreach (Transform child in t)
            DumpHierarchy(child, depth + 1, report, ref lines);
    }

    private static string Describe(GameObject go, int keyItemsLayer)
    {
        Bounds b = GetBounds(go);
        Animator animator = go.GetComponentInChildren<Animator>(true);
        string anim = animator == null ? "no" : (animator.runtimeAnimatorController != null ? animator.runtimeAnimatorController.name : "no controller");
        string source = "";
        GameObject prefabRoot = PrefabUtility.GetNearestPrefabInstanceRoot(go);
        if (prefabRoot != null)
        {
            string assetPath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
            source = $" prefab={(prefabRoot == go ? "" : prefabRoot.name + " > ")}{Path.GetFileName(assetPath)}";
        }
        Collider collider = go.GetComponent<Collider>();
        return $"{GetPath(go.transform)} | center={Format(b.center)} size={Format(b.size)} renderers={go.GetComponentsInChildren<Renderer>(true).Length} " +
               $"anim={anim} collider={(collider != null ? collider.GetType().Name + (collider.isTrigger ? "(trigger)" : "") : "-")} " +
               $"layer={LayerMask.LayerToName(go.layer)}{(go.layer == keyItemsLayer ? "*" : "")} static={(int)GameObjectUtility.GetStaticEditorFlags(go)} " +
               $"active={go.activeInHierarchy}{source}";
    }

    private static Bounds GetBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>(true);
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    private static string GetPath(Transform t)
    {
        string path = t.name;
        for (Transform parent = t.parent; parent != null; parent = parent.parent)
            path = parent.name + "/" + path;
        return path;
    }

    private static string Format(Vector3 v) => $"({v.x:0.#}, {v.y:0.#}, {v.z:0.#})";
}
