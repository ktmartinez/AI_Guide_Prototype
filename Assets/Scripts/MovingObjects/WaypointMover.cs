using System.Collections.Generic;
using UnityEngine;

// Moves a scene object (car, pedestrian, bird, ...) along a WaypointPath, pausing at waypoints that have dwell time.
// The pose is a pure function of MovingObjectClock time, so every networked client sees the object in the same place.
// For the AI guide to perceive the object, its mesh GameObject must be on the Key Items layer, carry the object's
// descriptive name, and have a non-trigger collider on the same GameObject as its renderer
// (see SpatialPerceptionSensor.InitializeSemanticRegistry). Movers must exist when the scene starts - don't spawn them later.
public class WaypointMover : MonoBehaviour
{
    // All enabled movers, used by the guide's moving-hazard warnings (AIGuide.CheckMovingHazards)
    public static readonly List<WaypointMover> Active = new List<WaypointMover>();

    public WaypointPath path;
    [Tooltip("Travel speed in meters per second")]
    public float speed = 1.4f;
    [Tooltip("Where in its cycle this mover starts (0-1), so several movers can share one path without overlapping")]
    [Range(0f, 1f)] public float cycleOffset = 0f;

    [Header("Orientation")]
    [Tooltip("Only turn around the vertical axis (walkers). Turn off to let vehicles pitch with sloped roads")]
    public bool yawOnly = true;
    [Tooltip("Extra rotation for models whose front isn't +Z")]
    public Vector3 modelRotationOffset;
    public float turnSharpness = 8f;

    [Header("Offsets")]
    public float heightOffset = 0f;
    [Tooltip("Vertical bobbing for flying/hovering movers (meters)")]
    public float bobAmplitude = 0f;
    public float bobFrequency = 0.5f;

    [Header("Animation")]
    public Animator animator;
    [Tooltip("Float parameter set to the current speed (0 while waiting); ignored if the controller doesn't have it")]
    public string speedParameter = "Speed";

    [Header("Guide Detection")]
    [Tooltip("Collider the guide uses for hazard warnings - the named mesh object on the Key Items layer")]
    public Collider hazardCollider;

    public Vector3 Velocity { get; private set; }

    private struct Leg
    {
        public float startDistance;
        public float endDistance;
        public float duration;
        public int waypointIndex; // set for waiting legs, -1 while travelling
        public bool IsTravel => startDistance != endDistance;
    }

    private readonly List<Leg> legs = new List<Leg>();
    private float cycleDuration;
    private int speedParameterHash;
    private bool hasSpeedParameter;
    private Quaternion targetRotation;

    void Awake()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        if (hazardCollider == null) hazardCollider = FindHazardCollider();

        if (animator != null && animator.runtimeAnimatorController != null && !string.IsNullOrEmpty(speedParameter))
        {
            speedParameterHash = Animator.StringToHash(speedParameter);
            foreach (AnimatorControllerParameter parameter in animator.parameters)
            {
                if (parameter.nameHash == speedParameterHash && parameter.type == AnimatorControllerParameterType.Float)
                    hasSpeedParameter = true;
            }
        }

        // Moving colliders should be kinematic bodies so physics doesn't treat them as static geometry
        Rigidbody body = GetComponent<Rigidbody>();
        if (body != null) body.isKinematic = true;

        BuildTimeline();
        targetRotation = transform.rotation;
        ApplyPose(MovingObjectClock.Now, true);
    }

    void OnEnable()
    {
        Active.Add(this);
    }

    void OnDisable()
    {
        Active.Remove(this);
    }

    void Update()
    {
        ApplyPose(MovingObjectClock.Now, false);
    }

    // Prefer the named Key Items mesh collider that the perception sensor registers
    private Collider FindHazardCollider()
    {
        int keyItemsLayer = LayerMask.NameToLayer("Key Items");
        Collider fallback = null;
        foreach (Collider candidate in GetComponentsInChildren<Collider>())
        {
            if (candidate.isTrigger) continue;
            if (candidate.gameObject.layer == keyItemsLayer && candidate.GetComponent<Renderer>() != null) return candidate;
            if (fallback == null) fallback = candidate;
        }
        return fallback;
    }

    // Lay out one full cycle as a sequence of waiting and travelling legs
    private void BuildTimeline()
    {
        legs.Clear();
        cycleDuration = 0f;
        if (path == null || speed <= 0f) return;

        path.EnsureBuilt();
        int count = path.WaypointCount;
        if (count < 2) return;

        if (path.closedLoop)
        {
            for (int i = 0; i < count; i++)
            {
                AddWait(i);
                float end = i + 1 < count ? path.GetWaypointDistance(i + 1) : path.Length;
                AddTravel(path.GetWaypointDistance(i), end);
            }
        }
        else
        {
            // Open paths go there and back again
            for (int i = 0; i < count - 1; i++)
            {
                AddWait(i);
                AddTravel(path.GetWaypointDistance(i), path.GetWaypointDistance(i + 1));
            }
            for (int i = count - 1; i > 0; i--)
            {
                AddWait(i);
                AddTravel(path.GetWaypointDistance(i), path.GetWaypointDistance(i - 1));
            }
        }
    }

    private void AddWait(int waypointIndex)
    {
        float dwell = path.GetDwellSeconds(waypointIndex);
        if (dwell <= 0f) return;

        float distance = path.GetWaypointDistance(waypointIndex);
        legs.Add(new Leg { startDistance = distance, endDistance = distance, duration = dwell, waypointIndex = waypointIndex });
        cycleDuration += dwell;
    }

    private void AddTravel(float from, float to)
    {
        float duration = Mathf.Abs(to - from) / speed;
        if (duration <= 0f) return;

        legs.Add(new Leg { startDistance = from, endDistance = to, duration = duration, waypointIndex = -1 });
        cycleDuration += duration;
    }

    private void ApplyPose(double now, bool snap)
    {
        if (legs.Count == 0 || cycleDuration <= 0f) return;

        // Where we are in the cycle (double math keeps precision with large room times)
        double cycleTime = (now + cycleOffset * (double)cycleDuration) % cycleDuration;
        if (cycleTime < 0) cycleTime += cycleDuration;
        float t = (float)cycleTime;

        int legIndex = 0;
        while (legIndex < legs.Count - 1 && t >= legs[legIndex].duration)
        {
            t -= legs[legIndex].duration;
            legIndex++;
        }

        Leg leg = legs[legIndex];
        float progress = Mathf.Clamp01(t / leg.duration);
        path.Evaluate(Mathf.Lerp(leg.startDistance, leg.endDistance, progress), out Vector3 position, out Vector3 tangent);

        Vector3 heading = tangent * (leg.endDistance >= leg.startDistance ? 1f : -1f);
        Velocity = leg.IsTravel ? heading * speed : Vector3.zero;

        if (bobAmplitude > 0f)
        {
            double bobPhase = (now * bobFrequency + cycleOffset) % 1.0;
            position.y += bobAmplitude * Mathf.Sin((float)bobPhase * 2f * Mathf.PI);
        }
        position.y += heightOffset;
        transform.position = position;

        Vector3 facing = leg.IsTravel ? heading : GetWaitingFacing(leg, legIndex);
        if (yawOnly) facing.y = 0f;
        if (facing.sqrMagnitude > 0.0001f)
            targetRotation = Quaternion.LookRotation(facing.normalized, Vector3.up) * Quaternion.Euler(modelRotationOffset);

        transform.rotation = snap
            ? targetRotation
            : Quaternion.Slerp(transform.rotation, targetRotation, 1f - Mathf.Exp(-turnSharpness * Time.deltaTime));

        if (hasSpeedParameter)
            animator.SetFloat(speedParameterHash, leg.IsTravel ? speed : 0f);
    }

    // While waiting, face the waypoint's forward (e.g. sitting at a bench) or the direction of the next leg
    private Vector3 GetWaitingFacing(Leg leg, int legIndex)
    {
        Waypoint settings = path.GetWaypointSettings(leg.waypointIndex);
        if (settings != null && settings.faceForwardWhileWaiting)
            return settings.transform.forward;

        Leg next = legs[(legIndex + 1) % legs.Count];
        path.Evaluate(next.startDistance, out _, out Vector3 nextTangent);
        return nextTangent * (next.endDistance >= next.startDistance ? 1f : -1f);
    }

    // Editor helper: place the object where it will be at the start of its cycle
    [ContextMenu("Snap To Cycle Start")]
    private void SnapToCycleStart()
    {
        if (animator == null) animator = GetComponentInChildren<Animator>();
        BuildTimeline();
        ApplyPose(0.0, true);
    }
}
