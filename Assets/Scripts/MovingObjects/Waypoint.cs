using UnityEngine;

// Optional per-waypoint settings for a WaypointPath child (e.g. a pedestrian pausing at a bench, a bird landing on a roof)
public class Waypoint : MonoBehaviour
{
    [Tooltip("Seconds a mover waits at this waypoint before continuing")]
    public float dwellSeconds = 0f;

    [Tooltip("While waiting here, face this waypoint's forward direction instead of the direction of travel")]
    public bool faceForwardWhileWaiting = false;
}
