using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

public class TeleportationHandler : MonoBehaviour
{
    public XRController leftTarget;
    public XRController rightTarget;
    public InputHelpers.Button teleportRayTrigger;
    public float activationThreshold = 0.95f;

    private XRInteractorLineVisual leftRay;
    public GameObject leftReticle;

    private XRInteractorLineVisual rightRay;
    public GameObject rightReticle;

    private XRRayInteractor leftRayInteractor;
    private XRRayInteractor rightRayInteractor;

    public LayerMask teleportRayMask;
    private LayerMask leftNormalMask;
    private LayerMask rightNormalMask;

    private bool leftWasPressedLastFrame;
    private bool rightWasPressedLastFrame;

    private CustomTeleportationProvider teleport;
    private CharacterController characterController;
    private float characterControllerCenterY;
    private float characterControllerHeight;

    // Variables to hold scripts we need access to
    private VRScreenreader m_VRScreenreaderScript;

    // Bools to control for screenreader/guide switch
    private bool screenreaderActive = false;

    // Global flag that allows other systems (e.g., navigation tasks)
    // to temporarily disable teleport input and rays.
    public static bool teleportationBlocked = false;

    // Start is called before the first frame update
    void Start()
    {
        //Debug.Log("[TeleportationHandler] NEW SCRIPT VERSION LOADED");
        leftRay = leftTarget.gameObject.GetComponent<XRInteractorLineVisual>();
        leftReticle = leftRay.reticle;

        rightRay = rightTarget.gameObject.GetComponent<XRInteractorLineVisual>();
        rightReticle = rightRay.reticle;

        leftRayInteractor = leftTarget.GetComponent<XRRayInteractor>();
        rightRayInteractor = rightTarget.GetComponent<XRRayInteractor>();

        if (leftRayInteractor != null)
            leftNormalMask = leftRayInteractor.raycastMask;

        if (rightRayInteractor != null)
            rightNormalMask = rightRayInteractor.raycastMask;

        teleportRayMask = LayerMask.GetMask("Floors");

        teleport = this.gameObject.GetComponent<CustomTeleportationProvider>();
        characterController = this.gameObject.GetComponent<CharacterController>();
        characterControllerCenterY = 0.88f;
        characterControllerHeight = 1.6f;

        m_VRScreenreaderScript = FindObjectOfType<VRScreenreader>();
        if (FindObjectOfType<SwitchTools>().VRScreenreaderActive)
            screenreaderActive = true;
    }

    // Update is called once per frame
    void Update()
    {
        // If teleportation is globally blocked, make sure rays/reticles are off and skip input.
        if (teleportationBlocked)
        {
            if (leftRay != null)
            {
                leftRay.enabled = false;
                if (leftReticle != null)
                    leftReticle.SetActive(false);
            }

            if (rightRay != null)
            {
                rightRay.enabled = false;
                if (rightReticle != null)
                    rightReticle.SetActive(false);
            }

            return;
        }

        bool leftIsPressed = CheckIfButtonDown(leftTarget);
        leftRay.enabled = leftIsPressed;
        leftReticle.SetActive(leftIsPressed);
        if (screenreaderActive && leftIsPressed)
            CheckForReticleHit(leftTarget, leftRay);

        bool rightIsPressed = CheckIfButtonDown(rightTarget);
        rightRay.enabled = rightIsPressed;
        rightReticle.SetActive(rightIsPressed);
        if (screenreaderActive && rightIsPressed)
        {
            CheckForReticleHit(rightTarget, rightRay);
        }

        if (!leftIsPressed && !rightIsPressed)
        {
            // If both teleportation triggers are not being held down, reset the lastHitObject
            if (m_VRScreenreaderScript != null)
                m_VRScreenreaderScript.lastHitObject = null;
        }

        if (leftRayInteractor != null)
            leftRayInteractor.raycastMask = leftIsPressed ? teleportRayMask : leftNormalMask;

        if (rightRayInteractor != null)
            rightRayInteractor.raycastMask = rightIsPressed ? teleportRayMask : rightNormalMask;

        if (leftRayInteractor != null && leftRayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit))
        {
            //Debug.Log($"[Left Interactor] Hitting: {hit.collider.name}");
        }

        if (rightRayInteractor != null && rightRayInteractor.TryGetCurrent3DRaycastHit(out RaycastHit hit2))
        {
            //Debug.Log($"[Right Interactor] Hitting: {hit2.collider.name}");
        }

        // If the action of teleportation has completed (scenes like the Tutorial don't have the custom provider)
        if (teleport != null && teleport.locomotionPhase == LocomotionPhase.Done)
        {
            Debug.Log("Teleport motion completed");
            characterController.center = new Vector3(0f, characterControllerCenterY, 0f);
            characterController.height = characterControllerHeight;
            if (m_VRScreenreaderScript != null)
            {
                if (m_VRScreenreaderScript.sharedMovementFound)
                    m_VRScreenreaderScript.PlayReferenceAudioPostTeleport();
            }
        }
    }

    public bool CheckIfButtonDown(XRController controller)
    {
        InputHelpers.IsPressed(controller.inputDevice, teleportRayTrigger, out bool isPressed, activationThreshold);
        return isPressed;
    }

    // Function to check if the ray hits something
    void CheckForReticleHit(XRController controller, XRInteractorLineVisual ray)
    {
        // Ensure the reticle exists and is active
        GameObject reticle = ray.reticle;
        if (reticle == null || !reticle.activeInHierarchy)
            return;

        // Get the position of the reticle
        Vector3 reticlePosition = reticle.transform.position;

        // Perform a Physics.OverlapSphere to check objects at the reticle's position
        float sphereRadius = 0.02f;
        int layerMask = ~LayerMask.GetMask("Ignore Raycast");
        Collider[] hitColliders = Physics.OverlapSphere(reticlePosition, sphereRadius, layerMask);

        if (hitColliders.Length > 0)
        {
            Debug.Log("Teleport reticle hit an object");
            // Hitting a teleportable object, so activate screenreader and share position of reticle
            if (screenreaderActive)
                m_VRScreenreaderScript.TeleportCheckReferenceAndPlayAudio(reticlePosition); // plays bounds audio before teleport completes
        }
        //else
            //Debug.Log("No objects detected at reticle position.");
    }
}
