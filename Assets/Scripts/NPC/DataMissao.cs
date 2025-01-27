using System.Collections.Generic;
using UnityEngine;

    [CreateAssetMenu(fileName = "NewMission", menuName = "Missions/Mission")]
    public class DataMissao: ScriptableObject{
        
        public dialogue[] dialogue;
        public string name;
    }
    
    [System.Serializable]
    public class dialogue {
        public string[] textoDialogue;
        public Sprite icon;
    }
