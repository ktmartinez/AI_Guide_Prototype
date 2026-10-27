using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using UnityEngine;

public class AIGuide : MonoBehaviour
{
    #region Variables and Scripts
    // Variables to hold scripts we need access to
    private AutomaticGuide m_AutomatedGuideScript;
    private OpenAIQueries m_OpenAIQueriesScript;
    private VRHandling m_VRHandlingScript;
    public SharedMovement m_SharedMovementScript;
    public GuideFollow m_GuideFollowScript;
    private AutomaticModification m_AutomaticModificationScript;
    private RealtimeGuideClient realtimeClient;
    private SwitchTools m_SwitchToolsScript;
    private CameraSystem camSystem;
    private SpatialPerceptionSensor perceptionSensor;

    // Variables for monitoring
    private bool guideRoleAssigned = false;
    private bool guideRoleAssignedStart = false;

    private GameObject lastHighlightedTarget;
    private Dictionary<GameObject, Material> originalMaterials = new Dictionary<GameObject, Material>();

    private bool isRecording = false;

    private bool wasMutingLastFrame = false;
    private bool wasVRButtonDownLastFrame = false;
    private bool isProactiveChecking = true;

    // Variables for hazard detection
    private float dangerZoneDistance = 1.5f;
    private float hazardCheckInterval = 0.25f; // hazard detection frequency (see CheckHazardDistances())
    private float hazardPromptCooldown = 6.0f; // response frequency from guide
    private LayerMask hazardLayerMask;
    private int maxHazardsDetected = 10;
    private Collider[] hazardObjectColliders;
    private float nextHazardCheckTime = 0f;
    private Vector3 previousPlayerPosition;
    private Vector3 trueWorldVelocity;
    public Transform headsetTransform;

    private Dictionary<int, float> promptedHazardsHistory = new Dictionary<int, float>();
    private float lastHazardPromptTime = -999f;
    private GameObject lastHazardPrompted;
    private float globalHazardCooldown = 4.0f; 
    private float perObjectCooldown = 20.0f;
    private float maxTTCOfInterest = 2.0f;

    // Variables for moving hazards (cars, pedestrians, animals - see WaypointMover)
    private float movingHazardRadius = 12.0f; // always consider movers this close (XZ distance) - faster movers are considered from further away
    private float maxMovingTTCOfInterest = 3.0f; // movers close faster than the player walks, so warn a little earlier
    private float movingHazardSpeechDelay = 1.0f; // roughly how long the guide takes to start speaking, so warn this much earlier again
    private float movingHazardInterruptSpeed = 2.5f; // only cut the guide off mid-sentence for movers closing faster than this (m/s) - cars, not pedestrians

    // Variables for prompting the user if they need assistance
    private float lastPlayerInteractionTime;
    private float idleTimeout = 120f; // seconds before guide asks if someone needs help
    private bool hasPromptedForHelp = false;

    // Variables for continuous description during routes
    private bool isDescribingRoute = false;
    private Coroutine routeDescriptionCoroutine;
    private string destination;

    // Variables for continuous hand movement instructions during grabbing
    private string targetToGrab;
    private bool isGrabbing = false;
    private Coroutine grabLoopCoroutine;

    // Variables for wizard components
    public string result;
    public int role = 1; // 1: human, 2: robot, 3: cane, 4: guide dog, 5: bird, 6: invisible

    // Audio relevant variables
    private AudioSource sfxAudioSource;
    private AudioClip chimeClip;
    private AudioClip processingClip;
    private AudioClip listeningClip;
    private AudioClip doneListeningClip;
    #endregion

    // Start is called before the first frame update
    void Start()
    {
        AddGuideComponents();

        SetupRealtimeClient();

        //InvokeRepeating("UpdateVisualContext", 2.0f, 7.0f);

        Debug.Log("AIGuide is active!");

        hazardLayerMask = LayerMask.GetMask("Key Items");
        hazardObjectColliders = new Collider[maxHazardsDetected];
    }

    private void AddGuideComponents()
    {
        // Find necessary components to the attached GameObject
        m_GuideFollowScript = FindObjectOfType<GuideFollow>(); // On Guide XR Rig
        m_SwitchToolsScript = FindObjectOfType<SwitchTools>(); // On Room Manager
        perceptionSensor = FindObjectOfType<SpatialPerceptionSensor>(); // On Guide XR Rig

        // Add necessary components to the attached GameObject
        m_AutomaticModificationScript = gameObject.AddComponent<AutomaticModification>();
        m_AutomatedGuideScript = gameObject.AddComponent<AutomaticGuide>();
        m_VRHandlingScript = gameObject.AddComponent<VRHandling>();
        m_OpenAIQueriesScript = gameObject.AddComponent<OpenAIQueries>();
        LoadAudioResources();

        // This line is needed if we use the invisible guide role
        if (role == 6)
            DisableColliders(FindObjectOfType<GuideRoleSync>().gameObject);
    }

    private void LoadAudioResources()
    {
        // Load clips once at the start
        chimeClip = Resources.Load<AudioClip>("Audio/subway_chime");
        processingClip = Resources.Load<AudioClip>("Audio/processing");
        listeningClip = Resources.Load<AudioClip>("Audio/listening");
        doneListeningClip = Resources.Load<AudioClip>("Audio/done_listening");

        // Add a special audio source for the sound effects so it doesn't interfere with the mic channel
        sfxAudioSource = gameObject.AddComponent<AudioSource>();
        sfxAudioSource.playOnAwake = false;
        sfxAudioSource.spatialBlend = 0; // 2D sound for UI effects (clearer)
    }

    private async void SetupRealtimeClient()
    {
        // Set up realtime client
        realtimeClient = gameObject.AddComponent<RealtimeGuideClient>();

        string basePrompt = GetFormattedPrompt();

        // Load config and connect to client
        realtimeClient.LoadConfig();
        realtimeClient._legacyHoldToSpeakOn = m_SwitchToolsScript != null && m_SwitchToolsScript.legacyHoldToSpeak;
        realtimeClient._continuousVoiceOn = m_SwitchToolsScript != null && m_SwitchToolsScript.continuousVoice;
        realtimeClient._defaultPushToTalkOn = m_SwitchToolsScript == null || m_SwitchToolsScript.UseDefaultPushToTalk;

        switch (m_SwitchToolsScript.activeGuideType)
        {
            case SwitchTools.GuideType.Baseline:
                await realtimeClient.Connect(basePrompt, true); // tell the client which type of initial session update to pass
                break;
            case SwitchTools.GuideType.AllCombined:
                await realtimeClient.Connect(basePrompt, true); // tell the client which type of initial session update to pass
                break;
            default:
                await realtimeClient.Connect(basePrompt, false);
                break;
        }

        while (!realtimeClient._isConnected)
        {
            // Buffer until the client is for sure connected
            await Task.Delay(100);
        }

        // Add a small buffer to let the server process the session.update since API will drop 'conversation.item.create' 
        // if they arrive at the same time
        await Task.Delay(500);

        // Fetch the memory from the manager if we can find it - should be on the guide rig
        MemoryManager memoryManager = FindObjectOfType<MemoryManager>();
        if (memoryManager != null)
        {
            string pastMemoryContext = memoryManager.GetFormattedSessionHistory();
            if (!string.IsNullOrEmpty(pastMemoryContext))
            {
                realtimeClient.SendTextContext(pastMemoryContext, "system");
                Debug.Log($"[AI Guide] Injected previous session memory into realtime client: {pastMemoryContext}");
            }
        }

        realtimeClient.OnAutoStopRecording += HandleAutoStop; // Subscribe to the event of whenever the client auto-stops (detected a user stopped speaking)
        m_SwitchToolsScript.OnGuideConfigurationChanged += HandleGuideTypeChanged;

        // Reset player interaction time with the client
        lastPlayerInteractionTime = Time.time;

        // Have the guide greet the player now that the session is fully set up
        await SendOpeningGreeting(memoryManager);
    }

    // For ensuring proper realtime data
    public string GetFormattedPrompt()
    {
        // Ensure data is fresh
        m_OpenAIQueriesScript.LoadRoomDescriptions();
        m_OpenAIQueriesScript.getGuideRole();

        // Determine baseline or version of improved guide
        string prompt = "";

        switch (m_SwitchToolsScript.activeGuideType)
        {
            case SwitchTools.GuideType.Baseline:
                Debug.Log("Using the baseline guide!");
                prompt = "You are Giddy, a warm, friendly, but still professional sighted guide for a blind player. " + m_OpenAIQueriesScript.enhancedContextClassification +
                   "Address the player's questions to the best of your ability."; 
                break;
            case SwitchTools.GuideType.ObjectDescription:
                Debug.Log("Using the object description guide!");
                prompt = $"You are Giddy, a warm, friendly, but still professional sighted guide for a blind player. {m_OpenAIQueriesScript.enhancedContextClassification} {m_OpenAIQueriesScript.universalNotesGuideline}" +
                    $"The player is asking you about what an object looks like. {m_OpenAIQueriesScript.objectDescriptionGuideline}";
                break;
            case SwitchTools.GuideType.ObjectLocation:
                Debug.Log("Using the object location guide!");
                prompt = $"You are Giddy, a warm, friendly, but still professional sighted guide for a blind player. {m_OpenAIQueriesScript.enhancedContextClassification} {m_OpenAIQueriesScript.universalNotesGuideline}" +
                    $"The player is asking you about where an object is. {m_OpenAIQueriesScript.objectLocationGuideline}";
                break;
            case SwitchTools.GuideType.SceneUnderstanding:
                Debug.Log("Using the scene understanding guide!");
                prompt = $"You are Giddy, a warm, friendly, but still professional sighted guide for a blind player. {m_OpenAIQueriesScript.enhancedContextClassification} {m_OpenAIQueriesScript.universalNotesGuideline}" +
                    $"The player is asking you about what the scene around you both is like. {m_OpenAIQueriesScript.sceneUnderstandingGuideline}";
                break;
            case SwitchTools.GuideType.Navigation:
                Debug.Log("Using the navigation guide!");
                prompt = $"You are Giddy, a warm, friendly, but still professional sighted guide for a blind player. {m_OpenAIQueriesScript.enhancedContextClassification} {m_OpenAIQueriesScript.universalNotesGuideline}" +
                    $"The player is asking you for information to help with navigating somewhere on their own. { m_OpenAIQueriesScript.spaceNavigationGuideline}";
                break;
            case SwitchTools.GuideType.ObjectGrabbing:
                Debug.Log("Using the object grabbing guide!");
                prompt = $"You are Giddy, a warm, friendly, but still professional sighted guide for a blind player. {m_OpenAIQueriesScript.enhancedContextClassification} {m_OpenAIQueriesScript.universalNotesGuideline}" +
                    $"The player is asking you to help them grab an object."; // {m_OpenAIQueriesScript.grabbingObjectGuideline}";
                break;
            case SwitchTools.GuideType.AllCombined:
                Debug.Log("Using the all-guideline intention guide!");
                StringBuilder sbPrompt = new StringBuilder();

                // Base Persona & Rules
                sbPrompt.AppendLine($"You are Giddy, a warm, friendly, but still professional sighted guide for a blind player.");
                sbPrompt.AppendLine(m_OpenAIQueriesScript.enhancedContextClassification);
                sbPrompt.AppendLine(m_OpenAIQueriesScript.universalNotesGuideline);
                //sbPrompt.AppendLine($"THE NAVIGATION REGISTRY: {m_OpenAIQueriesScript.objectClassifications}");
                // New guideline on trust/revealing uncertainty
                sbPrompt.Append(m_OpenAIQueriesScript.trustGuideline);

                // Command functions for guidance, teleportation, and modification are handled by the tools architecture native to Realtime

                // Conditional Behavioral Guidelines
                sbPrompt.AppendLine("\n### CONDITIONAL GUIDELINES ###");
                sbPrompt.AppendLine("Depending on what the user asks, apply the following rules. If the user has multiple intents, combine the rules naturally.");

                sbPrompt.AppendLine("\nIF THE USER WANTS AN OBJECT DESCRIPTION:");
                sbPrompt.AppendLine(m_OpenAIQueriesScript.objectDescriptionGuideline);

                sbPrompt.AppendLine("\nIF THE USER IS LOCATING A SPECIFIC OBJECT:");
                sbPrompt.AppendLine(m_OpenAIQueriesScript.objectLocationGuideline);

                sbPrompt.AppendLine(m_OpenAIQueriesScript.sceneUnderstandingGuideline);

                sbPrompt.AppendLine("\nIF THE USER WANTS INFORMATION TO HELP THEM NAVIGATE SOMEWHERE ON THEIR OWN:");
                sbPrompt.AppendLine(m_OpenAIQueriesScript.spaceNavigationGuideline);

                //sbPrompt.AppendLine("\nIF THE USER IS REACHING FOR OR GRABBING AN OBJECT:");
                //sbPrompt.AppendLine(m_OpenAIQueriesScript.grabbingObjectGuideline);

                sbPrompt.AppendLine("\nIF THE USER NEEDS TECHNICAL SUPPORT:");
                sbPrompt.AppendLine(m_OpenAIQueriesScript.technicalSupportGuideline);
                sbPrompt.AppendLine("IMPORTANT: When mentioning any object to a player in ANY of your responses, you must use its registry name so that the player can learn it.");

                prompt = sbPrompt.ToString();
                break;
            default:
                // Using the improved guide, but haven't set a specific intention yet
                Debug.Log("Providing only basic information/introduction to the guide session!");
                prompt = $"You are Giddy, a warm, friendly, but still professional sighted guide for a blind player. {m_OpenAIQueriesScript.contextClassification}";
                break;
        }
        return prompt;
    }

    // Handles event of user done talking
    private void HandleAutoStop()
    {
        playEffect("done_listening");
        isRecording = false; // Unlock it so they can press the button again later
    }

    // Checks if a new guide type was assigned and switches prompts accordingly
    public async void HandleGuideTypeChanged()
    {
        if (realtimeClient == null) return;

        // Regenerate the fresh prompt string based on the newly toggled bools
        string freshPrompt = GetFormattedPrompt();

        // Immediately after we give the guide its basic instructions, share the guidance prompt for all guides
        //await realtimeClient.UpdateGuidancePrompt(freshPrompt);

        // Determine which type of update this is (certain updates use certain tools) and push to OpenAI
        if (m_SwitchToolsScript.activeGuideType.Equals(SwitchTools.GuideType.ObjectGrabbing))
            await realtimeClient.UpdateGrabbingPrompt(freshPrompt);
        else
            await realtimeClient.UpdateLivePrompt(freshPrompt);

        Debug.Log($"Guide version shifted successfully. Guide was told: {freshPrompt}");
    }

    // Update is called once per frame
    void Update()
    {
        // Toggle idle checking when 'K' is pressed
        if (Input.GetKeyDown(KeyCode.K))
        {
            isProactiveChecking = !isProactiveChecking;
            Debug.Log("Checking on Player: " + (isProactiveChecking ? "Enabled" : "Disabled"));
        }

        // Calls until the appropriate scripts are assigned (when we have a player and a guide)
        // Needed for access to the player's interactions with the guide + sharing guide audio over network + adding/accessing camera system
        getSharedMovement();

        // If we're in a scene run from a guide client
        if (FindObjectOfType<GuideFollow>())
        {
            // Call the guide
            RealtimeGuide();

            if (isProactiveChecking)
            {
                // See if the player has been silent for a while
                CheckForIdlePlayer();

                // Check the player's velocity so we can determine hazards
                checkPlayerVelocity();

                // Check for objects too close to the player - static hazards wait so they don't interrupt the guidance/grabbing descriptions,
                // but moving hazards (cars, pedestrians) are still checked, since they can come at the player at any time
                CheckHazardDistances(!isDescribingRoute && m_OpenAIQueriesScript.targetForGuidance == null && !isGrabbing);
            }

            // Determine if guidance is required based on GPT-4 response
            checkGuidanceRequests();

            // Determine if modification is required based on GPT-4 response
            checkModificationRequests();

            // If any confederate is present at the start of the scene, assign the guide
            AssignSingleConfederate();

            // Check if both confederates are present and send guide roles each time they are (in case of confederates leaving and coming back)
            BothConfederatesPresent();
        }
    }

    private void checkPlayerVelocity()
    {
        // Assign the headset transform
        if (headsetTransform == null)
            headsetTransform = m_SharedMovementScript.playerRig.Camera.transform;
        else
        {
            // Calculate true world-space velocity based on position delta - ignore the Y axis to strictly track ground-plane movement
            Vector3 currentPos = new Vector3(headsetTransform.position.x, 0, headsetTransform.position.z);
            Vector3 prevPos = new Vector3(previousPlayerPosition.x, 0, previousPlayerPosition.z);

            trueWorldVelocity = (currentPos - prevPos) / Time.deltaTime;
            previousPlayerPosition = headsetTransform.position;
        }
    }

    private void RealtimeGuide()
    {
        bool vrButtonDown = m_VRHandlingScript.isButtonPressed;
        bool spaceDown = Input.GetKeyDown(KeyCode.Space);
        // For continuous voice mode
        bool spaceUp = Input.GetKeyUp(KeyCode.Space);
        // Create a strict "Down This Frame" trigger for VR to prevent toggle spamming
        bool isDownThisFrame = (vrButtonDown && !wasVRButtonDownLastFrame) || spaceDown;
        wasVRButtonDownLastFrame = vrButtonDown; // Store for next frame

        if (realtimeClient._defaultPushToTalkOn)
        {
            // Default push-to-talk mode: press once, then auto-stop after silence.

            // Start recording on press, but ONLY if we aren't already recording
            if ((spaceDown || vrButtonDown) && !isRecording)
            {
                playEffect("listening");
                isRecording = true;
                StartCoroutine(CaptureAndSendContext());
                RecordPlayerInteraction(); // record a player interaction since the player has started to speak, stop ongoing counters
            }
            // We DO NOT have a stop condition here. 
            // RealtimeGuideClient will detect silence, stop it, and trigger HandleAutoStop().
        }

        // Logic to mute the guide
        if (m_VRHandlingScript.isMutingButtonPressed || Input.GetKeyDown(KeyCode.M))
        {
            // Only fire if this is the FIRST frame the button is down
            if (!wasMutingLastFrame)
            {
                _ = realtimeClient.StopAiSpeech();
                // Failsafe: if guide is taking the user somewhere and gets stuck, mute cancels guidance and resets
                m_AutomatedGuideScript.CancelGuidance();
                m_GuideFollowScript.enabled = true;
                m_SharedMovementScript.guideCollider.enabled = false;
                m_SharedMovementScript.ForceStopAndReset();
                m_OpenAIQueriesScript.targetForGuidance = null;
                wasMutingLastFrame = true; // Lock it
            }
        }
        else
        {
            wasMutingLastFrame = false; // Reset when the user lets go
        }
    }

    // Coroutine for sending info to the realtime API to prevent freezing in VR
    private IEnumerator CaptureAndSendContext()
    {
        // Start Audio Recording immediately
        realtimeClient.StartRecording();
        realtimeClient._isProcessingCommand = false;

        StartCoroutine(CaptureImageContext());
        yield return null;
    }

    // Flag to track the callback
    private bool masksCaptured = false;

    private IEnumerator CaptureImageContext()
    {
        // Scenes like the Tutorial don't have a perception sensor, so they only get screenshots (no masks or spatial telemetry)
        bool hasSpatialTelemetry = perceptionSensor != null;

        // Trigger the screenshot
        camSystem.converted = false;
        camSystem.CaptureScreenshot(!hasSpatialTelemetry);

        // Trigger the mask screenshots
        masksCaptured = !hasSpatialTelemetry;
        if (hasSpatialTelemetry)
        {
            perceptionSensor.RequestVisualTelemetry(() =>
            {
                masksCaptured = true; // This fires when the batch rendering is complete
            });
        }

        // Wait for BOTH standard and mask captures to finish
        while (!camSystem.converted || !masksCaptured)
        {
            yield return null;
        }

        if (!hasSpatialTelemetry)
        {
            realtimeClient.SendVisualContext(camSystem.viewpointImageBase64, camSystem.birdsEyeImageBase64, camSystem.overheadImageBase64);
            yield break;
        }

        // Grab the dynamic text context NOW that captures are done
        string dynamicContext = perceptionSensor.GetDynamicSpatialContext();

        // Send the context once we have the links
        if (camSystem.converted)
        {
            //Debug.Log("Images converted. Sending to Vision API...");
            // All guides get the grounding information with the visual + spatial context
            realtimeClient.SendVisualAndSpatialContext(
                dynamicContext,
                camSystem.viewpointImageBase64,
                camSystem.overheadImageBase64,
                camSystem.overheadImageBase64,
                camSystem.overheadMaskBase64
            );
        }
    }

    #region Guide and Confederate Roles
    private void PresetAvatarRoles()
    {
        // Set avatars to correct roles in separate scenes for the guide
        string currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (currentSceneName.Equals("Tutorial"))
            role = 1; // human
        else if (currentSceneName.Equals("GuidePark1_Networked"))
            role = 2; // human
        else if (currentSceneName.Equals("GuidePark2_Networked"))
            role = 4; // dog
        else if (currentSceneName.Equals("GuidePark3_Networked"))
            role = 4; // robot
        else
            role = 1; // human is default guide for all other rooms
    }

    // Method to disable all colliders in the children of this gameObject
    private void DisableColliders(GameObject model)
    {
        if (model != null)
        {
            // Get all the colliders in the children of this gameObject
            Collider[] colliders = model.GetComponentsInChildren<Collider>();

            // Loop through each collider and disable it
            foreach (Collider collider in colliders)
                collider.enabled = false;
        }
    }

    // Triggers the assignment of the avatar in static conditions (avatar set once at beginning of scene) for confederate clients
    private void AssignSingleConfederate()
    {
        if (GameObject.FindWithTag("Confederate") && !guideRoleAssignedStart)
            StartCoroutine(AssignRoleStatic());
    }
    
    // This function makes it so that the guide role is reassigned every time we go back to having two avatars in the scene
    private void BothConfederatesPresent()
    {
        // Triggers the assignment of the avatar in static conditions (avatar set once at beginning of scene) for confederate clients
        var confederates = GameObject.FindGameObjectsWithTag("Confederate");

        if (confederates.Length == 2 && !guideRoleAssigned)
            StartCoroutine(AssignRoleStatic());
        else
            guideRoleAssigned = false;
    }

    private IEnumerator AssignRoleStatic()
    {
        role = 6;
        yield return new WaitForSeconds(5f);
        // Set avatars to correct roles in separate scenes for the guide
        string currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (currentSceneName.Equals("Tutorial"))
            role = 1; // human
        else if (currentSceneName.Equals("GuidePark1_Networked"))
            role = 2; // human
        else if (currentSceneName.Equals("GuidePark2_Networked"))
            role = 4; // dog, 4
        else if (currentSceneName.Equals("GuidePark3_Networked"))
            role = 4; // robot, 2

        guideRoleAssigned = true;
        guideRoleAssignedStart = true; // Stops assigning guide role for a single confed client
    }

    #endregion

    #region Visual and Audio Effects
    private void playEffect(string clipName)
    {
        switch(clipName)
        {
            case "subway_chime":
                {
                    //Debug.Log("Played arrival effect");
                    sfxAudioSource.loop = false;
                    sfxAudioSource.PlayOneShot(chimeClip);
                    break;
                }
            case "processing":
                {
                    //Debug.Log("Playing processing sound");
                    sfxAudioSource.clip = processingClip;
                    sfxAudioSource.loop = true;
                    sfxAudioSource.Play();
                    break;
                }
            case "listening":
                {
                    sfxAudioSource.mute = false;
                    sfxAudioSource.loop = false;
                    sfxAudioSource.PlayOneShot(listeningClip);
                    break;
                }
            case "done_listening":
                {
                    //Debug.Log("Played done listening sound");
                    if (sfxAudioSource.clip == processingClip) sfxAudioSource.Stop(); // If processing sound was used (in cases of high latency)

                    sfxAudioSource.loop = false;
                    sfxAudioSource.PlayOneShot(doneListeningClip);
                    break;
                }
        }
    }

    void HighlightSelectedReaderReference(GameObject selectedReference)
    {
        // Add a glow around the selectedReference + brighten its color
        Renderer renderer = selectedReference.GetComponent<Renderer>();
        if (!originalMaterials.ContainsKey(selectedReference))
            originalMaterials.Add(selectedReference, renderer.material);

        renderer.material = Resources.Load<Material>("Screenreader/Glow");
    }

    void ClearPreviousHighlight(GameObject selectedReference)
    {
        //selectedReference.GetComponent<Renderer>().material = previousMaterial;

        if (originalMaterials.TryGetValue(selectedReference, out Material originalMat))
        {
            selectedReference.GetComponent<Renderer>().material = originalMat;
            originalMaterials.Remove(selectedReference); // Clean up the dictionary
        }
    }

    IEnumerator ClearAfterTenSeconds(GameObject selectedReference)
    {
        yield return new WaitForSeconds(10f);
        ClearPreviousHighlight(selectedReference);
    }

    IEnumerator DelayGuideStopDuringTeleport(GameObject currentTarget)
    {
        yield return new WaitForSeconds(1f);
        m_GuideFollowScript.enabled = true; // Turn guide follow back on if no target is given to the guide
        m_SharedMovementScript.guideCollider.enabled = false; // Turns collider off so guide won't be grabbed accidentally as it follows the player
        playEffect("subway_chime");
        m_SharedMovementScript.ForceStopAndReset();
        ClearPreviousHighlight(currentTarget);
        m_OpenAIQueriesScript.targetForGuidance = null;
    }

    #endregion

    #region Checks for Guide Actions (guidance/modification)
    private void checkModificationRequests()
    {
        // Checking if a target GameObject was selected to be modified
        if (m_OpenAIQueriesScript.targetForModification != null)
        {
            // Call to create an audio beacon, then immediately set the target to null so it doesn't continuously call for beacon creation
            // Also calls to highlight the object while the temporary audio beacon exists
            Debug.Log("Has a target to modify: " + m_OpenAIQueriesScript.targetForModification);

            GameObject currentTarget = m_OpenAIQueriesScript.targetForModification;

            if (lastHighlightedTarget != currentTarget)
            {
                HighlightSelectedReaderReference(currentTarget);
                lastHighlightedTarget = currentTarget;
            }

            m_AutomaticModificationScript.AddAudioBeacon(currentTarget);
            // Wait for 10 seconds (length of audio beacon), then clear material
            StartCoroutine(ClearAfterTenSeconds(currentTarget));
            m_OpenAIQueriesScript.targetForModification = null;
            lastHighlightedTarget = null;
        }
    }

    private void checkGuidanceRequests()
    {
        // Checking if a target GameObject was selected to be moved to
        if (m_OpenAIQueriesScript.targetForGuidance != null)
        {
            //Debug.Log("Was passed a target for guidance " + m_OpenAIQueriesScript.targetForGuidance);

            GameObject currentTarget = m_OpenAIQueriesScript.targetForGuidance;

            if (lastHighlightedTarget != currentTarget)
            {
                HighlightSelectedReaderReference(currentTarget);
                lastHighlightedTarget = currentTarget;
            }

            //Debug.Log("Has a target to move to: " + m_OpenAIQueriesScript.targetForGuidance);
            m_SharedMovementScript.guideCollider.enabled = true; // Turns guide collider on so it's grabbable when there is a specific move target

            // If the player is grabbing the guide, call for the movement functions as appropriate
            // Turn off guide follow so that the guide begins to lead the player
            if (m_SharedMovementScript.movingWithGuide || Input.GetKeyDown(KeyCode.C)) // was playerGrabbingGuide
            {
                m_GuideFollowScript.enabled = false;
                if (m_OpenAIQueriesScript.modeOfTransportation == "guide")
                {
                    //Debug.Log("The mode of transit is guide");
                    m_AutomatedGuideScript.GuideToPosition(currentTarget); // was openAiQueries.targetForGuidance
                    // Calculate the distance between thePlayer and the current GameObject to monitor for player getting disconnected
                    float distance = Vector3.Distance(transform.position, m_SharedMovementScript.thePlayer.transform.position);

                    // Ensure targetActive is true so we don't start it the same frame we reach the goal
                    if (m_AutomatedGuideScript.targetActive)
                    {
                        StartRouteDescriptions(currentTarget.name);
                    }

                    // If they reach the target, make it stop grabbing and stop moving
                    if (!m_AutomatedGuideScript.targetActive)
                    {
                        StopRouteDescriptions(); // stop the guide from talking

                        m_GuideFollowScript.enabled = true; // Turn guide follow back on if no target is given to the guide
                        m_SharedMovementScript.guideCollider.enabled = false; // Turns collider off so guide won't be grabbed accidentally as it follows the player
                        playEffect("subway_chime");
                        m_SharedMovementScript.ForceStopAndReset();
                        ClearPreviousHighlight(currentTarget);
                        m_OpenAIQueriesScript.targetForGuidance = null;
                        lastHighlightedTarget = null;
                    }
                    else if (distance > 1.5f) // If the guide left the participant behind at some point during guidance and ended by standing more than an arm's reach away
                    {
                        m_GuideFollowScript.enabled = true; // Turn guide follow back on to make the guide return to player
                    } 
                }
                else
                {
                    //Debug.Log("The mode of transit is teleport");
                    m_AutomatedGuideScript.TeleportToPosition(m_OpenAIQueriesScript.targetForGuidance);
                    // If they reach the target, make it stop grabbing and stop moving
                    if (!m_AutomatedGuideScript.targetActive)
                    {
                        //Debug.Log("Guide reached the object");
                        StartCoroutine(DelayGuideStopDuringTeleport(currentTarget));
                    }
                }
            }
        }
        else
        {
            if (m_SharedMovementScript != null)
            {
                StopRouteDescriptions(); // keep the guide from talking if there are no targets

                m_GuideFollowScript.enabled = true; // Turn guide follow back on if no target is given to the guide
                if (m_SharedMovementScript.guideCollider != null) // Shared movement assigns the guide's collider once both the player and guide have spawned
                {
                    m_SharedMovementScript.guideCollider.enabled = false; // Turns collider off so guide won't be grabbed accidentally as it follows the player
                    m_SharedMovementScript.OnTriggerExit(m_SharedMovementScript.guideCollider); // Triggers the exit event so the system sets the guide's grabbing trigger to false
                }
            }
        }
    }
    #endregion

    #region External Script Grabbers
    private void getSharedMovement()
    {
        if (m_SharedMovementScript == null)
            m_SharedMovementScript = FindObjectOfType<SharedMovement>();

        // Can grab the camSystem once the shared movement script has been added + has added its own camera
        if (camSystem == null && m_SharedMovementScript != null && m_SharedMovementScript.camera != null)
            camSystem = m_SharedMovementScript.camera;
    }
    #endregion

    #region Proactive Hazard Helpers
    private void CheckHazardDistances(bool includeStaticHazards)
    {
        if (Time.time < nextHazardCheckTime) return;
        nextHazardCheckTime = Time.time + hazardCheckInterval;

        // Moving objects can come at a player who is standing still or listening to the guide, so check them first - they interrupt the guide if needed
        if (CheckMovingHazards()) return;
        if (!includeStaticHazards) return;

        // If the AI guide is currently speaking, completely drop this hazard calculation to prevent building up a queue of historical hazards
        if (realtimeClient._isAiSpeaking) return;

        // Use the true velocity to minimize what we talk about in a cramped space
        if (trueWorldVelocity.magnitude < 0.3f) return;

        int hitCount = Physics.OverlapSphereNonAlloc(
            headsetTransform.position,
            dangerZoneDistance,
            hazardObjectColliders,
            hazardLayerMask
        );

        GameObject bestCandidate = null;
        float highestUrgency = -1f;
        Vector3 normalizedVelocity = trueWorldVelocity.normalized;
        float playerRadius = 0.45f;

        for (int i = 0; i < hitCount; i++)
        {
            Collider hit = hazardObjectColliders[i];
            if (hit == null) continue;

            // Approximate floor level assuming the headset is ~1.5m above the ground
            float estimatedFloorY = headsetTransform.position.y - 1.5f;

            // If the bottom-most point of the object is higher than 30cm off the floor, skip it - it's probably on top of something else
            if (hit.bounds.min.y > estimatedFloorY + 0.3f) continue;

            // Flatten positions to the XZ plane to avoid false positives from height deltas
            Vector3 hazardPoint = hit.ClosestPoint(headsetTransform.position);
            Vector3 flatHazardPoint = new Vector3(hazardPoint.x, 0, hazardPoint.z);
            Vector3 flatPlayerPoint = new Vector3(headsetTransform.position.x, 0, headsetTransform.position.z);

            Vector3 vectorToHazard = flatHazardPoint - flatPlayerPoint;

            // Project the headset's forward direction flat onto the ground plane
            Vector3 flatHeadsetForward = new Vector3(headsetTransform.forward.x, 0, headsetTransform.forward.z).normalized;
            // Check if the hazard is generally in front of the player's face/body
            float gazeAlignment = Vector3.Dot(flatHeadsetForward, vectorToHazard.normalized);
            // A value of 0.2f creates roughly a 150-degree cone of awareness in front of the player - anything behind their shoulders is ignored
            if (gazeAlignment < 0.2f) continue;

            // Ensure the object is generally in front of movement
            float forwardAlignment = Vector3.Dot(normalizedVelocity, vectorToHazard.normalized);
            if (forwardAlignment < 0.1f) continue;

            // Cylinder lateral bounds check to make sure we're heading towards it fairly directly (different from cone which grabs peripheral objects, too)
            Vector3 projectedPath = Vector3.Project(vectorToHazard, normalizedVelocity);
            float perpendicularDistance = Vector3.Distance(vectorToHazard, projectedPath);
            if (perpendicularDistance > (playerRadius + 0.1f)) continue;

            float distance = Vector3.Distance(flatPlayerPoint, flatHazardPoint);

            // TTC - time to collision system for better hazard detection
            // Calculate the rate of closure explicitly along the vector to the hazard
            float approachVelocity = Vector3.Dot(trueWorldVelocity, vectorToHazard.normalized);

            // If the approach velocity is near zero or negative, the player is moving parallel or away
            if (approachVelocity <= 0.05f) continue;

            // TTC equation: Time = Distance / Velocity
            float timeToCollision = distance / approachVelocity;

            // Ignore hazards that aren't an imminent threat based on current speed
            if (timeToCollision > maxTTCOfInterest) continue;

            // Calculate urgency inversely proportional to TTC (lower TTC = much higher urgency)
            float urgency = 1f / (timeToCollision + 0.01f);

            if (urgency > highestUrgency)
            {
                highestUrgency = urgency;
                bestCandidate = hit.gameObject;
            }
        }

        if (bestCandidate != null && ShouldPrompt(bestCandidate))
        {
            HandleHazardPrompt(bestCandidate);
        }
    }

    private bool ShouldPrompt(GameObject hazard)
    {
        float currentTime = Time.time;

        // Global cooldown check (enforce conversational spacing and don't prompt for every hazard every second)
        if (currentTime - lastHazardPromptTime < globalHazardCooldown) return false;

        // Per-object temporary suppression (once we warn about an object, don't warn about it again for a while)
        int hazardID = hazard.GetInstanceID();
        if (promptedHazardsHistory.TryGetValue(hazardID, out float lastObjectPromptTime))
        {
            if (currentTime - lastObjectPromptTime < perObjectCooldown)
            {
                return false; // Suppress alert - we warned them about this specific object too recently
            }
        }

        return true;
    }

    // Update timing states immediately to lock out back-to-back hazard alerts
    private void MarkHazardPrompted(GameObject hazard)
    {
        promptedHazardsHistory[hazard.GetInstanceID()] = Time.time;
        lastHazardPromptTime = Time.time;
        lastHazardPrompted = hazard;
    }

    private void HandleHazardPrompt(GameObject hazard)
    {
        MarkHazardPrompted(hazard);

        string hazardName = hazard.name;
        string prompt = $"The player is approaching {hazardName}. " +
                        "Let them know briefly and clearly. DO NOT add any extra fluff. For example: " +
                        "Oh, you're about to walk into a tree. Be careful." +
                        "If you keep going forward, you'll walk straight into the tall building." +
                        "I think you're getting too close to a bush. You might want to step to the side to move around it." +
                        "You're coming up on the tall building now. " +
                        "Use the conversation history to see how you've warned the player before and change up your language.";
        // Debug.Log("Hazard Detection Response: " + prompt);

        _ = realtimeClient.SendManualPrompt(prompt);
    }

    // Warn about movers (cars, pedestrians, animals) on a collision course with the player, using their velocity
    // relative to the player so a car approaching a player who is standing still is still caught. Returns true if it prompted.
    private bool CheckMovingHazards()
    {
        if (WaypointMover.Active.Count == 0) return false;

        Vector3 headPosition = headsetTransform.position;
        Vector3 flatPlayerPoint = new Vector3(headPosition.x, 0, headPosition.z);
        // Approximate floor level assuming the headset is ~1.5m above the ground
        float estimatedFloorY = headPosition.y - 1.5f;
        float playerRadius = 0.45f;

        GameObject bestCandidate = null;
        Vector3 bestVectorToHazard = Vector3.zero;
        float bestRelativeSpeed = 0f;
        float lowestTimeToCollision = float.MaxValue;

        // Warn early enough that the guide has started speaking before the TTC window closes
        float warningWindow = maxMovingTTCOfInterest + movingHazardSpeechDelay;

        foreach (WaypointMover mover in WaypointMover.Active)
        {
            if (!mover.warnPlayer) continue; // ambient flavor like birds
            Collider hit = mover.hazardCollider;
            if (hit == null || !hit.enabled || !hit.gameObject.activeInHierarchy) continue;
            if ((hazardLayerMask.value & (1 << hit.gameObject.layer)) == 0) continue;

            // Only movers that share the player's body space matter - skip anything passing overhead
            Bounds bounds = hit.bounds;
            if (bounds.min.y > headPosition.y + 0.3f || bounds.max.y < estimatedFloorY) continue;

            // Velocity of the mover relative to the player, flattened to the ground plane
            Vector3 relativeVelocity = new Vector3(mover.Velocity.x - trueWorldVelocity.x, 0, mover.Velocity.z - trueWorldVelocity.z);
            if (relativeVelocity.sqrMagnitude < 0.01f) continue;

            // Fast movers (cars) cover more ground before the warning lands, so look further out for them
            Vector3 hazardPoint = hit.ClosestPoint(headPosition);
            Vector3 vectorToHazard = new Vector3(hazardPoint.x, 0, hazardPoint.z) - flatPlayerPoint;
            float distance = vectorToHazard.magnitude;
            if (distance > Mathf.Max(movingHazardRadius, relativeVelocity.magnitude * warningWindow)) continue;

            // Closest point of approach between the mover and the player - skip movers that are leaving or will pass by
            Vector3 relativeCenter = new Vector3(bounds.center.x, 0, bounds.center.z) - flatPlayerPoint;
            float timeToClosestApproach = -Vector3.Dot(relativeCenter, relativeVelocity) / relativeVelocity.sqrMagnitude;
            if (timeToClosestApproach <= 0f) continue;
            float missDistance = (relativeCenter + relativeVelocity * timeToClosestApproach).magnitude;
            if (missDistance > playerRadius + Mathf.Max(bounds.extents.x, bounds.extents.z)) continue;

            // TTC - how long until the gap closes at the current closing speed
            float timeToCollision = 0f;
            if (distance > 0.1f)
            {
                float closingSpeed = -Vector3.Dot(relativeVelocity, vectorToHazard / distance);
                if (closingSpeed <= 0.05f) continue;
                timeToCollision = distance / closingSpeed;
            }
            if (timeToCollision > warningWindow) continue;

            if (timeToCollision < lowestTimeToCollision)
            {
                lowestTimeToCollision = timeToCollision;
                bestCandidate = hit.gameObject;
                bestVectorToHazard = vectorToHazard;
                bestRelativeSpeed = relativeVelocity.magnitude;
            }
        }

        if (bestCandidate == null || !ShouldPrompt(bestCandidate)) return false;

        // Only cut the guide off for fast movers like cars - people and animals can wait until it finishes speaking
        bool interruptGuide = bestRelativeSpeed >= movingHazardInterruptSpeed;
        if (realtimeClient._isAiSpeaking && !interruptGuide) return false;

        HandleMovingHazardPrompt(bestCandidate, bestVectorToHazard, interruptGuide);
        return true;
    }

    private void HandleMovingHazardPrompt(GameObject hazard, Vector3 flatVectorToHazard, bool interruptGuide)
    {
        MarkHazardPrompted(hazard);

        // Describe where it's coming from relative to where the player is facing
        Vector3 flatHeadsetForward = new Vector3(headsetTransform.forward.x, 0, headsetTransform.forward.z).normalized;
        float angle = Vector3.SignedAngle(flatHeadsetForward, flatVectorToHazard, Vector3.up);
        string direction = Mathf.Abs(angle) <= 45f ? "the front" : Mathf.Abs(angle) >= 135f ? "behind" : angle > 0f ? "their right" : "their left";

        string prompt = $"{hazard.name} is moving toward the player from {direction}, about {Mathf.RoundToInt(flatVectorToHazard.magnitude)} meters away. " +
                        "Warn them briefly and clearly so they can stop or step out of the way. DO NOT add any extra fluff. For example: " +
                        "Careful, a car is coming from your left. Stay where you are for a moment. " +
                        "Heads up, someone is walking toward you from the front. You might want to step to the side. " +
                        "Use the conversation history to see how you've warned the player before and change up your language.";
        // Debug.Log("Moving Hazard Detection Response: " + prompt);

        // A car won't wait for the guide to finish its sentence, so cut off whatever it's saying (or about to say)
        if (interruptGuide)
            _ = InterruptWithPrompt(prompt);
        else
            _ = realtimeClient.SendManualPrompt(prompt);
    }

    // Cancel before sending so the two websocket sends don't overlap
    private async Task InterruptWithPrompt(string prompt)
    {
        await realtimeClient.CancelPrompt();
        await realtimeClient.SendManualPrompt(prompt);
    }

    // May need to think about selectively sending hazards // making a harsher cooldown timer between when it can warn
    #endregion

    #region Opening Greeting
    private async Task SendOpeningGreeting(MemoryManager memoryManager)
    {
        if (m_SwitchToolsScript == null || !m_SwitchToolsScript.openingGreeting) return;

        // Give the scene a moment to settle so the player is ready to hear the greeting
        float interactionTimeBeforeDelay = lastPlayerInteractionTime;
        await Task.Delay((int)(m_SwitchToolsScript.greetingDelay * 1000));

        // Don't talk over the player if they started talking to the guide during the delay (or the guide was destroyed)
        if (this == null || isRecording || realtimeClient._isAiSpeaking || lastPlayerInteractionTime != interactionTimeBeforeDelay)
        {
            Debug.Log("[AI Guide] Skipping opening greeting since the player already started interacting.");
            return;
        }

        // A returning player (stateful session with past memory) already knows the guide, so only welcome them back
        bool isReturningPlayer = memoryManager != null && memoryManager.statefulSession &&
                                 !string.IsNullOrEmpty(memoryManager.GetFormattedSessionHistory());

        string prompt = GetOpeningGreetingPrompt(isReturningPlayer);
        Debug.Log($"[AI Guide] Sending opening greeting (returning player: {isReturningPlayer})");

        _ = realtimeClient.SendManualPrompt(prompt);
    }

    private string GetOpeningGreetingPrompt(bool isReturningPlayer)
    {
        // Tell the player how to talk to the guide based on the active voice input mode
        string howToTalk;
        if (realtimeClient._continuousVoiceOn)
            howToTalk = "they can just start talking to you whenever they want";
        else if (realtimeClient._legacyHoldToSpeakOn)
            howToTalk = "they can hold down the A button on their right controller while they talk to you";
        else
            howToTalk = "they can press the A button on their right controller and then talk to you";

        if (isReturningPlayer)
        {
            return "The player has just arrived back in the virtual environment and hasn't said anything yet. " +
                   "You have met them before, so do NOT introduce yourself again or list everything you can do. " +
                   "In one or two short sentences, warmly welcome them back, and if it feels natural, briefly mention one thing you explored together last time. " +
                   "Then ask them one simple question about what they'd like to do or explore today. For example: " +
                   "Welcome back! It's Giddy again. Last time we explored [something from your memory] together. What would you like to explore today? " +
                   "Speak directly to the player and do not acknowledge these instructions. Do not call any tools.";
        }

        return "The player has just arrived in the virtual environment for the first time and hasn't said anything yet. " +
               "Greet them warmly and introduce yourself as Giddy, their sighted guide. " +
               "In a few short sentences, explain what you can help with: describing what's around them and what things look like, " +
               "telling them where things are, and guiding them to places by walking with them or teleporting them there. " +
               $"Let them know that {howToTalk}, and that they can ask you anything at any time. " +
               "Then end by asking them one simple, open question about what they'd like to know or do first. " +
               "Keep the whole greeting under about 20 seconds when spoken, and don't describe the scene in detail yet. For example: " +
               "Hi there, I'm Giddy, and I'll be your sighted guide today. I can tell you what's around you, describe what things look like, " +
               "let you know where things are, and walk you anywhere you'd like to go. [How to talk to you.] " +
               "So, what would you like to know about first? " +
               "Speak directly to the player and do not acknowledge these instructions. Do not call any tools.";
    }
    #endregion

    #region Proactive Silence Helpers
    private void CheckForIdlePlayer()
    {
        // If the AI hasn't prompted yet, and 60 seconds have passed since the last interaction
        if (!hasPromptedForHelp && (Time.time - lastPlayerInteractionTime) >= idleTimeout)
        {
            TriggerHelpPrompt();
        }
    }

    private void TriggerHelpPrompt()
    {
        // Immediately set to true to prevent firing multiple times
        hasPromptedForHelp = true;

        // Frame the prompt for the LLM so it knows the context of why it is speaking
        string prompt = "The player has been silent for a minute. " +
                        "Proactively, briefly, and naturally ask them if they need any help, guidance, or directions. For example: " +
                        "Hey, I noticed you've been quiet for a while. Do you need any help?" +
                        "Is there anything I can help you with?" +
                        "Remember, I can provide directions or guidance if you need it. Just let me know.";

        // Send to your existing client
        _ = realtimeClient.SendManualPrompt(prompt);
    }

    // Resets the interaction timer between player and guide (idle player timer)
    public void RecordPlayerInteraction()
    {
        //Debug.Log("Counter reset! Player interaction recorded.");
        lastPlayerInteractionTime = Time.time;
        hasPromptedForHelp = false; // Reset the flag so the AI can check in again later
    }

    #endregion

    #region Continuous Route Description

    private void StartRouteDescriptions(string targetName)
    {
        if (!isDescribingRoute)
        {
            isDescribingRoute = true;
            routeDescriptionCoroutine = StartCoroutine(RouteDescriptionLoop(targetName));
            destination = targetName;
        }
    }

    private void StopRouteDescriptions()
    {
        if (isDescribingRoute)
        {
            isDescribingRoute = false;
            if (routeDescriptionCoroutine != null)
            {
                StopCoroutine(routeDescriptionCoroutine);
                routeDescriptionCoroutine = null;
            }

            // Clear the backlog queue and active audio so it doesn't read the outdated, back-up prompts
            if (realtimeClient != null)
            {
                realtimeClient.StopAiSpeech();

                // Send a final description of what the user arrived at
                string prompt = $"We arrived at our destination: {destination}." +
                                $"Look at your latest visual context. Briefly describe what the user is looking at, focused on the destination, and inform them that they arrived.";

                _ = realtimeClient.SendManualPrompt(prompt);

                destination = ""; // Reset destination until the next target
            }
        }
    }

    private IEnumerator RouteDescriptionLoop(string targetName)
    {
        // Wait a second before starting so the guide doesn't speak over the initial "Let's go" sound/prompt
        yield return new WaitForSeconds(1.5f);

        // Set a minimum silence duration between descriptions
        float minimumSilenceInterval = 5.0f; // testing natural conversation intervals while walking

        while (isDescribingRoute)
        {
            // Grab new screenshots and send them
            StartCoroutine(CaptureImageContext()); // this function also calls SendVisualContext

            string prompt = $"We are currently navigating towards the {targetName}. " +
                            $"Look at your latest visual context. Briefly describe ONE important, NEW object the user is walking past. " +
                            $"The object you choose should be relevant to what a blind person being guided would want to hear about as they're being helped around. " +
                            $"You should only give one simple sentence, but change up your sentence structure regularly. For example: " +
                            "We're walking down a street lined with cartoonish trees." +
                            "We're passing a short, colorful building with a flat roof." +
                            "We're going in between a line of puffy green trees." +
                            "There's a patch of colorful flowers to your left.";

            _ = realtimeClient.SendManualPrompt(prompt);

            // Wait 1-2 seconds to allow the network request to go out
            yield return new WaitForSeconds(1.5f);

            // Now wait for the AI to actually finish talking
            yield return new WaitUntil(() => !realtimeClient._isAiSpeaking);

            // Force the AI to be quiet for X seconds before looking for the next object -
            // prevents the AI from spamming descriptions of the same area
            yield return new WaitForSeconds(minimumSilenceInterval);
        }
    }
    #endregion

    #region Continuous Grabbing Description
    // For the grabbing descriptions, if the user is using the grabbing guide, call it
    // otherwise, maybe have a bool in the openAIqueries that gets set when the function determines it wants to be grabbing
    public void StartGrabbing(string targetName)
    {
        // Grab assist tracks the player's hand with the perception sensor, which scenes like the Tutorial don't have
        if (perceptionSensor == null)
        {
            Debug.LogWarning("[AI Guide] Grab assist needs a SpatialPerceptionSensor in the scene, so it was skipped.");
            _ = realtimeClient.SendManualPrompt($"You can't track the player's hand in this area, so you can't coach them to grab the {targetName}. " +
                "Briefly tell them that, and offer to describe where it is instead. Keep it under 20 words.");
            return;
        }

        targetToGrab = targetName;
        isGrabbing = true;

        grabLoopCoroutine = StartCoroutine(GrabInstructionLoop());
    }

    public void StopGrabbing()
    {
        isGrabbing = false;

        if (grabLoopCoroutine != null)
        {
            StopCoroutine(grabLoopCoroutine);
            grabLoopCoroutine = null;
        }
    }

    private IEnumerator GrabInstructionLoop()
    {
        // Start a timer to stop this coroutine automatically after the user tries grabbing an object for too long
        float startTime = Time.time;
        float maxDuration = 120f; // Seconds before the loop forces a stop
        yield return new WaitForSeconds(0.5f);
        
        while (isGrabbing && (Time.time - startTime) < maxDuration)
        {
            // Before anything else, check if the hand is close enough to grab the object
            float currentDistance = perceptionSensor.GetHandDistanceToTargetByName(targetToGrab);

            // Check if the hand is within 5 centimeters (should work for our colliders)
            if (currentDistance < 0.05f)
            {
                Debug.Log($"Unity detected hand is {currentDistance}m from {targetToGrab}.");

                // Interrupt whatever the AI is currently saying
                _ = realtimeClient.CancelPrompt();

                // Force an immediate contextual voice confirmation
                _ = realtimeClient.SendManualPrompt($"The user just successfully grabbed the {targetToGrab}. " +
                    $"Say a quick, enthusiastic 'Got it!' or 'You found it!' and tell them to press the grip button to grab the object. Keep it under 15 words.");

                StopGrabbing();
                yield break; // Exit loop instantly
            }

            // Before we capture anything, ensure the AI has finished its previous thought -
            // this prevents backlogs and ensures the next capture is as fresh as possible
            yield return new WaitUntil(() => !realtimeClient._isAiSpeaking && !realtimeClient.IsActuallySpeaking);

            // Now capture fresh data
            Debug.Log("Starting new capture");

            // Trigger standard screenshots (helpful for offering semantic grouding with the exact distance and angles, e.g., the cup is behind something else)
            camSystem.converted = false;
            camSystem.CaptureHandScreenshots();

            // Trigger the mask screenshots
            bool masksCaptured = false; // Local flag to safely track this specific iteration
            perceptionSensor.RequestHandTelemetry(() =>
            {
                masksCaptured = true;
            });

            // Wait for BOTH standard and mask captures to finish
            while (!camSystem.converted || !masksCaptured)
            {
                yield return null;
            }

            // Grab the spatial telemetry text
            string dynamicContext = perceptionSensor.GetDynamicSpatialContext();

            // Build the repeated grabbing instruction
            // We inject the dynamic telemetry directly into the prompt so the LLM can read the distances/angles.
            string prompt = $"You are assisting the player to grab the '{targetToGrab}'. " +
                            $"Use the provided images and the following spatial telemetry to guide their hand to the object. " +
                            $"The telemetry provides the exact distance to the player's right hand, the vertical offset (whether they need to move their hand up or down), " +
                            $"and the relative angle (0=Front, 90=Right, 180=Back, 270=Left)." +
                            $"You are receiving four screenshots. Image 1 is the standard view of the player's hand. Image 2 is a color segmentation mask for the player's hand " +
                            $"(match the solid colors in this image to the hex codes in the text data). " +
                            $"Image 3 is a standard body shot from the player's side. Image 4 is the color segmentation mask for the body shot. " +
                            $"{dynamicContext}" +
                            $"{m_OpenAIQueriesScript.grabbingObjectGuideline}.";

            Debug.Log("Ready to send new grab instruction prompt");
            // Send the prompt and the updated context images
            _ = realtimeClient.SendImageAndSpatialAssistedPrompt(
                prompt, 
                camSystem.handImageBase64, 
                camSystem.handMaskBase64,
                camSystem.bodyImageBase64,
                camSystem.bodyMaskBase64
            );

            Debug.Log("Sent new grab instruction prompt");

            // We must wait a brief moment for the API WebSocket to return "response.created" 
            // which flips _isAiSpeaking to true. Otherwise, the while loop will restart instantly,
            // bypass the WaitUntil above, and fire a second request before the API answers the first.
            float apiTimeout = Time.time + 3.0f; // 3 second safety timeout
            yield return new WaitUntil(() => realtimeClient._isAiSpeaking || Time.time > apiTimeout);
        }

        // Max time duration timeout
        if ((Time.time - startTime) >= maxDuration)
        {
            Debug.Log("Grab Assist timed out.");

            // Message the user about pre-emptive stop
            _ = realtimeClient.CancelPrompt();
            _ = realtimeClient.SendManualPrompt($"The player has been trying to grab {targetToGrab} for too long and you are pausing the assistance. " +
                                                $"Politely inform the player of this, then ask the player to let you know if they still want to " +
                                                $"keep trying to grab it.");
            StopGrabbing();
        }
    }
    #endregion

}
