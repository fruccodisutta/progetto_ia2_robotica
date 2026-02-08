using UnityEngine;

public class CarBrain_Perception : MonoBehaviour
{
    [Header("--- Sensori & Percezione ---")]
    public float safetyDistance = 7.0f;
    public float stopDistance = 4.0f;    
    public float pedestrianSafetyDistance = 10.0f;
    public float poleSafetyDistance = 3.0f; 
    public float trafficCarSafetyDistance = 6.0f; 

    [Header("--- Tuning Sensibilità Pedoni ---")]
    public float pedestrianSightDistance = 20.0f; 
    public float pedestrianLateralMargin;
    public float pedestrianSafetyMargin = 1.0f; 
    // Margine laterale ridotto durante pickup (quando taxi si avvicina al marciapiede per prendere il passeggero)
    public float pickupLateralMargin = 0.5f;
    // Distanza minima consentita quando ci avviciniamo al NOSTRO passeggero
    public float pickupProximityLimit = 2.5f;

    // Riferimento al passeggero target
    private Transform currentPassengerTarget = null;
    // Flag che indica se ci stiamo avvicinando al pickup (taxi si stringe a destra, simulando un pick up naturale)
    private bool isApproachingPickup = false;

    [Header("--- Avoidance Ostacoli ---")]
    public float overtakeOffset;

    [Header("--- Sicurezza Sorpasso ---")]
    public bool isTwoWayRoad = true; 
    public float oncomingCheckDistance = 25.0f; 
    public LayerMask dynamicObstacleLayer; 
    public float laneWidth = 3.5f;

    // Output per il Brain
    public float targetOffset { get; private set; }
    public bool isWaitingForNewPath { get; private set; } // Blocca tutto se stiamo ricalcolando

    // Variabile di stato per la memoria del sorpasso
    private float overtakeMemoryTimer = 0f;
    // Tempo minimo che rimaniamo a sinistra dopo aver visto l'ultimo ostacolo
    private float keepOvertakeActiveTime = 1.2f;

    // Eventi
    public System.Action<WaypointNode> OnRoadBlockDetected;

    // Cache e Timer
    private float roadBlockCooldown = 0f; 
    private float safetyHysteresis = 0f;

    private SimpleLidar lidar;
    private CarBrain brain;
    private CarBrain_Navigation navigation; // Serve per analizzare i segmenti stradali (RoadBlock)
    private CarMotor motor;

    // Variabile per il debug visivo nella scena
    private WaypointNode debugLastBlockedNode = null;

    public void Initialize(CarBrain b, CarMotor m, SimpleLidar l, CarBrain_Navigation n)
    {
        brain = b;
        motor = m;
        lidar = l;
        navigation = n;
    }

    public void UpdateTimers(float deltaTime)
    {
        if (roadBlockCooldown > 0f) roadBlockCooldown -= deltaTime;
    }

    public void SetWaitingForPath(bool waiting)
    {
        isWaitingForNewPath = waiting;
    }

    public float CalculateSpeedBasedOnEnvironment(float currentMaxSpeed, bool isTrafficRestricted, out bool isIntentionalStop)
    {
        isIntentionalStop = false;
        float desiredSpeed = currentMaxSpeed;
        
        // Se stiamo aspettando il navigatore, stiamo fermi.
        if (isWaitingForNewPath) {
            isIntentionalStop = true;
            targetOffset = 0f;
            return 0f; 
        }

        if (lidar == null) return desiredSpeed;

        // 1. GESTIONE PEDONI
        var pedInfo = lidar.PedestrianSector;
        if (pedInfo.IsValid)
        {
            bool isRelevant = true;

            // Usa margine ridotto durante pickup (quando taxi si stringe a destra)
            float effectiveLateralMargin = isApproachingPickup ? pickupLateralMargin : pedestrianLateralMargin;
            float maxLateralDist = (laneWidth * 0.5f) + effectiveLateralMargin;

            // Se il pedone è più esterno di questo limite, lo ignoriamo (è al sicuro sul marciapiede)
            if (Mathf.Abs(pedInfo.LocalPoint.x) > maxLateralDist) 
            {
                isRelevant = false;
            }

            // FILTRO STERZATA (Curve Strette)
            // Se stiamo curvando forte, ignoriamo i pedoni che si trovano all'esterno della curva
            if (isRelevant)
            {
                float steer = motor.CurrentSteerAngle;
                if (steer < -10f && pedInfo.LocalPoint.x > 0.5f) isRelevant = false;
                else if (steer > 10f && pedInfo.LocalPoint.x < -0.5f) isRelevant = false;
            }
            

            if (isRelevant)
            {
                float pedDist = pedInfo.Distance;
                
                // CALCOLO SOGLIA DINAMICA
                float effectiveStopThreshold = stopDistance + pedestrianSafetyMargin;

                // ECCEZIONE PASSEGGERO:
                // Se l'oggetto visto è il nostro passeggero, riduciamo la soglia per permettere l'accostamento!
                if (currentPassengerTarget != null && pedInfo.ObjectDetected.transform == currentPassengerTarget)
                {
                    // Usiamo una soglia molto più bassa (es. 2.5m)
                    effectiveStopThreshold = pickupProximityLimit;
                }

                if (pedDist < effectiveStopThreshold)
                {
                    isIntentionalStop = true;
                    return 0f; // STOP ASSOLUTO
                }
                else 
                {
                    // Rallentamento progressivo
                    float factor = (pedDist - effectiveStopThreshold) / (pedestrianSightDistance - effectiveStopThreshold);
                    desiredSpeed = Mathf.Min(desiredSpeed, motor.maxSpeed * Mathf.Clamp01(factor));
                }
            }
        }

        // 2. GESTIONE OSTACOLI & SORPASSO 
        var frontInfo = lidar.FrontSector;
        bool obstacleDetected = false;
        GameObject obstacle = null;
        float rawDist = float.MaxValue;

        if (frontInfo.IsValid)
        {
            obstacle = frontInfo.ObjectDetected;
            rawDist = frontInfo.Distance;
            obstacleDetected = true;
        }

        // Variabile locale per decidere se in QUESTO frame vogliamo sorpassare
        bool requestOvertake = false;

        if (obstacleDetected)
        {
            // LOGICA ROADBLOCK (BARRIERA CHE BLOCCA LA STRADA)
            if (obstacle.CompareTag(CarConstants.TAG_ROADBLOCK) && roadBlockCooldown <= 0f)
            {
                if (rawDist < 10.0f)
                {
                    isIntentionalStop = true;
                    HandleRoadBlockDetection(obstacle);
                    return 0f;
                }
            }

            // DISTANZA DI SICUREZZA
            float currentSafetyThreshold = safetyDistance;
            if (obstacle.CompareTag(CarConstants.TAG_IMMUTABLE))
                currentSafetyThreshold = poleSafetyDistance;
            else if (obstacle.CompareTag(CarConstants.TAG_TRAFFIC_CAR))
                currentSafetyThreshold = trafficCarSafetyDistance;
            else if (obstacle.CompareTag(CarConstants.TAG_PEDESTRIAN))
                currentSafetyThreshold = pedestrianSafetyDistance;

            // Controlliamo se siamo bloccati (troppo vicini)
            bool isBlocked = rawDist < (currentSafetyThreshold + safetyHysteresis);
            
            // Se siamo bloccati, valutiamo il sorpasso O la frenata
            if (isBlocked) 
            {
                // LOGICA SORPASSO
                safetyHysteresis = 1.0f;

                bool sideFree = IsLeftSpaceFreeByLidar();
                bool oncomingFree = IsOncomingTrafficClear();
                bool forbiddenByRedLight = isTrafficRestricted && obstacle.CompareTag(CarConstants.TAG_TRAFFIC_CAR);

                bool canOvertake = sideFree && oncomingFree && 
                                !obstacle.CompareTag(CarConstants.TAG_ROADBLOCK) && 
                                !obstacle.CompareTag(CarConstants.TAG_PEDESTRIAN) &&
                                !obstacle.CompareTag(CarConstants.TAG_TRAFFIC_CAR) &&
                                !forbiddenByRedLight;

                if (canOvertake)
                {
                    requestOvertake = true;

                    float physicalLimit = 1f; 
                    if (rawDist > physicalLimit)
                    {
                        if (rawDist < stopDistance) desiredSpeed = 2.0f; // Manovra
                        else desiredSpeed = desiredSpeed * 0.6f;
                    }
                    else return 0f; // Troppo vicini
                }
                else
                {
                    // Non possiamo sorpassare -> Freniamo
                    float slowdownFactor = (rawDist - stopDistance) / (currentSafetyThreshold - stopDistance);
                    desiredSpeed = Mathf.Min(desiredSpeed, motor.maxSpeed * Mathf.Clamp01(slowdownFactor));
                    
                    // Se siamo troppo vicini e non possiamo sorpassare, stop
                    if (rawDist < stopDistance) 
                    {
                        isIntentionalStop = true; 
                        return 0f; 
                    }
                }
            }
        }
        
        if (requestOvertake)
        {
            overtakeMemoryTimer = keepOvertakeActiveTime;
        }
        else
        {
            // Se la strada è libera oppure non possiamo sorpassare, il timer scende
            if (overtakeMemoryTimer > 0) overtakeMemoryTimer -= Time.deltaTime;
        }

        // Applichiamo l'offset in base al timer
        if (overtakeMemoryTimer > 0)
        {
            // Rimaniamo a sinistra
            targetOffset = -overtakeOffset; 
            safetyHysteresis = 1.0f; // Manteniamo l'isteresi attiva per evitare frenate brusche
        }
        else
        {
            // Solo quando la memoria è scaduta torniamo al centro
            targetOffset = 0f;
            safetyHysteresis = 0f;
        }

        return desiredSpeed;
    }

    // Metodi HELPER
    private void HandleRoadBlockDetection(GameObject obstacle)
    {
        Debug.LogWarning($"[PERCEPTION] Barriera rilevata! Analisi...");
        
        RoadBlock blockScript = obstacle.GetComponent<RoadBlock>();
        if (blockScript != null) blockScript.TriggerDisappearance();

        WaypointNode nodeToBlock = null;

        if (navigation.HasPath())
        {
            var waypoints = navigation.Path.waypoints;
            for (int i = 1; i < waypoints.Count; i++)
            {
                Transform prevT = waypoints[i - 1];
                Transform currT = waypoints[i];
                WaypointNode candidate = currT.GetComponent<WaypointNode>();

                if (candidate != null && IsProtectedNode(candidate)) continue;

                if (IsBarrierOnSegment(obstacle.transform.position, prevT.position, currT.position, 4.0f))
                {
                   nodeToBlock = candidate;
                   break;
                }
            }
        }

        // Fallback
        if (nodeToBlock == null)
        {
            WaypointNode closest = AStarPathfinder.GetClosestNode(obstacle.transform.position);
            if (closest != null && !IsProtectedNode(closest)) nodeToBlock = closest;
        }

        if (nodeToBlock != null)
        {

            Debug.LogWarning($"[PERCEPTION] Barriera rilevata su: '{nodeToBlock.name}'. Invio richiesta blocco.");
            debugLastBlockedNode = nodeToBlock; 

            roadBlockCooldown = 10.0f;
            SetWaitingForPath(true);
            OnRoadBlockDetected?.Invoke(nodeToBlock);
        }
        else
        {
            Debug.LogWarning($"[PERCEPTION] Barriera vista ma non riesco ad associarla a nessun nodo!");
        }
    }

    private bool IsProtectedNode(WaypointNode node)
    {
        if (node.nodeType == WaypointNode.WaypointType.POI || 
            node.nodeType == WaypointNode.WaypointType.ChargingStation)
        {
            return true;
        }

        if (node.preventBlocking)
        {
            return true;
        }

        return false;     
    }

    private bool IsBarrierOnSegment(Vector3 p, Vector3 a, Vector3 b, float width)
    {
        Vector3 segment = b - a;
        if (segment == Vector3.zero) return false;
        Vector3 pointToA = p - a;
        float t = Vector3.Dot(pointToA, segment) / Vector3.Dot(segment, segment);
        if (t < -0.2f || t > 1.2f) return false;
        Vector3 closestPoint = a + (segment * t);
        return Vector3.Distance(p, closestPoint) < width;
    }

    private bool IsLeftSpaceFreeByLidar()
    {
        var leftInfo = lidar.LeftSector;
        if (!leftInfo.IsValid) return true;
        return leftInfo.Distance > 4.0f; 
    }

    private bool IsOncomingTrafficClear()
    {
        if (!isTwoWayRoad) return true;
        Vector3 leftLaneOrigin = brain.transform.position - (brain.transform.right * laneWidth);
        leftLaneOrigin.y += 1.0f; 

        RaycastHit hit;
        if (Physics.SphereCast(leftLaneOrigin, 1.0f, brain.transform.forward, out hit, oncomingCheckDistance, dynamicObstacleLayer))
        {
            if (hit.collider.CompareTag(CarConstants.TAG_TRAFFIC_CAR))
            {
                if (Vector3.Dot(brain.transform.forward, hit.collider.transform.forward) < -0.5f) return false; 
            }
        }
        return true;
    }

    // API per impostare il target (chiamata dal Brain)
    public void SetPassengerTarget(Transform target)
    {
        currentPassengerTarget = target;
    }

    // API per indicare che ci stiamo avvicinando al pickup (chiamata dal Brain/MissionController)
    public void SetApproachingPickup(bool approaching)
    {
        isApproachingPickup = approaching;
    }

    /* void OnDrawGizmos()
    {
        if (debugLastBlockedNode != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawSphere(debugLastBlockedNode.transform.position + Vector3.up * 3, 1.0f);
            //Gizmos.DrawLine(transform.position, debugLastBlockedNode.transform.position);
        }
    } */
}
