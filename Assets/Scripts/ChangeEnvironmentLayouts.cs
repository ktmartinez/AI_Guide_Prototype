using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.SceneManagement;

public class ChangeEnvironmentLayouts : MonoBehaviour
{
    // Variables to hold our ordered layouts
    public bool roomLayoutOne = false;
    public bool roomLayoutTwo = false;
    public bool roomLayoutThree = false;

    public bool tableLayoutOne = false;
    public bool tableLayoutTwo = false;
    public bool tableLayoutThree = false;

    private bool foundTables = false;
    private bool foundRooms = false;

    // Hold the objects we manipulate as we change layouts
    private GameObject[] availableLayouts = new GameObject[6]; // 1-3 = room layouts, 4-6 = table layouts

    // Start is called before the first frame update
    void Start()
    {
        CheckCurrentScene();
    }

    private void CheckCurrentScene()
    {
        string currentSceneName = SceneManager.GetActiveScene().name;
        GameObject mainRoom = GameObject.Find(currentSceneName);

        if (currentSceneName != "Kitchen" || currentSceneName != "Pharmacy")
        availableLayouts[0] = mainRoom; // Final room
        availableLayouts[1] = GameObject.Find(currentSceneName + " 2"); // E.g., Kitchen 2
        availableLayouts[2] = GameObject.Find(currentSceneName + " 3");
        foundRooms = mainRoom != null && availableLayouts[1] != null && availableLayouts[2] != null; // Scenes like the Tutorial only have one layout

        switch (currentSceneName)
        {
            case "Kitchen":
                availableLayouts[3] = mainRoom.transform.Find("Kitchen Prep Table").gameObject; // Final table
                availableLayouts[4] = mainRoom.transform.Find("Kitchen Prep Table 2").gameObject;
                availableLayouts[5] = mainRoom.transform.Find("Kitchen Prep Table 3").gameObject;
                foundTables = true;
                break;
            case "Pharmacy":
                availableLayouts[3] = mainRoom.transform.Find("Pharmacy Prep Table").gameObject; // Final table
                availableLayouts[4] = mainRoom.transform.Find("Pharmacy Prep Table 2").gameObject;
                availableLayouts[5] = mainRoom.transform.Find("Pharmacy Prep Table 3").gameObject;
                foundTables = true;
                break;
        }
    }

    // Update is called once per frame
    void Update()
    {
        if (foundRooms)
        {
            availableLayouts[2].SetActive(roomLayoutOne);
            availableLayouts[1].SetActive(roomLayoutTwo);
            availableLayouts[0].SetActive(roomLayoutThree); // 0 is the main room, which we need to switch to third
        }
        if (foundTables)
        {
            //Debug.Log("Ready to switch tables");
            availableLayouts[5].SetActive(tableLayoutOne);
            availableLayouts[4].SetActive(tableLayoutTwo);
            availableLayouts[3].SetActive(tableLayoutThree); // 3 is the main table, which we need to switch to third
        }
    }
}
