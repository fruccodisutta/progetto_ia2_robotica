using UnityEngine;
using System.Collections.Generic;

//Classe helper per gestire un circuito "naturale" di waypoint che definisce una strada o percorso.
//Usata principalmente per il movimento fluido delle auto lungo le strade.

public class WaypointCircuit : MonoBehaviour
{
    // I nodi che rappresentano il centro della corsia
    public List<Transform> waypoints = new List<Transform>();

    // Questa è la lista che contiene i punti della curva calcolata
    [HideInInspector]
    public List<Vector3> curvedPath = new List<Vector3>();
    
    [Header("Impostazioni Curva")]
    public bool loop = true;
    public float smoothAmount = 15f; 

    [Header("Tensione delle splines")]
    public float tension;

    private void Awake()
    {
        // Appena parte la simulazione, calcoliamo la curva
        CachePath();
    }

    // Calcola i punti intermedi per fare la curva dolce
    public void CachePath()
    {
        if (waypoints.Count < 2) return;
        curvedPath.Clear();

        int count = waypoints.Count;
        if (loop) count++; 

        for (int i = 0; i < count - 1; i++)
        {
            Vector3 p0 = waypoints[ClampIndex(i - 1)].position;
            Vector3 p1 = waypoints[ClampIndex(i)].position;
            Vector3 p2 = waypoints[ClampIndex(i + 1)].position;
            Vector3 p3 = waypoints[ClampIndex(i + 2)].position;

            int segments = (int)smoothAmount;
            for (int t = 0; t < segments; t++)
            {
                float k = t / (float)segments;
                Vector3 pos = GetCatmullRomPosition(k, p0, p1, p2, p3);
                curvedPath.Add(pos);
            }
        }
    }

    Vector3 GetCatmullRomPosition(float t, Vector3 p0, Vector3 p1, Vector3 p2, Vector3 p3)
    {
        // (0.0 = Morbido, 0.5 = Normale, 0.8 = Stretto/Reale)
        float tension = 0.8f; 

        // Matematica ottimizzata per Cardinal Spline (che include la tensione)
        Vector3 m1 = (1f - tension) * (p2 - p0) / 2f; // Tangente P1
        Vector3 m2 = (1f - tension) * (p3 - p1) / 2f; // Tangente P2

        float t2 = t * t;
        float t3 = t2 * t;

        // Polinomio di Hermite (Equazione standard per curve controllabili)
        Vector3 pos =
            (2f * t3 - 3f * t2 + 1f) * p1 +
            (t3 - 2f * t2 + t) * m1 +
            (-2f * t3 + 3f * t2) * p2 +
            (t3 - t2) * m2;

        return pos;
    }

    int ClampIndex(int i)
    {
        if (waypoints.Count == 0) return 0;
        if (loop)
        {
            if (i < 0) return waypoints.Count + i;
            return i % waypoints.Count;
        }
        else
        {
            return Mathf.Clamp(i, 0, waypoints.Count - 1);
        }
    }

    // Metodo helper per ottenere la posizione esatta dalla curva calcolata
    public Vector3 GetRoutePosition(int index)
    {
        if (curvedPath.Count == 0) return Vector3.zero;
        return curvedPath[index % curvedPath.Count];
    }

    // Trova l'indice del punto della curva più vicino alla tua posizione
    public int GetClosestWaypointIndex(Vector3 position)
    {
        float minDst = float.MaxValue;
        int closestIndex = 0;

        // Se abbiamo calcolato la curva usiamo quella, altrimenti i waypoint grezzi
        int count = curvedPath.Count > 0 ? curvedPath.Count : waypoints.Count;

        for (int i = 0; i < count; i++)
        {
            Vector3 pointPos = curvedPath.Count > 0 ? curvedPath[i] : waypoints[i].position;
            float dst = Vector3.Distance(position, pointPos);
            if (dst < minDst)
            {
                minDst = dst;
                closestIndex = i;
            }
        }
        return closestIndex;
    }

    private void OnDrawGizmos()
    {
        // Linea Gialla: Connessione diretta tra i waypoint
        if (waypoints.Count > 1)
        {
            Gizmos.color = Color.yellow;
            for (int i = 0; i < waypoints.Count - 1; i++)
            {
                 if (waypoints[i] != null && waypoints[i+1] != null)
                    Gizmos.DrawLine(waypoints[i].position, waypoints[i+1].position);
            }
               
            if (loop && waypoints.Count > 1 && waypoints[0] != null && waypoints[waypoints.Count -1] != null)
                Gizmos.DrawLine(waypoints[waypoints.Count - 1].position, waypoints[0].position);
        }

        // Linea Rossa: La curva effettiva che l'auto seguirà
        if (curvedPath.Count > 0)
        {
            Gizmos.color = Color.red;
            for (int i = 0; i < curvedPath.Count - 1; i++)
                Gizmos.DrawLine(curvedPath[i], curvedPath[i + 1]);
                
            if (loop)
                Gizmos.DrawLine(curvedPath[curvedPath.Count - 1], curvedPath[0]);
        }
    }
}