using System.Collections.Generic;
using UnityEngine;

// An ordered route built from this object's children, followed by WaypointMover.
// Closed loops suit cars circling a block; open paths are walked back and forth (ping-pong).
public class WaypointPath : MonoBehaviour
{
    public bool closedLoop = true;
    [Tooltip("Round corners with a Catmull-Rom curve through the waypoints")]
    public bool smooth = true;
    [Range(1, 20)] public int samplesPerSegment = 8;
    public Color gizmoColor = new Color(0f, 0.8f, 1f);

    private readonly List<Vector3> samples = new List<Vector3>();
    private readonly List<float> sampleDistances = new List<float>();
    private Waypoint[] waypointSettings = new Waypoint[0];
    private float[] waypointDistances = new float[0];
    private bool isBuilt;

    public float Length { get; private set; }
    public int WaypointCount => waypointDistances.Length;

    void Awake()
    {
        Build();
    }

    public void EnsureBuilt()
    {
        if (!isBuilt) Build();
    }

    // Sample the route into a polyline so movers can travel it at constant speed
    public void Build()
    {
        isBuilt = true;
        samples.Clear();
        sampleDistances.Clear();
        Length = 0f;

        int count = transform.childCount;
        waypointSettings = new Waypoint[count];
        waypointDistances = new float[count];
        if (count < 2) return;

        Vector3[] nodes = new Vector3[count];
        for (int i = 0; i < count; i++)
        {
            Transform child = transform.GetChild(i);
            nodes[i] = child.position;
            waypointSettings[i] = child.GetComponent<Waypoint>();
        }

        int segmentCount = closedLoop ? count : count - 1;
        int steps = smooth ? Mathf.Max(1, samplesPerSegment) : 1;
        samples.Add(nodes[0]);
        sampleDistances.Add(0f);

        for (int s = 0; s < segmentCount; s++)
        {
            waypointDistances[s] = Length;

            Vector3 p0 = nodes[GetNodeIndex(s - 1, count)];
            Vector3 p1 = nodes[s];
            Vector3 p2 = nodes[GetNodeIndex(s + 1, count)];
            Vector3 p3 = nodes[GetNodeIndex(s + 2, count)];

            for (int k = 1; k <= steps; k++)
            {
                float t = k / (float)steps;
                Vector3 point = smooth ? CatmullRom(p0, p1, p2, p3, t) : Vector3.Lerp(p1, p2, t);
                Length += Vector3.Distance(samples[samples.Count - 1], point);
                samples.Add(point);
                sampleDistances.Add(Length);
            }
        }

        if (!closedLoop)
            waypointDistances[count - 1] = Length;
    }

    // Closed loops wrap around; open paths repeat their end points so the curve starts/ends exactly on them
    private int GetNodeIndex(int i, int count)
    {
        if (closedLoop) return ((i % count) + count) % count;
        return Mathf.Clamp(i, 0, count - 1);
    }

    private static Vector3 CatmullRom(Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3, float t)
    {
        float t2 = t * t;
        float t3 = t2 * t;
        return 0.5f * ((2f * p1) + (-p0 + p2) * t + (2f * p0 - 5f * p1 + 4f * p2 - p3) * t2 + (-p0 + 3f * p1 - 3f * p2 + p3) * t3);
    }

    public float GetWaypointDistance(int index)
    {
        EnsureBuilt();
        return waypointDistances[index];
    }

    public Waypoint GetWaypointSettings(int index)
    {
        EnsureBuilt();
        return (index >= 0 && index < waypointSettings.Length) ? waypointSettings[index] : null;
    }

    public float GetDwellSeconds(int index)
    {
        Waypoint settings = GetWaypointSettings(index);
        return settings != null ? Mathf.Max(0f, settings.dwellSeconds) : 0f;
    }

    // Position and travel direction at a distance along the route
    public void Evaluate(float distance, out Vector3 position, out Vector3 tangent)
    {
        EnsureBuilt();

        if (samples.Count < 2 || Length <= 0f)
        {
            position = transform.childCount > 0 ? transform.GetChild(0).position : transform.position;
            tangent = transform.forward;
            return;
        }

        distance = closedLoop ? Mathf.Repeat(distance, Length) : Mathf.Clamp(distance, 0f, Length);

        // Binary search for the polyline segment containing this distance
        int lo = 0;
        int hi = sampleDistances.Count - 1;
        while (hi - lo > 1)
        {
            int mid = (lo + hi) / 2;
            if (sampleDistances[mid] <= distance) lo = mid;
            else hi = mid;
        }

        float segmentLength = sampleDistances[hi] - sampleDistances[lo];
        float t = segmentLength > 0f ? (distance - sampleDistances[lo]) / segmentLength : 0f;
        position = Vector3.Lerp(samples[lo], samples[hi], t);
        tangent = (samples[hi] - samples[lo]).normalized;
    }

    void OnDrawGizmos()
    {
        // Rebuild in edit mode so the drawn route follows waypoint edits
        if (!Application.isPlaying) Build();

        Gizmos.color = gizmoColor;
        for (int i = 1; i < samples.Count; i++)
            Gizmos.DrawLine(samples[i - 1], samples[i]);

        for (int i = 0; i < transform.childCount; i++)
            Gizmos.DrawWireSphere(transform.GetChild(i).position, 0.3f);
    }
}
