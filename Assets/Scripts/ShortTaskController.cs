using System.Collections;
using System.Collections.Generic;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;

public class ShortTaskController : MonoBehaviour
{
    // Variables to hold important gameobjects for controlling tasks
    public GameObject unloadingBag;
    public GameObject interactionTable;
    public GameObject preparationTool;
    private BoxCollider bagReferenceCollider;

    // Bools to control set-up and take down of short tasks from editor
    public bool navigationTaskActive;
    private bool previousNavTaskState;
    public bool unloadingAndPreparationTaskActive;
    private bool previousUnloadingAndPreparationTaskState;

    // Scripts we need access to
    private RandomTarget m_RandomTargetScript;
    private RandomObjectSpawner m_UnloadSpawnerAndPrepareTaskScript;
    private SwitchTools m_SwitchToolsScript;
    private SpatialPerceptionSensor perceptionSensor;

    // Variables to track scores
    public int navigationTaskScore = 0;
    public int unloadingTaskScore = 0;
    public int preparationTaskScore = 0;

    // Start is called before the first frame update
    void Start()
    {
        // All desired components should be on the Task Manager prefab with this controller
        m_RandomTargetScript = gameObject.GetComponent<RandomTarget>();

        // Grab switch tools script to check for VR Guide and Screenreader status
        m_SwitchToolsScript = FindFirstObjectByType<SwitchTools>();

        // The PrepareObject script needs an active reference to the VRHandling script
        gameObject.AddComponent<VRHandling>();

        // To clean-up our collision references with the enhanced guide
        perceptionSensor = FindObjectOfType<SpatialPerceptionSensor>();

        // Set up state variables for detecting changes
        previousNavTaskState = navigationTaskActive;
        previousUnloadingAndPreparationTaskState = unloadingAndPreparationTaskActive;

        // Set up Physics Matrix to ignore collisions between Interactables and any objects on the IgnoreCollisions layer
        Physics.IgnoreLayerCollision(7, 9, true); // Interactables, IgnoreCollisions
    }

    // Update is called once per frame
    void Update()
    {
        // Constantly check + update scores from tasks
        CheckScoreUpdates();

        // Check if navigation task is active or inactive
        if (m_RandomTargetScript != null)
            CheckNavigationTaskActive();

        // Check if unloading and preparation task is active or inactive -- needs to be run to set up m_UnloadSpawnerScript and m_PrepareSpawnerScript
        CheckUnloadingAndPreparationTaskActive();
    }

    private void CheckNavigationTaskActive()
    {
        if (navigationTaskActive != previousNavTaskState)
        {
            if (navigationTaskActive)
            {
                Debug.Log("Setting up navigation task");
                m_RandomTargetScript.SetUpRandomTargets();
            }
            else
            {
                Debug.Log("Taking down navigation task");
                m_RandomTargetScript.TakeDownRandomTargets();
            }

            // Update previousNavTaskState to match the new state of navigationTaskActive
            previousNavTaskState = navigationTaskActive;
        }
    }

    private void CheckUnloadingAndPreparationTaskActive()
    {        
        if (m_SwitchToolsScript.VRGuideActive)
        {
            bagReferenceCollider = unloadingBag.GetComponentInChildren<BoxCollider>();
        }
        else if (m_SwitchToolsScript.VRScreenreaderActive)
        {
            // Grab the reference collider of the unloading bag, which will interfere with spawned ingredients falling inside it
            bagReferenceCollider = unloadingBag.transform.Find("Reader Reference(Clone)").GetComponentInChildren<BoxCollider>();
        }
        
        if (previousUnloadingAndPreparationTaskState != unloadingAndPreparationTaskActive)
        {
            if (unloadingAndPreparationTaskActive)
            {
                // Set reference collider to IgnoreCollisions layer
                bagReferenceCollider.gameObject.layer = 9;

                Debug.Log("Setting up unloading and preparation task");
                SetUpUnloadAndPrepareSpawner();
            }
            else
            {
                bagReferenceCollider.gameObject.layer = 13;
                preparationTool.GetComponent<XRGrabInteractable>().enabled = true;

                Debug.Log("Taking down unloading and preparation task");
                TakeDownUnloadAndPrepareSpawner();
            }

            // Update previous task state to match the new state of unloadingAndPreparationTaskActive
            previousUnloadingAndPreparationTaskState = unloadingAndPreparationTaskActive;
        }
    }

    private void SetUpUnloadAndPrepareSpawner()
    {
        if (unloadingBag != null)
        {
            if (m_UnloadSpawnerAndPrepareTaskScript == null) // First time running the unloading and preparing task
            {
                unloadingBag.AddComponent<RandomObjectSpawner>(); // Add a spawner
                m_UnloadSpawnerAndPrepareTaskScript = unloadingBag.GetComponent<RandomObjectSpawner>();
            }
            m_UnloadSpawnerAndPrepareTaskScript.SpawnRandomObject();
        }

        /*if (interactionTable != null)
            {
                if (m_PrepareSpawnerScript == null) // First time running the unloading and preparing task
                {
                    interactionTable.AddComponent<RandomObjectSpawner>(); // Add a spawner
                    m_PrepareSpawnerScript = interactionTable.GetComponent<RandomObjectSpawner>();
                }
         }*/
    }

    private void TakeDownUnloadAndPrepareSpawner()
    {
        if (interactionTable != null)
        {
            if (m_UnloadSpawnerAndPrepareTaskScript != null) // If the bag has had a RandomObjectSpawner added (the task had begun at some point)
            {
                if (m_UnloadSpawnerAndPrepareTaskScript.spawnedObject != null) // Destroy any lingering spawnedObjects from unloading
                {
                    Destroy(m_UnloadSpawnerAndPrepareTaskScript.spawnedObject);
                }  
            }
        }


        // if (interactionTable != null)
        // {
        //     if (m_PrepareSpawnerScript != null) // If the interactionTable has had a RandomObjectSpawner added (the task had begun at some point)
        //     {
        //         if (m_PrepareSpawnerScript.spawnedObject != null) // Destroy any lingering spawnedObjects from preparing
        //             Destroy(m_PrepareSpawnerScript.spawnedObject);
        //     }
        // }
    }

    void CheckScoreUpdates()
    {
        // Pull latest scores from scripts
        if (m_RandomTargetScript != null)
            navigationTaskScore = m_RandomTargetScript.timesTargetReached;
    }
}
