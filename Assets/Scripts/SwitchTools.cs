using Normal.Realtime;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Serialization;

public class SwitchTools : MonoBehaviour
{
    // Define an event that other scripts can listen to
    public event Action OnGuideConfigurationChanged;

    // Set these in Unity Editor to switch tools/features
    [Header("Tool Activation")]
    public bool VRGuideActive = false;
    public bool VRScreenreaderActive = false;

    // Define all possible guide type
    public enum GuideType
    {
        Baseline,
        ObjectDescription,
        ObjectLocation,
        SceneUnderstanding,
        Navigation,
        ObjectGrabbing,
        //SightedGuidance,
        AllCombined // Deprecated for now, but kept for future use
    }

    [Header("Guide Settings")]
    [SerializeField]
    private GuideType _currentGuideType = GuideType.Baseline;
    [Tooltip("The silence time in seconds that the guide waits for before responding")]
    public float silenceThreshold = 2.5f; // default is 2.5 seconds, 1.2 cuts you off a lot
    [Tooltip("If on, the guide greets the player and introduces itself when the session starts")]
    public bool openingGreeting = true;
    [Tooltip("The time in seconds after the guide connects before it greets the player")]
    public float greetingDelay = 2f;

    public GuideType activeGuideType
    {
        get => _currentGuideType;
        set
        {
            if (_currentGuideType != value)
            {
                _currentGuideType = value;
                OnGuideConfigurationChanged?.Invoke();
            }
        }
    }

    /*[SerializeField] private bool _baselineGuide;
    public bool baselineGuide
    {
        get => _baselineGuide;
        set
        {
            if (_baselineGuide != value)
            {
                _baselineGuide = value;
                if (_baselineGuide == true)
                    OnGuideConfigurationChanged?.Invoke(); // Notify listeners!
            }
        }
    }

    [SerializeField] private bool _objectDescriptionGuide;
    public bool objectDescriptionGuide
    {
        get => _objectDescriptionGuide;
        set
        {
            if (_objectDescriptionGuide != value)
            {
                _objectDescriptionGuide = value;
                if (_objectDescriptionGuide == true)
                    OnGuideConfigurationChanged?.Invoke(); // Notify listeners!
            }
        }
    }

    [SerializeField] private bool _objectLocationGuide;
    public bool objectLocationGuide
    {
        get => _objectLocationGuide;
        set
        {
            if (_objectLocationGuide != value)
            {
                _objectLocationGuide = value;
                if (_objectLocationGuide == true)
                    OnGuideConfigurationChanged?.Invoke(); // Notify listeners!
            }
        }
    }

    [SerializeField] private bool _sceneUnderstandingGuide;
    public bool sceneUnderstandingGuide
    {
        get => _sceneUnderstandingGuide;
        set
        {
            if (_sceneUnderstandingGuide != value)
            {
                _sceneUnderstandingGuide = value;
                if (_sceneUnderstandingGuide == true)
                    OnGuideConfigurationChanged?.Invoke(); // Notify listeners!
            }
        }
    }

    [SerializeField] private bool _navigationGuide;
    public bool navigationGuide
    {
        get => _navigationGuide;
        set
        {
            if (_navigationGuide != value)
            {
                _navigationGuide = value;
                if (_navigationGuide == true)
                    OnGuideConfigurationChanged?.Invoke(); // Notify listeners!
            }
        }
    }

    [SerializeField] private bool _objectGrabbingGuide;
    public bool objectGrabbingGuide
    {
        get => _objectGrabbingGuide;
        set
        {
            if (_objectGrabbingGuide != value)
            {
                _objectGrabbingGuide = value;
                if (_objectGrabbingGuide == true)
                    OnGuideConfigurationChanged?.Invoke(); // Notify listeners!
            }
        }
    }

    [SerializeField] private bool _sightedGuidanceGuide;
    public bool sightedGuidanceGuide
    {
        get => _sightedGuidanceGuide;
        set
        {
            if (_sightedGuidanceGuide != value)
            {
                _sightedGuidanceGuide = value;
                if (_sightedGuidanceGuide == true)
                    OnGuideConfigurationChanged?.Invoke(); // Notify listeners!
            }
        }
    }

    private bool _allCombinedGuide = false; // definitively set to false for now - use later if we trust the AI to determine user intention
    public bool allCombinedGuide
    {
        get => _allCombinedGuide;
        set
        {
            if (_allCombinedGuide != value)
            {
                _allCombinedGuide = value;
                if (_allCombinedGuide == true)
                    OnGuideConfigurationChanged?.Invoke(); // Notify listeners!
            }
        }
    }*/

    [FormerlySerializedAs("pushToTalk")]
    [HideInInspector] public bool legacyHoldToSpeak = false;
    [HideInInspector] public bool continuousVoice = false;
    [HideInInspector] public bool personalVoicesOn = false;

    // Default guide interaction is the newer push-to-talk flow unless a legacy mode is explicitly enabled.
    public bool UseDefaultPushToTalk => !continuousVoice && !legacyHoldToSpeak;
    
    // Start is called before the first frame update
    void Start()
    {
        // If the guide is meant to be active in the scene
        if (VRGuideActive)
            FindObjectOfType<VRScreenreader>().gameObject.SetActive(false); // disable screenreader

        // If the screenreader is meant to be active in the scene
        if (VRScreenreaderActive)
            DisableGuide();

        // If neither tool is active, apply confederate settings
        if (!VRGuideActive && !VRScreenreaderActive)
        {
            FindObjectOfType<VRScreenreader>().gameObject.SetActive(false); // disable screenreader
            DisableGuide();
            ApplyRandomConfederateAvatar();
        }
    }

    private void DisableGuide()
    {
        GameObject guide = FindObjectOfType<GuideFollow>().gameObject;
        guide.SetActive(false); // disable XR Origin (Guide Rig)

        // Find all RealtimeAvatarManagers, if they have "Guide Avatar" assigned, deactivate them
        var managers = FindObjectsOfType<RealtimeAvatarManager>();
        foreach (RealtimeAvatarManager manager in managers)
        {
            if (manager.localAvatarPrefab.name == "Guide Avatar")
                manager.enabled = false;
        }
    }

    private void ApplyRandomConfederateAvatar()
    {
        // Find all RealtimeAvatarManagers, if they have "Guide Avatar" assigned, deactivate them
        var managers = FindObjectsOfType<RealtimeAvatarManager>();
        foreach (RealtimeAvatarManager manager in managers)
        {
            if (manager.localAvatarPrefab.name == "Player Avatar")
            {
                int randConfed = UnityEngine.Random.Range(1, 3);

                switch(randConfed)
                {
                    case 1:
                        manager.localAvatarPrefab = Resources.Load<GameObject>("Confederate_One Avatar");
                        break;
                    case 2:
                        manager.localAvatarPrefab = Resources.Load<GameObject>("Confederate_Two Avatar");
                        break;
                }
            }
        }
    }

#if UNITY_EDITOR
    // Safely catch Inspector dropdown changes during play mode
    private GuideType _lastValidatedGuideType;

    private void OnValidate()
    {
        if (Application.isPlaying && _currentGuideType != _lastValidatedGuideType)
        {
            _lastValidatedGuideType = _currentGuideType;
            Debug.Log($"[SwitchTools] Guide mode switched to: {_currentGuideType}");
            OnGuideConfigurationChanged?.Invoke();
        }
    }
#endif
}
