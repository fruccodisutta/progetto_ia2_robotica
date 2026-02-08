using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(CarMotor))]
public class CarBrain_Navigation : MonoBehaviour
{
    [Header("Settings")]
    public float reachThreshold = 2.0f;
    public float brakingDistance = 20.0f; 
    public float approachSpeed = 4.0f; 

    [Header("Steering Dynamics")]
    public float offsetSmoothTime = 0.5f;       // Tempo per completare il cambio corsia (smorzato)
    public float minLookAhead = 3.0f;           // Distanza minima di sguardo
    public float maxLookAhead = 15.0f;          // Distanza massima di sguardo (a velocità massima)
    public float curvatureSensitivity = 1.5f;   // Quanto "reagisce" alle curve (1.0 = normale, 2.0 = molto sensibile)

    // Variabile di stato per SmoothDamp
    private float offsetVelocity;

    // Stato Interno
    public WaypointCircuit Path { get; private set; }
    public int CurrentWaypointIndex { get; private set; }
    
    // Variabili Sterzo
    private float currentOffset = 0f;
    private float targetOffset = 0f;

    private CarMotor motor;
    private CarBrain brain;

    public void Initialize(CarBrain carBrain, CarMotor carMotor)
    {
        this.brain = carBrain;
        this.motor = carMotor;
    }

    // CurrentWaypointIndex rappresenta un indice sulla CURVA (curvedPath).
    // Questi helper convertono tra indice curva <-> indice waypoint del grafo.
    private int GetCurveSegmentSize()
    {
        if (Path == null) return 1;
        return Mathf.Max(1, (int)Path.smoothAmount);
    }

    private int CurveIndexToWaypointIndex(int curveIndex)
    {
        if (Path == null || Path.waypoints == null || Path.waypoints.Count == 0) return -1;
        int segmentSize = GetCurveSegmentSize();
        int idx = curveIndex / segmentSize;
        return Mathf.Clamp(idx, 0, Path.waypoints.Count - 1);
    }

    private int WaypointIndexToCurveIndex(int waypointIndex)
    {
        if (Path == null || Path.curvedPath == null || Path.curvedPath.Count == 0) return 0;
        int segmentSize = GetCurveSegmentSize();
        int idx = waypointIndex * segmentSize;
        return Mathf.Clamp(idx, 0, Path.curvedPath.Count - 1);
    }

    public void SetPath(List<Transform> newPathPoints)
    {
        if (Path == null) 
        { 
            GameObject routeObj = new GameObject("DynamicRoute"); 
            Path = routeObj.AddComponent<WaypointCircuit>(); 
        }
        // Trova la posizione corrente del taxi nel nuovo percorso
        // invece di resettare sempre a 0. Questo garantisce che il taxi continui
        // dalla sua posizione attuale seguendo il grafo, anche quando il percorso
        // viene ricalcolato (es. cambio meteo, destinazione, policy).
        int startWaypointIndex = 0;
        bool foundCurrentWaypoint = false;
        
        // Se abbiamo un path esistente, trova il waypoint corrente nel nuovo path
        if (Path.waypoints != null && Path.waypoints.Count > 0)
        {
            int currentWaypointIdx = CurveIndexToWaypointIndex(CurrentWaypointIndex);
            if (currentWaypointIdx >= 0 && currentWaypointIdx < Path.waypoints.Count)
            {
                Transform currentWaypoint = Path.waypoints[currentWaypointIdx];
            
            // Cerca questo waypoint nel nuovo path
            for (int i = 0; i < newPathPoints.Count; i++)
            {
                if (newPathPoints[i] == currentWaypoint)
                {
                    startWaypointIndex = i;
                    foundCurrentWaypoint = true;
                    Debug.Log($"[NAVIGATION] Ricalcolo percorso: continuo dal waypoint corrente '{currentWaypoint.name}' (indice {i})");
                    break;
                }
            }
            
            // Fallback
            // Se non troviamo il waypoint corrente nel nuovo path, cerca il più vicino
            // (es. quando il percorso cambia completamente per la pioggia)
            if (!foundCurrentWaypoint && brain != null)
            {
                float minDist = float.MaxValue;
                for (int i = 0; i < newPathPoints.Count; i++)
                {
                    float dist = Vector3.Distance(brain.transform.position, newPathPoints[i].position);
                    if (dist < minDist)
                    {
                        minDist = dist;
                        startWaypointIndex = i;
                    }
                }
                
                Debug.Log($"[NAVIGATION] Ricalcolo percorso: waypoint corrente '{currentWaypoint.name}' non trovato nel nuovo path, uso il nodo più vicino: '{newPathPoints[startWaypointIndex].name}' (indice {startWaypointIndex})");
            }
            }
        }

        Path.waypoints = newPathPoints;
        Path.loop = false; 
        Path.CachePath(); 
        // CurrentWaypointIndex è un indice sulla CURVA: convertiamo dall'indice waypoint.
        CurrentWaypointIndex = WaypointIndexToCurveIndex(startWaypointIndex);
        currentOffset = 0; 
        targetOffset = 0; 
    }

    public void SetTargetOffset(float offset)
    {
        targetOffset = offset;
    }

    public bool HasPath() => Path != null && Path.curvedPath.Count > 0;

    public void UpdateSteering()
    {
        if (!HasPath()) return;

        // GESTIONE OFFSET (SMOOTH DAMPING)
        // Calcoliamo se stiamo "Uscendo" (offset aumenta) o "Rientrando" (offset diminuisce verso 0)
        bool isReturningToCenter = Mathf.Abs(targetOffset) < Mathf.Abs(currentOffset);

        // Se stiamo rientrando, usiamo un tempo molto più lungo (dolcezza)
        // Se stiamo scartando un ostacolo, usiamo il tempo normale (reattività)
        float dynamicSmoothTime = isReturningToCenter ? offsetSmoothTime * 2.0f : offsetSmoothTime;

        // Applichiamo SmoothDamp con il tempo dinamico
        currentOffset = Mathf.SmoothDamp(currentOffset, targetOffset, ref offsetVelocity, dynamicSmoothTime);

        // ANALISI CURVATURA
        // Calcoliamo quanto la strada curva nei prossimi metri
        float upcomingCurvature = GetUpcomingCurvature(10.0f); // Analizza i prossimi 10 metri

        // CALCOLO LOOK-AHEAD ADATTIVO
        // Base: andiamo veloci -> guardiamo lontano
        float speedRatio = Mathf.Clamp01(Mathf.Abs(motor.CurrentSpeed) / motor.maxSpeed);
        float speedBasedLookAhead = Mathf.Lerp(minLookAhead, maxLookAhead, speedRatio);

        // Se c'è una curva, ignoriamo la velocità e guardiamo vicino
        // Lerp tra "Sguardo da Velocità" e "Sguardo Minimo" basato sulla curvatura
        float finalLookAhead = Mathf.Lerp(speedBasedLookAhead, minLookAhead, upcomingCurvature * curvatureSensitivity);
        
        finalLookAhead = Mathf.Max(finalLookAhead, minLookAhead);

        // Debug Visivo
        Vector3 rawTargetPos = GetLookAheadPosition(finalLookAhead);
        Debug.DrawLine(brain.transform.position, rawTargetPos, Color.yellow); 

        // CALCOLO TARGET FINALE
        Vector3 nextPosSample = Path.GetRoutePosition(GetLookAheadIndex(finalLookAhead + 1.0f));
        Vector3 roadDirection = (nextPosSample - rawTargetPos).normalized;
        if (roadDirection == Vector3.zero) roadDirection = brain.transform.forward;

        Vector3 rightVector = Vector3.Cross(Vector3.up, roadDirection).normalized;
        Vector3 finalTargetPos = rawTargetPos + (rightVector * currentOffset);

        // STERZATA
        Vector3 targetDir = finalTargetPos - brain.transform.position;
        float targetAngle = Vector3.SignedAngle(brain.transform.forward, targetDir, Vector3.up);
        targetAngle = Mathf.Clamp(targetAngle, -motor.maxSteerAngle, motor.maxSteerAngle);

        motor.SetSteerAngle(Mathf.Lerp(motor.CurrentSteerAngle, targetAngle, Time.deltaTime * motor.steeringSpeed));

        // AVANZAMENTO WAYPOINT
        CheckWaypointProgress();
    }

    // Calcola "quanto curva" la strada
    private float GetUpcomingCurvature(float scanDistance)
    {
        if (Path.curvedPath.Count < 3) return 0f;

        // Prendiamo il vettore della strada ADESSO
        Vector3 currentDir = (Path.GetRoutePosition(CurrentWaypointIndex + 1) - Path.GetRoutePosition(CurrentWaypointIndex)).normalized;
        
        // Prendiamo il vettore della strada PIÙ AVANTI
        int futureIndex = GetLookAheadIndex(scanDistance);
        Vector3 futureDir = (Path.GetRoutePosition(futureIndex + 1) - Path.GetRoutePosition(futureIndex)).normalized;

        // Calcoliamo l'angolo tra i due vettori (0 = dritto, 90 = curva secca)
        float angle = Vector3.Angle(currentDir, futureDir);

        // Normalizziamo: assumiamo che 45 gradi sia una "curva massima" per il nostro range
        return Mathf.Clamp01(angle / 45.0f);
    }

    // Helper per trovare la posizione futura sulla curva senza alterare l'indice attuale
    private Vector3 GetLookAheadPosition(float distance)
    {
        // Stima semplice: prendiamo il punto corrente e cerchiamo in avanti
        // Nota: Per una precisione assoluta servirebbe calcolare la distanza sulla spline,
        // ma per un comportamento "morbido" basta scorrere i waypoint.
        float accumulatedDist = 0f;
        int lookAheadIndex = CurrentWaypointIndex;
        Vector3 lastPoint = Path.GetRoutePosition(lookAheadIndex);

        // Scansioniamo in avanti finché non copriamo la "distance" richiesta
        while (accumulatedDist < distance && lookAheadIndex < Path.curvedPath.Count - 1)
        {
            lookAheadIndex++;
            Vector3 nextPoint = Path.GetRoutePosition(lookAheadIndex);
            accumulatedDist += Vector3.Distance(lastPoint, nextPoint);
            lastPoint = nextPoint;
        }
        
        return lastPoint;
    }

    // Helper necessario per calcolare la direzione della strada al punto di lookahead
    private int GetLookAheadIndex(float distance)
    {
        float accumulatedDist = 0f;
        int index = CurrentWaypointIndex;
        Vector3 lastPoint = Path.GetRoutePosition(index);
        while (accumulatedDist < distance && index < Path.curvedPath.Count - 1)
        {
            index++;
            Vector3 nextPoint = Path.GetRoutePosition(index);
            accumulatedDist += Vector3.Distance(lastPoint, nextPoint);
            lastPoint = nextPoint;
        }
        return index;
    }

    // Logica di avanzamento estratta per pulizia
    private void CheckWaypointProgress()
    {
        Vector3 rawTargetPos = Path.GetRoutePosition(CurrentWaypointIndex);
        Vector3 nextPos = Path.GetRoutePosition(CurrentWaypointIndex + 1);
        Vector3 roadDirection = (nextPos - rawTargetPos).normalized;
        
        Vector3 vectorToCar = brain.transform.position - rawTargetPos;
        float distanceAlongRoad = Vector3.Dot(vectorToCar, roadDirection); // Proiezione

        // Se abbiamo superato il punto, avanziamo
        if (distanceAlongRoad > 0 || Vector3.Distance(brain.transform.position, rawTargetPos) < reachThreshold)
        {
            CurrentWaypointIndex++;
            if (CurrentWaypointIndex >= Path.curvedPath.Count)
            {
                if (Path.loop) CurrentWaypointIndex = 0;
            }
        }
    }

    // LOGICA PREDITTIVA CURVA
    public float GetPredictiveCornerFactor()
    {
        if (!HasPath() || Path.curvedPath.Count < 2) return 1.0f;

        float accumulatedDist = 0f;
        if (CurrentWaypointIndex < Path.curvedPath.Count)
        {
            accumulatedDist = Vector3.Distance(brain.transform.position, Path.GetRoutePosition(CurrentWaypointIndex));
        }

        int scanLimit = Mathf.Min(CurrentWaypointIndex + 10, Path.curvedPath.Count - 2);

        for (int i = CurrentWaypointIndex; i < scanLimit; i++)
        {
            if (accumulatedDist > brakingDistance) return 1.0f;

            Vector3 pCurr = Path.GetRoutePosition(i);
            Vector3 pNext = Path.GetRoutePosition(i + 1);
            Vector3 pAfter = Path.GetRoutePosition(i + 2);

            Vector3 v1 = (pNext - pCurr).normalized;
            Vector3 v2 = (pAfter - pNext).normalized;

            if (Vector3.Angle(v1, v2) > 45.0f)
            {
                return Mathf.Clamp01(accumulatedDist / brakingDistance);
            }
            accumulatedDist += Vector3.Distance(pNext, pAfter);
        }
        return 1.0f;
    }

    public bool IsPathComplete 
    {
        get 
        {
            if (Path == null || Path.curvedPath.Count == 0) return true;
            // Se non è un loop e l'indice ha raggiunto la fine
            if (!Path.loop && CurrentWaypointIndex >= Path.curvedPath.Count - 1) return true;
            return false;
        }
    }

    // Ottiene il waypoint attuale del path (per usarlo come start node in ricalcoli).
    // Garantisce che i ricalcoli seguano la topologia del grafo.
    public WaypointNode GetCurrentWaypointNode()
    {
        if (!HasPath() || Path.curvedPath.Count == 0) return null;
        int waypointIdx = CurveIndexToWaypointIndex(CurrentWaypointIndex);
        if (waypointIdx < 0 || waypointIdx >= Path.waypoints.Count) return null;
        
        // Ritorna il Transform come WaypointNode
        return Path.waypoints[waypointIdx].GetComponent<WaypointNode>();
    }

    // Restituisce l'indice del waypoint del grafo corrispondente all'indice curva corrente.
    public int GetCurrentWaypointRawIndex()
    {
        if (!HasPath() || Path.waypoints == null || Path.waypoints.Count == 0) return -1;
        return CurveIndexToWaypointIndex(CurrentWaypointIndex);
    }

    // Ottiene il prossimo waypoint valido (non-POI) nel path corrente.
    // Usato quando il current waypoint è un nodo POI e non può essere usato per pathfinding.
    public WaypointNode GetNextNonPOIWaypoint()
    {
        if (!HasPath() || Path.waypoints == null || Path.waypoints.Count == 0) return null;
        
        int currentWaypointIdx = CurveIndexToWaypointIndex(CurrentWaypointIndex);
        if (currentWaypointIdx < 0) return null;
        
        // Cerca dal currentIndex + 1 in avanti
        for (int i = currentWaypointIdx + 1; i < Path.waypoints.Count; i++)
        {
            var node = Path.waypoints[i].GetComponent<WaypointNode>();
            if (node != null && node.nodeType != WaypointNode.WaypointType.POI)
            {
                return node;
            }
        }
        
        return null;
    }

    // Imposta manualmente il CurrentWaypointIndex.
    // Usata solo se necessaria
    public void SetCurrentWaypointIndex(int index)
    {
        if (Path == null || Path.waypoints == null)
        {
            Debug.LogWarning("[NAVIGATION] Impossibile impostare CurrentWaypointIndex: nessun path attivo");
            return;
        }

        int clampedWaypointIndex = Mathf.Clamp(index, 0, Path.waypoints.Count - 1);
        CurrentWaypointIndex = WaypointIndexToCurveIndex(clampedWaypointIndex);
        Debug.Log($"[NAVIGATION] CurrentWaypointIndex impostato manualmente (waypoint={clampedWaypointIndex} -> curveIndex={CurrentWaypointIndex})");
    }

    // Metodo per resettare il path
    public void ClearPath()
    {
        if (Path != null)
        {
            Path.waypoints = new List<Transform>(); // Svuota i waypoint
            Path.curvedPath.Clear(); // Svuota la curva cacheata
        }
        targetOffset = 0f;
        currentOffset = 0f;
    }

    // Calcola la distanza rimanente del percorso corrente
    public float CalculateRemainingPathDistance()
    {
        if (Path == null || Path.waypoints == null || Path.waypoints.Count == 0) return 0f;
        
        int waypointIdx = CurveIndexToWaypointIndex(CurrentWaypointIndex);
        if (waypointIdx < 0 || waypointIdx >= Path.waypoints.Count) return 0f;

        float dist = 0f;
        // Distanza dal veicolo al waypoint corrente target
        dist += Vector3.Distance(brain.transform.position, Path.waypoints[waypointIdx].position);
        
        // Somma le distanze tra i restanti waypoint
        for (int i = waypointIdx; i < Path.waypoints.Count - 1; i++)
        {
            dist += Vector3.Distance(Path.waypoints[i].position, Path.waypoints[i + 1].position);
        }
        
        return dist;
    }
}
