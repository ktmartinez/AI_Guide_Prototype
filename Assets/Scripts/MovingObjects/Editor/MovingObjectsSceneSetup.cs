using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.AI;
using Object = UnityEngine.Object;

// Tools > Moving Objects > Set Up Movers For Open Scene (or ... In All Themed Scenes): builds each themed scene's on-theme moving
// objects from the specs in MovingObjectsSceneSpecs.cs. Objects already in the scene are converted in place where possible (they keep
// their names, so the guide's room descriptions still apply), and everything new goes under one "Moving Objects" root.
// Re-running is safe: the root is rebuilt and converted objects are reconfigured, so after a scene merge you can take either side's
// scene file and run the tool again. Route problems (points off the ground or navmesh, walls in the way, routes close to the player's
// spawn) are logged as warnings.
public static partial class MovingObjectsSceneSetup
{
    private const string RootName = "Moving Objects";
    private const string MoverFolder = "Assets/Prefabs/Moving Objects/";
    private const string ControllerFolder = MoverFolder + "Animators/";
    private const string AudioFolder = MoverFolder + "Audio/";
    private const string KeyItemsLayerName = "Key Items";

    // ---------- Specs ----------

    private class RouteSpec
    {
        public readonly string Name;
        public readonly bool Loop;
        public bool Smooth = true;
        public bool Relative;               // points are offsets from the converted object's starting position
        public readonly List<Vector3> Points = new List<Vector3>();
        public readonly List<bool> Grounded = new List<bool>();
        public readonly Dictionary<int, float> Dwell = new Dictionary<int, float>();

        public RouteSpec(string name, bool loop = true)
        {
            Name = name;
            Loop = loop;
        }

        // A point on the ground - y is roughly the ground height there and gets snapped to whatever is below
        public RouteSpec Ground(float x, float y, float z) { Points.Add(new Vector3(x, y, z)); Grounded.Add(true); return this; }

        // A point in the air at an exact height (flyers)
        public RouteSpec Air(float x, float y, float z) { Points.Add(new Vector3(x, y, z)); Grounded.Add(false); return this; }

        // Pause at the last point added
        public RouteSpec Wait(float seconds) { Dwell[Points.Count - 1] = seconds; return this; }

        // A ring of ground or air points (circling animals, orbiting potions)
        public RouteSpec Circle(Vector3 center, float radius, int count, bool grounded, float startDegrees = 0f)
        {
            for (int i = 0; i < count; i++)
            {
                float angle = (startDegrees + i * 360f / count) * Mathf.Deg2Rad;
                Points.Add(center + new Vector3(Mathf.Cos(angle), 0f, Mathf.Sin(angle)) * radius);
                Grounded.Add(grounded);
            }
            return this;
        }
    }

    private class AudioSpec
    {
        public string Loop;
        public string[] Occasional = new string[0];
        public string Depart;
        public float Interval = 20f;
        public float MinDistance = 2f;
        public float MaxDistance = 25f;
        public float MovingVolume = 1f;
        public float WaitingVolume = 0.3f;
        public float MovingPitch = 1f;
        public float WaitingPitch = 0.85f;
        public float OneShotVolume = 1f;
        public int Priority = 128;
    }

    private class MoverSpec
    {
        public string Name;                 // what the guide calls it - the main mesh's name (kept as-is for existing Key Items)
        public string Existing;             // hierarchy path (or unique path suffix) of an object already in the scene to make move
        public bool Detach;                 // move the existing object to the top of the scene first (e.g. a horse parented to another horse)
        public string Asset;                // prefab or model to place when not converting
        public Func<Transform, GameObject> Build; // custom builder (UFO, broom), given the root to parent to
        public Action<GameObject> Setup;    // extra work on the object before it's measured (e.g. hitching a horse to a cart)
        public float Size;                  // placed models: scale so the largest dimension is this many meters (0 keeps the model size)

        public RouteSpec Route;
        // Unset values keep what the mover prefab already has (or WaypointMover's defaults)
        public float? Speed;
        public float CycleOffset;
        public float? HeightOffset;         // default for converted objects and models: lift the pivot so the mesh bottom touches the ground
        public float? Bob;
        public float? BobFrequency;
        public bool? Warn;
        public bool? YawOnly;
        public Vector3? RotationOffset;     // for models whose front isn't +Z
        public float? TurnSharpness;

        // Idle/Move animator built from the model's own clips (first clip whose name contains one of the words)
        public string ClipModel;
        public string AnimatorOn;           // child path that holds the model's Animator, if not the mover itself
        public string[] IdleClips;
        public string[] MoveClips;
        public float MoveAnimationSpeed = 1f;

        public AudioSpec Audio;             // unset keeps a mover prefab's own audio
    }

    private class SceneSpec
    {
        public readonly string Scene;
        public readonly List<MoverSpec> Movers = new List<MoverSpec>();
        public SceneSpec(string scene) { Scene = scene; }
        public MoverSpec Add(MoverSpec mover) { Movers.Add(mover); return mover; }
    }

    // ---------- Menu ----------

    [MenuItem("Tools/Moving Objects/Set Up Movers For Open Scene")]
    private static void SetUpOpenScene()
    {
        string sceneName = EditorSceneManager.GetActiveScene().name;
        SceneSpec spec = GetSceneSpecs().FirstOrDefault(s => s.Scene == sceneName);
        if (spec == null)
        {
            Debug.LogWarning($"[Scene Movers] No mover specs for '{sceneName}'. Themed scenes: {string.Join(", ", GetSceneSpecs().Select(s => s.Scene))}");
            return;
        }
        Apply(spec);
    }

    [MenuItem("Tools/Moving Objects/Set Up Movers In All Themed Scenes")]
    public static void SetUpAllScenes()
    {
        if (!Application.isBatchMode && !EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;
        foreach (SceneSpec spec in GetSceneSpecs())
        {
            EditorSceneManager.OpenScene($"Assets/Scenes/{spec.Scene}.unity", OpenSceneMode.Single);
            Apply(spec);
            EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
        }
        AssetDatabase.SaveAssets();
    }

    // Batch mode: -executeMethod MovingObjectsSceneSetup.SetUpScenesFromCommandLine [-movers "Flower Shop,Kitchen"] [-nosave]
    public static void SetUpScenesFromCommandLine()
    {
        string[] args = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(args, "-movers");
        HashSet<string> wanted = index >= 0 && index + 1 < args.Length
            ? new HashSet<string>(args[index + 1].Split(',').Select(s => s.Trim()))
            : null;
        bool save = !args.Contains("-nosave");
        foreach (SceneSpec spec in GetSceneSpecs())
        {
            if (wanted != null && !wanted.Contains(spec.Scene)) continue;
            EditorSceneManager.OpenScene($"Assets/Scenes/{spec.Scene}.unity", OpenSceneMode.Single);
            Apply(spec);
            if (save) EditorSceneManager.SaveScene(EditorSceneManager.GetActiveScene());
            MovingObjectsCapture.CaptureOpenScene();
        }
        AssetDatabase.SaveAssets();
    }

    // ---------- Building ----------

    private static void Apply(SceneSpec spec)
    {
        Physics.SyncTransforms();
        // Only routes and newly placed movers live under the root - converted scene objects stay outside it, so rebuilding never deletes them
        GameObject previous = FindRoot(RootName);
        if (previous != null) Object.DestroyImmediate(previous);

        GameObject root = new GameObject(RootName);
        Transform routes = new GameObject("Routes").transform;
        routes.SetParent(root.transform, false);

        int built = 0;
        foreach (MoverSpec mover in spec.Movers)
        {
            try
            {
                if (BuildMover(mover, root.transform, routes)) built++;
            }
            catch (Exception e)
            {
                Debug.LogError($"[Scene Movers] {spec.Scene}: building '{mover.Name}' failed: {e}");
            }
        }

        Physics.SyncTransforms();
        EditorSceneManager.MarkSceneDirty(root.scene);
        Debug.Log($"[Scene Movers] {spec.Scene}: built {built}/{spec.Movers.Count} movers.");
    }

    private static bool BuildMover(MoverSpec spec, Transform root, Transform routes)
    {
        GameObject go;
        bool isMoverPrefab = false;
        if (!string.IsNullOrEmpty(spec.Existing))
        {
            go = FindInScene(spec.Existing);
            if (go == null)
            {
                Debug.LogWarning($"[Scene Movers] '{spec.Name}': couldn't find existing object '{spec.Existing}', skipped.");
                return false;
            }
            go.SetActive(true);
            if (spec.Detach) go.transform.SetParent(null, true);
        }
        else if (spec.Build != null)
        {
            go = spec.Build(root);
            if (go == null) return false;
            go.name = spec.Name;
        }
        else
        {
            GameObject asset = AssetDatabase.LoadAssetAtPath<GameObject>(spec.Asset);
            if (asset == null)
            {
                Debug.LogWarning($"[Scene Movers] '{spec.Name}': couldn't load '{spec.Asset}', skipped.");
                return false;
            }
            go = (GameObject)PrefabUtility.InstantiatePrefab(asset, root);
            if (spec.Size > 0f) go.transform.localScale *= spec.Size / Mathf.Max(0.01f, MaxDimension(GetBounds(go)));
            isMoverPrefab = asset.GetComponent<WaypointMover>() != null;
            go.name = spec.Name;
        }
        spec.Setup?.Invoke(go);

        // Measure before anything moves it: where it starts (for relative routes) and how far its pivot sits above its lowest point
        Physics.SyncTransforms();
        Vector3 start = go.transform.position;
        float pivotAboveBottom = start.y - GetBounds(go).min.y;

        ClearStaticFlags(go);
        TurnOffShadows(go);
        Collider hazard = EnsureNamedCollider(go, spec);
        EnsureKinematicBody(go);
        SetUpAnimator(go, spec);

        WaypointPath path = BuildRoute(spec.Route, routes, spec.Name, start, go);

        if (!go.TryGetComponent(out WaypointMover mover)) mover = go.AddComponent<WaypointMover>();
        mover.path = path;
        mover.cycleOffset = spec.CycleOffset;
        if (spec.Speed.HasValue) mover.speed = spec.Speed.Value;
        if (spec.Bob.HasValue) mover.bobAmplitude = spec.Bob.Value;
        if (spec.BobFrequency.HasValue) mover.bobFrequency = spec.BobFrequency.Value;
        if (spec.Warn.HasValue) mover.warnPlayer = spec.Warn.Value;
        if (spec.YawOnly.HasValue) mover.yawOnly = spec.YawOnly.Value;
        if (spec.RotationOffset.HasValue) mover.modelRotationOffset = spec.RotationOffset.Value;
        if (spec.TurnSharpness.HasValue) mover.turnSharpness = spec.TurnSharpness.Value;
        if (spec.HeightOffset.HasValue) mover.heightOffset = spec.HeightOffset.Value;
        else if (!isMoverPrefab && spec.Route.Grounded.Any(g => g)) mover.heightOffset = Mathf.Max(0f, pivotAboveBottom);
        if (hazard != null) mover.hazardCollider = hazard;
        Transform animatorHolder = string.IsNullOrEmpty(spec.AnimatorOn) ? go.transform : go.transform.Find(spec.AnimatorOn);
        mover.animator = animatorHolder != null ? animatorHolder.GetComponentInChildren<Animator>() : go.GetComponentInChildren<Animator>();

        SetUpAudio(go, spec);
        mover.SnapToCycleStart();
        if (PrefabUtility.IsPartOfPrefabInstance(go))
        {
            PrefabUtility.RecordPrefabInstancePropertyModifications(mover);
            PrefabUtility.RecordPrefabInstancePropertyModifications(go.transform);
        }
        ValidateRoute(spec, path, go);
        return true;
    }

    private static WaypointPath BuildRoute(RouteSpec route, Transform routes, string moverName, Vector3 start, GameObject mover)
    {
        GameObject pathObject = new GameObject(route.Name ?? moverName + " Route");
        pathObject.transform.SetParent(routes, false);
        WaypointPath path = pathObject.AddComponent<WaypointPath>();
        path.closedLoop = route.Loop;
        path.smooth = route.Smooth;

        for (int i = 0; i < route.Points.Count; i++)
        {
            Vector3 point = route.Relative ? start + route.Points[i] : route.Points[i];
            if (route.Grounded[i])
            {
                if (TryFindGround(point, mover, out float groundY)) point.y = groundY;
                else Debug.LogWarning($"[Scene Movers] '{moverName}': no ground near point {i + 1} {point}.");
            }
            GameObject waypoint = new GameObject("Waypoint " + (i + 1));
            waypoint.transform.SetParent(pathObject.transform, false);
            waypoint.transform.position = point;
            if (route.Dwell.TryGetValue(i, out float dwell))
                waypoint.AddComponent<Waypoint>().dwellSeconds = dwell;
        }
        return path;
    }

    // Highest walkable surface within a few meters of the hinted height, ignoring movers, the mover itself and triggers
    private static bool TryFindGround(Vector3 hint, GameObject self, out float groundY)
    {
        groundY = hint.y;
        RaycastHit[] hits = Physics.RaycastAll(hint + Vector3.up * 4f, Vector3.down, 12f, Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore);
        float best = float.NegativeInfinity;
        foreach (RaycastHit hit in hits)
        {
            if (hit.collider.transform.IsChildOf(self.transform)) continue;
            if (hit.collider.GetComponentInParent<WaypointMover>() != null) continue;
            if (hit.normal.y < 0.6f) continue; // walls and steep slopes aren't ground
            if (hit.point.y > best) best = hit.point.y;
        }
        if (float.IsNegativeInfinity(best)) return false;
        groundY = best;
        return true;
    }

    private static void ClearStaticFlags(GameObject go)
    {
        foreach (Transform t in go.GetComponentsInChildren<Transform>(true))
            if (GameObjectUtility.GetStaticEditorFlags(t.gameObject) != 0)
                GameObjectUtility.SetStaticEditorFlags(t.gameObject, 0);
    }

    // The perception sensor and hazard warnings need the main mesh to carry the mover's name, a renderer, a non-trigger collider and the
    // Key Items layer. Keep an existing Key Items collider (and its name, which the room descriptions use) if there is one, otherwise put
    // a box on the biggest mesh and give it the mover's name.
    private static Collider EnsureNamedCollider(GameObject go, MoverSpec spec)
    {
        int keyItems = LayerMask.NameToLayer(KeyItemsLayerName);
        foreach (Collider existing in go.GetComponentsInChildren<Collider>(true))
            if (!existing.isTrigger && existing.gameObject.layer == keyItems && existing.GetComponent<Renderer>() != null)
                return existing;

        Renderer main = go.GetComponentsInChildren<Renderer>(true)
            .Where(r => r is MeshRenderer || r is SkinnedMeshRenderer)
            .OrderByDescending(r => r.bounds.size.sqrMagnitude)
            .FirstOrDefault();
        if (main == null)
        {
            Debug.LogWarning($"[Scene Movers] '{spec.Name}' has no mesh to name and give a collider.");
            return null;
        }
        return AddNamedCollider(main, spec.Name);
    }

    internal static Collider AddNamedCollider(Renderer renderer, string name)
    {
        GameObject mesh = renderer.gameObject;
        mesh.name = name;
        mesh.layer = LayerMask.NameToLayer(KeyItemsLayerName);
        foreach (Collider other in mesh.GetComponents<Collider>())
            if (!other.isTrigger) Object.DestroyImmediate(other, true);

        BoxCollider box = mesh.AddComponent<BoxCollider>();
        if (renderer is SkinnedMeshRenderer skinned && skinned.sharedMesh != null)
        {
            box.center = skinned.sharedMesh.bounds.center;
            box.size = skinned.sharedMesh.bounds.size;
        }
        return box;
    }

    private static void EnsureKinematicBody(GameObject go)
    {
        if (!go.TryGetComponent(out Rigidbody body)) body = go.AddComponent<Rigidbody>();
        body.isKinematic = true;
        body.useGravity = false;
        body.interpolation = RigidbodyInterpolation.None;
    }

    // ---------- Animation ----------

    private static void SetUpAnimator(GameObject go, MoverSpec spec)
    {
        if (string.IsNullOrEmpty(spec.ClipModel)) return;

        AnimatorController controller = GetOrCreateSpeedController(spec);
        if (controller == null) return;

        Transform holder = string.IsNullOrEmpty(spec.AnimatorOn) ? go.transform : go.transform.Find(spec.AnimatorOn);
        if (holder == null)
        {
            Debug.LogWarning($"[Scene Movers] '{spec.Name}': no child '{spec.AnimatorOn}' for the animator.");
            return;
        }
        Animator animator = holder.GetComponentInChildren<Animator>(true);
        if (animator == null) animator = holder.gameObject.AddComponent<Animator>();
        animator.runtimeAnimatorController = controller;
        animator.applyRootMotion = false;
        animator.cullingMode = AnimatorCullingMode.CullUpdateTransforms;
    }

    // Two states like the existing mover controllers: Idle while Speed < 0.1, Move above it
    private static AnimatorController GetOrCreateSpeedController(MoverSpec spec)
    {
        string modelName = Path.GetFileNameWithoutExtension(spec.ClipModel);
        List<AnimationClip> clips = AssetDatabase.LoadAllAssetsAtPath(spec.ClipModel)
            .OfType<AnimationClip>()
            .Where(c => !c.name.StartsWith("__preview__"))
            .ToList();
        AnimationClip move = PickClip(clips, spec.MoveClips);
        AnimationClip idle = PickClip(clips, spec.IdleClips);
        if (move == null)
        {
            Debug.LogWarning($"[Scene Movers] '{spec.Name}': no move clip in {spec.ClipModel}. Clips: {string.Join(", ", clips.Select(c => c.name))}");
            return null;
        }

        // Named after the clips actually used ("Horse - Idle and Walk"), so specs that end up with the same clips share one controller
        string controllerPath = $"{ControllerFolder}{modelName} - {(idle != null ? ClipLabel(idle) : "Hold")} and {ClipLabel(move)}.controller";
        AnimatorController existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(controllerPath);
        if (existing != null) return existing;

        Debug.Log($"[Scene Movers] '{spec.Name}': animator from {modelName} - idle '{(idle != null ? idle.name : "-")}', move '{move.name}' " +
                  $"(move loops: {AnimationUtility.GetAnimationClipSettings(move).loopTime}). All clips: {string.Join(", ", clips.Select(c => c.name))}");

        AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(controllerPath);
        controller.AddParameter("Speed", AnimatorControllerParameterType.Float);
        AnimatorStateMachine machine = controller.layers[0].stateMachine;

        AnimatorState idleState = machine.AddState("Idle");
        idleState.motion = idle != null ? idle : move;
        if (idle == null) idleState.speed = 0f; // hold the first frame of the move clip
        AnimatorState moveState = machine.AddState("Move");
        moveState.motion = move;
        moveState.speed = spec.MoveAnimationSpeed;
        machine.defaultState = idleState;

        AnimatorStateTransition toMove = idleState.AddTransition(moveState);
        toMove.hasExitTime = false;
        toMove.duration = 0.2f;
        toMove.AddCondition(AnimatorConditionMode.Greater, 0.1f, "Speed");
        AnimatorStateTransition toIdle = moveState.AddTransition(idleState);
        toIdle.hasExitTime = false;
        toIdle.duration = 0.2f;
        toIdle.AddCondition(AnimatorConditionMode.Less, 0.1f, "Speed");

        AssetDatabase.SaveAssets();
        return controller;
    }

    // "CharacterArmature|Fast_Flying" -> "Fast Flying"
    private static string ClipLabel(AnimationClip clip) => clip.name.Substring(clip.name.LastIndexOf('|') + 1).Replace('_', ' ');

    private static AnimationClip PickClip(List<AnimationClip> clips, string[] words)
    {
        if (words == null) return null;
        foreach (string word in words)
        {
            AnimationClip exact = clips.FirstOrDefault(c => string.Equals(ClipLabel(c).Replace(' ', '_'), word, StringComparison.OrdinalIgnoreCase));
            if (exact != null) return exact;
            AnimationClip partial = clips.FirstOrDefault(c => c.name.IndexOf(word, StringComparison.OrdinalIgnoreCase) >= 0);
            if (partial != null) return partial;
        }
        return null;
    }

    // ---------- Audio ----------

    private static void SetUpAudio(GameObject go, MoverSpec spec)
    {
        if (spec.Audio == null) return; // keep a mover prefab's own sounds

        if (!go.TryGetComponent(out MoverAudio audio)) audio = go.AddComponent<MoverAudio>();
        AudioSpec a = spec.Audio;
        audio.loopClip = LoadClip(a.Loop, spec.Name);
        audio.occasionalClips = a.Occasional.Select(c => LoadClip(c, spec.Name)).Where(c => c != null).ToArray();
        audio.departClip = LoadClip(a.Depart, spec.Name);
        audio.honkClip = null;
        audio.occasionalInterval = a.Interval;
        audio.minDistance = a.MinDistance;
        audio.maxDistance = a.MaxDistance;
        audio.movingVolume = a.MovingVolume;
        audio.waitingVolume = a.WaitingVolume;
        audio.movingPitch = a.MovingPitch;
        audio.waitingPitch = a.WaitingPitch;
        audio.oneShotVolume = a.OneShotVolume;
        audio.priority = a.Priority;
        if (PrefabUtility.IsPartOfPrefabInstance(go)) PrefabUtility.RecordPrefabInstancePropertyModifications(audio);
    }

    // Clip names are looked up in the movers' Audio folder first, then as full asset paths
    private static AudioClip LoadClip(string name, string moverName)
    {
        if (string.IsNullOrEmpty(name)) return null;
        AudioClip clip = AssetDatabase.LoadAssetAtPath<AudioClip>(name.StartsWith("Assets/") ? name : AudioFolder + name);
        if (clip == null) Debug.LogWarning($"[Scene Movers] '{moverName}': audio clip '{name}' not found yet - that sound is skipped.");
        return clip;
    }

    // ---------- Validation ----------

    private static void ValidateRoute(MoverSpec spec, WaypointPath path, GameObject go)
    {
        List<Vector3> points = new List<Vector3>();
        foreach (Transform waypoint in path.transform) points.Add(waypoint.position);
        int segments = spec.Route.Loop ? points.Count : points.Count - 1;

        for (int i = 0; i < segments; i++)
        {
            int next = (i + 1) % points.Count;
            if (!spec.Route.Grounded[i] || !spec.Route.Grounded[next]) continue;
            Vector3 a = points[i];
            Vector3 b = points[next];
            foreach (float height in new[] { 0.6f, 1.5f })
            {
                foreach (RaycastHit hit in Physics.RaycastAll(a + Vector3.up * height, (b - a).normalized, Vector3.Distance(a, b), Physics.DefaultRaycastLayers, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider.transform.IsChildOf(go.transform) || hit.collider is TerrainCollider) continue;
                    if (hit.collider.GetComponentInParent<WaypointMover>() != null) continue;
                    Debug.LogWarning($"[Scene Movers] '{spec.Name}': leg {i + 1}->{next + 1} hits '{GetPath(hit.collider.transform)}' at {hit.point} (h={height}).");
                    break;
                }
            }
        }

        int offNavmesh = 0;
        for (int i = 0; i < points.Count; i++)
            if (spec.Route.Grounded[i] && !NavMesh.SamplePosition(points[i], out _, 1.5f, NavMesh.AllAreas))
                offNavmesh++;

        Transform spawn = FindPlayerSpawn();
        string spawnNote = spawn != null ? $", nearest {points.Min(p => Vector3.Distance(Flat(p), Flat(spawn.position))):0.0}m from the player spawn" : "";
        Debug.Log($"[Scene Movers] '{spec.Name}': {(spec.Route.Loop ? "loop" : "back-and-forth")} of {points.Count} points, {path.Length:0}m{spawnNote}" +
                  (offNavmesh > 0 ? $", {offNavmesh} ground points off the navmesh" : "") + ".");
    }

    // ---------- Helpers ----------

    private static Vector3 Flat(Vector3 v) => new Vector3(v.x, 0f, v.z);

    internal static Bounds GetBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.zero);
        Bounds b = renderers[0].bounds;
        for (int i = 1; i < renderers.Length; i++) b.Encapsulate(renderers[i].bounds);
        return b;
    }

    internal static float MaxDimension(Bounds b) => Mathf.Max(b.size.x, Mathf.Max(b.size.y, b.size.z));

    // Moving shadows are one of the bigger per-object costs on Quest, and movers read fine without them
    private static void TurnOffShadows(GameObject go)
    {
        foreach (Renderer r in go.GetComponentsInChildren<Renderer>(true))
        {
            if (r.shadowCastingMode == UnityEngine.Rendering.ShadowCastingMode.Off) continue;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (PrefabUtility.IsPartOfPrefabInstance(r)) PrefabUtility.RecordPrefabInstancePropertyModifications(r);
        }
    }

    private static Transform FindPlayerSpawn()
    {
        foreach (Unity.XR.CoreUtils.XROrigin rig in Object.FindObjectsOfType<Unity.XR.CoreUtils.XROrigin>())
            if (rig.GetComponentInChildren<GuideFollow>() == null && rig.GetComponentInParent<GuideFollow>() == null)
                return rig.transform;
        return null;
    }

    private static GameObject FindRoot(string name)
    {
        return EditorSceneManager.GetActiveScene().GetRootGameObjects().FirstOrDefault(g => g.name == name);
    }

    // Finds by full hierarchy path, by path suffix ("Market Deliveries/Delivery Cart"), or by unique name, including inactive objects.
    // Objects under the Moving Objects root count too, so converted objects that were moved there are found again on a re-run.
    internal static GameObject FindInScene(string pathOrName)
    {
        GameObject match = null;
        int matches = 0;
        foreach (GameObject root in EditorSceneManager.GetActiveScene().GetRootGameObjects())
        {
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
            {
                string path = GetPath(t);
                if (path == pathOrName) return t.gameObject;
                if (path.EndsWith("/" + pathOrName) || t.name == pathOrName)
                {
                    match ??= t.gameObject;
                    matches++;
                }
            }
        }
        if (matches > 1) Debug.LogWarning($"[Scene Movers] '{pathOrName}' matches {matches} objects - using {GetPath(match.transform)}.");
        return match;
    }

    internal static string GetPath(Transform t)
    {
        string path = t.name;
        for (Transform parent = t.parent; parent != null; parent = parent.parent)
            path = parent.name + "/" + path;
        return path;
    }
}
