using Normal.Realtime;
using UnityEngine;

// Shared clock for moving scene objects (cars, pedestrians, animals).
// When the Normcore room is connected we use its synchronized room time, so every client computes the
// same position for every mover without sending any per-object network traffic. Offline we fall back to local time.
public static class MovingObjectClock
{
    private static Realtime realtime;
    private static float nextSearchTime;

    public static bool IsSynced => realtime != null && realtime.connected;

    public static double Now
    {
        get
        {
            // Look up the scene's Realtime instance (throttled, and again after a scene change destroys the old one)
            if (realtime == null && Time.unscaledTime >= nextSearchTime)
            {
                realtime = Object.FindObjectOfType<Realtime>();
                nextSearchTime = Time.unscaledTime + 1f;
            }

            if (realtime != null && realtime.connected)
                return realtime.room.time;

            return Time.timeAsDouble;
        }
    }
}
