using UnityEngine;
using System.Collections.Generic;

// Rapprenta una zona della città con proprietà specifiche per la simulazione
[RequireComponent(typeof(BoxCollider))]
public class CityZone : MonoBehaviour
{
    [System.Serializable]
    public enum ZoneType
    {
        Residential,      // Zona residenziale
        Industrial,       // Zona industriale
        Suburban,         // Periferia
        Center            // Zona centrale
    }

    [Header("Zone Identity")]
    // Nome della zona (es. "Centro Storico", "Quartiere Residenziale")
    public string zoneName = "Unnamed Zone";
    // Tipo di zona (residenziale di default)
    public ZoneType zoneType = ZoneType.Residential;

    [Header("Waypoints Association")]
    // Lista dei waypoints associati alla zona
    public List<WaypointNode> associatedWaypoints = new List<WaypointNode>();

    [Header("Knowledge Base ID")]
    // ID della zona (utile per query al Knowledge Graph Neo4j)
    public string kbZoneId = "";
    
    // Metadata aggiuntivi in formato JSON per query al Knowledge Graph
    [TextArea(3, 10)]
    public string kbMetadata = "{}";

    [Header("Visualization")]
    // Debug: colore gizmo per la visualizzazione della zona
    public Color gizmoColor = new Color(0.3f, 0.6f, 1.0f, 0.3f);

    private BoxCollider zoneCollider;

    void Awake()
    {
        zoneCollider = GetComponent<BoxCollider>();
        
        // Le zone funzionano con dei semplici collider trigger (non ci sono collissioni vere e proprie)
        if (!zoneCollider.isTrigger)
        {
            Debug.LogWarning($"[CityZone] '{zoneName}': Il BoxCollider non è impostato come Trigger. Impostazione automatica in corso...");
            zoneCollider.isTrigger = true;
        }
    }

    void Start()
    {
        Debug.Log($"[CityZone] Zona '{zoneName}' ({zoneType}) inizializzata");
    }

    // Restituisce informazioni formattate sulla zona
    public string GetZoneInfo()
    {
        return $"{zoneName} ({zoneType}) - Waypoints: {associatedWaypoints.Count}";
    }

    // Controlla se la zona contiene un dato waypoint
    public bool ContainsWaypoint(WaypointNode waypoint)
    {
        return associatedWaypoints.Contains(waypoint);
    }

    // Associa automaticamente i waypoints all'interno del collider della zona (si può usare direttamente nell'editor)
    [ContextMenu("Auto-Associate Waypoints nel collider")]
    public void AutoAssociateWaypoints()
    {
        associatedWaypoints.Clear();
        
        BoxCollider collider = GetComponent<BoxCollider>();
        if (collider == null)
        {
            Debug.LogError($"[CityZone] '{zoneName}': Nessun BoxCollider associato alla zona!");
            return;
        }
        
        // Debug: Controllo se i waypoint sono registrati in AllNodes
        Debug.Log($"[CityZone] '{zoneName}': Controllo WaypointNode.AllNodes count: {(WaypointNode.AllNodes != null ? WaypointNode.AllNodes.Count : 0)}");
        
        // Calcolo dei waypoint all'interno della zona
        WaypointNode[] waypoints;
        
        if (WaypointNode.AllNodes != null && WaypointNode.AllNodes.Count > 0)
        {
            waypoints = WaypointNode.AllNodes.ToArray();
            Debug.Log($"[CityZone] '{zoneName}': Using WaypointNode.AllNodes ({waypoints.Length} waypoints)");
        }
        else
        {
            waypoints = FindObjectsByType<WaypointNode>(FindObjectsSortMode.None);
            Debug.Log($"[CityZone] '{zoneName}': WaypointNode.AllNodes vuoto, uso FindObjectsByType: ({waypoints.Length} waypoints trovati)");
        }
        
        if (waypoints.Length == 0)
        {
            Debug.LogWarning($"[CityZone] '{zoneName}': Nessun waypoint trovato nella scena!");
            return;
        }
        
        // Get collider
        Bounds bounds = collider.bounds;
        
        // Controlla tutti i waypoints
        int checkedCount = 0;
        foreach (var waypoint in waypoints)
        {
            if (waypoint == null) continue;
            
            checkedCount++;
            Vector3 waypointPos = waypoint.transform.position;
            
            if (bounds.Contains(waypointPos))
            {
                associatedWaypoints.Add(waypoint);
                
                // Imposta la relazione bidirezionale waypoint → zona
                waypoint.parentZone = this;
                
                Debug.Log($"[CityZone] '{zoneName}': Aggiunto il waypoint '{waypoint.name}' in posizione {waypointPos}");
            }
            else
            {
                if (checkedCount <= 3)
                {
                    float distance = Vector3.Distance(bounds.center, waypointPos);
                    Debug.Log($"[CityZone] '{zoneName}': Waypoint '{waypoint.name}' fuori dai bounds. Distanza dal centro: {distance:F2}m");
                }
            }
        }
        
        Debug.Log($"[CityZone] '{zoneName}': Controllati {checkedCount} waypoints, auto-associati {associatedWaypoints.Count} waypoints");
    }

    /* void OnDrawGizmos()
    {
        BoxCollider col = GetComponent<BoxCollider>();
        if (col == null) return;

        // Draw zone bounds
        Gizmos.color = gizmoColor;
        Matrix4x4 oldMatrix = Gizmos.matrix;
        Gizmos.matrix = transform.localToWorldMatrix;
        Gizmos.DrawCube(col.center, col.size);
        Gizmos.matrix = oldMatrix;

        // Draw zone outline
        Gizmos.color = new Color(gizmoColor.r, gizmoColor.g, gizmoColor.b, 1f);
        Gizmos.DrawWireCube(transform.position + col.center, col.size);

        #if UNITY_EDITOR
        // Draw zone label
        UnityEditor.Handles.Label(
            transform.position + Vector3.up * (col.size.y / 2 + 2f),
            $"{zoneName}\n({zoneType})",
            new GUIStyle()
            {
                alignment = TextAnchor.MiddleCenter,
                normal = new GUIStyleState() { textColor = Color.white }
            }
        );
        #endif
    } */

    /* void OnDrawGizmosSelected()
    {
        if (associatedWaypoints.Count > 0)
        {
            Gizmos.color = Color.yellow;
            foreach (var waypoint in associatedWaypoints)
            {
                if (waypoint != null)
                {
                    Gizmos.DrawLine(transform.position, waypoint.transform.position);
                    Gizmos.DrawWireSphere(waypoint.transform.position, 1f);
                }
            }
        }
    } */
}
