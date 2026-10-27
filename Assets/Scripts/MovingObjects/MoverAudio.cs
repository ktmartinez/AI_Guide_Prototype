using System.Collections.Generic;
using UnityEngine;

// Spatial sound for a WaypointMover: a looping sound (engine, chatter, footsteps) that follows whether the mover is moving or waiting,
// a one-shot when it sets off (a bird taking off), occasional one-shots (barks, chirps), and a honk when the local player stands in its path.
// Sounds are timed from MovingObjectClock and a per-object seed instead of Random, so every networked client hears the same sound at the
// same moment - except the honk, which reacts to each client's own player.
[RequireComponent(typeof(WaypointMover))]
public class MoverAudio : MonoBehaviour
{
    [Header("Looping Sound")]
    [Tooltip("Engine, chatter or footsteps. Each mover starts at a different point in the clip so crowds don't play in sync")]
    public AudioClip loopClip;
    [Range(0f, 1f)] public float movingVolume = 1f;
    [Tooltip("Volume while waiting at a waypoint - 0 for footsteps, some for an idling engine or people chatting on a bench")]
    [Range(0f, 1f)] public float waitingVolume = 0.3f;
    public float movingPitch = 1f;
    public float waitingPitch = 0.8f;
    [Tooltip("How quickly volume and pitch follow the mover starting and stopping")]
    public float fadeSharpness = 3f;

    [Header("One-Shots")]
    [Tooltip("Played when the mover sets off after waiting at a waypoint (e.g. a bird taking off)")]
    public AudioClip departClip;
    [Tooltip("Played every so often, picked at random (e.g. barks, chirps)")]
    public AudioClip[] occasionalClips = new AudioClip[0];
    [Tooltip("Average seconds between occasional clips")]
    public float occasionalInterval = 15f;
    [Range(0f, 1f)] public float oneShotVolume = 1f;
    [Tooltip("One-shots play up to this much higher or lower in pitch so repeats don't sound identical")]
    [Range(0f, 0.5f)] public float oneShotPitchVariation = 0.1f;

    [Header("Honk")]
    [Tooltip("Played when this client's player is standing in the mover's path (vehicles)")]
    public AudioClip honkClip;
    public float honkDistance = 15f;
    [Tooltip("How far to the side of the mover's path the player can be and still get honked at")]
    public float honkPathHalfWidth = 1.5f;
    public float honkCooldown = 8f;

    [Header("Spatial Settings")]
    [Tooltip("Full volume within this distance")]
    public float minDistance = 2f;
    [Tooltip("Fades to silence by this distance")]
    public float maxDistance = 40f;
    [Range(0f, 5f)] public float dopplerLevel = 0.5f;
    [Tooltip("When too many sounds play at once, lower numbers keep playing (vehicles should win over birds)")]
    [Range(0, 256)] public int priority = 128;

    private WaypointMover mover;
    private AudioSource loopSource;
    private AudioSource oneShotSource;
    private uint seed;
    private bool wasMoving;
    private long lastOccasionalSlot = long.MinValue;
    private float lastHonkTime = -999f;

    private static AudioListener listener;
    private static float nextListenerSearchTime;

    private bool IsMoving => mover.Velocity.sqrMagnitude > 0.0001f;

    void Awake()
    {
        mover = GetComponent<WaypointMover>();
        seed = HashString(gameObject.scene.name + GetHierarchyPath(transform));

        AnimationCurve rolloff = BuildRolloffCurve();
        loopSource = CreateSource(rolloff);
        oneShotSource = CreateSource(rolloff);
    }

    void Start()
    {
        // WaypointMover has placed itself by now, so we know whether we start out moving or waiting
        wasMoving = IsMoving;
        if (loopClip == null) return;

        loopSource.clip = loopClip;
        loopSource.loop = true;
        loopSource.volume = wasMoving ? movingVolume : waitingVolume;
        loopSource.pitch = wasMoving ? movingPitch : waitingPitch;
        loopSource.Play();
        loopSource.time = Random01(0, 0) * loopClip.length;
    }

    void Update()
    {
        bool moving = IsMoving;

        if (loopClip != null)
        {
            float blend = 1f - Mathf.Exp(-fadeSharpness * Time.deltaTime);
            loopSource.volume = Mathf.Lerp(loopSource.volume, moving ? movingVolume : waitingVolume, blend);
            loopSource.pitch = Mathf.Lerp(loopSource.pitch, moving ? movingPitch : waitingPitch, blend);
        }

        if (moving && !wasMoving) PlayOneShot(departClip);
        wasMoving = moving;

        UpdateOccasionalClips();
        UpdateHonk();
    }

    // Time is split into slots of occasionalInterval, and each slot plays one clip at a seeded point within it
    private void UpdateOccasionalClips()
    {
        if (occasionalClips == null || occasionalClips.Length == 0 || occasionalInterval <= 0f) return;

        double now = MovingObjectClock.Now;
        long slot = (long)System.Math.Floor(now / occasionalInterval);
        if (slot == lastOccasionalSlot) return;

        double playTime = (slot + Random01(slot, 1)) * occasionalInterval;
        if (now < playTime) return;

        lastOccasionalSlot = slot;
        // Skip slots we only notice late (e.g. when the clock switches over to the network's room time) instead of playing them all at once
        if (now - playTime > 1.0) return;

        PlayOneShot(occasionalClips[(int)(Random01(slot, 2) * occasionalClips.Length)]);
    }

    // Honk when this client's player is standing in the path ahead of the mover
    private void UpdateHonk()
    {
        if (honkClip == null || Time.time - lastHonkTime < honkCooldown) return;

        Vector3 heading = new Vector3(mover.Velocity.x, 0f, mover.Velocity.z);
        if (heading.sqrMagnitude < 0.01f) return;
        heading.Normalize();

        AudioListener player = GetListener();
        if (player == null) return;

        Vector3 toPlayer = player.transform.position - transform.position;
        toPlayer.y = 0f;
        float ahead = Vector3.Dot(toPlayer, heading);
        if (ahead <= 0f || ahead > honkDistance) return;
        if ((toPlayer - heading * ahead).magnitude > honkPathHalfWidth) return;

        lastHonkTime = Time.time;
        PlayOneShot(honkClip);
    }

    private void PlayOneShot(AudioClip clip)
    {
        if (clip == null) return;
        oneShotSource.pitch = 1f + Random.Range(-oneShotPitchVariation, oneShotPitchVariation);
        oneShotSource.PlayOneShot(clip, oneShotVolume);
    }

    // The listener sits on the local player's headset (throttled lookup, repeated after a scene change destroys the old one)
    private static AudioListener GetListener()
    {
        if (listener == null && Time.unscaledTime >= nextListenerSearchTime)
        {
            listener = FindObjectOfType<AudioListener>();
            nextListenerSearchTime = Time.unscaledTime + 1f;
        }
        return listener;
    }

    private AudioSource CreateSource(AnimationCurve rolloff)
    {
        AudioSource source = gameObject.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 1f; // fully 3D
        source.spatialize = true; // uses the project's spatializer plugin (HRTF) once one is set in Project Settings > Audio
        source.dopplerLevel = dopplerLevel;
        source.priority = priority;
        source.minDistance = minDistance;
        source.maxDistance = maxDistance;
        source.rolloffMode = AudioRolloffMode.Custom;
        source.SetCustomCurve(AudioSourceCurveType.CustomRolloff, rolloff);
        return source;
    }

    // Inverse-distance falloff like Unity's Logarithmic rolloff, but reaching silence at maxDistance so far-off movers stop taking up voices.
    // The curve's x axis is distance / maxDistance.
    private AnimationCurve BuildRolloffCurve()
    {
        float fullVolumeEnd = Mathf.Clamp(minDistance / Mathf.Max(maxDistance, 0.01f), 0.001f, 1f);
        List<Keyframe> keys = new List<Keyframe> { new Keyframe(0f, 1f) };
        for (float x = fullVolumeEnd; x < 1f; x *= 1.5f)
        {
            float fade = Mathf.Clamp01((1f - x) / 0.25f); // fade out over the last quarter of the range
            keys.Add(new Keyframe(x, fullVolumeEnd / x * fade));
        }
        keys.Add(new Keyframe(1f, 0f));

        // Straight lines between the keys
        Keyframe[] frames = keys.ToArray();
        for (int i = 0; i < frames.Length; i++)
        {
            if (i > 0) frames[i].inTangent = (frames[i].value - frames[i - 1].value) / (frames[i].time - frames[i - 1].time);
            if (i < frames.Length - 1) frames[i].outTangent = (frames[i + 1].value - frames[i].value) / (frames[i + 1].time - frames[i].time);
        }
        return new AnimationCurve(frames);
    }

    // The same on every client for objects placed in the scene, unlike instance IDs
    private static string GetHierarchyPath(Transform t)
    {
        string path = t.GetSiblingIndex() + t.name;
        for (Transform parent = t.parent; parent != null; parent = parent.parent)
            path = parent.GetSiblingIndex() + parent.name + "/" + path;
        return path;
    }

    // FNV-1a - string.GetHashCode isn't guaranteed to match between the editor and device builds
    private static uint HashString(string text)
    {
        uint hash = 2166136261;
        foreach (char c in text)
        {
            hash ^= c;
            hash *= 16777619;
        }
        return hash;
    }

    // Seeded number in [0, 1) - the same on every client for the same mover, slot and channel
    private float Random01(long slot, uint channel)
    {
        uint h = seed ^ (channel * 0x9E3779B9u);
        h = Mix(h ^ (uint)slot);
        h = Mix(h ^ (uint)(slot >> 32));
        return (h >> 8) / 16777216f;
    }

    private static uint Mix(uint h)
    {
        h ^= h >> 16;
        h *= 0x85EBCA6Bu;
        h ^= h >> 13;
        h *= 0xC2B2AE35u;
        h ^= h >> 16;
        return h;
    }
}
