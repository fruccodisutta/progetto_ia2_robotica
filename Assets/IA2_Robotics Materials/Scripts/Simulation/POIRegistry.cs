using UnityEngine;
using System.Collections.Generic;

// Registry per mappare gli id_unity ai WaypointNode P.O.I nella scena.
// Usato per trovare rapidamente le destinazioni dalle richieste del backend.
public class POIRegistry : MonoBehaviour
{
    public static POIRegistry Instance { get; private set; }
    
    [Header("Debug")]
    public bool debugLog = false;
    
    // Dizionario id_unity -> nome P.O.I
    private static readonly Dictionary<int, string> POI_NAMES = new Dictionary<int, string>()
    {
        {0, "Casa"},          
        {1, "Casa (1)"},       
        {2, "Casa (2)"},       
        {3, "Stadio"},
        {4, "Abbigliamento"},
        {5, "Museum"},
        {6, "Fabbrica"},
        {7, "DrugStore"},
        {8, "BusStation"},
        {9, "Cinema"},
        {10, "Parco 1"},
        {11, "Parco 2"},
        {12, "Banca"},
        {13, "Supermarket"},
        {14, "Hospital"},
        {15, "Discopub"},
        {16, "Palestra"},
        {17, "Hotel"},
        {18, "Meccanico"},
        {19, "Libreria"},
        {20, "Bakery"},
        {21, "Pizzeria"},
        {22, "Ristorante"},
        {23, "CoffeeShop"},
        {24, "FriedChicken"},
        {25, "Scuola"}  
    };
    
    // Mappa runtime: id_unity -> WaypointNode
    private Dictionary<int, WaypointNode> poiByUnityId = new Dictionary<int, WaypointNode>();
    
    // Mappa runtime: nome -> WaypointNode  
    private Dictionary<string, WaypointNode> poiByName = new Dictionary<string, WaypointNode>();
    
    void Awake()
    {
        // Singleton pattern
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }
        Instance = this;
    }
    
    void Start()
    {
        Initialize();
    }
    
    // Inizializza il registry cercando tutti i POI nella scena.
    public void Initialize()
    {
        poiByUnityId.Clear();
        poiByName.Clear();
        
        int foundCount = 0;
        int mappedCount = 0;
        
        // Cerca tutti i WaypointNode di tipo P.O.I
        foreach (var node in WaypointNode.AllNodes)
        {
            if (node.nodeType == WaypointNode.WaypointType.POI)
            {
                foundCount++;
                string nodeName = node.gameObject.name;
                
                if (!poiByName.ContainsKey(nodeName))
                {
                    poiByName[nodeName] = node;
                }

                foreach (var kvp in POI_NAMES)
                {
                    if (kvp.Value == nodeName)
                    {
                        poiByUnityId[kvp.Key] = node;
                        mappedCount++;

                        break;
                    }
                }
            }
        }
        
        if (debugLog)
        {
            Debug.Log($"[POI_REGISTRY] Inizializzazione completata: {foundCount} POI trovati, {mappedCount} mappati con id_unity");
            
            // Verifica POI mancanti
            foreach (var kvp in POI_NAMES)
            {
                if (!poiByUnityId.ContainsKey(kvp.Key))
                {
                    Debug.LogWarning($"[POI_REGISTRY] POI non trovato nella scena: id_unity={kvp.Key} ('{kvp.Value}')");
                }
            }
        }
    }
    
    // Ottiene un POI dal suo id_unity.
    public WaypointNode GetPOIByUnityId(int unityId)
    {
        if (poiByUnityId.TryGetValue(unityId, out WaypointNode node))
        {
            return node;
        }
        
        Debug.LogWarning($"[POI_REGISTRY] POI non trovato per id_unity={unityId}");
        return null;
    }
    
    // Ottiene un POI dal suo id_unity come stringa (per compatibilità con JSON).
    public WaypointNode GetPOIByUnityId(string unityIdStr)
    {
        if (int.TryParse(unityIdStr, out int unityId))
        {
            return GetPOIByUnityId(unityId);
        }
        
        Debug.LogWarning($"[POI_REGISTRY] Impossibile parsare id_unity: '{unityIdStr}'");
        return null;
    }
    
    // Ottiene un POI dal suo nome.
    public WaypointNode GetPOIByName(string name)
    {
        if (poiByName.TryGetValue(name, out WaypointNode node))
        {
            return node;
        }
        
        // Prova ricerca case-insensitive
        foreach (var kvp in poiByName)
        {
            if (kvp.Key.Equals(name, System.StringComparison.OrdinalIgnoreCase))
            {
                return kvp.Value;
            }
        }
        
        Debug.LogWarning($"[POI_REGISTRY] POI non trovato per nome: '{name}'");
        return null;
    }
    
    // Ottiene il nome del POI dal suo id_unity.
    public static string GetPOIName(int unityId)
    {
        if (POI_NAMES.TryGetValue(unityId, out string name))
        {
            return name;
        }
        return null;
    }
    

    public static bool IsValidUnityId(int unityId)
    {
        return POI_NAMES.ContainsKey(unityId);
    }
    
    // Ottiene tutti i POI mappati.
    public Dictionary<int, WaypointNode> GetAllMappedPOIs()
    {
        return new Dictionary<int, WaypointNode>(poiByUnityId);
    }
    
    // Conta i POI mappati.
    public int GetMappedCount()
    {
        return poiByUnityId.Count;
    }
}
