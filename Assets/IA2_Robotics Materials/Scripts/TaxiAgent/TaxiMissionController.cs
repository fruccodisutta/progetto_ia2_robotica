using UnityEngine;
using System.Collections.Generic;

public class TaxiMissionController : MonoBehaviour
{
    [Header("Scenario Strada Chiusa Design")]
    public List<GameObject> scenarioBarriers = new List<GameObject>();

    [Header("Riferimenti Modulari")]
    public CarBrain carBrain;
    public CarBattery carBattery;
    public CarMotor carMotor; // Reference per il controllore del motore

    [Header("Database Luoghi")]
    public List<WaypointNode> pointsOfInterest = new List<WaypointNode>();
    private List<PedestrianAI> allPedestrians = new List<PedestrianAI>();

    [Header("Impostazioni Missione")]
    public float waitTimeAtStop = 2.0f;

    [Header("Camera Pickup View")]
    public CameraManager cameraManager;
    public float pickupCameraDistance = 8f;
    public float pickupCameraLeadTime = 0f;

    [Header("Pickup Horn")]
    public AudioClip pickupHornClip;
    public float pickupHornDistance = 6f;
    [Range(0f, 5f)] public float pickupHornVolume = 2f;
    [Range(1f, 6f)] public float pickupHornGain = 3f;
    public bool pickupHorn3D = true;

    [Header("Driving Policy (per A* Pesato)")]
    //Policy di guida corrente - determina i pesi delle zone per il pathfinding (Comfort di default)
    public string currentDrivingPolicy = "Comfort";
    // Policy obbligatoria per sicurezza utente (es. gravidanza) - impedisce switch a ECO
    public string requiredPolicy = null;

    public enum MissionState
    {
        Idle,
        DrivingToPickup,
        DrivingToDropoff,
        DrivingToSafeStop,
        DrivingToChargingStation,
        Charging
    }

    [Header("Debug Stato")]
    public MissionState currentState = MissionState.Idle;

    private WaypointNode currentNavTargetNode;
    private WaypointNode finalDropOffNode;
    private PedestrianAI currentPassenger;
    private bool isWaitingAction = false;
    private bool passengerOnboard = false;
    private bool pickupHornPlayed = false;
    private AudioSource pickupHornSource;

    // Policy richiesta dal passeggero (da applicare al momento dell'imbarco)
    private string pendingPassengerPolicy = "Comfort";

    public bool PassengerOnboard => passengerOnboard;

    public event System.Action OnPassengerBoarded;
    public event System.Action OnReachedDropoff;
    public event System.Action OnReachedSafeStop;
    public event System.Action OnChargingStarted;
    public event System.Action OnChargingCompleted;
    public event System.Action<string> OnRouteRecalculated; 

    void Start()
    {
        if (carBrain == null) carBrain = GetComponent<CarBrain>();
        if (carBattery == null) carBattery = GetComponent<CarBattery>();
        if (carMotor == null) carMotor = GetComponent<CarMotor>();
        if (cameraManager == null) cameraManager = FindFirstObjectByType<CameraManager>();

        if (carBrain != null) carBrain.OnRoadBlockDetected += HandleRoadBlock;

        PedestrianAI[] peds = FindObjectsByType<PedestrianAI>(FindObjectsSortMode.None);
        allPedestrians.AddRange(peds);

        if (WeatherManager.Instance != null)
        {
            WeatherManager.Instance.OnWeatherChanged += HandleWeatherChanged;
        }

        PreloadZoneMultipliers();
    }

    // Pre-carica i moltiplicatori delle zone dal backend per il pathfinding pesato.
    // Chiamata all'avvio per popolare la cache di KBService.
    void PreloadZoneMultipliers()
    {
        if (KBService.Instance != null)
        {
            KBService.Instance.PreloadMultipliers(currentDrivingPolicy);
            // Pre-carichiamo anche 'Eco' perché usata per il pickup (fastest route)
            KBService.Instance.PreloadMultipliers("Eco");
        }
        else
        {
            Debug.LogWarning("[TAXI MISSION CONTROLLER] KBService non disponibile. I pesi delle zone non verranno applicati.");
        }
    }

    // Ottiene i moltiplicatori delle zone dalla cache di KBService.
    // Se la cache è vuota, ritorna null (A* userà pesi uniformi).
    Dictionary<string, float> GetZoneMultipliersFromCache(MissionState? overrideState = null)
    {
        if (KBService.Instance == null)
            return null;

        MissionState stateToCheck = overrideState ?? currentState;

        // Applica i pericoli del meteo solo se il passeggero è a bordo (DrivingToDropoff)
        string weather = null;
        if (stateToCheck == MissionState.DrivingToDropoff)
        {
            weather = GetCurrentWeather();
        }
        
        var multipliers = KBService.Instance.GetZoneMultipliersSync(currentDrivingPolicy, weather);
        
        if (multipliers.Count > 0)
        {
            Debug.Log($"[TAXI MISSION CONTROLLER] Utilizzo dei moltiplicatori di zona per la policy '{currentDrivingPolicy}': {multipliers.Count} zone (Meteo: {weather ?? "Nessuno"})");
        }
        
        return multipliers.Count > 0 ? multipliers : null;
    }


    void Update()
    {
        switch (currentState)
        {
            case MissionState.Idle:
                if (Input.GetKeyDown(KeyCode.Space)) GeneratePassengerMission();
                break;

            case MissionState.DrivingToPickup:
            case MissionState.DrivingToDropoff:
            case MissionState.DrivingToSafeStop:
            case MissionState.DrivingToChargingStation:
                if (!isWaitingAction) CheckArrival();
                break;

            case MissionState.Charging:
                if (!carBattery.IsCharging)
                {
                    Debug.Log("[TAXI MISSION CONTROLLER] Ricarica completata.");
                    currentState = MissionState.Idle;
                    OnChargingCompleted?.Invoke();
                }
                break;
        }

        UpdatePickupCamera();
        UpdatePickupHorn();
    }

    void GeneratePassengerMission()
    {
        PedestrianAI selectedPedestrian = null;
        WaypointNode pickupNode = null;

        List<PedestrianAI> candidates = new List<PedestrianAI>(allPedestrians);
        int pedAttempts = 0;
        while (selectedPedestrian == null && pedAttempts < 20)
        {
            if (candidates.Count == 0) break;

            int rndIdx = Random.Range(0, candidates.Count);
            PedestrianAI candidate = candidates[rndIdx];

            if (candidate.IsOnSidewalk() && candidate.personality == PedestrianAI.Personality.Calm)
            {
                WaypointNode nearestStop = GetClosestStoppingNode(candidate.transform.position, 10.0f);

                if (nearestStop != null)
                {
                    selectedPedestrian = candidate;
                    pickupNode = nearestStop;
                }
            }

            candidates.RemoveAt(rndIdx);
            pedAttempts++;
        }

        if (selectedPedestrian == null || pickupNode == null)
        {
            Debug.LogWarning("[TAXI MISSION CONTROLLER] Impossibile trovare un pedone idoneo vicino a una corsia di sosta.");
            return;
        }

        currentPassenger = selectedPedestrian;
        passengerOnboard = false;
        pickupHornPlayed = false;

        currentPassenger.HailTaxi(carBrain.transform);
        carBrain.SetCurrentPassenger(currentPassenger.transform);

        int attempts = 0;
        bool isValidDestination = false;

        do
        {
            finalDropOffNode = pointsOfInterest[Random.Range(0, pointsOfInterest.Count)];

            float dist = Vector3.Distance(pickupNode.transform.position, finalDropOffNode.transform.position);

            if (finalDropOffNode != pickupNode)
            {
                isValidDestination = true;
            }
            attempts++;
        } while (!isValidDestination && attempts < 100);

        Debug.Log($"[TAXI MISSION CONTROLLER] Chiamata da: {currentPassenger.name}. Pickup: {pickupNode.name} -> Dest: {finalDropOffNode.name}");

        SetDestination(pickupNode, MissionState.DrivingToPickup);
    }

    void CheckArrival()
    {
        if (currentNavTargetNode == null) return;

        float dist = Vector3.Distance(carBrain.transform.position, currentNavTargetNode.transform.position);

        // PICKUP: Accosta a destra E riduce margine laterale
        if (currentState == MissionState.DrivingToPickup)
        {
            if (dist < 20.0f && dist > 3.0f)
            {
                carBrain.SetMissionOffset(1.5f);
                carBrain.SetApproachingPickup(true);
            }
            else
            {
                carBrain.SetMissionOffset(0f);
                carBrain.SetApproachingPickup(false);
            }
        }
        else if (currentState == MissionState.DrivingToDropoff)
        {
            if (dist < 20.0f && dist > 3.0f)
            {
                carBrain.SetApproachingPickup(true);
            }
            else
            {
                carBrain.SetApproachingPickup(false);
            }
            carBrain.SetMissionOffset(0f);
        }
        else
        {
            carBrain.SetMissionOffset(0f);
            carBrain.SetApproachingPickup(false);
        }

        bool isCloseEnough = dist <= 2.5f;
        bool isPathFinished = carBrain.HasFinishedPath && dist < 10.0f;

        if (isCloseEnough || isPathFinished)
        {
            carBrain.StopNavigation();
            carBrain.SetMissionOffset(0f);
            carBrain.SetApproachingPickup(false);

            StartCoroutine(HandleArrivalSequence());
        }
    }

    System.Collections.IEnumerator HandleArrivalSequence()
    {
        isWaitingAction = true;

        if (currentState == MissionState.DrivingToPickup)
        {
            Debug.Log("[TAXI MISSION CONTROLLER] Arrivato dal passeggero. Attendo imbarco...");

            if (currentPassenger != null)
            {
                currentPassenger.ApproachVehicle();

                float approachTimer = 0f;
                float maxWaitTime = 6.0f;

                while (approachTimer < maxWaitTime)
                {
                    if (currentPassenger == null) break;

                    float distToTaxi = Vector3.Distance(currentPassenger.transform.position, carBrain.transform.position);
                    if (distToTaxi < 2.5f)
                    {
                        break;
                    }

                    approachTimer += Time.deltaTime;
                    yield return null;
                }
            }

            // Attesa per simulare l'apertura della portiera e l'imbarco del passeggero
            yield return new WaitForSeconds(0.5f);

            if (currentPassenger != null) currentPassenger.BoardTaxi();

            Debug.Log($"[TAXI MISSION CONTROLLER] Passeggero a bordo. Partenza per {finalDropOffNode.name}");
            yield return new WaitForSeconds(1.0f);

            carBrain.SetCurrentPassenger(null);
            passengerOnboard = true;
            if (cameraManager != null) cameraManager.SwitchToTaxiView();
            pickupHornPlayed = true;
            OnPassengerBoarded?.Invoke();

            // Ripristino la policy richiesta dal passeggero
            currentDrivingPolicy = pendingPassengerPolicy;
            ApplyDrivingPhysics(currentDrivingPolicy);
            Debug.Log($"[TAXI MISSION CONTROLLER] Ripristino policy passeggero: {currentDrivingPolicy}");

            SetDestinationWithBarrier(finalDropOffNode, MissionState.DrivingToDropoff, true);
        }
        else if (currentState == MissionState.DrivingToDropoff)
        {
            Debug.Log("[TAXI MISSION CONTROLLER] Destinazione raggiunta. Scarico passeggero.");

            yield return new WaitForSeconds(waitTimeAtStop);

            Vector3 dropOffset = carBrain.transform.right * 2.0f;
            Vector3 dropPosition = carBrain.transform.position + dropOffset;

            if (TryGetSidewalkPosition(dropPosition, 2.0f, out Vector3 sidewalkPos))
            {
                dropPosition = sidewalkPos;
            }
            else
            {
                Debug.LogWarning("[TAXI MISSION CONTROLLER] Dropoff: nessuna posizione sidewalk trovata. Uso posizione originale.");
            }

            if (currentPassenger != null)
            {
                currentPassenger.ExitTaxi(dropPosition);
            }

            Debug.Log("[TAXI MISSION CONTROLLER] Corsa completata.");
            currentPassenger = null;
            passengerOnboard = false;
            requiredPolicy = null; // Reset per la prossima missione
            currentState = MissionState.Idle;
            OnReachedDropoff?.Invoke();
        }
        else if (currentState == MissionState.DrivingToSafeStop)
        {
            Debug.Log("[TAXI MISSION CONTROLLER] Stop sicuro raggiunto. Termino corsa.");

            yield return new WaitForSeconds(waitTimeAtStop);

            if (passengerOnboard && currentPassenger != null)
            {
                Vector3 dropOffset = carBrain.transform.right * 2.0f;
                Vector3 dropPosition = carBrain.transform.position + dropOffset;

                if (TryGetSidewalkPosition(dropPosition, 2.0f, out Vector3 sidewalkPos))
                {
                    dropPosition = sidewalkPos;
                }
                else
                {
                    Debug.LogWarning("[TAXI MISSION CONTROLLER] SafeStop: nessuna posizione sidewalk trovata. Uso posizione originale.");
                }

                currentPassenger.ExitTaxi(dropPosition);
            }

            currentPassenger = null;
            passengerOnboard = false;
            requiredPolicy = null; // Reset per la prossima missione
            currentState = MissionState.Idle;
            OnReachedSafeStop?.Invoke();
        }
        else if (currentState == MissionState.DrivingToChargingStation)
        {
            Debug.Log("[TAXI MISSION CONTROLLER] Arrivato alla colonnina. Ricarica batteria...");
            carBrain.StartCharging();
            currentState = MissionState.Charging;
            OnChargingStarted?.Invoke();
        }

        isWaitingAction = false;
    }

    void UpdatePickupCamera()
    {
        if (cameraManager == null)
        {
            cameraManager = FindFirstObjectByType<CameraManager>();
            if (cameraManager == null) return;
        }

        if (currentState == MissionState.DrivingToPickup && currentPassenger != null && carBrain != null)
        {
            float speed = carMotor != null ? Mathf.Abs(carMotor.CurrentSpeed) : 0f;
            float leadDistance = Mathf.Max(pickupCameraDistance, speed * pickupCameraLeadTime);
            float dist = Vector3.Distance(carBrain.transform.position, currentPassenger.transform.position);

            if (dist <= leadDistance)
            {
                cameraManager.SwitchToPassengerView(currentPassenger.transform, carBrain.transform);
                return;
            }
        }

        if (cameraManager.IsPassengerViewActive)
        {
            cameraManager.SwitchToTaxiView();
        }
    }

    void UpdatePickupHorn()
    {
        if (pickupHornClip == null) return;

        if (currentState == MissionState.DrivingToPickup && currentPassenger != null && carBrain != null)
        {
            float dist = Vector3.Distance(carBrain.transform.position, currentPassenger.transform.position);
            if (!pickupHornPlayed && dist <= pickupHornDistance)
            {
                PlayPickupHorn();
                pickupHornPlayed = true;
            }
            return;
        }

        if (currentState != MissionState.DrivingToPickup)
        {
            pickupHornPlayed = false;
        }
    }

    void PlayPickupHorn()
    {
        if (pickupHornSource == null)
        {
            pickupHornSource = gameObject.AddComponent<AudioSource>();
            pickupHornSource.playOnAwake = false;
            pickupHornSource.loop = false;
            pickupHornSource.rolloffMode = AudioRolloffMode.Logarithmic;
        }

        pickupHornSource.spatialBlend = pickupHorn3D ? 1f : 0f;
        pickupHornSource.PlayOneShot(pickupHornClip, pickupHornVolume * pickupHornGain);
    }

    // Metodo analogo a SetDestination, ma con barriere attive
    void SetDestinationWithBarrier(WaypointNode target, MissionState newState, bool applyZoneWeights = false)
    {
        WaypointNode startNode = GetPathfindingStartNode(allowOffGraph: false);
        
        // Ottieni i pesi delle zone SOLO se richiesto (es. solo per dropoff con passeggero)
        // Se dobbiamo andare alla colonnina di ricarica, non ha senso applicare i pesi delle zone, cerchiamo la più vicina 
        Dictionary<string, float> zoneMultipliers = applyZoneWeights ? GetZoneMultipliersFromCache(newState) : null;
        
        if (applyZoneWeights && zoneMultipliers != null)
        {
            Debug.Log($"[TAXI MISSION CONTROLLER] Applicazione pesi zone per policy '{currentDrivingPolicy}' sul percorso verso {target.name}");
        }
        
        List<WaypointNode> path = AStarPathfinder.FindPath(startNode, target, zoneMultipliers);

        if (path != null && path.Count > 0)
        {
            List<Transform> pathTransforms = new List<Transform>();
            foreach (var n in path) pathTransforms.Add(n.transform);

            carBrain.SetNewPath(pathTransforms);
            carBrain.PathRefreshed();
            currentNavTargetNode = target;
            currentState = newState;

            ActivateRelevantBarriers(path);
        }
        else
        {
            Debug.LogError($"[TAXI MISSION CONTROLLER] Impossibile trovare percorso verso {target.name}");
            currentState = MissionState.Idle;
        }
    }

    // Ottiene il nodo di partenza per il pathfinding.
    // Il taxi partirà sempre da un nodo connesso al grafo.
    // UNICA ECCEZIONE: allowOffGraph=true per escape da barriere.
    WaypointNode GetPathfindingStartNode(bool allowOffGraph = false)
    {
        // Caso barriere: escape off-graph permesso
        if (allowOffGraph)
        {
            Debug.Log("[TAXI MISSION CONTROLLER] 🚧 Using GetClosestNode for barrier escape (off-graph allowed)");
            return AStarPathfinder.GetClosestNode(carBrain.transform.position);
        }
        
        var navModule = carBrain.GetComponent<CarBrain_Navigation>();
        
        // Caso navigazione attiva - usa current/next waypoint
        if (navModule != null && navModule.HasPath())
        {
            WaypointNode currentWaypoint = navModule.GetCurrentWaypointNode();
            
            // Caso 1: Current waypoint è valido (non POI, non bloccato)
            if (currentWaypoint != null && 
                !currentWaypoint.isBlocked && 
                currentWaypoint.nodeType != WaypointNode.WaypointType.POI)
            {
                Debug.Log($"[TAXI MISSION CONTROLLER] ✅ Uso il waypoint corrente: {currentWaypoint.name}");
                return currentWaypoint;
            }
            
            // Caso 2: Current è POI - Cerca prossimo waypoint valido nel path
            if (currentWaypoint != null && currentWaypoint.nodeType == WaypointNode.WaypointType.POI)
            {
                WaypointNode nextValidNode = navModule.GetNextNonPOIWaypoint();
                if (nextValidNode != null)
                {
                    Debug.Log($"[TAXI MISSION CONTROLLER] ✅ La destinazione '{currentWaypoint.name}' è un POI, uso il prossimo waypoint valido: {nextValidNode.name}");
                    return nextValidNode;
                }
                else
                {
                    Debug.LogWarning($"[TAXI MISSION CONTROLLER] ⚠️ La destinazione '{currentWaypoint.name}' è un POI e non ci sono altri waypoint validi. Cerco un percorso alternativo.");
                }
            }
            
            // Sub-caso 2c: Current è bloccato o null ma abbiamo path
            if (currentWaypoint != null && currentWaypoint.isBlocked)
            {
                Debug.LogWarning($"[TAXI MISSION CONTROLLER] ⚠️ La destinazione '{currentWaypoint.name}' è bloccata. Cerco un percorso alternativo.");
            }
        }
        
        // Caso 3: Fallback - Usa GetClosestNode
        // Se arriviamo qui, significa che non abbiamo path attivo o waypoint corrente non valido
        // In questo caso, usiamo GetClosestNode di A* che trova semplicemente il nodo più vicino al taxi.
        Debug.LogWarning("[TAXI MISSION CONTROLLER] ⚠️ Nessun path attivo o waypoint corrente non valido. Uso GetClosestNode fallback...");
        
        WaypointNode closestNode = AStarPathfinder.GetClosestNode(carBrain.transform.position);
        
        if (closestNode != null)
        {
            Debug.Log($"[TAXI MISSION CONTROLLER] ✅ Fallback: Uso il nodo più vicino: {closestNode.name}");
            return closestNode;
        }
        
        // Ultimo resort: Nessun Nodo Trovato!
        Debug.LogError("[TAXI MISSION CONTROLLER] ❌ CRITICO: Nessun nodo trovato! Il grafo potrebbe essere vuoto o rotto.");
        return null;
    }

    void SetDestination(WaypointNode target, MissionState newState, bool applyZoneWeights = false)
    {
        WaypointNode startNode = GetPathfindingStartNode(allowOffGraph: false);
        
        Dictionary<string, float> zoneMultipliers = applyZoneWeights ? GetZoneMultipliersFromCache() : null;
        
        if (applyZoneWeights && zoneMultipliers != null)
        {
            Debug.Log($"[MISSION] Applying zone weights for policy '{currentDrivingPolicy}' on route to {target.name}");
        }
        
        List<WaypointNode> path = AStarPathfinder.FindPath(startNode, target, zoneMultipliers);

        if (path != null && path.Count > 0)
        {
            List<Transform> pathTransforms = new List<Transform>();
            foreach (var n in path) pathTransforms.Add(n.transform);

            carBrain.SetNewPath(pathTransforms);
            carBrain.PathRefreshed();
            currentNavTargetNode = target;
            currentState = newState;
        }
        else
        {
            Debug.LogError($"[MISSION] Impossibile trovare percorso verso {target.name}");
            currentState = MissionState.Idle;
        }
    }

    void OnDestroy()
    {
        if (carBrain != null) carBrain.OnRoadBlockDetected -= HandleRoadBlock;
        
        if (WeatherManager.Instance != null)
        {
            WeatherManager.Instance.OnWeatherChanged -= HandleWeatherChanged;
        }
    }

    void ActivateRelevantBarriers(List<WaypointNode> currentPath)
    {
        foreach (var b in scenarioBarriers) if (b != null) b.SetActive(false);
        if (currentPath.Count < 2) return;
        List<GameObject> candidates = new List<GameObject>();
        foreach (var barrier in scenarioBarriers)
        {
            if (barrier == null) continue;
            Vector3 bPos = barrier.transform.position;
            bool isOnPath = false;
            for (int i = 0; i < currentPath.Count - 1; i++)
            {
                Vector3 p1 = currentPath[i].transform.position;
                Vector3 p2 = currentPath[i + 1].transform.position;
                float dist = DistancePointToLineSegment(bPos, p1, p2);
                if (dist < 4.5f)
                {
                    float segLen = Vector3.Distance(p1, p2);
                    if (Vector3.Distance(bPos, p1) < segLen + 2f && Vector3.Distance(bPos, p2) < segLen + 2f)
                    {
                        isOnPath = true; break;
                    }
                }
            }
            if (isOnPath) candidates.Add(barrier);
        }
        if (candidates.Count > 0)
        {
            int randomIndex = Random.Range(0, candidates.Count);
            candidates[randomIndex].SetActive(true);
        }
    }

    float DistancePointToLineSegment(Vector3 p, Vector3 a, Vector3 b)
    {
        Vector3 segment = b - a;
        if (segment == Vector3.zero) return Vector3.Distance(p, a);
        Vector3 pointToA = p - a;
        float t = Vector3.Dot(pointToA, segment) / Vector3.Dot(segment, segment);
        t = Mathf.Clamp01(t);
        Vector3 closestPoint = a + (segment * t);
        return Vector3.Distance(p, closestPoint);
    }

    void HandleRoadBlock(WaypointNode blockedNode)
    {
        if (blockedNode == null || blockedNode.isBlocked) return;

        WaypointNode currentTaxiNode = AStarPathfinder.GetClosestNode(carBrain.transform.position);

        Debug.Log($"[TAXI MISSION CONTROLLER] Richiesta blocco per: '{blockedNode.name}'.\n" +
                $"          Taxi attualmente su: '{currentTaxiNode.name}'.\n" +
                $"          Target attuale: '{currentNavTargetNode?.name}'.");

        if (blockedNode == currentNavTargetNode)
        {
            Debug.LogWarning("[TAXI MISSION CONTROLLER] Il nodo bloccato è la mia destinazione finale! Impossibile completare missione.");
            return;
        }

        if (blockedNode == currentTaxiNode)
        {
            Debug.LogError("!!! CRITICAL !!! Il taxi si trova SOPRA il nodo da bloccare. ");
        }

        StartCoroutine(HandleRoadBlockSequence(blockedNode));
    }

    bool TryRecalculatePath()
    {
        if (currentNavTargetNode == null) return false;
        
        // BARRIERE: Prova prima con current waypoint (mantiene topologia grafo)
        WaypointNode startNode = GetPathfindingStartNode(allowOffGraph: false);
        Dictionary<string, float> zoneMultipliers = passengerOnboard ? GetZoneMultipliersFromCache() : null;
        List<WaypointNode> path = AStarPathfinder.FindPath(startNode, currentNavTargetNode, zoneMultipliers);
        
        if (path != null && path.Count > 0)
        {
            Debug.Log($"[TAXI MISSION CONTROLLER] Ricalcolo (via current waypoint) riuscito da: {startNode.name}");
            List<Transform> pathTransforms = new List<Transform>();
            foreach (var n in path) pathTransforms.Add(n.transform);
            carBrain.SetNewPath(pathTransforms);
            return true;
        }
        
        // FALLBACK: Se current waypoint fallisce, prova nodi vicini (OFF-GRAPH per barriere)
        Debug.LogWarning("[TAXI MISSION CONTROLLER] Ricalcolo dal nodo corrente fallito, provo nodi vicini (barrier escape)");
        Vector3 carPos = carBrain.transform.position;
        List<WaypointNode> sortedNodes = new List<WaypointNode>(WaypointNode.AllNodes);
        sortedNodes.Sort((a, b) => Vector3.Distance(carPos, a.transform.position).CompareTo(Vector3.Distance(carPos, b.transform.position)));

        int attempts = 0;
        foreach (WaypointNode potentialStart in sortedNodes)
        {
            if (attempts >= 50) break;
            if (potentialStart.isBlocked)
            {
                attempts++;
                continue;
            }

            path = AStarPathfinder.FindPath(potentialStart, currentNavTargetNode, zoneMultipliers);
            if (path != null && path.Count > 0)
            {
                Debug.Log($"[TAXI MISSION CONTROLLER] Ricalcolo (via barrier escape) riuscito da: {potentialStart.name}");
                List<Transform> pathTransforms = new List<Transform>();
                foreach (var n in path) pathTransforms.Add(n.transform);
                carBrain.SetNewPath(pathTransforms);
                return true;
            }
        }
        Debug.LogError("[TAXI MISSION CONTROLLER] Ricalcolo FALLITO. Nessun nodo vicino permette di raggiungere la destinazione (Topologia Disconnessa).");
        return false;
    }

    WaypointNode GetClosestStoppingNode(Vector3 position, float maxSearchRadius)
    {
        WaypointNode bestNode = null;
        float closestDistSqr = maxSearchRadius * maxSearchRadius;

        foreach (var node in WaypointNode.AllNodes)
        {
            if (!node.isStoppingAllowed) continue;
            if (node.isBlocked) continue;

            float distSqr = (node.transform.position - position).sqrMagnitude;
            if (distSqr < closestDistSqr)
            {
                closestDistSqr = distSqr;
                bestNode = node;
            }
        }
        return bestNode;
    }

    public void StartMissionWithPickup(WaypointNode destination, WaypointNode pickupNode, PedestrianAI passenger, string drivingPolicy = null)
    {
        if (destination == null || pickupNode == null) return;
        if (!CanStartNewMission()) return;

        StopChargingIfNeeded();
        
        // Settiamo la driving policy della missione
        if (!string.IsNullOrEmpty(drivingPolicy))
        {
            pendingPassengerPolicy = drivingPolicy;
            // Per il Pickup usiamo "Eco" (più veloce perché non applica pesi di superficie, ignora comfort), ma rispettiamo il traffico
            currentDrivingPolicy = "Eco";
            ApplyDrivingPhysics("Eco");
            
            Debug.Log($"[TAXI MISSION CONTROLLER] Pickup Mode: Uso Policy 'Eco' per avvicinamento (Passeggero vuole {pendingPassengerPolicy})");

            // Preload dei moltiplicatori per la nuova policy
            if (KBService.Instance != null)
            {
                KBService.Instance.PreloadMultipliers(currentDrivingPolicy);
                // Preload anche quella futura
                KBService.Instance.PreloadMultipliers(pendingPassengerPolicy);
            }
        }

        Debug.Log($"[TAXI MISSION CONTROLLER] API Backend: Pickup {pickupNode.name} -> Dest {destination.name} (Policy: {currentDrivingPolicy})");

        currentPassenger = passenger;
        finalDropOffNode = destination;
        passengerOnboard = false;
        pickupHornPlayed = false;

        if (currentPassenger != null)
        {
            currentPassenger.HailTaxi(carBrain.transform);
            carBrain.SetCurrentPassenger(currentPassenger.transform);
        }

        SetDestination(pickupNode, MissionState.DrivingToPickup, true);
    }

    public void StartMissionFromCoordinator(WaypointNode destination)
    {
        if (destination == null) return;
        if (!CanStartNewMission()) return;

        StopChargingIfNeeded();

        finalDropOffNode = destination;
        currentPassenger = null;
        passengerOnboard = true;

        SetDestination(destination, MissionState.DrivingToDropoff, true);
    }

    public bool BeginChargingRoutine()
    {
        WaypointNode nearestCharger = FindNearestChargingStation();
        if (nearestCharger == null) return false;

        StopChargingIfNeeded();
        
        // Forziamo la Eco Policy per andare alla colonnina di ricarica (massima efficienza, ignorando comfort)
        currentDrivingPolicy = "Eco";
        ApplyDrivingPhysics("Eco");
        Debug.Log("[TAXI MISSION CONTROLLER] Going to Charging Station - Switching to Eco Policy");
        
        if (KBService.Instance != null) KBService.Instance.PreloadMultipliers("Eco");

        SetDestination(nearestCharger, MissionState.DrivingToChargingStation);
        return true;
    }

    public void StopChargingIfNeeded()
    {
        if (carBattery != null && carBattery.IsCharging)
        {
            carBattery.StopCharging();
        }
    }

    public void ReturnToChargingStation()
    {
        BeginChargingRoutine();
    }

    public DestinationChangeResult ChangeDestination(WaypointNode newDestination)
    {
        var result = new DestinationChangeResult();

        if (newDestination == null)
        {
            Debug.LogError("[TAXI MISSION CONTROLLER] ChangeDestination: nuova destinazione null!");
            result.success = false;
            result.errorMessage = "Destinazione non valida";
            return result;
        }

        if (currentState != MissionState.DrivingToDropoff || !passengerOnboard)
        {
            Debug.LogWarning($"[TAXI MISSION CONTROLLER] ChangeDestination: impossibile durante stato {currentState}");
            result.success = false;
            result.errorMessage = "Nessun passeggero a bordo";
            return result;
        }

        WaypointNode startNode = GetPathfindingStartNode(allowOffGraph: false);
        
        // Cambio destinazione con passeggero → applica i pesi della policy
        Dictionary<string, float> zoneMultipliers = GetZoneMultipliersFromCache();
        List<WaypointNode> path = AStarPathfinder.FindPath(startNode, newDestination, zoneMultipliers);

        if (path == null || path.Count == 0)
        {
            Debug.LogError($"[TAXI MISSION CONTROLLER] ChangeDestination: impossibile raggiungere {newDestination.name}");
            result.success = false;
            result.errorMessage = "Impossibile raggiungere la nuova destinazione";
            return result;
        }

        float distanceUnits = CalculatePathDistanceLocal(path);
        float distanceKm = distanceUnits * SimulationUnits.UnitsToKmFactor;

        float tempoStimato = carBattery.EstimateTimeMinutes(distanceKm);
        float timeToCharge = 0f;

        // SAFE RETURN CHECK
        float returnToChargerKm = 0f;
        // Trova la stima della distanza alla colonnina di ricarica più vicina dalla nuova destinazione (non dalla posizione attuale del taxi)
        WaypointNode nearestCharger = FindNearestChargingStation(); 
        {
            // Local search logic
            float minDist = float.MaxValue;
            foreach (var node in WaypointNode.AllNodes)
            {
                if (node.nodeType != WaypointNode.WaypointType.ChargingStation) continue;
                float d = Vector3.Distance(newDestination.transform.position, node.transform.position);
                if (d < minDist) minDist = d;
            }
            if (minDist < float.MaxValue) returnToChargerKm = minDist * SimulationUnits.UnitsToKmFactor * 1.3f; // Fallback estimate
        }
        
        float returnConsumption = carBattery.GetEstimatedConsumption(returnToChargerKm, 0.7f); // Eco return

        Debug.Log($"[TAXI MISSION CONTROLLER][BATTERY CHECK] Destinazione: {distanceKm:F2}km | Ritorno Charger: {returnToChargerKm:F2}km | Consumo Ritorno: {returnConsumption:F1}%");

        // Check Logic
        float totalReq = carBattery.GetEstimatedConsumption(distanceKm) + returnConsumption + 5.0f; // Current Policy + Return + Buffer

        if (carBattery.currentBattery < totalReq)
        {
            // Eco fallback check: calcola il percorso A* con i pesi ECO per avere la distanza corretta
            float ecoDistanceKm = distanceKm; // Fallback alla distanza attuale
            float ecoReturnKm = returnToChargerKm;
            
            string weather = WeatherManager.Instance != null && WeatherManager.Instance.IsRaining ? "rain" : null;
            Dictionary<string, float> ecoMultipliers = KBService.Instance?.GetZoneMultipliersSync("Eco", weather);
            
            if (ecoMultipliers != null)
            {
                WaypointNode ecoStartNode = GetPathfindingStartNode(allowOffGraph: false);
                if (ecoStartNode != null)
                {
                    var ecoPath = AStarPathfinder.FindPath(ecoStartNode, newDestination, ecoMultipliers);
                    if (ecoPath != null && ecoPath.Count > 0)
                    {
                        ecoDistanceKm = CalculatePathDistanceLocal(ecoPath) * SimulationUnits.UnitsToKmFactor;
                        Debug.Log($"[TAXI MISSION CONTROLLER] Percorso ECO calcolato: {ecoPath.Count} nodi, {ecoDistanceKm:F2}km (vs {distanceKm:F2}km policy attuale)");
                        
                        // Ricalcola anche il ritorno alla colonnina con ECO
                        if (nearestCharger != null)
                        {
                            var ecoReturnPath = AStarPathfinder.FindPath(newDestination, nearestCharger, ecoMultipliers);
                            if (ecoReturnPath != null && ecoReturnPath.Count > 0)
                            {
                                ecoReturnKm = CalculatePathDistanceLocal(ecoReturnPath) * SimulationUnits.UnitsToKmFactor;
                            }
                        }
                    }
                }
            }
            
            float ecoMultiplier = 0.7f;
            if (KBService.Instance != null)
            {
                ecoMultiplier = KBService.Instance.GetPolicyParametersSync("Eco").consumption_multiplier;
            }

            float ecoReturnConsumption = carBattery.GetEstimatedConsumption(ecoReturnKm, ecoMultiplier);
            float ecoReq = carBattery.GetEstimatedConsumption(ecoDistanceKm, ecoMultiplier) + ecoReturnConsumption + 5.0f;

            Debug.Log($"[TAXI MISSION CONTROLLER][BATTERY CHECK] Tentativo Fallback ECO: ReqStandard={totalReq:F1}% EcoReq={ecoReq:F1}% (Current={carBattery.currentBattery:F1}%)");

            if (carBattery.currentBattery >= ecoReq)
            {
                // CHECK: se la policy è obbligatoria, NON permettiamo lo switch
                if (!string.IsNullOrEmpty(requiredPolicy))
                {
                    Debug.LogWarning($"[TAXI MISSION CONTROLLER] ChangeDestination RIFIUTATO: Policy '{requiredPolicy}' è obbligatoria per sicurezza utente!");
                    result.success = false;
                    result.errorMessage = $"Batteria non sufficiente per raggiungere la nuova destinazione in modalità {requiredPolicy}. Impossibile cambiare.";
                    return result;
                }
                
                Debug.LogWarning($"[TAXI MISSION CONTROLLER] ChangeDestination: Sufficiente solo in ECO. Distanza ECO: {ecoDistanceKm:F2}km");
                ChangeDrivingPolicy("Eco", "policy_forced_eco");
                result.errorMessage = "Auto-Switch a ECO per garantire il ritorno alla base.";
            }
            else
            {
                Debug.Log($"[TAXI MISSION CONTROLLER] ChangeDestination RIFIUTATO: Fallito anche check ECO ({ecoReq:F1}% req > {carBattery.currentBattery:F1}% available).");
                result.success = false;
                result.errorMessage = "Batteria insufficiente per garantire il ritorno alla stazione di ricarica.";
                result.needsRecharge = true;
                
                // Calcola il tempo stimato per ricaricare abbastanza da garantire il ritorno alla colonnina (non per completare la corsa, ma solo per avere abbastanza batteria per tornare alla base in sicurezza)
                timeToCharge = carBattery.GetTimeToCharge(totalReq);
                result.rechargeTimeMinutes = timeToCharge;
                return result;
            }
        }

        finalDropOffNode = newDestination;

        List<Transform> pathTransforms = new List<Transform>();
        foreach (var n in path) pathTransforms.Add(n.transform);
        carBrain.SetNewPath(pathTransforms);
        currentNavTargetNode = newDestination;

        Debug.Log($"[TAXI MISSION CONTROLLER] Destinazione cambiata: {newDestination.name}, dist={distanceKm:F2}km");

        result.success = true;
        result.distanceKm = distanceKm;
        result.estimatedTimeMinutes = tempoStimato;
        result.needsRecharge = false; // Se siamo qui, la batteria è sufficiente
        result.rechargeTimeMinutes = 0f;
        result.newDestinationName = newDestination.name;

        return result;
    }

    // Cambia la policy di guida a metà corsa e ricalcola il percorso con i nuovi pesi delle zone.
    public void ChangeDrivingPolicy(string newPolicy, string reasonCode = "policy")
    {
        if (string.IsNullOrEmpty(newPolicy)) return;
        
        // Se la policy richiesta è già quella corrente, non fare nulla
        if (newPolicy.Equals(currentDrivingPolicy, System.StringComparison.OrdinalIgnoreCase))
        {
            Debug.Log($"[TAXI MISSION CONTROLLER] Policy già impostata a '{currentDrivingPolicy}', nessun ricalcolo necessario.");
            return;
        }
        
        // Se stiamo andando a prendere il passeggero, non cambiamo la policy corrente (Eco)
        // ma aggiorniamo quella che useremo dopo.
        if (currentState == MissionState.DrivingToPickup)
        {
            pendingPassengerPolicy = newPolicy;
            Debug.Log($"[TAXI MISSION CONTROLLER] Policy aggiornata (pending): {newPolicy}. Sarà applicata all'imbarco.");
            return;
        }

        currentDrivingPolicy = newPolicy;
        Debug.Log($"[TAXI MISSION CONTROLLER] Driving policy cambiata in: {newPolicy} (Reason {reasonCode})");
        
        // Applica Fisica (Speed, Accel) dalla KB
        ApplyDrivingPhysics(newPolicy);

        // Scarica i moltiplicatori delle zone e ricalcola il percorso con i nuovi pesi delle zone.
        if (KBService.Instance != null)
        {
            string weather = GetCurrentWeather();
            KBService.Instance.GetZoneMultipliers(newPolicy, weather, (multipliers) =>
            {
                Debug.Log($"[TAXI MISSION CONTROLLER] Ricevuti {multipliers.Count} zone multipliers per policy '{newPolicy}' + weather '{weather ?? "clear"}'");
                RecalculateRouteWithMultipliers(multipliers, newPolicy);
                OnRouteRecalculated?.Invoke(reasonCode);
            });
        }
    }

    // Richiede i parametri fisici al KBService e li applica al CarMotor
    private void ApplyDrivingPhysics(string policy)
    {
        if (KBService.Instance == null) return;
        
        // Ensure CarMotor reference
        if (carMotor == null)
        {
            carMotor = GetComponent<CarMotor>();
            if (carMotor == null) carMotor = GetComponentInChildren<CarMotor>();
            if (carMotor == null) carMotor = FindFirstObjectByType<CarMotor>(); // Fallback
            
            if (carMotor == null)
            {
                Debug.LogError("[TAXI MISSION CONTROLLER] CRITICAL: CarMotor non trovato! La fisica non può essere applicata.");
                return;
            }
            else
            {
                Debug.Log($"[TAXI MISSION CONTROLLER] CarMotor trovato: {carMotor.name}");
            }
        }

        KBService.Instance.GetPolicyParameters(policy, (paramsData) =>
            {
                if (carMotor != null)
                {
                    carMotor.maxSpeed = paramsData.max_speed;
                    carMotor.acceleration = paramsData.acceleration;
                    carMotor.brakePower = paramsData.brake_power;
                    
                    // Applica il moltiplicatore di consumo alla batteria, se disponibile
                    if (carBattery != null)
                    {
                        // Protezione contro valori non validi: se il moltiplicatore è troppo basso, usiamo 1.0 (nessun effetto)
                        float mult = paramsData.consumption_multiplier > 0.1f ? paramsData.consumption_multiplier : 1.0f;
                        carBattery.consumptionMultiplier = mult;
                        Debug.Log($"[TAXI MISSION CONTROLLER] Fisica Applicata ({policy}): MaxSpeed={paramsData.max_speed}, Acc={paramsData.acceleration}, ConsumptionMult={mult:F2}x");
                    }
                    else
                    {
                        Debug.Log($"[TAXI MISSION CONTROLLER] Fisica Applicata ({policy}): MaxSpeed={paramsData.max_speed}, Acc={paramsData.acceleration} (Battery not found)");
                    }
                }
            });
        }

    // Restituisce la condizione meteo corrente per la query KB.
    private string GetCurrentWeather()
    {
        if (WeatherManager.Instance != null && WeatherManager.Instance.IsRaining)
        {
            return "rain";
        }
        return null;
    }

    // Gestisce i cambi di meteo. Invalida la cache e ricalcola il percorso.
    // I pericoli meteorologici si applicano solo quando il passeggero è a bordo (DrivingToDropoff).
    private void HandleWeatherChanged(bool isRaining)
    {
        string weatherStr = isRaining ? "rain" : "clear";
        Debug.Log($"[TAXI MISSION CONTROLLER] Situazione meteo cambiata in: {weatherStr}");

        // Invalida la cache per ottenere i moltiplicatori aggiornati con il nuovo meteo.
        if (KBService.Instance != null)
        {
            KBService.Instance.InvalidateCache();
        }

        // Quando si va a prendere il passeggero, la priorità è la velocità - nessun penalty meteo
        if (currentState == MissionState.DrivingToDropoff)
        {
            Debug.Log("[TAXI MISSION CONTROLLER] Passeggero a bordo - ricalcolo per sicurezza meteo");
            string weather = isRaining ? "rain" : null;
            if (KBService.Instance != null)
            {
                KBService.Instance.GetZoneMultipliers(currentDrivingPolicy, weather, (multipliers) =>
                {
                    Debug.Log($"[TAXI MISSION CONTROLLER] Ricalcolo per meteo: {multipliers.Count} moltiplicatori per '{currentDrivingPolicy}' + '{weatherStr}'");
                    RecalculateRouteWithMultipliers(multipliers, currentDrivingPolicy);
                    string reason = isRaining ? "weather_rain" : "weather_clear";
                    OnRouteRecalculated?.Invoke(reason);
                });
            }
        }
        else if (currentState == MissionState.DrivingToPickup)
        {
            Debug.Log("[TAXI MISSION CONTROLLER] Nessun passeggero - meteo ignorato per percorso di prelievo");
        }
    }
    
    // Ricalcola il percorso corrente con i moltiplicatori delle zone forniti.
    private void RecalculateRouteWithMultipliers(Dictionary<string, float> multipliers, string policyName)
    {
        Debug.Log($"[TAXI MISSION CONTROLLER] Ricalcolo percorso: policy={policyName}, moltiplicatori={multipliers?.Count ?? 0}");
        
        // Se stiamo navigando verso il dropoff, ricalcola il percorso
        if (currentState == MissionState.DrivingToDropoff && finalDropOffNode != null)
        {
            // Usa sempre il nodo più vicino alla posizione corrente del taxi come punto di partenza
            var startNode = GetPathfindingStartNode(allowOffGraph: false);
            Debug.Log($"[TAXI MISSION CONTROLLER] A* da '{startNode?.name}' a '{finalDropOffNode?.name}'");
            
            var path = AStarPathfinder.FindPath(startNode, finalDropOffNode, multipliers);
            
            // Applica il nuovo percorso solo se ha più di 1 nodo (altrimenti il taxi è già alla destinazione)
            if (path != null && path.Count > 1)
            {
                List<Transform> pathTransforms = new List<Transform>();
                foreach (var n in path) pathTransforms.Add(n.transform);
                carBrain.SetNewPath(pathTransforms);
                Debug.Log($"[TAXI MISSION CONTROLLER] Percorso ricalcolato con policy '{policyName}': {path.Count} nodi");
            }
            else if (path != null && path.Count == 1)
            {
                Debug.Log($"[TAXI MISSION CONTROLLER] Già alla destinazione, nessun cambio percorso necessario");
            }
            else
            {
                Debug.LogWarning($"[TAXI MISSION CONTROLLER] Impossibile ricalcolare il percorso con policy '{policyName}', mantenendo il percorso corrente");
            }
        }
        else if (currentState == MissionState.DrivingToPickup && currentNavTargetNode != null)
        {
            // Ricalcola anche il percorso di prelievo se necessario
            var nearestNode = GetPathfindingStartNode(allowOffGraph: false);
            var path = AStarPathfinder.FindPath(nearestNode, currentNavTargetNode, multipliers);
            
            // Applica il nuovo percorso solo se ha più di 1 nodo
            if (path != null && path.Count > 1)
            {
                List<Transform> pathTransforms = new List<Transform>();
                foreach (var n in path) pathTransforms.Add(n.transform);
                carBrain.SetNewPath(pathTransforms);
                Debug.Log($"[TAXI MISSION CONTROLLER] Percorso di prelievo ricalcolato con policy '{policyName}': {path.Count} nodi");
            }
            else if (path != null && path.Count == 1)
            {
                Debug.Log($"[TAXI MISSION CONTROLLER] Già al punto di prelievo, nessun cambio percorso necessario");
            }
        }
    }

    // Termina la corsa anticipatamente al prossimo nodo di sosta disponibile.
    public bool EndRideAtNearestStop(bool passengerOnboardAtRequest)
    {
        if (currentState != MissionState.DrivingToDropoff && currentState != MissionState.DrivingToPickup)
        {
            Debug.LogWarning("[TAXI MISSION CONTROLLER] EndRideAtNearestStop: impossibile, stato non valido");
            return false;
        }

        var navModule = carBrain.GetComponent<CarBrain_Navigation>();
        if (navModule == null || !navModule.HasPath())
        {
            Debug.LogWarning("[TAXI MISSION CONTROLLER] EndRideAtNearestStop: Nessun path attivo!");
            return false;
        }

        WaypointNode nextStoppingNode = FindNextStoppingNodeInCurrentPath(navModule);

        if (nextStoppingNode == null)
        {
            Debug.LogWarning("[TAXI MISSION CONTROLLER] EndRideAtNearestStop: Nessun nodo di sosta trovato nel percorso corrente. Impossibile terminare corsa.");
            return false;
        }

        Debug.Log($"[TAXI MISSION CONTROLLER] Fine corsa anticipata: fermata al prossimo stop '{nextStoppingNode.name}' nel percorso corrente");

        // Aggiorna stato passeggero
        if (!passengerOnboardAtRequest)
        {
            passengerOnboard = false;
            currentPassenger = null;
            carBrain.SetCurrentPassenger(null);
        }

        // Tronca il path al nodo di sosta (NON ricalcola)
        TruncatePathToNode(navModule, nextStoppingNode);
        
        // Aggiorna stato missione
        currentNavTargetNode = nextStoppingNode;
        currentState = MissionState.DrivingToSafeStop;

        return true;
    }

    // Trova il prossimo nodo con isStoppingAllowed=true nel percorso corrente.
    private WaypointNode FindNextStoppingNodeInCurrentPath(CarBrain_Navigation navModule)
    {
        if (navModule == null || navModule.Path == null || navModule.Path.waypoints == null)
            return null;

        var waypoints = navModule.Path.waypoints;
        Vector3 taxiPos = carBrain.transform.position;

        // 1. Calcola distanza percorsa dal taxi (dalla partenza del path fino alla posizione attuale)
        float taxiProgressDistance = CalculateDistanceAlongPath(waypoints, taxiPos);
        
        Debug.Log($"[TAXI MISSION CONTROLLER] Taxi procede lungo il percorso: {taxiProgressDistance:F1}m");

        // 2. Cerca il primo nodo stopping che è AVANTI rispetto al taxi
        float accumulatedDistance = 0f;
        
        for (int i = 0; i < waypoints.Count; i++)
        {
            var node = waypoints[i].GetComponent<WaypointNode>();
            
            // Calcola distanza accumulata fino a questo waypoint
            if (i > 0)
            {
                accumulatedDistance += Vector3.Distance(
                    waypoints[i - 1].position,
                    waypoints[i].position
                );
            }
            
            // Se il nodo è un stopping node E è AVANTI rispetto al taxi
            if (node != null && node.isStoppingAllowed)
            {
                // Margine di sicurezza: il nodo deve essere almeno 5m avanti
                float distanceAhead = accumulatedDistance - taxiProgressDistance;
                
                if (distanceAhead >= 5f)
                {
                    Debug.Log($"[TAXI MISSION CONTROLLER] Trovato stopping node '{node.name}' all'indice {i}, {distanceAhead:F1}m AVANTI al taxi");
                    return node;
                }
                else
                {
                    Debug.LogWarning($"[TAXI MISSION CONTROLLER] Nodo stopping '{node.name}' trovato ma troppo vicino/indietro ({distanceAhead:F1}m), skippo...");
                }
            }
        }

        Debug.LogWarning("[TAXI MISSION CONTROLLER] Nessun nodo stopping trovato AVANTI al taxi nel percorso corrente");
        return null;
    }

    // Calcola la distanza percorsa dal taxi lungo il path.
    // Trova il segmento del path più vicino al taxi e calcola la distanza accumulata fino a quel punto.
    private float CalculateDistanceAlongPath(List<Transform> waypoints, Vector3 taxiPos)
    {
        if (waypoints.Count < 2) return 0f;

        float accumulatedDistance = 0f;
        float bestDistance = float.MaxValue;
        float taxiProgress = 0f;

        // Trova il segmento più vicino al taxi
        for (int i = 0; i < waypoints.Count - 1; i++)
        {
            Vector3 p1 = waypoints[i].position;
            Vector3 p2 = waypoints[i + 1].position;

            // Proietta la posizione del taxi sul segmento
            Vector3 segment = p2 - p1;
            Vector3 toTaxi = taxiPos - p1;
            float segmentLength = segment.magnitude;

            if (segmentLength < 0.01f) continue;

            // Parametro t lungo il segmento (0 = p1, 1 = p2)
            float t = Mathf.Clamp01(Vector3.Dot(toTaxi, segment) / (segmentLength * segmentLength));
            Vector3 closestPoint = p1 + segment * t;

            float distanceToSegment = Vector3.Distance(taxiPos, closestPoint);

            // Se questo segmento è il più vicino al taxi
            if (distanceToSegment < bestDistance)
            {
                bestDistance = distanceToSegment;
                // La distanza percorsa è: distanza accumulata fino a p1 + distanza lungo il segmento
                taxiProgress = accumulatedDistance + (segmentLength * t);
            }

            accumulatedDistance += segmentLength;
        }

        return taxiProgress;
    }

    // Tronca il path corrente fino al nodo specificato (incluso).
    private void TruncatePathToNode(CarBrain_Navigation navModule, WaypointNode targetNode)
    {
        if (navModule == null || navModule.Path == null || navModule.Path.waypoints == null)
            return;

        var waypoints = navModule.Path.waypoints;
        
        // Trova l'indice del nodo target
        int targetIndex = -1;
        for (int i = 0; i < waypoints.Count; i++)
        {
            var node = waypoints[i].GetComponent<WaypointNode>();
            if (node == targetNode)
            {
                targetIndex = i;
                break;
            }
        }

        if (targetIndex == -1)
        {
            Debug.LogError($"[TAXI MISSION CONTROLLER] TruncatePathToNode: Nodo '{targetNode.name}' non trovato nel path!");
            return;
        }

        // Tronca la lista waypoints fino a targetIndex (incluso)
        List<Transform> truncatedWaypoints = new List<Transform>();
        for (int i = 0; i <= targetIndex; i++)
        {
            truncatedWaypoints.Add(waypoints[i]);
        }

        Debug.Log($"[TAXI MISSION CONTROLLER] Path troncato: da {waypoints.Count} a {truncatedWaypoints.Count} waypoints (fino a '{targetNode.name}')");
        
        int currentIdx = navModule.GetCurrentWaypointRawIndex();
        Debug.Log($"[TAXI MISSION CONTROLLER] Prima del troncamento: CurrentWaypointIndex (raw) = {currentIdx}");
        
        navModule.Path.waypoints = truncatedWaypoints;
        
        navModule.Path.CachePath();
        
        if (currentIdx < 0 || currentIdx >= truncatedWaypoints.Count)
        {
            navModule.SetCurrentWaypointIndex(truncatedWaypoints.Count - 1);
            Debug.LogWarning($"[TAXI MISSION CONTROLLER] CurrentWaypointIndex era {currentIdx}, oltre il troncamento. Impostato a {truncatedWaypoints.Count - 1}");
        }
        else
        {
            Debug.Log($"[TAXI MISSION CONTROLLER] Dopo il troncamento: CurrentWaypointIndex preservato = {currentIdx}");
        }
        
        carBrain.PathRefreshed();
    }

    public void CancelCurrentMission()
    {
        if (currentState == MissionState.Idle) return;
        
        Debug.Log("[TAXI MISSION CONTROLLER] Mission cancelled externally");
        
        if (carBrain != null)
        {
            carBrain.SetCurrentPassenger(null);
        }
        
        if (currentPassenger != null)
        {
            var agent = currentPassenger.GetComponent<UnityEngine.AI.NavMeshAgent>();
            if (agent != null)
            {
                agent.isStopped = false;
            }
            
            currentPassenger.ReturnToWandering();
            
            currentPassenger = null;
        }
        
        currentState = MissionState.Idle;
        currentNavTargetNode = null;
        finalDropOffNode = null;
        passengerOnboard = false;
    }

    private float CalculatePathDistanceLocal(List<WaypointNode> path)
    {
        float totalDistance = 0f;
        for (int i = 0; i < path.Count - 1; i++)
        {
            totalDistance += Vector3.Distance(path[i].transform.position, path[i + 1].transform.position);
        }
        return totalDistance;
    }

    // Trova una posizione valida sul marciapiede (escludendo l'area "Dangerous").
    private bool TryGetSidewalkPosition(Vector3 desiredPosition, float searchRadius, out Vector3 result)
    {
        result = desiredPosition;

        int dangerousArea = UnityEngine.AI.NavMesh.GetAreaFromName("Dangerous");
        int dangerousMask = dangerousArea != -1 ? (1 << dangerousArea) : 0;
        int allAreas = UnityEngine.AI.NavMesh.AllAreas;
        int sidewalkMask = allAreas & ~dangerousMask;

        UnityEngine.AI.NavMeshHit hit;
        if (UnityEngine.AI.NavMesh.SamplePosition(desiredPosition, out hit, searchRadius, sidewalkMask))
        {
            result = hit.position;
            return true;
        }

        return false;
    }

    private bool CanStartNewMission()
    {
        return currentState == MissionState.Idle || currentState == MissionState.DrivingToChargingStation || currentState == MissionState.Charging;
    }

    private WaypointNode FindNearestChargingStation()
    {
        WaypointNode nearestCharger = null;
        float minDst = float.MaxValue;
        Vector3 carPos = carBrain.transform.position;

        foreach (var node in WaypointNode.AllNodes)
        {
            if (node.nodeType == WaypointNode.WaypointType.ChargingStation)
            {
                float dst = Vector3.Distance(carPos, node.transform.position);
                if (dst < minDst)
                {
                    minDst = dst;
                    nearestCharger = node;
                }
            }
        }

        return nearestCharger;
    }

    System.Collections.IEnumerator HandleRoadBlockSequence(WaypointNode blockedNode)
    {
        blockedNode.isBlocked = true;
        yield return new WaitForSeconds(1.0f);
        bool pathFound = TryRecalculatePath();
        if (!pathFound)
        {
            blockedNode.isBlocked = false;
            StartCoroutine(WaitAndRetryRoutine(1.0f));
        }
        else
        {
            carBrain.PathRefreshed();
            StartCoroutine(ResetNodeRoutine(blockedNode, 15f));
            OnRouteRecalculated?.Invoke("roadblock");
        }
    }

    System.Collections.IEnumerator WaitAndRetryRoutine(float delay)
    {
        yield return new WaitForSeconds(delay);
        if (TryRecalculatePath()) carBrain.PathRefreshed();
    }

    System.Collections.IEnumerator ResetNodeRoutine(WaypointNode node, float delay)
    {
        yield return new WaitForSeconds(delay);
        if (node != null) node.isBlocked = false;
    }

    public struct DestinationChangeResult
    {
        public bool success;
        public string errorMessage;
        public float distanceKm;
        public float estimatedTimeMinutes;
        public bool needsRecharge;
        public float rechargeTimeMinutes;
        public string newDestinationName;
    }
}
