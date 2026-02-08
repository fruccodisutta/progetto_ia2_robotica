using UnityEngine;
using System.Collections.Generic;

public class AStarPathfinder : MonoBehaviour
{
    // Trova il percorso più breve tra due nodi (versione senza pesi zone).
    // Utile per il pickup del passeggero (non vengono usati moltiplicatori di zona).
    public static List<WaypointNode> FindPath(WaypointNode startNode, WaypointNode targetNode)
    {
        return FindPath(startNode, targetNode, null);
    }

    // Trova il percorso ottimale tra due nodi considerando i pesi delle zone.
    // I moltiplicatori vengono applicati al costo degli archi che ENTRANO in una zona.
    // Esempio: se centro_storico ha multiplier 2.5, attraversare un arco che arriva
    // nel centro storico costa 2.5 volte di più.
    public static List<WaypointNode> FindPath(
        WaypointNode startNode, 
        WaypointNode targetNode, 
        Dictionary<string, float> zoneMultipliers)
    {
        if (startNode == null || targetNode == null) return null;

        List<WaypointNode> openSet = new List<WaypointNode>();
        openSet.Add(startNode);

        Dictionary<WaypointNode, WaypointNode> cameFrom = new Dictionary<WaypointNode, WaypointNode>();
        Dictionary<WaypointNode, float> gScore = new Dictionary<WaypointNode, float>();
        gScore[startNode] = 0;

        Dictionary<WaypointNode, float> fScore = new Dictionary<WaypointNode, float>();
        fScore[startNode] = Vector3.Distance(startNode.transform.position, targetNode.transform.position);

        // Sicurezza anti-loop infinito: limitiamo le iterazioni
        int safetyLoopCount = 0;
        int maxLoops = 10000; 

        while (openSet.Count > 0)
        {
            safetyLoopCount++;
            if (safetyLoopCount > maxLoops)
            {
                Debug.LogError("A* Overflow: Il pathfinding ha superato il limite di iterazioni. Possibile loop infinito nel grafo.");
                return null;
            }

            WaypointNode current = GetLowestFScoreNode(openSet, fScore);

            if (current == targetNode)
            {
                return ReconstructPath(cameFrom, current);
            }

            openSet.Remove(current);

            // CONTROLLO DI SICUREZZA 1: Se outgoingConnections è null (non inizializzata), saltiamo
            if (current.outgoingConnections == null) continue;

            foreach (WaypointNode neighbor in current.outgoingConnections)
            {
                // Se un vicino è stato cancellato o è null, lo ignoriamo.
                if (neighbor == null || neighbor.isBlocked) 
                {
                    continue; 
                }

                // Se il vicino è un POI, deve essere escluso dal pathfinding
                // A MENO CHE non sia esso stesso la destinazione finale (targetNode).
                if (neighbor.nodeType == WaypointNode.WaypointType.POI && neighbor != targetNode)
                {
                    continue;
                }

                // Calcola il costo base (distanza euclidea)
                float baseCost = Vector3.Distance(current.transform.position, neighbor.transform.position);
                // Applica il moltiplicatore della zona del nodo DESTINAZIONE
                // Questo penalizza l'INGRESSO in zone problematiche
                float zoneMultiplier = GetZoneMultiplier(neighbor, zoneMultipliers);
                float weightedCost = baseCost * zoneMultiplier;

                // DEBUG: Log solo archi penalizzati (mult > 1)
                /* if (zoneMultiplier > 1.0f)
                {
                    Debug.Log($"[A*] PENALIZZATO: {current.name} → {neighbor.name} | zona={neighbor.parentZone?.kbZoneId} | x{zoneMultiplier} | costo: {baseCost:F1} → {weightedCost:F1}");
                } */

                float tentativeGScore = gScore[current] + weightedCost;

                if (!gScore.ContainsKey(neighbor) || tentativeGScore < gScore[neighbor])
                {
                    cameFrom[neighbor] = current;
                    gScore[neighbor] = tentativeGScore;
                    fScore[neighbor] = gScore[neighbor] + Vector3.Distance(neighbor.transform.position, targetNode.transform.position);

                    if (!openSet.Contains(neighbor))
                    {
                        openSet.Add(neighbor);
                    }
                }
            }
        }

        Debug.LogWarning("Percorso non trovato! (OpenSet esaurito)");
        return null;
    }

    // Ottiene il moltiplicatore per un nodo basato sulla sua zona.
    // Se il nodo non ha zona o non ci sono moltiplicatori, ritorna 1.0 (nessuna penalità).
    private static float GetZoneMultiplier(WaypointNode node, Dictionary<string, float> zoneMultipliers)
    {
        if (zoneMultipliers == null || zoneMultipliers.Count == 0)
            return 1.0f;

        if (node.parentZone == null)
            return 1.0f;

        string zoneId = node.parentZone.kbZoneId;
        if (string.IsNullOrEmpty(zoneId))
            return 1.0f;

        // Cerca il moltiplicatore per questa zona
        if (zoneMultipliers.TryGetValue(zoneId, out float multiplier))
        {
            return multiplier;
        }

        return 1.0f; // Zona non trovata = nessuna penalità
    }

    private static WaypointNode GetLowestFScoreNode(List<WaypointNode> openSet, Dictionary<WaypointNode, float> fScore)
    {
        WaypointNode lowest = openSet[0];
        float minScore = fScore.ContainsKey(lowest) ? fScore[lowest] : float.MaxValue;

        foreach (var node in openSet)
        {
            float score = fScore.ContainsKey(node) ? fScore[node] : float.MaxValue;
            if (score < minScore)
            {
                minScore = score;
                lowest = node;
            }
        }
        return lowest;
    }

    private static List<WaypointNode> ReconstructPath(Dictionary<WaypointNode, WaypointNode> cameFrom, WaypointNode current)
    {
        List<WaypointNode> totalPath = new List<WaypointNode>();
        totalPath.Add(current);

        while (cameFrom.ContainsKey(current))
        {
            current = cameFrom[current];
            totalPath.Add(current);
        }

        totalPath.Reverse();
        return totalPath;
    }

    public static WaypointNode GetClosestNode(Vector3 position)
    {
        // Usiamo la lista statica pre-caricata
        if (WaypointNode.AllNodes.Count == 0) 
        {
            Debug.LogWarning("Nessun WaypointNode trovato nella scena!");
            return null;
        }

        WaypointNode closest = null;
        float minDst = float.MaxValue;

        foreach (var node in WaypointNode.AllNodes)
        {
            if (node == null) continue; 

            // Ottimizzazione matematica: confrontare le distanze al quadrato (sqrMagnitude)
            // evita l'operazione costosa della radice quadrata (Mathf.Sqrt).
            float distSq = (node.transform.position - position).sqrMagnitude;
            
            if (distSq < minDst)
            {
                minDst = distSq;
                closest = node;
            }
        }
        return closest;
    }
}