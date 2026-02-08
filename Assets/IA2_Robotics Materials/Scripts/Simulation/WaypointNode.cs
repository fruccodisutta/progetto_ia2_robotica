using UnityEngine;
using System.Collections.Generic;

public class WaypointNode : MonoBehaviour
{
    //Tipologie del nodo
    public enum WaypointType { Road, POI, ChargingStation }

    [Header("Tipologia del nodo")]
    public WaypointType nodeType = WaypointType.Road;

    [Header("Stato Dinamico")]
    public bool isBlocked = false;

    // Utile per il pick-up dei passeggeri 
    public bool isStoppingAllowed = false;

    [Header("Safety Settings")]
    //Se TRUE, questo nodo non verrà MAI bloccato dal sistema RoadBlock (utile per le destinazioni del taxi)."
    public bool preventBlocking = false;

    // Questa lista contiene tutti i nodi attivi nella scena.
    public static List<WaypointNode> AllNodes = new List<WaypointNode>();
    public List<WaypointNode> outgoingConnections = new List<WaypointNode>();

    [Header("Zone Association")]
    public CityZone parentZone = null;
    
    [Header("Knowledge Base ID")]
    [Tooltip("ID del POI nel database Neo4j (deve corrispondere a id_unity in seed.py)")]
    public int poiId = -1; // -1 indica che non è stato configurato

    private void OnEnable()
    {
        // Appena il nodo si attiva, si aggiunge alla lista
        if (!AllNodes.Contains(this)) AllNodes.Add(this);
    }

    private void OnDisable()
    {
        // Se il nodo viene spento o distrutto, si rimuove
        AllNodes.Remove(this);
    }

    // Metodo Helper per visualizzare i nodi 
    private void OnDrawGizmos()
    {
        if (nodeType == WaypointType.ChargingStation) Gizmos.color = Color.green;
        else if (nodeType == WaypointType.POI) Gizmos.color = Color.magenta;
        else Gizmos.color = Color.cyan;
        Gizmos.DrawWireSphere(transform.position, 0.5f);

        Gizmos.color = Color.blue;
        foreach (var neighbor in outgoingConnections)
        {
            if (neighbor != null)
            {
                DrawArrow(transform.position, neighbor.transform.position);
            }
        }
    }

    // Metodo Helper per disegnare una freccia (ovvero l'arco direzionato fra i nodi)
    void DrawArrow(Vector3 start, Vector3 end)
    {
        Vector3 direction = (end - start).normalized;
        float distance = Vector3.Distance(start, end);
        
        if (distance < 0.1f) return;

        Gizmos.DrawLine(start, end);

        Vector3 arrowTip = end - (direction * 0.5f); 
        float arrowHeadLength = 1.0f;
        float arrowHeadAngle = 25.0f;

        Vector3 right = Quaternion.LookRotation(direction) * Quaternion.Euler(0, 180 + arrowHeadAngle, 0) * Vector3.forward;
        Vector3 left = Quaternion.LookRotation(direction) * Quaternion.Euler(0, 180 - arrowHeadAngle, 0) * Vector3.forward;

        Gizmos.DrawLine(arrowTip, arrowTip + right * arrowHeadLength);
        Gizmos.DrawLine(arrowTip, arrowTip + left * arrowHeadLength);
    }
}
