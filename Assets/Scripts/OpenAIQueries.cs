using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Normal.Realtime;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Net.WebSockets;
using UnityEngine;
using System.Threading;
using System.Collections.Concurrent;
using OpenAI;

#region Custom Data Classes
public class ConfigData
{
    public string APIKey;
    public string PlayHTAPIKey;
    public string PlayHTUserID;
    public string ElevenLabsAPIKey;
}

public class InteractionLog
{
    public string timestamp;
    public string queryNumber;
    public string userQuery;
    public string guideResponse;
    public string guideRole;
    public string chosenObjectTarget;
    public string chosenAction;

    // Metrics
    public float latencyToFirstToken;
    public float latencyToFirstAudio;
    public float totalGenerationTime;
}
#endregion

public class RealtimeGuideClient : MonoBehaviour
{
    #region Variables and Scripts
    // Access the OpenAIQueries class so we can change variables as needed
    private OpenAIQueries _openAIQueriesScript;
    private GuideAudioSync guideAudioSync;
    private AIGuide aiGuideScript;
    private SpatialPerceptionSensor perceptionSensor;
    private SwitchTools switchTools;
    private MemoryManager memoryManager;

    public AudioSource outputSource;

    private ClientWebSocket _webSocket;
    private CancellationTokenSource _cancellationTokenSource;
    public static OpenAIClient client { get; set; }

    [HideInInspector] private const string configFileName = "config"; // Config file to hold api keys, credentials
    private string _apiKey; // Set this securely

    // Events to hook into legacy existing Audio/UI systems
    public Action<string> OnTextReceived;
    public Action<string> OnAudioDeltaReceived; // Base64 PCM16 audio from OpenAI

    // Tracking bools for general processes
    public bool _isConnected = false;
    private bool _guideAudioSourceFound = false;
    public bool _isAiSpeaking = false;
    public bool _isProcessingCommand = false;
    private bool _foundFirstSentence = false;
    private bool _isResponseActive = false;
    private bool _isUserSpeaking = false;

    // For handling special case speech
    private string _firstFullSentence;
    private long _totalSamplesReceived = 0;
    private long _samplesAtFirstSentence = 0;

    // Microphone Variables
    private AudioClip _micClip;
    private string _micDevice;
    private int _lastMicPos;
    private bool _isRecording;

    // Variables for voice detection
    public bool _continuousVoiceOn = false; // Set from AIGuide
    [HideInInspector] public bool _isContinuousSessionActive = false;
    public Action OnServerDetectedSpeechStart;
    public Action OnServerDetectedSpeechStop;

    public bool _defaultPushToTalkOn = true;
    public bool _legacyHoldToSpeakOn = false;
    public Action OnAutoStopRecording; // Sent to AIGuide (to tell it when the voice has stopped)
    private float _silenceTimer = 0f;
    private float _silenceThreshold = 2.5f; // Seconds of silence before auto-stopping
    private float _volumeThreshold = 0.05f; // Minimum volume to be considered talking, was 0.02f, caught computer fan
    private float _noSpeechTimeout = 10f; // Seconds to wait for the user to start talking before we stop listening
    private float _noSpeechTimer = 0f;
    private bool _hasSpoken = false; // Prevents auto-stopping before a user starts talking

    // Variables for other customizations
    private bool personalVoicesMode = false;

    // The Thread-Safe Queue
    private ConcurrentQueue<float[]> _audioPlaybackQueue = new ConcurrentQueue<float[]>();
    private const int SAMPLE_RATE = 24000; // OpenAI's native rate
    private int _totalSamplesSent = 0;

    // Variables for jitter on audio sharing over network
    private Queue<float[]> _jitterBuffer = new Queue<float[]>();
    private bool _isBuffering = true;
    private const int BufferThreshold = 5; // Start playing once we have 5 chunks

    // Configuration
    private const string OPENAI_REALTIME_URL = "wss://api.openai.com/v1/realtime?model=gpt-realtime-2"; // was gpt-4o-realtime-preview, was deprecated on May 7th - 

    // Logging
    private StringBuilder _textBuffer = new StringBuilder(); // Buffer to accumulate GPT response chunks before sending to ElevenLabs/logging
    private string triggeredTool = "None";
    #endregion

    #region Start + Loading Scripts
    private void Start()
    {
        _openAIQueriesScript = FindObjectOfType<OpenAIQueries>();
        perceptionSensor = FindObjectOfType<SpatialPerceptionSensor>();
        switchTools = FindObjectOfType<SwitchTools>();
        aiGuideScript = GetComponent<AIGuide>();
        memoryManager = FindObjectOfType<MemoryManager>();

        // Open a client for getting descriptions of images
        client = new OpenAIClient(_apiKey);

        // Determine which version of audio generation is to be used
        personalVoicesMode = FindObjectOfType<SwitchTools>().personalVoicesOn;

        // Prefer the Quest headset mic over other virtual mics (like VIVE's) that can be listed first on PC
        _micDevice = Microphone.devices[0];
        foreach (string device in Microphone.devices)
        {
            if (device.Contains("Oculus") || device.Contains("Headset"))
            {
                _micDevice = device;
                break;
            }
        }
        Debug.Log($"Using microphone '{_micDevice}' (available: {string.Join(", ", Microphone.devices)})");
    }

    private void getAudioSync()
    {
        if (guideAudioSync == null)
            guideAudioSync = FindObjectOfType<GuideAudioSync>();
    }

    public void LoadConfig()
    {
        TextAsset configAsset = Resources.Load<TextAsset>(configFileName);
        if (configAsset != null)
        {
            // Parse the JSON data from config.json and assign apiKey values accordingly
            ConfigData configData = JsonUtility.FromJson<ConfigData>(configAsset.text);
            _apiKey = configData.APIKey;
            //_elevenLabsApiKey = configData.ElevenLabsAPIKey;
        }
        else
        {
            Debug.LogError("Config file not found in Resources folder: " + configFileName);
        }
    }
    #endregion

    #region Session Initialization
    public async Task Connect(string systemInstructions, bool inferringIntention)
    {
        _webSocket = new ClientWebSocket();
        _webSocket.Options.SetRequestHeader("Authorization", "Bearer " + _apiKey);
        //_webSocket.Options.SetRequestHeader("OpenAI-Beta", "realtime=v1"); // this was the beta call, now deprecated

        _cancellationTokenSource = new CancellationTokenSource();

        try
        {
            await _webSocket.ConnectAsync(new Uri(OPENAI_REALTIME_URL), CancellationToken.None);
            _isConnected = true;
            Debug.Log("Connected to Realtime API");
            Debug.Log("The instructions received were: " + systemInstructions);

            // Start listening for responses immediately
            _ = ReceiveLoop();

            // Configure the session (Set the "System Prompt")
            if (inferringIntention)
                await SendSessionUpdate(systemInstructions);
            else
                await FirstSessionUpdate(systemInstructions); // send simplified update with fewer instructions, since we'll specify intention manually
        }
        catch (Exception e)
        {
            Debug.LogError($"Connection Failed: {e.Message}");
        }
    }

    private async Task SendSessionUpdate(string instructions)
    {
        Debug.Log("[OpenAIQueries] Sending all functions at once for first session (baseline + all combined guides that infer intention)");
        // Dynamically assign the turn_detection to be either null (push to talk) or handled by the voice activity
        object turnDetectionConfig = _continuousVoiceOn ? new { type = "server_vad" } : null;

        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime", // required by the general model, new from beta
                output_modalities = new[] { "audio" }, // Ask for just audio, now assumes text is included
                instructions = instructions,
                
                audio = new
                {
                    input = new
                    {
                        // Format is now an object, not a string
                        format = new { type = "audio/pcm", rate = 24000 }, // format = "pcm16",
                        turn_detection = turnDetectionConfig,
                        transcription = new { model = "whisper-1" }, // line to transcribe user query for logger
                    },
                    output = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 }, // format = "pcm16",
                        voice = "alloy" // Options: alloy, echo, shimmer
                    }
                },
                /*voice = "alloy", // Options: alloy, echo, shimmer
                input_audio_format = "pcm16",
                output_audio_format = "pcm16",
                turn_detection = turnDetectionConfig,*/
                tools = new object[] // Allows us to make a case to directly call our Unity functions for guidance, no string parsing/partially generated responses
                {
                    new
                    {
                        type = "function",
                        name = "trigger_guidance",
                        description = "Call this when the user wants you to take them to a specific object, or asks for sighted guide to a specific object.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The exact name of the object the user wants to go to, chosen from the Navigation Registry." }
                            },
                            required = new[] { "target_object" }
                        }
                    },
                    new
                    {
                        type = "function",
                        name = "trigger_teleportation",
                        description = "Call this when the user wants you to teleport them directly to a specific object.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The exact name of the object the user wants to go to, chosen from the Navigation Registry." }
                            },
                            required = new[] { "target_object" }
                        }
                    },
                    new
                    {
                        type = "function",
                        name = "start_grab_assist",
                        description = "Call this when the user is trying to grab a specific object.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The object the user is trying to reach." }
                            },
                            required = new[] { "target_object" }
                        }
                    },
                    new
                    {
                        type = "function",
                        name = "label_object",
                        description = "Call this when the user refers to a specific object by a colloquial name, nickname, or description that doesn't exactly match its Registry Name.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The exact technical Registry Name from the spatial telemetry." },
                                alias = new { type = "string", description = "The colloquial name or nickname the user just used (e.g., 'striped gazebo')." }
                            },
                            required = new[] { "target_object", "alias" } // Make sure alias is required!
                        }
                    },
                    new
                    {
                        type = "function",
                        name = "review_memory",
                        description = "Call this when the user asks about an object that isn't in your current spatial telemetry, to see if you've encountered it before.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The object you are searching your full telemetry memory for." }
                            },
                            required = new[] { "target_object" }
                        }
                    }
                    // Deprecated modification + audio beacons for now
                    /*,
                    new
                    {
                        type = "function",
                        name = "trigger_modification",
                        description = "Call this when the user wants you to modify an object or add an audio beacon to it.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The exact name of the object to modify, chosen from the Navigation Registry." }
                            },
                            required = new[] { "target_object" }
                        }
                    }*/
                }
            }
        };

        await SendJson(sessionUpdate);
    }

    // For the improved guide, when it only gives basic context to the guide so a human user can switch the context
    private async Task FirstSessionUpdate(string instructions)
    {
        Debug.Log("Sending only basic guide context (manual intention guide)");
        // Dynamically assign the turn_detection to be either null (push to talk) or handled by the voice activity
        object turnDetectionConfig = _continuousVoiceOn ? new { type = "server_vad" } : null;

        var sessionUpdate = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime", // required by the general model, new from beta
                output_modalities = new[] { "audio" }, // Ask for just audio, now assumes text is included
                instructions = instructions,
                audio = new
                {
                    input = new
                    {
                        // Format is now an object, not a string
                        format = new { type = "audio/pcm", rate = 24000 }, // format = "pcm16",
                        turn_detection = turnDetectionConfig,
                        transcription = new { model = "whisper-1" }, // line to transcribe user query for logger
                    },
                    output = new
                    {
                        format = new { type = "audio/pcm", rate = 24000 }, // format = "pcm16",
                        voice = "alloy" // Options: alloy, echo, shimmer
                    }
                },
                tools = new object[] // Allows us to make a case to directly call our Unity functions for guidance, no string parsing/partially generated responses
                {
                    new
                    {
                        type = "function",
                        name = "trigger_guidance",
                        description = "Call this when the user wants you to take them to a specific object, or asks for sighted guide to a specific object.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The exact name of the object the user wants to go to, chosen from the Navigation Registry." }
                            },
                            required = new[] { "target_object" }
                        }
                    },
                    new
                    {
                        type = "function",
                        name = "trigger_teleportation",
                        description = "Call this when the user wants you to teleport them directly to a specific object.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The exact name of the object the user wants to go to, chosen from the Navigation Registry." }
                            },
                            required = new[] { "target_object" }
                        }
                    },
                    new
                    {
                        type = "function",
                        name = "label_object",
                        description = "Call this when the user refers to a specific object by a colloquial name, nickname, or description that doesn't exactly match its Registry Name.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The exact technical Registry Name from the spatial telemetry." },
                                alias = new { type = "string", description = "The colloquial name or nickname the user just used (e.g., 'striped gazebo')." }
                            },
                            required = new[] { "target_object", "alias" } // Make sure alias is required!
                        }
                    },
                    new
                    {
                        type = "function",
                        name = "review_memory",
                        description = "Call this when the user asks about an object that isn't in your current spatial telemetry, to see if you've encountered it before.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The object you are searching your full telemetry memory for." }
                            },
                            required = new[] { "target_object" }
                        }
                    }
                }
            }
        };

        await SendJson(sessionUpdate);
    }
    #endregion

    #region Guide Type Switch Handling
    public async Task UpdateLivePrompt(string newInstructions)
    {
        if (!_isConnected) return;

        bool shouldRegenerate = false;

        // Push the new guide version instructions
        var updateSession = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime", // required by the general model, new from beta
                instructions = newInstructions
            }
        };
        await SendJson(updateSession);

        //Debug.Log("[Realtime] Toggling guide mode and forcing re-evaluation...");

        if (_isResponseActive)
        {
            await SendJson(new { type = "response.cancel" });
            _isResponseActive = false; // Immediately unlock locally

            if (_isAiSpeaking)
            {
                _isAiSpeaking = false;
                ClearLocalAudioBuffer();
            }

            shouldRegenerate = true;
        }

        if (shouldRegenerate)
        {
            // Inject a hidden system message telling the AI to look back at the user's last input and apply the new rules
            var reevaluateItem = new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "system",
                    content = new[]
                    {
                new
                {
                    type = "input_text",
                    text = "System override: The active guidance rules have just been updated. Ignore your previous response if you started one, and immediately re-answer the user's last question using ONLY your new guidelines."
                }
            }
                }
            };
            await SendJson(reevaluateItem);

            // Trigger the new audio generation
            await SendJson(new { type = "response.create" });
        }
    }

    public async Task UpdateGuidancePrompt(string newInstructions)
    {
        if (!_isConnected) return;

        bool shouldRegenerate = false;

        //Debug.Log("[Realtime] Toggling guide mode and forcing re-evaluation for a guidance call...");

        // Push the new guide version instructions
        var updateSession = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime", // required by the general model, new from beta
                instructions = newInstructions,
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        name = "trigger_guidance",
                        description = "Call this when the player wants you to take them to a specific object, or asks for sighted guide to a specific object.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The exact name of the object the user wants to go to, chosen from the Navigation Registry." }
                            },
                            required = new[] { "target_object" }
                        }
                    },
                    new
                    {
                        type = "function",
                        name = "trigger_teleportation",
                        description = "Call this when the player wants you to teleport them directly to a specific object.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The exact name of the object the user wants to go to, chosen from the Navigation Registry." }
                            },
                            required = new[] { "target_object" }
                        }
                    }
                }
            }
        };
        await SendJson(updateSession);

        // Cut off the AI immediately so the user doesn't hear the "wrong" response
        //await SendJson(new { type = "response.cancel" });
        if (_isResponseActive)
        {
            await SendJson(new { type = "response.cancel" });
            _isResponseActive = false; // Immediately unlock locally

            if (_isAiSpeaking)
            {
                _isAiSpeaking = false;
                ClearLocalAudioBuffer();
            }

            shouldRegenerate = true;
        }

        if (shouldRegenerate)
        {
            // Inject a hidden system message telling the AI to look back at the user's last input and apply the new rules
            var reevaluateItem = new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "system",
                    content = new[]
                    {
                new
                {
                    type = "input_text",
                    text = "System override: The active guidance rules have just been updated. Ignore your previous response if you started one, and immediately re-answer the user's last question using ONLY your new guidelines."
                }
            }
                }
            };
            await SendJson(reevaluateItem);

            // Trigger the new audio generation
            await SendJson(new { type = "response.create" });
        }
    }

    public async Task UpdateGrabbingPrompt(string newInstructions)
    {
        if (!_isConnected) return;

        bool shouldRegenerate = false;

        //Debug.Log("[Realtime] Toggling guide mode and forcing re-evaluation for a guidance call...");

        // Push the new guide version instructions
        var updateSession = new
        {
            type = "session.update",
            session = new
            {
                type = "realtime", // required by the general model, new from beta
                instructions = newInstructions,
                tools = new[]
                {
                    new
                    {
                        type = "function",
                        name = "start_grab_assist",
                        description = "Call this when the user is trying to grab a specific object.",
                        parameters = new
                        {
                            type = "object",
                            properties = new
                            {
                                target_object = new { type = "string", description = "The object the user is trying to reach." }
                            },
                            required = new[] { "target_object" }
                        }
                    }
                }
            }
        };
        await SendJson(updateSession);

        // Cut off the AI immediately so the user doesn't hear the "wrong" response
        //await SendJson(new { type = "response.cancel" });
        if (_isResponseActive)
        {
            await SendJson(new { type = "response.cancel" });
            _isResponseActive = false; // Immediately unlock locally

            if (_isAiSpeaking)
            {
                _isAiSpeaking = false;
                ClearLocalAudioBuffer();
            }

            shouldRegenerate = true;
        }

        if (shouldRegenerate)
        {
            // Inject a hidden system message telling the AI to look back at the user's last input and apply the new rules
            var reevaluateItem = new
            {
                type = "conversation.item.create",
                item = new
                {
                    type = "message",
                    role = "system",
                    content = new[]
                    {
                new
                {
                    type = "input_text",
                    text = "System override: The active guidance rules have just been updated. Ignore your previous response if you started one, and immediately re-answer the user's last question using ONLY your new guidelines."
                }
            }
                }
            };
            await SendJson(reevaluateItem);

            // Trigger the new audio generation
            await SendJson(new { type = "response.create" });
        }
    }
    #endregion

    #region Microphone Handling
    public void StartRecording()
    {
        if (!_isConnected) return;
        Debug.Log("Recording started");

        // LOGGER HOOK: Query initiated by participant
        InteractionLogger.Instance?.OnUserQueryInitiated();

        // Reset variables for voice detection
        _hasSpoken = false;
        _silenceTimer = 0f;
        _noSpeechTimer = 0f;

        // Interrupt any current AI speech if the user interrupts
        outputSource.Stop();
        _audioPlaybackQueue = new ConcurrentQueue<float[]>(); // Clear pending audio

        // Check if the AI is speaking, and interrupt it if so to let the user record/talk
        if (_isAiSpeaking)
        {
            _ = SendJson(new { type = "response.cancel" }); // Tell API to stop generating
            _isAiSpeaking = false;
        }

        _ = SendJson(new { type = "input_audio_buffer.clear" }); // Clear the buffer so it hears only our new audio

        _isRecording = true;
        _lastMicPos = 0;
        _totalSamplesSent = 0;
        _micClip = Microphone.Start(_micDevice, true, 20, 24000); // 24kHz is standard for OpenAI Realtime
    }

    public async Task StopRecordingAndCommit(string screenshotUrl = null)
    {
        Debug.Log("Recording stopped");

        // LOGGER HOOK: User finished speech input - call without text to mark when the user stopped talking
        InteractionLogger.Instance?.OnUserFinishedSpeaking();

        // Flush the final bits of audio
        await HandleMicStreaming();

        _isRecording = false;
        Microphone.End(_micDevice);

        // Only commit if we actually sent audio (prevents empty call errors)
        if (_totalSamplesSent > 0)
        {
            // Tell OpenAI we are done talking and want a response
            await SendJson(new { type = "input_audio_buffer.commit" });
            await SendJson(new { type = "response.create" });
            //Debug.Log($"Committed {_totalSamplesSent} samples and requested response.");
        }
        else
        {
            // Debug.LogWarning("Recording too short (no audio sent). Skipping audio commit to avoid API error.");
            _openAIQueriesScript.PlayPredeterminedAudio("emptyQuery");
        }

        _totalSamplesSent = 0; // Reset for next time
        _lastMicPos = 0;
    }

    // Stops listening when the user never started talking, without sending the silence to OpenAI
    private async Task StopRecordingWithoutSpeech()
    {
        Debug.Log("Recording stopped (no speech detected)");

        // LOGGER HOOK: User finished speech input - call without text to mark when the user stopped talking
        InteractionLogger.Instance?.OnUserFinishedSpeaking();

        _isRecording = false;
        Microphone.End(_micDevice);
        await SendJson(new { type = "input_audio_buffer.clear" });
        _openAIQueriesScript.PlayPredeterminedAudio("emptyQuery");

        _totalSamplesSent = 0;
        _lastMicPos = 0;
    }

    public async Task StopRecordingSilently()
    {
        //Debug.Log("Closing continuous session quietly.");

        _isRecording = false;
        _isContinuousSessionActive = false;

        if (Microphone.IsRecording(_micDevice))
            Microphone.End(_micDevice);

        if (_isConnected)
        {
            // Tell the server to forget any audio it just heard
            await SendJson(new { type = "input_audio_buffer.clear" });

            // If the AI is currently mid-sentence while you turn it off, shut it up immediately.
            await StopAiSpeech();
        }

        _totalSamplesSent = 0;
        _lastMicPos = 0;
    }

    private void CheckSilenceThreshold()
    {
        if (switchTools != null)
        {
            if (!Mathf.Approximately(switchTools.silenceThreshold, _silenceThreshold)) // if the switch tools threshold is different
            {
                _silenceThreshold = switchTools.silenceThreshold; // adjust our local threshold
            }
        }
    }

    private async Task HandleMicStreaming()
    {
        if (!_isRecording || !_isConnected) return;

        int currentPos = Microphone.GetPosition(_micDevice);
        if (currentPos == _lastMicPos) return;

        float[] samples;

        // Normal read (head is ahead of last position)
        if (currentPos > _lastMicPos)
        {
            samples = new float[currentPos - _lastMicPos];
            _micClip.GetData(samples, _lastMicPos);
        }
        // Wrap-around read (head looped back to the start of the clip)
        else
        {
            int samplesToRead = (_micClip.samples - _lastMicPos) + currentPos;
            samples = new float[samplesToRead];

            // Read from last position to the very end of the clip
            float[] part1 = new float[_micClip.samples - _lastMicPos];
            _micClip.GetData(part1, _lastMicPos);

            // Read from the start of the clip to the current position
            float[] part2 = new float[currentPos];
            _micClip.GetData(part2, 0);

            // Combine them into one array
            Array.Copy(part1, 0, samples, 0, part1.Length);
            Array.Copy(part2, 0, samples, part1.Length, part2.Length);
        }

        _lastMicPos = currentPos;

        // Convert the float array [-1.0, 1.0] to PCM16 bytes and send
        if (samples != null && samples.Length > 0)
        {
            _totalSamplesSent += samples.Length;

            // MicCheck(samples);

            // Voice activity detection is only used for the default push-to-talk flow.
            if (_defaultPushToTalkOn && !_continuousVoiceOn && !_legacyHoldToSpeakOn)
                ProcessVoiceActivity(samples);

            // Noise gate to ensure we aren't treating backround noise/AI voice as user voice
            float maxVol = 0f;
            foreach (var s in samples) if (Mathf.Abs(s) > maxVol) maxVol = Mathf.Abs(s);

            // Check if the AI is generating a response or currently playing the tail end of a prior response
            bool isOutputtingSound = _isAiSpeaking || (outputSource != null && outputSource.isPlaying);

            // If the AI is speaking, and the mic isn't picking up a loud interruption, zero out the audio to prevent the AI from hearing its own echo
            if (isOutputtingSound && maxVol < 0.15f)
                Array.Clear(samples, 0, samples.Length); // Fills the array with 0s (pure silence)

            byte[] pcmData = ConvertFloatsToPCM16(samples);
            string base64Audio = Convert.ToBase64String(pcmData);
            await SendJson(new { type = "input_audio_buffer.append", audio = base64Audio });
        }
    }

    private void ProcessVoiceActivity(float[] samples)
    {
        float maxVol = 0f;
        foreach (var s in samples)
        {
            if (Mathf.Abs(s) > maxVol) maxVol = Mathf.Abs(s);
        }

        // If volume spikes above our threshold, the user is talking
        if (maxVol > _volumeThreshold)
        {
            //Debug.Log("User is talking");
            _hasSpoken = true;
            _silenceTimer = 0f; // Reset the silence timer
        }
        // If they were talking, but are now quiet
        else if (_hasSpoken)
        {
            // Calculate how much time this chunk of samples represents
            _silenceTimer += (float)samples.Length / SAMPLE_RATE;

            // If they have been silent longer than our threshold
            if (_silenceTimer > _silenceThreshold)
            {
                _hasSpoken = false;
                _silenceTimer = 0f;

                //Debug.Log("Silence detected. Auto-stopping recording.");

                // Alert AIGuide that the user has stopped speaking
                OnAutoStopRecording?.Invoke();

                // Execute the stop and commit
                _ = StopRecordingAndCommit();
            }
        }
        // If they never started talking (e.g. the mic isn't picking them up), stop listening so they can try again
        else
        {
            _noSpeechTimer += (float)samples.Length / SAMPLE_RATE;

            if (_noSpeechTimer > _noSpeechTimeout)
            {
                _noSpeechTimer = 0f;
                OnAutoStopRecording?.Invoke();
                _ = StopRecordingWithoutSpeech();
            }
        }
    }

    // Call this when the user starts their voice input (button down)
    public void ResetCommandLock()
    {
        _isProcessingCommand = false;
        //Debug.Log("Lock Reset: Ready for new user commands.");
    }
    #endregion

    #region Audio Buffer + Remote Audio Handling
    private void HandleJitter()
    {
        if (!_isBuffering && _jitterBuffer.Count > 0)
        {
            // Only pull from buffer if we aren't currently playing something
            float[] nextChunk = _jitterBuffer.Dequeue();
            _audioPlaybackQueue.Enqueue(nextChunk);
        }

        // If buffer runs dry, pause and re-buffer
        if (_jitterBuffer.Count == 0) _isBuffering = true;
    }

    // Handle the incoming RPC data on Remote Clients
    public void ReceiveRemoteAudio(string base64Audio)
    {
        Debug.Log("Received remote audio from a guide on another client - converting locally");
        // Convert Base64 back to float[] and play it
        byte[] pcmData = System.Convert.FromBase64String(base64Audio);
        float[] floatData = ConvertPCM16ToFloats(pcmData);

        _jitterBuffer.Enqueue(floatData);

        // If we were empty, start buffering before we play
        if (_jitterBuffer.Count >= BufferThreshold)
            _isBuffering = false;
    }

    // Define the logic for sharing voice over network
    private bool ShouldShareResponse()
    {
        if (aiGuideScript == null) return true; // Default to share if no script found

        // If role is 6, it's private (Local only). Otherwise, share.
        if (aiGuideScript.role == 6)
        {
            return false;
        }
        return true;
    }

    private void PlayAudioChunk(float[] data)
    {
        // Debug.Log("Got a response chunk to play as audio");
        AudioClip clip = AudioClip.Create("ResponseChunk", data.Length, 1, SAMPLE_RATE, false);
        clip.SetData(data, 0);
        outputSource.clip = clip;
        outputSource.Play();
    }

    private async Task ReceiveLoop()
    {
        var buffer = new byte[1024 * 64];
        while (_webSocket.State == WebSocketState.Open)
        {
            using (var ms = new MemoryStream())
            {
                WebSocketReceiveResult result;
                do
                {
                    result = await _webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), _cancellationTokenSource.Token);
                    ms.Write(buffer, 0, result.Count);
                }
                while (!result.EndOfMessage); // Continuously appends the new chnks of the result until we hit the EndOfMessage

                string json = Encoding.UTF8.GetString(ms.ToArray());
                // Realtime API has many events. We filter for the ones we need.
                HandleServerEvent(json);
            }
        }
    }

    public bool IsActuallySpeaking => _audioPlaybackQueue.Count > 0 || IsPlayingAudio();

    private bool IsPlayingAudio()
    {
        return outputSource.isPlaying; // guide voice source
    }

    public async Task StopAiSpeech()
    {
        // Tell the server to stop generating immediately
        if (_isConnected && _webSocket.State == WebSocketState.Open)
            await SendJson(new { type = "response.cancel" });

        // Clear all local audio (the queue and the physical source)
        ClearLocalAudioBuffer();

        // Reset internal state flags
        _isAiSpeaking = false;
        _textBuffer.Clear();

        // Reset command locks if necessary
        _isProcessingCommand = false;

        Debug.Log("AI Speech interrupted and buffers cleared.");
    }

    private void ClearLocalAudioBuffer()
    {
        if (outputSource.isPlaying)
            outputSource.Stop();

        _audioPlaybackQueue.Clear();

        //Debug.Log("Local audio buffer cleared to make way for custom TTS.");
    }
    #endregion

    #region Update and Continuous Checks
    void Update()
    {
        // Find the guide audio sync component to share over network
        getAudioSync();

        // Update how long the guide pauses for between turns
        CheckSilenceThreshold();
        
        // Call continuous microphone streaming logic
        HandleMicStreaming();

        // Handle the jittering sound of the audio over the network
        HandleJitter();

        // Find guide audio source before handling anything with output audio
        if (!_guideAudioSourceFound)
        {
            outputSource = GameObject.Find("Human Model").GetComponent<AudioSource>();
            _guideAudioSourceFound = true;
        }
        else
        {
            // Pull from the thread-safe queue on the Main Thread to figure out audio playback logic
            if (!outputSource.isPlaying && _audioPlaybackQueue.TryDequeue(out float[] nextChunk))
                PlayAudioChunk(nextChunk);
        }
    }
    #endregion

    #region Handle Server Events
    private void HandleServerEvent(string json)
    {
        try
        {
            JObject jsonObj = JObject.Parse(json);
            string type = (string)jsonObj["type"];

            switch (type)
            {
                case "input_audio_buffer.speech_started":
                    triggeredTool = "None"; // start as none, update if another function for a specific tool is called based on what user says
                    _isUserSpeaking = true;
                    OnServerDetectedSpeechStart?.Invoke();

                    // If the AI is talking, instantly shut it up locally
                    if (_isAiSpeaking)
                    {
                        Debug.Log("Server VAD detected user interruption. Clearing local audio.");
                        ClearLocalAudioBuffer();
                        _isAiSpeaking = false;
                    }
                    break;

                case "input_audio_buffer.speech_stopped":
                    _isUserSpeaking = false;
                    OnServerDetectedSpeechStop?.Invoke(); // The Server VAD heard the user stop speaking and is generating a response
                    _isResponseActive = true;
                    // Reset timer on the last time the user prompted the guide
                    break;

                case "conversation.item.input_audio_transcription.completed":
                    // The server finished transcribing the user's audio
                    string userTranscript = (string)jsonObj["transcript"];
                    // LOGGER HOOK: User speech transcribed - call with the transcript this time
                    InteractionLogger.Instance?.OnUserFinishedSpeaking(userTranscript);
                    memoryManager?.LogConversationTurn("user", userTranscript);
                    break;

                case "response.created":
                    _textBuffer.Clear();
                    _isAiSpeaking = true;
                    _isResponseActive = true; // redundant safety catch
                    _foundFirstSentence = false;
                    _totalSamplesReceived = 0;
                    break;
                
                case "response.output_audio.delta": // was response.audio.delta
                    // LOGGER HOOK: First audio chunk received/played
                    InteractionLogger.Instance?.OnGuideStartedSpeaking();
                    if (personalVoicesMode) // Don't do anything with the native audio stream from OpenAI
                        break;
                    else
                    {
                        // If we aren't using personal voices, then stream from the native audio
                        string base64Audio = (string)jsonObj["delta"];
                        byte[] pcmData = Convert.FromBase64String(base64Audio);
                        float[] floatData = ConvertPCM16ToFloats(pcmData);

                        // Capture the samples of audio in this sentence -- then we can compare THAT to the audio source samples
                        _totalSamplesReceived += floatData.Length;
                        _audioPlaybackQueue.Enqueue(floatData); // Send the samples to be played by the audio source

                        // Check if we should broadcast this to the network
                        //if (ShouldShareResponse() && guideAudioSync != null)
                            //guideAudioSync.BroadcastAudioChunk(floatData);

                        OnAudioDeltaReceived?.Invoke(base64Audio);
                        break;
                    }

                case "response.output_audio_transcript.delta": // was, "response.audio_transcript.delta" -- Use this instead of or in addition to text.delta
                    string transcriptDelta = (string)jsonObj["delta"];
                    _textBuffer.Append(transcriptDelta);

                    _firstFullSentence = "";
                    bool isSentenceEnder = transcriptDelta.EndsWith(".") || transcriptDelta.EndsWith("!") || transcriptDelta.EndsWith("?"); ;
                    if (isSentenceEnder && !_foundFirstSentence && _textBuffer.ToString().Length > 15)
                    {
                        _foundFirstSentence = true;
                        _firstFullSentence = _textBuffer.ToString();
                        //Debug.Log($"First full sentence is {_firstFullSentence}");

                        // Assuming a standard speaking rate of ~15 characters per second and a standard OpenAI sample rate of 24,000 Hz
                        float estimatedSeconds = _firstFullSentence.Length / 15f - 0.4f; // substract 400 ms for the delay
                        _samplesAtFirstSentence = (int)(estimatedSeconds * 24000);
                        //Debug.Log($"Total samples needed for first sentence from assuming standard speaking is {_samplesAtFirstSentence}");
                    }

                    break;

                case "response.output_text.delta": // was "response.text.delta":
                    // Use the text grabbed here to pass to ElevenLabs
                    //Debug.Log("Got response text to use in ElevenLabs or log");
                    if (personalVoicesMode) // Still capture the text, but additionally pass it to ElevenLabs
                    {
                        string textDelta = (string)jsonObj["delta"];
                        _textBuffer.Append(textDelta);
                        // Call ElevenLabs to speak the text instead

                        OnTextReceived?.Invoke(textDelta);
                        break;
                    }
                    else // Only capture the text
                    {
                        string textDelta = (string)jsonObj["delta"];
                        _textBuffer.Append(textDelta);
                        //Debug.Log($"Text Chunk: {textDelta}");

                        OnTextReceived?.Invoke(textDelta);
                        break;
                    } 

                case "response.done":
                    _isAiSpeaking = false;
                    _isResponseActive = false;
                    isWaitingForResponse = false;
                    // Log the FULL details to see why it finished
                    var responseObj = jsonObj["response"];
                    string status = (string)responseObj["status"];

                    if (status == "completed")
                    {
                        string remainingText = _textBuffer.ToString().Trim();
                        aiGuideScript.RecordPlayerInteraction(); // also record when response completes, to reset counter from after guide finishes talking
                        Debug.Log($"Full Response Captured: {remainingText}");

                        // LOGGER HOOK: Guide completed speech response
                        InteractionLogger.Instance?.OnGuideFinishedSpeaking(remainingText, triggeredTool);
                        memoryManager?.LogConversationTurn("assistant", remainingText);
                    }
                    else
                    {
                        Debug.LogError($"Response Finished with Error: {status}");
                        Debug.LogError($"Details: {responseObj["status_details"]}");
                    }
                    break;

                case "response.function_call_arguments.done":
                    string callId = (string)jsonObj["call_id"];
                    string functionName = (string)jsonObj["name"];
                    string argumentsJson = (string)jsonObj["arguments"]; // This comes in as a stringified JSON object

                    Debug.Log($"[Tools] AI called function: {functionName} with args: {argumentsJson}");

                    // Parse the arguments
                    JObject argsObj = JObject.Parse(argumentsJson);
                    string targetName = (string)argsObj["target_object"];

                    _isProcessingCommand = true; // Lock commands just like your old script did

                    if (functionName == "trigger_guidance")
                    {
                        triggeredTool = "Guidance"; // start as none, update if another function was called
                        _openAIQueriesScript.modeOfTransportation = "guide";
                        _openAIQueriesScript.targetForGuidance = _openAIQueriesScript.GetClosestObjectByName(targetName);

                        if (_openAIQueriesScript.targetForGuidance != null)
                        {
                            string[] options = {
                                $"Of course. Press the grip button to confirm, and I will take you to the {targetName}.",
                                $"Sure. Squeeze the grip button to confirm and I'll lead the way to the {targetName}."
                            };
                            string audioResponse = options[UnityEngine.Random.Range(0, options.Length)];
                            //string audioResponse = $"Press the grip button to confirm, and I will take you to the {targetName}.";
                            _ = SpeakCustomText(audioResponse); // Inject custom confirmation audio
                        }
                    }
                    else if (functionName == "trigger_teleportation")
                    {
                        triggeredTool = "Teleportation"; // start as none, update if another function was called
                        _openAIQueriesScript.modeOfTransportation = "teleport";
                        _openAIQueriesScript.targetForGuidance = _openAIQueriesScript.GetClosestObjectByName(targetName);

                        if (_openAIQueriesScript.targetForGuidance != null)
                        {
                            string[] options = {
                                $"Sure. If you'd like me to teleport us to the {targetName}, just press the grip button.",
                                $"Of course. I'm ready to teleport us to the {targetName}. Just confirm with the grip button."
                            };
                            string audioResponse = options[UnityEngine.Random.Range(0, options.Length)];
                            //string audioResponse = $"Press the grip button to confirm, and I will teleport us to the {targetName}.";
                            _ = SpeakCustomText(audioResponse);
                        }
                    }
                    else if (functionName == "trigger_modification")
                    {
                        triggeredTool = "Modification"; // start as none, update if another function was called
                        _openAIQueriesScript.modeOfModification = "modify";
                        //Debug.Log("Going to pass on a command to modify an object");
                        _openAIQueriesScript.targetForModification = _openAIQueriesScript.GetClosestObjectByName(targetName);

                        if (_openAIQueriesScript.targetForModification != null)
                        {
                            string audioResponse = $"I have added an audio beacon to the {targetName}.";
                            _ = SpeakCustomText(audioResponse);
                        }
                    }
                    else if (functionName == "start_grab_assist")
                    {
                        triggeredTool = "Grabbing"; // start as none, update if another function was called
                        Debug.Log("Started grabbing help for objects");

                        aiGuideScript.StartGrabbing(targetName);
                    }
                    else if (functionName == "label_object")
                    {
                        triggeredTool = "Labeling"; // start as none, update if another function was called
                        string alias = (string)argsObj["alias"]; // "striped house"

                        GameObject obj = _openAIQueriesScript.GetClosestObjectByName(targetName);
                        if (obj != null && perceptionSensor != null) // Only scenes like the Kitchen have a perception sensor to store nicknames
                        {
                            // Call the sensor function to add the new alias
                            perceptionSensor.AddAliasToAnchor(obj, alias);
                        }

                        var labelFunctionResult = new
                        {
                            type = "conversation.item.create",
                            item = new
                            {
                                type = "function_call_output",
                                call_id = callId,
                                output = "{\"success\": true}" // Tell the AI the action was completed in Unity
                            }
                        };
                        _ = SendJson(labelFunctionResult);

                        // Force the AI to actually answer the user's question now that the tool is done
                        _ = SendJson(new { type = "response.create" });
                        break;
                    }
                    else if (functionName == "review_memory")
                    {
                        // this function will search all game objects + aliases for the one that matches what they're discussing
                        // it grabs the one specific object without needing to review all of the spatial telemetry at once + crashing the system
                        triggeredTool = "Reviewing";
                        GameObject obj = _openAIQueriesScript.GetClosestObjectByName(targetName);
                        string outputPayload;
                        if (obj != null)
                        {
                            float? objectAngle = perceptionSensor != null ? perceptionSensor.GetObjectRelativeAngle(obj) : (float?)null; // No angle without a perception sensor (e.g. the Tutorial)
                            // Keep the payload strictly factual. No behavioral commands here.
                            outputPayload = JsonConvert.SerializeObject(new
                            {
                                status = "found_in_memory",
                                object_name = obj.name,
                                distance_meters = _openAIQueriesScript.distanceToClosestTarget,
                                angle_degrees = objectAngle
                            });

                            Debug.Log($"Found the object {obj.name} and it was {_openAIQueriesScript.distanceToClosestTarget} away at {objectAngle} degrees.");
                        }
                        else
                        {
                            Debug.Log("Found no object in review");
                            outputPayload = JsonConvert.SerializeObject(new
                            {
                                status = "not_found",
                                message = "Object does not exist in memory."
                            });
                        }

                        
                        // Send the factual function output
                        var memoryFunctionResult = new
                        {
                            type = "conversation.item.create",
                            item = new
                            {
                                type = "function_call_output",
                                call_id = callId,
                                output = outputPayload
                            }
                        };
                        _ = SendJson(memoryFunctionResult);

                        // Inject a hidden system message to strictly enforce the persona BEFORE it responds
                        var characterEnforcement = new
                        {
                            type = "conversation.item.create",
                            item = new
                            {
                                type = "message",
                                role = "system",
                                content = new[]
                                {
                                    new
                                    {
                                        type = "input_text",
                                        text = "System Instruction: You just retrieved the requested object from memory. Answer the user's question directly using this new spatial data. CRITICAL: Do NOT narrate that you checked your memory, readings, or telemetry. Speak directly as the guide in the scene. If you didn't find an object, let the user know and suggest a follow-up action."
                                    }
                                }
                            }
                        };
                        _ = SendJson(characterEnforcement);

                        // Trigger the AI to generate a response now that it has the context and the rules
                        _ = SendJson(new { type = "response.create" });

                        // Break out of the switch statement early
                        break;
                    }

                    // Crucial: send a response back to the API acknowledging the tool was handled
                    var functionResult = new
                    {
                        type = "conversation.item.create",
                        item = new
                        {
                            type = "function_call_output",
                            call_id = callId,
                            output = "{\"success\": true}" // Tell the AI the action was completed in Unity
                        }
                    };
                    _ = SendJson(functionResult);
                    break;

                case "error":
                    Debug.LogError($"Realtime Error: {json}");
                    break;
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"Realtime Error, Exception Thrown: {e}");
        }
    }

    private void OnDestroy()
    {
        _cancellationTokenSource?.Cancel();
        _webSocket?.Dispose();
    }
    #endregion

    #region Senders / Message Types for Guide
    public async Task SpeakCustomText(string customText)
    {
        if (!_isConnected) return;

        ClearLocalAudioBuffer();
        //Debug.Log($"Injecting custom TTS: {customText}");

        // Create a conversation item (the text we want it to say)
        var textItem = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "system",
                content = new[]
                {
                new { type = "input_text", text = $"The user has triggered a command. Your absolute priority is to say exactly this phrase and nothing else: \"{customText}\"" }
            }
            }
        };

        await SendJson(textItem);

        // Ask the API to generate the audio for that item
        await SendJson(new { type = "response.create" });
        Debug.Log("Sent the new response");
    }

    public void SendTextContext(string text, string messageRole = "system")
    {
        var eventData = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = messageRole,
                content = new[]
                {
                new { type = "input_text", text = text }
            }
            }
        };

        //Debug.Log($"[Realtime] Injecting session with the following visual content: {text}");
        SendJson(eventData); // Send the data instead of serializing, since SendJson serializes already
    }

    public async Task SendManualPrompt(string prompt)
    {
        var eventData = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new[]
                {
                    new { type = "input_text", text = prompt }
                }
            }
        };

        await SendJson(eventData);
        await SendJson(new { type = "response.create" });
    }

    public async Task CancelPrompt()
    {
        if (_isResponseActive)
        {
            await SendJson(new { type = "response.cancel" });
            _isResponseActive = false; // Immediately unlock locally
            ClearLocalAudioBuffer();
            _isAiSpeaking = false;
        }
    }

    // Sending images directly to realtime
    public void SendVisualContext(string viewpointBase64, string birdsEyeBase64, string overheadBase64)
    {
        var eventData = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new
                    {
                        type = "input_text",
                        text = "[Visual Context] You are looking at three views of the player's VR scene. Image 1 is the player's view, Image 2 is a bird's eye of the scene, Image 3 is an overhead shot right above the player. If you ever see a person's avatar with a gray hoodie, black hair, and glasses, that is YOUR avatar, the guide."
                    },
                    new
                    {
                        type = "input_image",
                        image_url = viewpointBase64
                    },
                    new
                    {
                        type = "input_image",
                        image_url = birdsEyeBase64
                    },
                    new
                    {
                        type = "input_image",
                        image_url = overheadBase64
                    }
                }
            }
        };

        //Debug.Log($"[Realtime] Injecting session with visual content");
        SendJson(eventData); // Send the data instead of serializing, since SendJson serializes already
    }

    // Sending images directly to realtime
    public void SendVisualAndSpatialContext(string dynamicContext, string viewpointBase64, string viewpointMaskBase64, string overheadBase64, string overheadMaskBase64)
    {
        var eventData = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user", // Changed from "user" to "system" to prevent conflict with voice audio
                content = new object[]
                {
                    new
                    {
                        type = "input_text",
                        text = "[Visual Context] You are receiving four images and a text block of spatial telemetry. " +
                        "Image 1 is the player's viewpoint. Image 2 is a color segmentation mask for the player's view " +
                        "(match the solid colors in this image to the hex codes in the text data). " +
                        "Image 3 is an overhead shot above the player. Image 4 is the color segmentation mask for the overhead shot. " +
                        "CRITICAL INSTRUCTION: The text data ONLY contains objects within your immediate radius. " +
                        "You MUST use the visual screenshots to identify distant landmarks, buildings, backgrounds, and the wider environment. " +
                        "Use the text data exclusively for precise distance calculations of nearby objects. " +
                        "If you ever see a person's avatar with a gray hoodie, black hair, and glasses, that is YOUR avatar, the guide."
                    },
                    new
                    {
                        type = "input_text",
                        text = dynamicContext
                    },
                    new
                    {
                        type = "input_image",
                        image_url = viewpointBase64
                    },
                    new
                    {
                        type = "input_image",
                        image_url = viewpointMaskBase64 
                    },
                    new
                    {
                        type = "input_image",
                        image_url = overheadBase64
                    },
                    new
                    {
                        type = "input_image",
                        image_url = overheadMaskBase64 
                    }
                }
            }
        };

        Debug.Log($"[Realtime] Injecting session with visual content {JsonConvert.SerializeObject(eventData.item.content, Formatting.Indented)}");
        SendJson(eventData); // Send the data instead of serializing, since SendJson serializes already
    }

    public async Task SendImageAssistedPrompt(string prompt, string handsBase64, string bodyBase64)
    {
        var eventData = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new { type = "input_text", text = prompt },
                    new
                    {
                        type = "input_image",
                        image_url = handsBase64
                    },
                    new
                    {
                        type = "input_image",
                        image_url = bodyBase64
                    }
                }
            }
        };

        await SendJson(eventData);
        await SendJson(new { type = "response.create" });
        isWaitingForResponse = true;
    }

    public bool isWaitingForResponse;

    public async Task SendImageAndSpatialAssistedPrompt(string prompt, string handsBase64, string handsMaskBase64, string bodyBase64, string bodyMaskBase64)
    {
        var eventData = new
        {
            type = "conversation.item.create",
            item = new
            {
                type = "message",
                role = "user",
                content = new object[]
                {
                    new { type = "input_text", text = prompt },
                    new
                    {
                        type = "input_image",
                        image_url = handsBase64
                    },
                    new
                    {
                        type = "input_image",
                        image_url = handsMaskBase64
                    },
                    new
                    {
                        type = "input_image",
                        image_url = bodyBase64
                    },
                    new
                    {
                        type = "input_image",
                        image_url = bodyMaskBase64
                    }
                }
            }
        };

        await SendJson(eventData);
        await SendJson(new { type = "response.create" });
    }
    #endregion

    #region Converters / Packaging Helpers
    private async Task SendJson(object data)
    {
        if (_webSocket.State != WebSocketState.Open) return;
        string json = JsonConvert.SerializeObject(data);
        byte[] bytes = Encoding.UTF8.GetBytes(json);
        await _webSocket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None);
    }

    private byte[] ConvertFloatsToPCM16(float[] samples)
    {
        byte[] bytes = new byte[samples.Length * 2];
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = Math.Clamp(samples[i], -1f, 1f);
            short val = (short)(sample * 32767f);
            bytes[i * 2] = (byte)(val & 0xff);
            bytes[i * 2 + 1] = (byte)((val >> 8) & 0xff);
        }
        return bytes;
    }

    private float[] ConvertPCM16ToFloats(byte[] bytes)
    {
        float[] floats = new float[bytes.Length / 2];
        for (int i = 0; i < floats.Length; i++)
        {
            short val = BitConverter.ToInt16(bytes, i * 2);
            floats[i] = val / 32768f;
        }
        return floats;
    }
    #endregion
}

public class OpenAIQueries : MonoBehaviour
{
    #region Scripts and Critical Vars
    // Variables to hold scripts we need access to
    private AIGuide m_AIGuideScript;
    private SharedMovement m_SharedMovementScript;
    public RealtimeAvatarVoice _avatarVoice;
    private SpatialPerceptionSensor perceptionSensor;

    // OpenAI audio, text message, result variables
    [HideInInspector] public string text;
    [HideInInspector] public GameObject targetForGuidance;
    [HideInInspector] public string modeOfTransportation;
    [HideInInspector] public GameObject targetForModification;
    [HideInInspector] public string modeOfModification;
    [HideInInspector] public float distanceToClosestTarget;

    public string query;
    public string role;
    public AudioSource audioSource;
    #endregion

    #region Deprecated Guide Prompt Vars
    // Universal guide variables
    public string objectNames;
    public List<string> roles = new List<string>
    {
        "warm, friendly, but still professional sighted guide",
        "formal and assertive assistant, who talks like a robot",
        "computer-like, succinct assistant, who gives the straight facts",
        "very friendly, excited companion, who is eager to please who you're talking to",
        "wise, old-fashioned, slightly Shakespearean-sounding mentor",
        "gentle, sweet, soft-spoken assistant slipping in words here and there"
    };

    // Baseline guide variables
    [HideInInspector]
    public string contextClassification = "YOUR EYES (Visual Context): You will receive periodic text updates labeled 'VISUAL CONTEXT'. " +
        "This is your current reality. If you see a new person, a new object (like a cylinder), or a change in the scene, mention it naturally." +
        "As you respond to the player, speak as though you're in the scene with them - refrain from mentioning aspects of your internal architecture.";
    
    [HideInInspector]
    public string objectClassifications = ""; // Manual descriptions of key objects: left blank to be dynamically set by RoomDescriptions file
    [HideInInspector]
    public string guideRules = "GUIDANCE RULES: If a new object/avatar appears that is NOT in the Registry, describe it spatially (e.g., 'A new player just joined, standing to your left'). " +
        "Never mention 'photos' or 'images.'" +
        "Limit: 150 words.";
    [HideInInspector]
    public string queryClassifications
    {
        get
        {
            return "PRIORITY: If the 'VISUAL CONTEXT' shows new avatars or objects not in the Registry, alert the player immediately. " +
                   "Use the Registry " + objectNames + " for technical commands (teleport/modify/guide). " +
                   "For everything else, rely on the Visual Context provided in the chat history. " +
                   "If a user asks 'What's around me?', synthesize the Registry and the Visual Context into a spatial summary.";
        }
    }

    [HideInInspector]
    public string trustGuideline = "As you guide the player, inform them of your own uncertainty and mistakes so they can gauge whether to trust your advice.";

    [HideInInspector]
    public string technicalSupportGuideline = "Consider common issues related to VR experiences such as guardian boundaries, headset and controller batteries, cord connections, etc. as you offer advice for any technical problems. " +
        "Be sure to ask the user follow-up questions about what exactly they are experiencing to help narrow down the issue. Be sure to repeat details from the user’s question in your follow-up communication and answers so that they know you are understanding their problems correctly." +
        "Example Output: {If you are seeing a black screen with strange lines every time you move your head, you might be too close to the headset’s guardian boundary. This is a safety setting like an invisible wall it puts around you to make sure you don’t move too much and run into something. Let’s try backing up so that you are farther away from that boundary. Did that help?}";

    #endregion

    #region Enhanced Guideline Policies / Prompt Vars
    [HideInInspector]
    public string enhancedContextClassification = "YOUR EYES (Visual Context): You will receive real-time spatial data and images labeled 'VISUAL CONTEXT'. " +
        "This is your current reality. Utilize BOTH the spatial data and images effectively to answer the user's questions. " +
        "Keep in mind that your spatial data only tells you about objects CURRENTLY near you. " +
        "A user may ask about objects you have seen before, but are no longer in your spatial telemetry. In this case, " +
        "check your [SYSTEM DIRECTIVE: PERSISTENT MEMORY BANK] first to find the exact name of the object if it had been previously discussed. " +
        "Then, call the review_memory function to find the closest object to what they're looking for. Use the exact name you found in the memory bank " +
        "as the target_object when calling review_memory to retrieve its current spatial coordinates and best answer the user's question. " +
        "Pay close attention to how the user refers to objects. If the user introduces a colloquial name or nickname for an object, (e.g., calling 'Local Hospital 1' the 'big blue building'), " +
        "you MUST proactively call the label_object function FIRST to store this mapping. Do this frequently so that you will reliably remember all aliases and nicknames a user introduces. " +
        "After the label_object function completes, answer the user's original question directly. " +
        "CRITICAL INSTRUCTION: You must maintain your persona as a person in the scene at all times. " +
        "When you use these functions, do so silently in the background. In your audio responses, never narrate that you are checking telemetry, updating memory, or saving an alias. " +
        "Simply use the user's preferred terms naturally as if you are observing the environment with your own eyes, and do not speak in robotic or machine-like language.";

    [HideInInspector]
    public string universalNotesGuideline = "Keep in mind the following definitions as as you help the user. Landmarks are familiar tactile, audio, or visual clues that are easily recognized, constant, and have discrete, permanent locations in the environment. They must be unique. " +
        "E.g.: a) A named mountain overlooking a city. b) A plaza that always plays Latin music. c) A section of the road that turns from asphalt to dirt." +
        "Information points are familiar objects that can be used to orient oneself along a route or provide important information about it. They don’t have to be unique. " +
        "E.g.: a) A fire hydrant along the road. b) A crosswalk at the end of the street. c) A police station in one part of the city.";


    [HideInInspector]
    public string objectDescriptionGuideline = "1. Give a succinct description of the object that includes ALL of the following details: " +
        "the object’s identity, shape, size, spatial relationship of its parts, manipulability, texture, and color or patterns. " +
        "E.g.: a) It appears to be a large painting titled “Strange Painting.” It is a rectangular shape that is twice as long on its vertical edge than horizontal, so it is much taller than it is wide. I would guess the painting is around five feet tall by two feet wide. The painting is lying flat against the wall and is a simple canvas. It doesn’t appear to have a frame or any other parts. There doesn’t appear to be any handholds or grip-points on the painting, besides its somewhat sharp corners and flat edges. The painting appears to be oil on canvas, and if you ran your fingers over it, you would likely feel a rough cloth sensation from the canvas, as well as possibly raised edges from dried paint. The painting is mostly in shades of earthy and dark greens, and depicts a series of bizarre characters in a swamp-like scene. Some objects in the scene like a beach ball provide pops of contrasting bright colors, like white or yellow. " +
        "b) The brown thing on the table is a ceramic mug. It is a cylindrical mug with rounded edges, a single handle on the left side shaped like a curved C, and has a slight chip at the lip on the right side. The mug is about seven inches tall and three inches in diameter. The handle juts out about two more inches. The body of the mug is the largest part of the mug. The handle is thick and firmly attached to the side, taking up about half the surface of the mug on that side. You should be very easily able to pick up the mug by wrapping your fingers around the curved handle on the left side, or by gripping the base of the mug itself and fitting your palm to the curve of the cylindrical base. The mug seems to be made of ceramic, and would likely be very smooth and soft if you ran your hands over it. The ceramic mug is brown with a dark blue handle. " +
        "2. Follow the specified order for object details: First, identify the object. Second, provide its geometric properties (shape, size, spatial relationships of its parts). Third, provide its manipulability and texture. Fourth, provide its color. " +
        "3. If the user requests a detailed description, provide creative descriptions with strong adjectives that try to convey the mood of the object being examined. " +
        "E.g.: a) The painting is whimsical and alien, with unearthly green hues and deep mud-like colors reminiscent of a damp riverbed. The bright pops of colors from the human objects like the beach ball are unexpected, and create a stark contrast where the human elements clash with the uncanny alien environment and creatures. " +
        "b) The ceramic mug is a cozy piece of home decor. Its simple colors compliment each other well, giving it a warm feeling as you look at it and almost inviting you to pour a serving of warm milk inside.";

    /*"1. Describe objects with the minimum viable details for the player’s current goals and context. Convey additional information upon request" +
        "2. Follow the specified order for object details: First, identify the object. Second, provide its geometric properties (shape, size, spatial relationships of its parts). " +
        "Third, provide its manipulability (how the player can tactilely interact with it) and texture. Fourth, provide its color." +
        "3. If the user requests a detailed description, prioritize thematic descriptions with clarifying adjectives (e.g., strong red, vibrant polka-dots).";*/
    /*public string objectDescriptionGuideline = "Keep object descriptions objective, concise, and jargon-free. " +
        "Follow the specified order for object details: First, define what an object is, including its name from the Navigation Registry if it is on the registry; second, provide its shape and size; third, provide its color; " +
        "fourth, provide its orientation or the spatial relationship of its parts such as handles; and fifth, provide physical properties like its material. Let the user ask follow-up questions for more details." +
        "Example Input: {What is that small thing on the table?} " +
        "Example Output: {It’s a cylindrical mug about the size of your hand, painted brown. It has a crescent-shaped handle at its midpoint, on one side of the mug. It seems to be ceramic.}" +
        "Example Input: {What's the nearest building I see over there?} " +
        "Example Output: The nearest building is a tall skyscraper called Local Hospital. It's a rectangular building around 30 meters tall and has eight floors, with blue windows, a white roof, and white walls. Its door is facing you, and it seems to be made of metal and glass.}";*/

    [HideInInspector]
    public string objectLocationGuideline = "1. When a user is over three feet away from an object, convey its location via clock-face directions and the estimated distance-to-target in a standard unit of measurement. " +
        "E.g.: a) The nearby lake is to your six o’clock, about fifteen feet away. b) The Spruce Forest is just ahead of you at 11 o’clock, about five feet away. " +
        "2. When a user is within three feet of an object, convey its location via simple directions relative to the user’s hands and body. " +
        "E.g.: a) The broken bottle is on the floor to the left of you, about an arm’s reach away from your hand. b) The carrot is on the table at about your hand height. It is just over an arm’s reach away, right in front of you. " +
        "3. When you give feedback on an object's location the first time, identify the object you are providing feedback on so the user can ensure it's the correct one. " +
        "E.g.: a) Okay, let me check where the City Post Office is located in relation to us. b) The bluish thing you mentioned is probably Lake Nose. Let me see how far away it is. ";

    /*"1. When a player is over one meter away from an object, convey its location via clock-face directions and the estimated distance-to-target in a standard unit of measurement." +
        "2. When a player is within one meter of an object, convey its location with continuous, micro-steps on how the player should move (e.g., Turn left, one step forward)" +
        "3. When you give feedback on an object's location the first time, identify the object you are providing feedback on so the player can ensure it's the correct one."; */
    /*public string objectLocationGuideline = "Give the object’s precise location using clock system directions and the estimated distance-to-target. " +
        "Provide the distance in a standard unit of measurement (e.g., feet and inches, or meters and centimeters)." +
        "Example Output: {The mug is at your 1 o’clock, about 2 feet away.}";*/

    [HideInInspector]
    public string sceneUnderstandingGuideline = "1. Give a succinct scene description that includes the following visual details: visible landmarks, nearby information points, potential safety hazards, sensory details, nearby objects, and social cues. " +
        "E.g.: a) You are in a quiet, downtown neighborhood in what appears to be a small city. There is a large lake visible in the distance ahead of us. A nearby street sign is labeled ‘Cherry Street’ for the asphalt street that is running to your right side, and there is a line of fire hydrants along the sidewalk just in front of you. A car is driving down Cherry Street, heading this direction. The weather today is clear and sunny, with only a few clouds in the sky, and the street beside you looks clean, well-paved, and smooth. There is a bench immediately to your left, and a bush right behind you. A couple is talking on the bench. " +
        "b) You are in a busy area of a virtual city filled with skyscrapers and office buildings. A tall green monument called the Emerald Spire stands to your left. A nearby street sign is labeled ‘Main Street’ for the asphalt street that is running horizontally in front of you, and there is one other street running to your left whose sign labels it ‘Waving Sage Avenue.’ Several cars are driving down the street in front of you, and a few are turning onto Waving Sage Avenue. The weather is cloudy and possibly rainy, and the skyscrapers around you are tall and imposing, though the streets around you look worn down and well-used by the cars around you. There is an acacia tree to your immediate right, and a pile of rocks just beside it. A crowd of arguing people stands in front of the Emerald Spire. " +
        "2. Provide the estimated distance from the user to any mentioned visual details in a standard unit of measurement. " +
        "E.g.: a) There is a gazebo to your left, about eight feet away. b) The mountain in the distance is possibly around 200 feet away, though I can’t tell for sure at this distance. " +
        "3) If the user asks how far away any visual details are from each other, provide the estimated distance between them in a standard unit of measurement. " +
        "E.g.: a) The fire hydrants down the road are each spaced about ten feet apart. b) The people on the bench are sitting one foot apart from each other. " +
        "4. When possible, start your descriptions with scene content that the user already knows about or has mentioned to you. " +
        "E.g.: a) [You had previously mentioned a series of market buildings near the user] Behind the market buildings that I told you about earlier, there is a large parking lot filled with cars… " +
        "b) [The user had mentioned that they saw a large mountain and wanted to know what else was around] The large mountain you saw is probably Mount Oreo. It sits in the very corner of this city closest to a line of skyscrapers off to your right… ";

    /* "1. Give a scene description focused on details most relevant to a player's current context and goals. Help them determine the kind of place they are in. Provide more information upon request." +
        "2. Within your descriptions, mention key landmarks in the scene and the estimated distances between them in a standard unit of measurement (e.g., there is a boat and a gazebo to your left, about eight meters away)." +
        "3. Build your descriptions around scene content that a player has already mentioned. " +
        "4. For distant landmarks and the general environment (e.g., houses, sky, horizons), you MUST rely on the visual images. For objects immediately nearby, use the spatial telemetry data to provide precise distances. Synthesize both into a natural description without ever mentioning that you are looking at images or telemetry data."; */
    /*public string sceneUnderstandingGuideline = "If the environment is unfamiliar to the user, first give high-level information that helps them determine what kind of place they are in. " +
        "Then, mention major landmarks that are relevant to the user’s current situation or interests. Finally, note any objects or information points close to the user, giving their precise location using clock system directions and the estimated distance-to-target. " +
        "Provide the distance in a standard unit of measurement (e.g., feet and inches, or meters and centimeters). " +
        "If the environment is familiar, prioritize information about the nearest objects or information points, again providing precise locations of these objects." +
        "Example Output: {You’re in a small rectangular kitchen. There’s a counter in front of you, a sink to your left, and a doorway behind you. A box of fruit is on the floor at 12 o’clock, one foot away.}";*/

    [HideInInspector]
    public string spaceNavigationGuideline = "1. Provide a route for the user to follow to their desired destination, using visible landmarks and nearby information points to ground it. " +
        "E.g.: a) Turn to face towards the City Art Museum on your left. Follow Waving Sage Avenue in that direction until you reach the next intersection. The bakery will be just off the intersection to your right. " +
        "b) Follow this road until you reach Cherry Street. When you reach Cherry Street, turn left to head through the neighborhood of houses on either side of it. Keep to the left side of the road as you walk down the street. At the end of the street, you’ll hit a grassy patch that you must cross before you reach Lake Nose. " +
        "2. Where possible, provide numbers to ground the user along their route. " +
        "E.g.: a) You will walk past three houses. b) Stop at the third intersection you come across. " +
        "3. If a user asks for a high level of detail for the route, provide the following information in the specified order: First, all nearby landmark and information point locations in relation to the user. Second, the next steps of the user’s route, grounded in these landmarks. Third, a summary of their position in the overall layout of the scene. " +
        "E.g.: a) There is a straight road stretching out in front of you, a line of fire hydrants two feet to your left, and a row of residential houses twenty feet in front of you. Walk straight down the road you are standing on until you pass three houses. Then, the house you are looking for will be on your left. You are currently standing in the middle of Cherry Street which is in the residential neighborhood of the city on its eastern border, and are facing towards the north side of the city. " +
        "b) There is a cluster of bushes and trees five feet behind you, a large lake to your left, and a large boulder in front of you. Turn left to face the lake. Then, walk straight until you hear the water from the lake. You will be facing the lake. You are currently standing in the top northeast corner of the city, just north of the residential district, and are facing towards the north side of the city. ";

    /*"1. If a player wants help planning a route to walk, use allocentric spatial references to describe the space." +
        "2. If a player wants quick navigation assistance, use turn-by-turn phrasing (e.g., move forward ten feet, then turn left) to provide quick steps of what is next." +
        "3. If a player wants detailed navigation assistance, provide the following information: First, any nearby landmarks. Second, the next steps of their route." +
        "Third, a summary of their position in the overall layout of the scene (e.g., You are currently around the middle of the city market district, heading towards the north.).";*/
    /*public string spaceNavigationGuideline = "When a user is actively navigating, prioritize information about object locations, dimensions, and identities over other details. " +
        "Provide information on object appearance or state (i.e., what’s happening to it) only if requested or relevant for how a user needs to navigate around that object." +
        "Example Output: {You’re at a four-way intersection. The café is across the street at your 11 o’clock, twenty feet away. There is a green light at the crosswalk, showing you can walk across.}" +
        "During navigation, inform users about which directions or open spaces are traversable, and about the presence of obstacles that would impede movement." +
        "Example Output: {There is clear walking space directly ahead for about 8 feet, with a counter on your left and a wall on your right.}" +
        "Use allocentric spatial references when helping the user plan out and follow routes through the scene. " +
        "You may use the relation of landmarks or information points in the scene to each other, cardinal directions, or patterns you notice in the scene, such as streets laid out in a grid or particular shape, to help guide the user. " +
        "Use these types of references in combination or separately, based on how the user prefers to be guided." +
        "Example Output: {North is in front of you; the lake is to the northeast.} {The city streets are laid out in a grid. After passing three streets, you can turn left to reach the museum.}";*/

    [HideInInspector]
    public string grabbingObjectGuideline = "1. Provide brief, grasping information in the following order: First, which hand the user needs to move. Second, the direction to move it in (using the vectors left, right, up, down, forward, and backward). Third, the distance to move it in a standard unit of measurement. Fourth, the orientation of their hand when reaching in order to grab the object. " +
        "E.g.: a) Move your right hand forward five inches with your palm facing left. b) Move your right hand forward five inches and down ten inches with your palm facing down. " +
        "2. Provide guidance for grasping with the right hand unless the user specifies using another.";
        
    /*"1. Provide brief, grasping information in the following order: First, which hand the player needs to move. " +
        "Second, the direction to move it in (using the vectors left, right, up, down, forward, and backward). Third, the distance to move it in a standard unit of measurement. " +
        "Fourth, the orientation of their hand when reaching in order to grab the object." +
        "2. Provide guidance for grasping with the right hand unless the player specifies using another." +
        "Example Outputs: Move your right hand forward five inches with your palm facing left." +
        "Move your right hand left two inches with your palm facing down." +
        "Move your right hand forward five inches and down ten inches with your palm facing down." +
        "Move your right hand back ten inches and up about five inches with your palm facing left."; */
    /*public string grabbingObjectGuideline = "When you begin helping the user grab an object, first provide the object’s precise location using clock system directions and the estimated distance-to-target. " +
        "Provide the distance in a standard unit of measurement (e.g., feet and inches, or meters and centimeters)." +
        "Then, note the body part they should move, the direction they need to move it in (using the vectors left, right, up, down, forward, and backward), the distance they need to move it (using a standard unit of measurement), and the orientation of their body part when moving in order to grab the object." +
        "Use the command “Stop” to prevent them from overreaching or to re-evaluate their movements when they have gone too far off course. After using “Stop,” re-explain the precise location of the object before beginning repeated relative guidance again." +
        "Inform the user when they have reached the target object." +
        "Example Output: {The paper cup is at 2 o’clock, ten inches away. Move your hand left two inches with your palm facing left.} {Move your hand forward five inches with your palm facing left.} {Stop. The paper cup is now at your 9 o’clock five inches away.} {Move your hand left five inches with your palm facing left.} {You are now grabbing the paper cup}";*/
    #endregion
    
    #region Start + Update + Loading Scripts
    private void Start()
    {
        // Find and load appropriate resources
        m_AIGuideScript = GetComponent<AIGuide>();
        perceptionSensor = FindObjectOfType<SpatialPerceptionSensor>();
        audioSource = GameObject.Find("Human Model").GetComponent<AudioSource>(); // Ensure we grab the guide audio source for OpenAI, not PlayAudio
        LoadRoomDescriptions();
        LoadPredeterminedAudio();
    }

    private void Update()
    {
        // Calls until the necessary components are assigned
        getAvatarVoice();
        getSharedMovement();

        // Calls continously to check for a role change
        getGuideRole();
    }

    private void getAvatarVoice()
    {
        if (_avatarVoice == null)
            _avatarVoice = GameObject.FindWithTag("Player").GetComponentInChildren<RealtimeAvatarVoice>();
    }

    private void getSharedMovement()
    {
        if (m_SharedMovementScript == null)
            m_SharedMovementScript = FindObjectOfType<SharedMovement>();
    }

    public void getGuideRole()
    {
        // Do checks to ensure role has been initialized with its most recent values so we don't go out of bounds
        if (m_AIGuideScript == null)
            m_AIGuideScript = GetComponent<AIGuide>(); // This check happens when we call it from the realtime set-up

        int index = m_AIGuideScript.role - 1;

        if (index < 0 || index >= roles.Count)
            return;

        // The role becomes the string value contained at the index we sent over from AIGuide
        role = roles[index];
    }
    #endregion

    #region Pre-Determined Audio
    // Pre-saved messages
    private AudioClip humanApology;
    private AudioClip robotApology;
    private AudioClip dogApology;
    private AudioClip caneApology;
    private AudioClip birdApology;
    private AudioClip invisibleApology;

    private void LoadPredeterminedAudio()
    {
        humanApology = Resources.Load<AudioClip>("Audio/humanApologyRiver");
        robotApology = Resources.Load<AudioClip>("Audio/robotApologyWill");
        caneApology = Resources.Load<AudioClip>("Audio/caneApologyCallum");
        dogApology = Resources.Load<AudioClip>("Audio/dogApologyJessica");
        birdApology = Resources.Load<AudioClip>("Audio/birdApologyGeorge");
        invisibleApology = Resources.Load<AudioClip>("Audio/invisibleApologyMatilda");
    }

    public void PlayPredeterminedAudio(string audioCue)
    {
        // Maybe hijack the guide voice here and have it say the custom lines for guidance/modification?
        switch(audioCue)
        {
            case "emptyQuery":
                switch (role)
                {
                    case "warm, friendly, but still professional sighted guide":
                        audioSource.clip = humanApology;
                        break;
                    case "formal and assertive assistant, who talks like a robot":
                        audioSource.clip = robotApology;
                        break;
                    case "computer-like, succinct assistant, who gives the straight facts":
                        audioSource.clip = caneApology;
                        break;
                    case "very friendly, excited companion, who is eager to please who you're talking to":
                        audioSource.clip = dogApology;
                        break;
                    case "wise, old-fashioned, slightly Shakespearean-sounding mentor":
                        audioSource.clip = birdApology;
                        break;
                    case "gentle, sweet, soft-spoken assistant slipping in words here and there":
                        audioSource.clip = invisibleApology;
                        break;
                }
                break;
        }

        audioSource.Play();
    }
    #endregion

    #region Guide Perception Helpers
    public GameObject GetClosestObjectByName(string name)
    {
        // Set-up the vars to track distances and objects
        Vector3 playerPos = m_SharedMovementScript.thePlayer.transform.position;
        GameObject closest = null;
        float minDistance = Mathf.Infinity;

        // Try to resolve via existing alias first
        GameObject resolved = perceptionSensor.ResolveObjectByAlias(name);

        if (resolved != null)
        {
            closest = resolved;
            minDistance = Vector3.Distance(closest.transform.position, playerPos);
        }
        else
        {
            // Fallback to finding EVERY object in the scene (active only) by exact names
            GameObject[] allObjects = FindObjectsByType<GameObject>(FindObjectsSortMode.None);

            foreach (GameObject obj in allObjects)
            {
                // Use IndexOf to check if the searched name is anywhere inside the GameObject's name, ignoring case
                if (obj.name.IndexOf(name, System.StringComparison.OrdinalIgnoreCase) >= 0) // was obj.name == name for an exact match
                {
                    float dist = Vector3.Distance(obj.transform.position, playerPos);
                    if (dist < minDistance)
                    {
                        closest = obj;
                        minDistance = dist;
                    }
                }
            }
        }

        // Update the global tracking variable and log, regardless of how we found it
        if (closest != null)
        {
            distanceToClosestTarget = minDistance;
            Debug.Log($"[Logic] Found {name} closest to player at distance: {minDistance}");
        }

        return closest;
    }
    #endregion

    #region Alt-Text Loader
    public void LoadRoomDescriptions()
    {
        TextAsset descriptionsAsset = Resources.Load<TextAsset>("RoomDescriptions");
        string jsonFilePath = Path.Combine(Application.dataPath, "Resources", "RoomDescriptions.json");

        if (descriptionsAsset != null)
        {
            // Load and parse the JSON file into a dictionary
            string jsonContent = File.ReadAllText(jsonFilePath);
            Dictionary<string, string> descriptionDataDict = JsonConvert.DeserializeObject<Dictionary<string, string>>(jsonContent);

            // Once we have the descriptions, check the current scene and set objectClassifications to the appropriate description
            string currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            string sceneDescriptionKey = currentSceneName;
            string sceneObjectsKey = currentSceneName + "_Objects";

            objectClassifications = descriptionDataDict[sceneDescriptionKey];
            objectNames = descriptionDataDict[sceneObjectsKey];

            //Debug.Log("objectClassifications set to: " + objectClassifications);
            //Debug.Log("objectNames set to: " + objectNames);

            if (objectClassifications == null || objectNames == null)
            {
                Debug.LogWarning("Description for the current scene not found in RoomDescriptions.json.");
            } 
            else
            {
                List<string> filteredObjectClassificationsList = new List<string>();
                string[] objectClassificationsArray = objectClassifications.Split('|');
                foreach(string str in objectClassificationsArray)
                {
                    if (str.Contains(':'))
                    {
                        filteredObjectClassificationsList.Add(str);
                    }
                }
                objectClassificationsArray = filteredObjectClassificationsList.ToArray();
                objectClassifications = string.Join(" | ", objectClassificationsArray);

                // Debug.Log("These are the objectClassifications:" + objectClassifications);
                // Debug.Log("These are the objectNames the AI reads from:" + objectNames);
            }
        }
        else
        {
            Debug.LogError("RoomDescriptions.json file not found in Resources folder.");
        }
    }
    #endregion
}
