using Unity.XR.CoreUtils;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Moving Objects > Add Test Movers Around Player: drops a small test street into the open scene so the movers' motion,
// spatial audio and the guide's moving-hazard warnings can be tried in Play mode. Everything goes under one "Test Movers" object -
// delete it (or Undo) before saving the scene if you don't want to keep it.
public static class MovingObjectsTestSetup
{
    private const string RootName = "Test Movers";
    private const string PrefabFolder = "Assets/Prefabs/Moving Objects/";
    private const string BenchPrefab = "Assets/Environment/Monster Pet Shop/LowPolyDungeon/Prefabs/Furniture/Bench.prefab";

    // Layout frame: x to the player's right, z ahead of them
    private static Vector3 origin;
    private static Vector3 right;
    private static Vector3 forward;

    [MenuItem("Tools/Moving Objects/Add Test Movers Around Player")]
    private static void AddTestMovers()
    {
        // Replace the previous test street instead of stacking a second one on top
        GameObject previous = GameObject.Find(RootName);
        if (previous != null) Undo.DestroyObjectImmediate(previous);

        Transform player = FindPlayer();
        if (player != null)
        {
            origin = player.position;
            forward = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
        }
        else
        {
            // No player rig in the scene - build around the middle of the Scene view instead
            origin = SceneView.lastActiveSceneView != null ? SceneView.lastActiveSceneView.pivot : Vector3.zero;
            forward = Vector3.forward;
        }
        if (forward.sqrMagnitude < 0.01f) forward = Vector3.forward;
        right = Vector3.Cross(Vector3.up, forward);

        GameObject root = new GameObject(RootName);
        root.transform.position = origin;

        // Road loop whose near side runs left to right 4m ahead - step onto it to get honked at and warned about the car
        WaypointPath road = CreatePath(root, "Road Loop", true, Point(-12, 4), Point(12, 4), Point(12, 16), Point(-12, 16));
        AddMover(root, "White Car", road);

        // Pedestrian walking back and forth just ahead of the player
        WaypointPath sidewalk = CreatePath(root, "Sidewalk", false, Point(-8, 1.5f), Point(8, 1.5f));
        AddMover(root, "Woman in White Shirt", sidewalk);

        // Bench sitter: walks to a bench behind the player on their right, sits facing the road for a while, then walks back
        Vector3 seatPoint = Point(6, -6);
        WaypointPath benchWalk = CreatePath(root, "Bench Walk", false, Point(6, 0), seatPoint);
        Waypoint seat = benchWalk.transform.GetChild(1).gameObject.AddComponent<Waypoint>();
        seat.dwellSeconds = 12f;
        seat.faceForwardWhileWaiting = true;
        seat.transform.rotation = Quaternion.LookRotation(forward);
        AddBench(root, seatPoint - forward * 0.3f);
        AddMover(root, "Man in Gray Shirt (Sits)", benchWalk);

        // Corgi trotting in a circle behind the player on their left
        Vector3[] circle = new Vector3[8];
        for (int i = 0; i < circle.Length; i++)
        {
            float angle = i * Mathf.PI * 2f / circle.Length;
            circle[i] = Point(-4 + Mathf.Cos(angle) * 2.5f, -3 + Mathf.Sin(angle) * 2.5f);
        }
        AddMover(root, "Corgi", CreatePath(root, "Corgi Circle", true, circle));

        // Sparrow flying overhead that lands on the ground for a few seconds each lap
        WaypointPath flight = CreatePath(root, "Sparrow Flight", true, Point(3, 2, 3.5f), Point(-2, 5, 4f), Point(-6, 0, 3f), Point(-4, -1), Point(0, -2, 3f));
        flight.transform.GetChild(3).gameObject.AddComponent<Waypoint>().dwellSeconds = 6f;
        AddMover(root, "Sparrow", flight);

        Undo.RegisterCreatedObjectUndo(root, "Add Test Movers");
        Selection.activeGameObject = root;
        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log($"[Test Movers] Added a test street around {(player != null ? player.name : "the Scene view")}. " +
                  "Press Play with headphones on, and step forward onto the road to hear the honk and the guide's warning. " +
                  $"Delete '{RootName}' before saving if you don't want to keep it.", root);
    }

    [MenuItem("Tools/Moving Objects/Add Test Movers Around Player", true)]
    private static bool CanAddTestMovers()
    {
        return !EditorApplication.isPlaying;
    }

    // The participant's rig - the guide's rig also has an XR Origin, so skip that one
    private static Transform FindPlayer()
    {
        foreach (XROrigin rig in Object.FindObjectsOfType<XROrigin>())
        {
            if (rig.GetComponentInChildren<GuideFollow>() == null && rig.GetComponentInParent<GuideFollow>() == null)
                return rig.transform;
        }
        return Camera.main != null ? Camera.main.transform : null;
    }

    // A point in the layout frame, dropped onto whatever floor is below it and then raised by height
    private static Vector3 Point(float x, float z, float height = 0f)
    {
        Vector3 point = origin + right * x + forward * z;
        if (Physics.Raycast(point + Vector3.up, Vector3.down, out RaycastHit hit, 4f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
            point.y = hit.point.y;
        return point + Vector3.up * height;
    }

    private static WaypointPath CreatePath(GameObject root, string name, bool closedLoop, params Vector3[] points)
    {
        GameObject pathObject = new GameObject(name);
        pathObject.transform.SetParent(root.transform, false);
        WaypointPath path = pathObject.AddComponent<WaypointPath>();
        path.closedLoop = closedLoop;

        for (int i = 0; i < points.Length; i++)
        {
            GameObject waypoint = new GameObject("Waypoint " + (i + 1));
            waypoint.transform.SetParent(pathObject.transform, false);
            waypoint.transform.position = points[i];
        }
        return path;
    }

    private static void AddMover(GameObject root, string prefabName, WaypointPath path)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(PrefabFolder + prefabName + ".prefab");
        if (prefab == null)
        {
            Debug.LogWarning($"[Test Movers] Couldn't find {PrefabFolder}{prefabName}.prefab, so it was skipped.");
            return;
        }

        GameObject instance = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
        WaypointMover mover = instance.GetComponent<WaypointMover>();
        mover.path = path;
        mover.SnapToCycleStart(); // show it on its route in the Scene view
        PrefabUtility.RecordPrefabInstancePropertyModifications(mover);
    }

    // Lay the bench's long side across the player's view, so the sitter faces the road
    private static void AddBench(GameObject root, Vector3 position)
    {
        GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(BenchPrefab);
        if (prefab == null) return; // the sitter still sits, just without a bench

        GameObject bench = (GameObject)PrefabUtility.InstantiatePrefab(prefab, root.transform);
        bench.transform.SetPositionAndRotation(position, Quaternion.identity);

        Bounds bounds = new Bounds(position, Vector3.zero);
        foreach (Renderer r in bench.GetComponentsInChildren<Renderer>())
            bounds.Encapsulate(r.bounds);
        bench.transform.rotation = Quaternion.LookRotation(bounds.size.x >= bounds.size.z ? forward : right);
    }
}
