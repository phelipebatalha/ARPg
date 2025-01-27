using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public class EventResult : MonoBehaviour
{
    public GameObject[] boxes;
    private GameObject defbox;
    void Start()
    {
        MeshRenderer meshRenderer = new MeshRenderer();
    }

    // Update is called once per frame
    void Update()
    {
        if(boxes[0].activeInHierarchy)
        {
            MeshRenderer defboxMehRenderer = defbox.GetComponent<MeshRenderer>();
        }   
    }
}
