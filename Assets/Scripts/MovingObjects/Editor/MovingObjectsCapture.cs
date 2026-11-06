using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

// Tools > Moving Objects > Capture Mover Previews: renders a top-down map of the open scene with every mover route drawn on it, plus a
// close-up of each mover where it starts its cycle, to Logs/MoverCaptures/<scene>/. Used to check routes and looks without a headset.
public static class MovingObjectsCapture
{
    private const string OutputFolder = "Logs/MoverCaptures";

    [MenuItem("Tools/Moving Objects/Capture Mover Previews")]
    public static void CaptureOpenScene()
    {
        string sceneName = EditorSceneManager.GetActiveScene().name;
        string folder = Path.Combine(OutputFolder, sceneName);
        Directory.CreateDirectory(folder);
        foreach (string old in Directory.GetFiles(folder, "*.png")) File.Delete(old);

        WaypointMover[] movers = Object.FindObjectsOfType<WaypointMover>();
        GameObject cameraObject = new GameObject("Mover Capture Camera") { hideFlags = HideFlags.HideAndDontSave };
        Camera cam = cameraObject.AddComponent<Camera>();
        cam.nearClipPlane = 0.1f;
        cam.farClipPlane = 2000f;
        RenderTexture target = new RenderTexture(900, 600, 24);
        cam.targetTexture = target;
        List<GameObject> markers = new List<GameObject>();

        try
        {
            foreach (WaypointMover mover in movers)
            {
                mover.SnapToCycleStart();
                Bounds b = GetBounds(mover.gameObject);
                float distance = Mathf.Clamp(b.size.magnitude * 2.2f, 3f, 40f);
                Vector3 side = (-mover.transform.forward * 0.5f + mover.transform.right).normalized;
                cam.orthographic = false;
                cam.fieldOfView = 50f;
                cam.transform.position = b.center + side * distance + Vector3.up * distance * 0.4f;
                cam.transform.LookAt(b.center);
                Save(cam, target, Path.Combine(folder, Sanitize(mover.name) + ".png"));
            }

            // Top-down map with each route drawn as a coloured line and the player spawn as a white post
            Bounds area = new Bounds();
            bool first = true;
            Material lineMaterial = new Material(Shader.Find("Sprites/Default"));
            int index = 0;
            foreach (WaypointPath path in Object.FindObjectsOfType<WaypointPath>())
            {
                path.Build();
                List<Vector3> samples = new List<Vector3>();
                for (float d = 0f; d <= path.Length; d += 0.5f)
                {
                    path.Evaluate(d, out Vector3 p, out _);
                    samples.Add(p + Vector3.up * 0.5f);
                }
                if (samples.Count < 2) continue;

                GameObject line = new GameObject("Route Marker") { hideFlags = HideFlags.HideAndDontSave };
                LineRenderer renderer = line.AddComponent<LineRenderer>();
                renderer.sharedMaterial = lineMaterial;
                renderer.widthMultiplier = 0.8f;
                renderer.positionCount = samples.Count;
                renderer.SetPositions(samples.ToArray());
                renderer.loop = path.closedLoop;
                Color color = Color.HSVToRGB((index++ * 0.17f) % 1f, 0.9f, 1f);
                renderer.startColor = renderer.endColor = color;
                markers.Add(line);

                foreach (Vector3 s in samples)
                {
                    if (first) { area = new Bounds(s, Vector3.zero); first = false; }
                    else area.Encapsulate(s);
                }
            }

            foreach (Unity.XR.CoreUtils.XROrigin rig in Object.FindObjectsOfType<Unity.XR.CoreUtils.XROrigin>())
            {
                GameObject post = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                post.hideFlags = HideFlags.HideAndDontSave;
                Object.DestroyImmediate(post.GetComponent<Collider>());
                post.transform.position = rig.transform.position + Vector3.up * 3f;
                post.transform.localScale = new Vector3(2.5f, 6f, 2.5f);
                post.GetComponent<Renderer>().sharedMaterial = new Material(Shader.Find("Unlit/Color")) { color = Color.white };
                markers.Add(post);
                if (first) { area = new Bounds(rig.transform.position, Vector3.zero); first = false; }
                else area.Encapsulate(rig.transform.position);
            }

            cam.orthographic = true;
            cam.transform.rotation = Quaternion.Euler(90f, 0f, 0f);
            if (!first)
            {
                cam.orthographicSize = Mathf.Max(area.extents.z, area.extents.x / 1.5f) + 10f;
                cam.transform.position = area.center + Vector3.up * 300f;
                Save(cam, target, Path.Combine(folder, "_Map.png"));
            }

            // Whole-scene overview (240m x 160m around the origin) with a 20m grid: x = 0 and z = 0 are red, other lines grey
            for (int x = -120; x <= 120; x += 20)
                markers.Add(GridLine(lineMaterial, new Vector3(x, 80f, -80f), new Vector3(x, 80f, 80f), x == 0 ? Color.red : Color.gray));
            for (int z = -80; z <= 80; z += 20)
                markers.Add(GridLine(lineMaterial, new Vector3(-120f, 80f, z), new Vector3(120f, 80f, z), z == 0 ? Color.red : Color.gray));
            cam.orthographicSize = 80f;
            cam.transform.position = new Vector3(0f, 300f, 0f);
            Save(cam, target, Path.Combine(folder, "_Overview.png"));
            Debug.Log($"[Mover Capture] Saved {movers.Length} mover previews and a route map to {folder}");
        }
        finally
        {
            foreach (GameObject marker in markers) Object.DestroyImmediate(marker);
            cam.targetTexture = null;
            Object.DestroyImmediate(cameraObject);
            target.Release();
            Object.DestroyImmediate(target);
        }
    }

    // Batch-mode entry point: captures every themed scene as it is saved on disk
    public static void CaptureAllThemedScenes()
    {
        foreach (string scene in MovingObjectsSceneSurvey.ThemedScenes)
        {
            EditorSceneManager.OpenScene($"Assets/Scenes/{scene}.unity", OpenSceneMode.Single);
            CaptureOpenScene();
        }
    }

    private static GameObject GridLine(Material material, Vector3 from, Vector3 to, Color color)
    {
        GameObject line = new GameObject("Grid Marker") { hideFlags = HideFlags.HideAndDontSave };
        LineRenderer renderer = line.AddComponent<LineRenderer>();
        renderer.sharedMaterial = material;
        renderer.widthMultiplier = 0.6f;
        renderer.positionCount = 2;
        renderer.SetPositions(new[] { from, to });
        renderer.startColor = renderer.endColor = color;
        return line;
    }

    private static void Save(Camera cam, RenderTexture target, string file)
    {
        cam.Render();
        RenderTexture previous = RenderTexture.active;
        RenderTexture.active = target;
        Texture2D image = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        image.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
        image.Apply();
        RenderTexture.active = previous;
        File.WriteAllBytes(file, image.EncodeToPNG());
        Object.DestroyImmediate(image);
    }

    private static Bounds GetBounds(GameObject go)
    {
        Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
        if (renderers.Length == 0) return new Bounds(go.transform.position, Vector3.one);
        Bounds b = renderers[0].bounds;
        foreach (Renderer r in renderers.Skip(1)) b.Encapsulate(r.bounds);
        return b;
    }

    private static string Sanitize(string name) => string.Concat(name.Split(Path.GetInvalidFileNameChars()));
}
