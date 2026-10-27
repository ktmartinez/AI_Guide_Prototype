using System.Collections;
using System.IO;
using UnityEngine;
using System;
using System.Collections.Generic;
#if UNITY_ANDROID
using UnityEngine.Android;
#endif

public class CameraSystem : MonoBehaviour
{
    // Variables to set camera parameters
    private float birdHeight = 30f; // 15f for test scene
    private float birdZOffset = -10f; // Moves the camera back from directly over the player to get a better angle
    private float fieldOfView = 80f; // 60f is default
    private Vector3 birdRotation = new Vector3(65f, 0f, 0f);

    private Vector3 handLocalPos = new Vector3(0, 0.1f, 0.3f);
    private Vector3 handRotation = new Vector3(30, 0, 0);

    private Vector3 bodyLocalPos = new Vector3(1.0f, 0.5f, 0.5f);
    private Vector3 bodyRotation = new Vector3(10, -90, 0);

    private Vector3 overheadLocalPos = new Vector3(0, 5.0f, 0); // 5 meters directly above the head
    private Vector3 overheadRotation = new Vector3(90, 0, 0);   // Looking straight down

    private Transform head;

    // Public camera variables for AIGuide script to access
    [HideInInspector] public Camera birdEyeCamera;
    [HideInInspector] public Camera viewpointCamera;
    [HideInInspector] public Camera overheadCamera;
    [HideInInspector] public Camera handCam;
    [HideInInspector] public Camera bodyCam;

    // Mask Cameras
    [HideInInspector] public Camera viewpointMaskCamera;
    [HideInInspector] public Camera overheadMaskCamera;
    [HideInInspector] public Camera handMaskCamera;
    [HideInInspector] public Camera bodyMaskCamera;

    // Variables for monitoring
    private bool calledCamerasToStart = false;
    public bool converted = false;

    [Header("Instance Masking Settings")]
    private SpatialPerceptionSensor perceptionSensor;
    public Camera maskCamera;
    private Material unlitMaskMaterialBase; // the material we'll alter to mask things
    private int maskLayer = 17;

    // Start is called before the first frame update
    void Start()
    {
        // Gets the masking material from the perception sensor
        perceptionSensor = FindObjectOfType<SpatialPerceptionSensor>();
        if (perceptionSensor != null) // Only scenes like the Kitchen have a perception sensor, the Tutorial doesn't need masking
            unlitMaskMaterialBase = perceptionSensor.unlitMaskMaterialBase;

        // Pulls the viewpointCamera automatically from the Main Camera under XR Origin
        viewpointCamera = GameObject.Find("Main Camera").GetComponent<Camera>();
        createBirdEyeCamera();
        createOverheadCamera();

        createHandCam();
        createBodyCam();

        createMaskCameras();

        // Grabs the user's headset transform from AI guide script to guide hand + body cam positioning
        head = viewpointCamera.transform;
    }

    private void Update()
    {
        // If there is a guide in the scene, pull the bird eye camera from it, then begin sending screenshots
        if (GetComponent<SharedMovement>().theGuide != null && !calledCamerasToStart)
        {
            birdEyeCamera = GameObject.Find("Bird's Eye Camera").GetComponent<Camera>();

            if (!calledCamerasToStart)
            {
                // Capture once from viewpoint + bird's eye cameras so guide sees start of scene
                CaptureScreenshot();
                calledCamerasToStart = true;
            }
        }
    }

    // LateUpdate runs AFTER the XR Rig moves the camera - use this to move hand/body cams
    void LateUpdate()
    {
        if (head == null) return;

        if (handCam != null)
        {
            handCam.transform.position = head.position + (head.rotation * handLocalPos);
            handCam.transform.rotation = head.rotation * Quaternion.Euler(handRotation);
        }

        if (bodyCam != null)
        {
            bodyCam.transform.position = head.position + (head.rotation * bodyLocalPos);
            bodyCam.transform.rotation = head.rotation * Quaternion.Euler(bodyRotation);
        }

        // Keep the overhead camera centered on the player, but pointing straight down
        if (overheadCamera != null)
        {
            // We only take the head position, not rotation, so the camera doesn't tilt when the player looks up/down
            overheadCamera.transform.position = head.position + overheadLocalPos;
            overheadCamera.transform.rotation = Quaternion.Euler(overheadRotation);
        }
    }

    private void createMaskCameras()
    {
        // Viewpoint Mask Camera
        GameObject vpMaskObj = new GameObject("Viewpoint Mask Camera");
        viewpointMaskCamera = vpMaskObj.AddComponent<Camera>();
        SetupMaskCamera(viewpointMaskCamera);

        // Overhead Mask Camera
        GameObject ohMaskObj = new GameObject("Overhead Mask Camera");
        overheadMaskCamera = ohMaskObj.AddComponent<Camera>();
        SetupMaskCamera(overheadMaskCamera);

        // Hand Mask Camera
        GameObject haMaskObj = new GameObject("Hand Mask Camera");
        handMaskCamera = haMaskObj.AddComponent<Camera>();
        SetupMaskCamera(handMaskCamera);

        // Body Mask Camera
        GameObject boMaskObj = new GameObject("Body Mask Camera");
        bodyMaskCamera = boMaskObj.AddComponent<Camera>();
        SetupMaskCamera(bodyMaskCamera);
    }

    private void createBirdEyeCamera()
    {
        GameObject newCamera = new GameObject("Bird's Eye Camera");
        birdEyeCamera = newCamera.AddComponent<Camera>();

        // If we're in the test scene, alter the birdHeight variable to be closer since the scene isn't as big
        string currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
        if (currentSceneName.Equals("Tutorial")) // was GuideTest_Networked
            birdHeight = 15f;

        // Camera has specified height it goes above the guide to get bird's eye view + rotation + widened field of view to look down at the scene
        birdHeight = birdHeight + transform.position.y;
        birdEyeCamera.transform.position = new Vector3(transform.position.x, birdHeight, transform.position.z + birdZOffset);
        birdEyeCamera.transform.eulerAngles = birdRotation;
        birdEyeCamera.fieldOfView = fieldOfView;
    }

    private void createOverheadCamera()
    {
        GameObject newCamera = new GameObject("Overhead Camera");
        overheadCamera = newCamera.AddComponent<Camera>();

        SetupCamera(overheadCamera);
        overheadCamera.fieldOfView = 80f; // Wide enough to see the immediate vicinity
    }

    private void createHandCam()
    {
        GameObject newCamera = new GameObject("Hand Camera");
        handCam = newCamera.AddComponent<Camera>();
        //handCam.transform.SetParent(head); // Parent to headset so it moves with the player

        // Camera needs to have specified height and angle to be pointed at user's hands at all times
        SetupCamera(handCam); // Point 30 degrees down and forward (not 90 straight down)

        // Don't enable at the start - only activate during StartGrabbing and deactivate on StopGrabbing
        handCam.enabled = false;
    }

    private void createBodyCam()
    {
        GameObject newCamera = new GameObject("Body Camera");
        bodyCam = newCamera.AddComponent<Camera>();
        //bodyCam.transform.SetParent(head);

        // Camera needs to have specified height and angle to be showing user's whole body from the side at all times/profile view to assist in grabbing
        SetupCamera(bodyCam); // Point 10 degrees down and towards the player

        // Don't enable at the start - only activate during StartGrabbing and deactivate on StopGrabbing
        bodyCam.enabled = false;
    }

    private void SetupCamera(Camera cam)
    {
        // DO NOT PARENT IT TO THE HEAD
        // Leave it completely unparented in the hierarchy so the XR rig can't mess with it
        cam.transform.SetParent(null);

        // Set Near Clip Plane very low so it doesn't clip hands
        cam.nearClipPlane = 0.05f;
        cam.fieldOfView = 90f;
    }

    private void SetupMaskCamera(Camera cam)
    {
        cam.transform.SetParent(null);
        cam.cullingMask = 1 << maskLayer;
        cam.clearFlags = CameraClearFlags.SolidColor;
        cam.backgroundColor = Color.black;
        cam.enabled = false;
        cam.nearClipPlane = 0.05f;
        cam.fieldOfView = 80f; // Match your main camera FOVs
    }

    // Pass your active anchors dictionary/list from your perception script into this method
    public void CaptureMaskedScenes(Dictionary<string, ObjectAnchor> activeAnchors, Action onComplete)
    {
        StartCoroutine(CaptureBatchMasksCoroutine(activeAnchors, onComplete));
    }

    private IEnumerator CaptureBatchMasksCoroutine(Dictionary<string, ObjectAnchor> activeAnchors, Action onComplete)
    {
        yield return new WaitForEndOfFrame();

        Dictionary<Renderer, Material[]> originalMaterials = new Dictionary<Renderer, Material[]>();
        Dictionary<GameObject, int> originalLayers = new Dictionary<GameObject, int>();

        // Swap all objects to their respective mask colors
        foreach (var kvp in activeAnchors)
        {
            ObjectAnchor anchor = kvp.Value;
            if (anchor.gameObjectReference == null) continue;

            GameObject targetObj = anchor.gameObjectReference;
            originalLayers[targetObj] = targetObj.layer;
            targetObj.layer = maskLayer;

            Renderer[] renderers = targetObj.GetComponentsInChildren<Renderer>();
            foreach (Renderer r in renderers)
            {
                originalMaterials[r] = r.materials;
                r.gameObject.layer = maskLayer;

                Material[] solidMats = new Material[r.materials.Length];
                for (int i = 0; i < solidMats.Length; i++)
                {
                    solidMats[i] = new Material(unlitMaskMaterialBase);
                    solidMats[i].color = anchor.uniqueColorID;
                }
                r.materials = solidMats;
            }
        }

        // Align mask cameras to current main camera positions
        viewpointMaskCamera.transform.position = viewpointCamera.transform.position;
        viewpointMaskCamera.transform.rotation = viewpointCamera.transform.rotation;

        overheadMaskCamera.transform.position = overheadCamera.transform.position;
        overheadMaskCamera.transform.rotation = overheadCamera.transform.rotation;

        // Render Viewpoint Mask
        viewpointMaskBase64 = RenderCameraToBase64(viewpointMaskCamera);

        // Render Overhead Mask
        overheadMaskBase64 = RenderCameraToBase64(overheadMaskCamera);

        // Restore all original states
        foreach (var kvp in activeAnchors)
        {
            ObjectAnchor anchor = kvp.Value;
            if (anchor.gameObjectReference == null) continue;

            GameObject targetObj = anchor.gameObjectReference;
            targetObj.layer = originalLayers[targetObj];

            Renderer[] renderers = targetObj.GetComponentsInChildren<Renderer>();
            foreach (Renderer r in renderers)
            {
                if (originalMaterials.ContainsKey(r))
                {
                    r.materials = originalMaterials[r];
                }
                r.gameObject.layer = originalLayers[targetObj];
            }
        }

        onComplete?.Invoke();
    }

    // Pass your active anchors dictionary/list from your perception script into this method
    public void CaptureMaskedHands(Dictionary<string, ObjectAnchor> activeAnchors, Action onComplete)
    {
        StartCoroutine(CaptureHandMasksCoroutine(activeAnchors, onComplete));
    }

    private IEnumerator CaptureHandMasksCoroutine(Dictionary<string, ObjectAnchor> activeAnchors, Action onComplete)
    {
        yield return new WaitForEndOfFrame();

        Dictionary<Renderer, Material[]> originalMaterials = new Dictionary<Renderer, Material[]>();
        Dictionary<GameObject, int> originalLayers = new Dictionary<GameObject, int>();

        // Swap all objects to their respective mask colors
        foreach (var kvp in activeAnchors)
        {
            ObjectAnchor anchor = kvp.Value;
            if (anchor.gameObjectReference == null) continue;

            GameObject targetObj = anchor.gameObjectReference;
            originalLayers[targetObj] = targetObj.layer;
            targetObj.layer = maskLayer;

            Renderer[] renderers = targetObj.GetComponentsInChildren<Renderer>();
            foreach (Renderer r in renderers)
            {
                originalMaterials[r] = r.materials;
                r.gameObject.layer = maskLayer;

                Material[] solidMats = new Material[r.materials.Length];
                for (int i = 0; i < solidMats.Length; i++)
                {
                    solidMats[i] = new Material(unlitMaskMaterialBase);
                    solidMats[i].color = anchor.uniqueColorID;
                }
                r.materials = solidMats;
            }
        }

        // Align mask cameras to current main camera positions
        handMaskCamera.transform.position = handCam.transform.position;
        handMaskCamera.transform.rotation = handCam.transform.rotation;

        bodyMaskCamera.transform.position = bodyCam.transform.position;
        bodyMaskCamera.transform.rotation = bodyCam.transform.rotation;

        // Render Hand Mask
        handMaskBase64 = RenderCameraToBase64(handMaskCamera);

        // Render Body Mask
        bodyMaskBase64 = RenderCameraToBase64(bodyMaskCamera);

        // Restore all original states
        foreach (var kvp in activeAnchors)
        {
            ObjectAnchor anchor = kvp.Value;
            if (anchor.gameObjectReference == null) continue;

            GameObject targetObj = anchor.gameObjectReference;
            targetObj.layer = originalLayers[targetObj];

            Renderer[] renderers = targetObj.GetComponentsInChildren<Renderer>();
            foreach (Renderer r in renderers)
            {
                if (originalMaterials.ContainsKey(r))
                {
                    r.materials = originalMaterials[r];
                }
                r.gameObject.layer = originalLayers[targetObj];
            }
        }

        onComplete?.Invoke();
    }

    public void CaptureScreenshot()
    {
        converted = false;
        StartCoroutine(CaptureScreenshotCoroutine(viewpointCamera));
        //StartCoroutine(CaptureScreenshotCoroutine(birdEyeCamera));
        StartCoroutine(CaptureScreenshotCoroutine(overheadCamera));
    }

    public Coroutine CaptureHandScreenshots()
    {
        return StartCoroutine(CaptureHandAndBody());
    }

    private IEnumerator CaptureHandAndBody()
    {
        converted = false;
        yield return StartCoroutine(CaptureScreenshotCoroutine(handCam));
        yield return StartCoroutine(CaptureScreenshotCoroutine(bodyCam));
    }

    private IEnumerator CaptureScreenshotCoroutine(Camera camera)
    {
        yield return new WaitForEndOfFrame();

        if (camera == viewpointCamera) viewpointImageBase64 = RenderCameraToBase64(camera);
        else if (camera == birdEyeCamera) birdsEyeImageBase64 = RenderCameraToBase64(camera);
        else if (camera == overheadCamera) overheadImageBase64 = RenderCameraToBase64(camera);
        else if (camera == handCam) handImageBase64 = RenderCameraToBase64(camera);
        else if (camera == bodyCam) bodyImageBase64 = RenderCameraToBase64(camera);

        converted = true;
    }

    private string RenderCameraToBase64(Camera camera)
    {
        RenderTexture renderTexture = new RenderTexture(Screen.width, Screen.height, 24);
        camera.targetTexture = renderTexture;
        camera.Render();

        Texture2D texture = new Texture2D(Screen.width, Screen.height, TextureFormat.RGB24, false);
        RenderTexture.active = renderTexture;
        texture.ReadPixels(new Rect(0, 0, Screen.width, Screen.height), 0, 0);
        texture.Apply();

        byte[] bytes = texture.EncodeToJPG(75);
        string base64 = $"data:image/jpeg;base64,{Convert.ToBase64String(bytes)}";

        camera.targetTexture = null;
        RenderTexture.active = null;
        Destroy(renderTexture);
        Destroy(texture);

        return base64;
    }

    [HideInInspector] public string viewpointImageBase64;
    [HideInInspector] public string birdsEyeImageBase64;
    [HideInInspector] public string overheadImageBase64;
    [HideInInspector] public string handImageBase64;
    [HideInInspector] public string bodyImageBase64;

    [HideInInspector] public string viewpointMaskBase64;
    [HideInInspector] public string overheadMaskBase64;
    [HideInInspector] public string handMaskBase64;
    [HideInInspector] public string bodyMaskBase64;

    private string GetBase64FromBytes(byte[] imageBytes)
    {
        string base64 = Convert.ToBase64String(imageBytes);
        return $"data:image/jpeg;base64,{base64}";
    }
}