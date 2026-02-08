using UnityEngine;
using System;
using System.Collections;
using System.Collections.Generic;

public class MissionCoordinator : MonoBehaviour
{
    [Header("Riferimenti")]
    public UnityBackendConnector connector;
    public CarBattery carBattery;
    public TaxiMissionController missionController;
    public CarBrain carBrain;

    [Header("Impostazioni")]
    //Fattore globale Unity units → km
    [SerializeField] private float unitsToKmFactor = SimulationUnits.DefaultUnitsToKmFactor;
    //Forza il fattore di default (0.001) per evitare valori incoerenti
    public bool forceDefaultUnitsToKmFactor = true;
    //Stima dei minuti per ogni missione in coda
    public float queueWaitMinutesPerMission = 5f;
    public bool debugLog = true;
    public bool logDistanceCalibration = false;

    [Header("FSM")]
    public CoordinatorState currentState = CoordinatorState.Idle;

    private MissionData currentMission;
    private readonly Queue<MissionData> missionQueue = new Queue<MissionData>();
    private readonly Dictionary<string, MissionData> pendingQueueConfirmations = new Dictionary<string, MissionData>();
    private readonly List<PedestrianAI> allPedestrians = new List<PedestrianAI>();

    private bool endRideResponseSent = false;

    public event Action<string, string> OnMissionStatusChanged;

    public enum CoordinatorState
    {
        Idle,
        EvaluatingBooking,
        PreChargeForMission,
        DrivingToPickup,
        DrivingToDropoff,
        EndingRideToSafeStop,
        ReturningToStation,
        Charging
    }

    void Awake()
    {
        float factor = forceDefaultUnitsToKmFactor ? SimulationUnits.DefaultUnitsToKmFactor : unitsToKmFactor;
        SimulationUnits.SetUnitsToKmFactor(factor);
        if (debugLog && forceDefaultUnitsToKmFactor && !Mathf.Approximately(unitsToKmFactor, factor))
        {
            Log($"[MISSION COORDINATOR] UnitsToKmFactor override -> {factor:F4} (valore impostato nell'inspector: {unitsToKmFactor:F4})");
        }
    }

    void Start()
    {
        if (connector == null) connector = UnityBackendConnector.Instance;
        if (carBattery == null) carBattery = FindFirstObjectByType<CarBattery>();
        if (missionController == null) missionController = FindFirstObjectByType<TaxiMissionController>();
        if (carBrain == null) carBrain = FindFirstObjectByType<CarBrain>();

        if (connector != null)
        {
            connector.OnMessageReceived += HandleBackendMessage;
            connector.OnConnected += HandleConnectionEstablished;
            Log("[MISSION COORDINATOR] MissionCoordinator inizializzato e in ascolto");
        }

        if (missionController != null)
        {
            missionController.OnPassengerBoarded += HandlePassengerBoarded;
            missionController.OnReachedDropoff += HandleRideCompleted;
            missionController.OnReachedSafeStop += HandleRideCompleted;
            missionController.OnChargingStarted += HandleChargingStarted;
            missionController.OnChargingCompleted += HandleChargingCompleted;
            missionController.OnRouteRecalculated += HandleRouteRecalculated;
        }

        StartCoroutine(WaitForPOIRegistry());
        allPedestrians.AddRange(FindObjectsByType<PedestrianAI>(FindObjectsSortMode.None));
    }

    void OnDestroy()
    {
        if (connector != null)
        {
            connector.OnMessageReceived -= HandleBackendMessage;
            connector.OnConnected -= HandleConnectionEstablished;
        }

        if (missionController != null)
        {
            missionController.OnPassengerBoarded -= HandlePassengerBoarded;
            missionController.OnReachedDropoff -= HandleRideCompleted;
            missionController.OnReachedSafeStop -= HandleRideCompleted;
            missionController.OnChargingStarted -= HandleChargingStarted;
            missionController.OnChargingCompleted -= HandleChargingCompleted;
            missionController.OnRouteRecalculated -= HandleRouteRecalculated;
        }
    }

    void Update()
    {
        if (currentState == CoordinatorState.PreChargeForMission && currentMission != null)
        {
            if (!carBattery.IsCharging && missionController != null)
            {
                if (missionController.currentState != TaxiMissionController.MissionState.DrivingToChargingStation &&
                    missionController.currentState != TaxiMissionController.MissionState.Charging)
                {
                    missionController.BeginChargingRoutine();
                }
            }
        }

        if (currentState == CoordinatorState.Idle && currentMission == null)
        {
            if (missionQueue.Count == 0 && carBattery.NeedsCharging())
            {
                BeginReturnToCharging();
            }
        }
    }

    private void HandleConnectionEstablished()
    {
        Log("[MISSION COORDINATOR] Connessione stabilita! Eseguo handshake...");

        string jsonPing = "{" +
            "\"type\": \"unity_message\"," +
            "\"session_id\": \"" + connector.SessionId + "\"," +
            "\"action\": \"ping\"," +
            "\"payload\": {}" +
        "}";

        connector.SendToBackend(jsonPing);

        float currentBat = carBattery != null ? carBattery.currentBattery : 100f;
        Vector3 pos = carBrain != null ? carBrain.transform.position : transform.position;

        string jsonStatus = "{" +
            "\"type\": \"unity_message\"," +
            "\"session_id\": \"" + connector.SessionId + "\"," +
            "\"action\": \"status\"," +
            "\"payload\": {" +
                "\"status\": \"idle\"," +
                "\"battery\": " + currentBat.ToString("F0") + "," +
                "\"position\": {\"x\": " + pos.x.ToString("F1").Replace(",", ".") + ", \"y\": " + pos.z.ToString("F1").Replace(",", ".") + "}" +
            "}" +
        "}";

        connector.SendToBackend(jsonStatus);
    }

    IEnumerator WaitForPOIRegistry()
    {
        yield return new WaitForSeconds(0.5f);
        if (POIRegistry.Instance == null)
        {
            var go = new GameObject("POIRegistry");
            go.AddComponent<POIRegistry>();
        }
    }

    private void HandleBackendMessage(string jsonMessage)
    {
        try
        {
            var baseMsg = JsonUtility.FromJson<BaseMessage>(jsonMessage);

            if (baseMsg.type == "richiesta_prenotazione")
            {
                var request = JsonUtility.FromJson<RichiestaPrenotazione>(jsonMessage);
                StartCoroutine(ProcessBookingRequest(request));
            }
            else if (baseMsg.type == "risposta_coda_attesa")
            {
                var response = JsonUtility.FromJson<RispostaCodaAttesa>(jsonMessage);
                HandleQueueResponse(response);
            }
            else if (baseMsg.type == "cambio_destinazione")
            {
                var request = JsonUtility.FromJson<CambioDestinazioneRequest>(jsonMessage);
                HandleDestinationChange(request);
            }
            else if (baseMsg.type == "fine_corsa")
            {
                var request = JsonUtility.FromJson<FineCorsaRequest>(jsonMessage);
                HandleEndRide(request);
            }
            else if (baseMsg.type == "annulla_prenotazione")
            {
                HandleBookingCancellation(baseMsg.session_id);
            }
            else if (baseMsg.type == "cambio_policy")
            {
                var request = JsonUtility.FromJson<CambioPolicyRequest>(jsonMessage);
                HandlePolicyChange(request);
            }
        }
        catch (Exception ex) { Debug.LogWarning($"[MISSION_COORDINATOR] Parse Error: {ex.Message}"); }
    }

    private IEnumerator ResolvePolicyMultiplier(string policyName, System.Action<float> callback)
    {
        if (string.IsNullOrEmpty(policyName) || KBService.Instance == null)
        {
            callback(1.0f);
            yield break;
        }

        bool done = false;
        KBService.Instance.GetPolicyParameters(policyName, (paramsData) =>
        {
            float mult = paramsData.consumption_multiplier > 0.1f ? paramsData.consumption_multiplier : 1.0f;
            callback(mult);
            done = true;
        });

        // Wait per la callback con timeout
        float timeout = 2.0f;
        while (!done && timeout > 0)
        {
            timeout -= Time.deltaTime;
            yield return null;
        }
        
        if (!done)
        {
            Debug.LogWarning($"[MISSION COORDINATOR] Timeout resolving policy '{policyName}'. Using default 1.0x");
            callback(1.0f);
        }
    }

    private IEnumerator ProcessBookingRequest(RichiestaPrenotazione request)
    {
        if (request == null || request.payload == null || request.payload.destinazione == null)
        {
            yield break;
        }

        bool canAcceptNow = !IsMissionActive();
        CoordinatorState previousState = currentState;

        if (canAcceptNow)
        {
            SetState(CoordinatorState.EvaluatingBooking, "Valuto prenotazione");
        }

        while (POIRegistry.Instance == null) yield return new WaitForSeconds(0.1f);

        WaypointNode destination = ResolveDestinationNode(request.payload.destinazione);
        if (destination == null)
        {
            SendErrorResponse(request.session_id, $"Destinazione '{request.payload.destinazione.nome}' non trovata");
            if (canAcceptNow) SetState(previousState, "Prenotazione fallita");
            yield break;
        }

        PedestrianAI passenger = null;
        WaypointNode pickupNode = null;
        SelectPedestrianByUserId(request.payload.user_id, out passenger, out pickupNode);

        // Attesa per il posizionamento del pedone. Se il pedone non è in una posizione di pickup, attendo che ci arrivi.
        if (passenger != null && (passenger.currentState == PedestrianAI.AIState.MovingToPickup || !passenger.IsOnSidewalk()))
        {
            Debug.Log($"[MISSION COORDINATOR][WAIT] Il pedone '{passenger.name}' si sta riposizionando, attendo che arrivi...");
            float waitTimeout = 0f;
            float maxWaitTime = 60f; // Massima attesa per il riposizionamento del pedone
            
            while (passenger != null && passenger.gameObject.activeSelf && (passenger.currentState == PedestrianAI.AIState.MovingToPickup || !passenger.IsOnSidewalk()))
            {
                yield return new WaitForSeconds(0.5f);
                waitTimeout += 0.5f;
                
                if (waitTimeout >= maxWaitTime)
                {
                    Debug.LogWarning($"[MISSION COORDINATOR][WAIT] Timeout riposizionamento del pedone: {maxWaitTime}s");
                    break;
                }
            }
            
            Debug.Log($"[MISSION COORDINATOR][WAIT] ✓ Riposizionamento del pedone completato dopo {waitTimeout:F1}s. Procedo con la prenotazione.");
        }

        MissionData mission = new MissionData
        {
            sessionId = request.session_id,
            userId = request.payload.user_id,
            destinationNode = destination,
            destinationName = destination.name,
            pickupNode = pickupNode,
            passenger = passenger,
            drivingPolicy = string.IsNullOrEmpty(request.payload.driving_policy) ? "Comfort" : request.payload.driving_policy,
            requiredPolicy = request.payload.required_policy // Null se l'utente può cambiare policy
        };
        
        Debug.Log($"[MISSION COORDINATOR] Driving policy ricevuta: {mission.drivingPolicy}");

        // NEW: Resolve Multiplier (Wait for async)
        float resolvedMultiplier = 1.0f;
        yield return ResolvePolicyMultiplier(mission.drivingPolicy, (m) => resolvedMultiplier = m);
        mission.policyConsumptionMultiplier = resolvedMultiplier;
        Debug.Log($"[MISSION COORDINATOR] Policy Multiplier Resolved: {mission.drivingPolicy} -> {resolvedMultiplier}x");

        if (!TryUpdateMissionEstimates(mission, carBrain.transform.position))
        {
            SendErrorResponse(request.session_id, "Destinazione irraggiungibile");
            if (canAcceptNow) SetState(previousState, "Prenotazione fallita");
            yield break;
        }

        if (!canAcceptNow)
        {
            pendingQueueConfirmations[request.session_id] = mission;
            int queueSize = missionQueue.Count + 1;
            // Calcolo dinamico: somma di tutte le missioni in coda + quella corrente
            float waitTime = CalculateQueueWaitTime(missionQueue.Count); 
            SendQueueWaitRequest(request.session_id, queueSize, waitTime);
            yield break;
        }

        currentMission = mission;
        endRideResponseSent = false;

        // Log finale di conferma
        Debug.Log($"[MISSION COORDINATOR] CONFERMA FINALE: Avvio missione per l'utente '{request.payload.user_id}' con il pedone '{(passenger != null ? passenger.name : "NULL")}' (userId='{(passenger != null ? passenger.userId : "NULL")}') verso il nodo di pickup '{(pickupNode != null ? pickupNode.name : "NULL")}'");

        TryStartMission(currentMission, allowStartDrive: true);
    }

    private void TryStartMission(MissionData mission, bool allowStartDrive)
    {
        if (mission == null) return;

        if (!TryUpdateMissionEstimates(mission, carBrain.transform.position))
        {
            SendErrorResponse(mission.sessionId, "Destinazione irraggiungibile");
            ClearCurrentMission();
            SetState(CoordinatorState.Idle, "In attesa");
            return;
        }

        bool canReach = carBattery.CanReachDestination(mission.estimatedDistanceKm);

        if (canReach)
        {
            if (!mission.bookingResponseSent)
            {
                SendConfirmResponse(mission.sessionId, mission.estimatedEtaMin, mission.estimatedDistanceKm, carBattery.currentBattery);
                mission.bookingResponseSent = true;
                mission.bookingResponseType = "confermato";
            }

            if (allowStartDrive)
            {
                StartMissionDrive(mission);
            }
            else
            {
                SetState(CoordinatorState.Idle, "In attesa");
            }
        }
        else
        {
            mission.requiresPreCharge = true;
            mission.estimatedChargeMinutes = EstimateChargingWaitMinutes(mission, carBrain.transform.position);

            if (!mission.bookingResponseSent)
            {
                SendLowBatteryResponse(mission.sessionId, carBattery.currentBattery, mission.estimatedChargeMinutes);
                mission.bookingResponseSent = true;
                mission.bookingResponseType = "batteria_scarica";
            }

            currentMission = mission;
            SetState(CoordinatorState.PreChargeForMission, "Ricarica necessaria");
            SendExplainabilityMessage("Batteria bassa: mi dirigo alla stazione di ricarica.");
            if (missionController != null)
            {
                missionController.BeginChargingRoutine();
            }
        }
    }

    private void StartMissionDrive(MissionData mission)
    {
        if (mission == null || missionController == null) return;

        if (mission.pickupNode != null && mission.passenger != null)
        {
            mission.passengerOnboard = false;
            SetState(CoordinatorState.DrivingToPickup, $"Pickup {mission.pickupNode.name}");
            
            // Sincronizza requiredPolicy con TaxiMissionController
            missionController.requiredPolicy = mission.requiredPolicy;
            if (!string.IsNullOrEmpty(mission.requiredPolicy))
            {
                Debug.Log($"[MISSION COORDINATOR] Policy '{mission.requiredPolicy}' obbligatoria per sicurezza utente - switch ECO disabilitato");
            }
            
            missionController.StartMissionWithPickup(mission.destinationNode, mission.pickupNode, mission.passenger, mission.drivingPolicy);
        }
    }

    private void StartMissionAfterPreCharge()
    {
        if (currentMission == null) return;

        if (currentMission.bookingResponseType != "confermato")
        {
            SendConfirmResponse(currentMission.sessionId, currentMission.estimatedEtaMin, currentMission.estimatedDistanceKm, carBattery.currentBattery);
            currentMission.bookingResponseSent = true;
            currentMission.bookingResponseType = "confermato";
        }

        StartMissionDrive(currentMission);
    }

    private void HandlePassengerBoarded()
    {
        if (currentMission == null) return;

        currentMission.passengerOnboard = true;
        SetState(CoordinatorState.DrivingToDropoff, $"Verso {currentMission.destinationName}");
        NotifyPassengerPickup(currentMission);
    }

    private void HandleRideCompleted()
    {
        if (currentMission == null) return;

        if (!endRideResponseSent)
        {
            SendRideCompletedResponse(currentMission.sessionId);
            endRideResponseSent = true;
        }

        ClearCurrentMission();
        TryStartNextFromQueue();
    }

    private void HandleChargingStarted()
    {
        if (currentState == CoordinatorState.ReturningToStation || currentState == CoordinatorState.PreChargeForMission)
        {
            SetState(CoordinatorState.Charging, "In ricarica");
        }
    }

    private void HandleChargingCompleted()
    {
        if (currentState == CoordinatorState.Charging)
        {
            if (currentMission != null)
            {
                Log("Ricarica completata, avvio missione.");
                StartMissionAfterPreCharge();
                return;
            }

            SetState(CoordinatorState.Idle, "In attesa");
            if (currentMission == null && missionQueue.Count > 0)
            {
                TryStartNextFromQueue();
            }
        }
    }

    private void TryStartNextFromQueue()
    {
        if (missionQueue.Count > 0)
        {
            MissionData next = missionQueue.Dequeue();
            NotifyQueuePositions();
            currentMission = next;
            endRideResponseSent = false;
            TryStartMission(next, allowStartDrive: true);
            return;
        }

        if (!BeginReturnToCharging())
        {
            SetState(CoordinatorState.Idle, "In attesa");
        }
    }

    private bool BeginReturnToCharging()
    {
        if (missionController == null) return false;

        if (missionController.BeginChargingRoutine())
        {
            SetState(CoordinatorState.ReturningToStation, "Rientro in stazione");
            return true;
        }
        return false;
    }

    private void HandleQueueResponse(RispostaCodaAttesa response)
    {
        if (!pendingQueueConfirmations.TryGetValue(response.session_id, out MissionData mission)) return;
        pendingQueueConfirmations.Remove(response.session_id);

        if (response.payload.accetta)
        {
            missionQueue.Enqueue(mission);
            int pos = missionQueue.Count;
            // Calcolo wait time considerando che ora questa missione è l'ultima della coda (index = Count - 1)
            float realWaitTime = CalculateQueueWaitTime(missionQueue.Count - 1);
            var payload = new QueueConfirmPayload { posizione_in_coda = pos, tempo_stimato_minuti = realWaitTime };
            var msg = new QueueConfirmMessage { type = "conferma_coda", session_id = response.session_id, esito = "accettato", payload = payload };
            connector.SendJsonPayload(msg);

            // Se il taxi è libero (o sta rientrando alla stazione di ricarica), avvia subito la missione!
            // Questo gestisce il caso in cui la conferma arriva DOPO che il taxi ha finito la corsa precedente.
            if (currentMission == null && (currentState == CoordinatorState.Idle || currentState == CoordinatorState.ReturningToStation))
            {
                Log("[MISSION COORDINATOR] Taxi libero, avvio missione dalla coda appena confermata.");
                TryStartNextFromQueue();
            }
        }
        else
        {
            connector.SendToBackend("{\"type\":\"conferma_coda\",\"session_id\":\"" + response.session_id + "\",\"esito\":\"rifiutato\"}");
        }
    }

    private void HandleDestinationChange(CambioDestinazioneRequest request)
    {
        if (currentMission == null || currentState != CoordinatorState.DrivingToDropoff || !currentMission.passengerOnboard)
        {
            SendDestinationChangeResponse(request.session_id, false, "Nessun passeggero a bordo", 0, 0, false, 0);
            return;
        }

        if (request.session_id != currentMission.sessionId)
        {
            SendDestinationChangeResponse(request.session_id, false, "Sessione non valida", 0, 0, false, 0);
            return;
        }

        WaypointNode newDestination = ResolveDestinationNode(request.payload.nuova_destinazione);
        if (newDestination == null)
        {
            SendDestinationChangeResponse(request.session_id, false, "Destinazione non trovata", 0, 0, false, 0);
            return;
        }

        var result = missionController.ChangeDestination(newDestination);
        if (result.success)
        {
            currentMission.destinationNode = newDestination;
            currentMission.destinationName = newDestination.name;
            currentMission.estimatedDistanceKm = result.distanceKm;
            currentMission.estimatedEtaMin = result.estimatedTimeMinutes;
            SendDestinationChangeResponse(
                request.session_id,
                true,
                result.errorMessage,
                result.distanceKm,
                result.estimatedTimeMinutes,
                result.needsRecharge,
                result.rechargeTimeMinutes
            );
            OnMissionStatusChanged?.Invoke("rerouting", $"Nuova destinazione: {newDestination.name}");
            
            // AGGIORNAMENTO CODA: Se la durata corrente cambia, avvisiamo tutti quelli in attesa
            NotifyQueuePositions();
        }
        else
        {
            SendDestinationChangeResponse(request.session_id, false, result.errorMessage, 0, 0, false, 0);
        }
    }

    private void HandlePolicyChange(CambioPolicyRequest request)
    {
        StartCoroutine(ProcessPolicyChangeCoroutine(request));
    }

    private IEnumerator ProcessPolicyChangeCoroutine(CambioPolicyRequest request)
    {
        if (currentMission == null)
        {
            Debug.LogWarning("[MISSION COORDINATOR] Policy richiesta ma nessuna missione attiva");
            yield break;
        }

        if (request.session_id != currentMission.sessionId)
        {
            Debug.LogWarning($"[MISSION COORDINATOR] Cambio di policy non valido: {request.session_id} vs {currentMission.sessionId}");
            yield break;
        }

        string newPolicy = request.payload?.nuova_policy;
        if (string.IsNullOrEmpty(newPolicy))
        {
            Debug.LogWarning("[MISSION COORDINATOR] Policy richiesta non valida");
            yield break;
        }

        // Se la policy richiesta è già quella corrente, avvisa l'utente
        if (newPolicy.Equals(currentMission.drivingPolicy, System.StringComparison.OrdinalIgnoreCase))
        {
            Debug.Log($"[MISSION COORDINATOR] Policy già impostata a '{currentMission.drivingPolicy}', ignoro richiesta.");
            SendExplainabilityMessage($"Modalità {currentMission.drivingPolicy} già attiva.", -1f, currentMission.drivingPolicy);
            yield break;
        }



        // Resolve Multiplier
        float multiplier = 1.0f;
        yield return ResolvePolicyMultiplier(newPolicy, (m) => multiplier = m);

        // Ottieni i moltiplicatori di zona per la nuova policy (per calcolare il percorso A* corretto)
        Dictionary<string, float> newPolicyZoneMultipliers = null;
        string weather = WeatherManager.Instance != null && WeatherManager.Instance.IsRaining ? "rain" : null;
        
        bool multipliersFetched = false;
        if (KBService.Instance != null)
        {
            KBService.Instance.GetZoneMultipliers(newPolicy, weather, (mults) => {
                newPolicyZoneMultipliers = mults;
                multipliersFetched = true;
            });
            
            // Attendi il fetch (max 2 secondi)
            float waitTime = 0f;
            while (!multipliersFetched && waitTime < 2f)
            {
                yield return new WaitForSeconds(0.1f);
                waitTime += 0.1f;
            }
        }

        // Security Check (remaining route + return to charger)
        // IMPORTANTE: Calcoliamo il percorso con i pesi della NUOVA policy, non usiamo la distanza del percorso attuale
        float tripConsumption = 0f;
        float tripDistanceKm = 0f;

        if (carBrain != null && carBattery != null)
        {

            // Il cambio policy avviene sempre con passeggero a bordo (DrivingToDropoff)
            // Calcola il percorso dalla posizione attuale alla destinazione con i pesi della NUOVA policy
            WaypointNode currentNode = AStarPathfinder.GetClosestNode(carBrain.transform.position);
            
            if (currentNode != null && currentMission.destinationNode != null)
            {
                var pathToDestination = AStarPathfinder.FindPath(
                    currentNode, 
                    currentMission.destinationNode, 
                    newPolicyZoneMultipliers  // <-- USA I PESI DELLA NUOVA POLICY
                );
                
                if (pathToDestination != null && pathToDestination.Count > 0)
                {
                    tripDistanceKm = CalculatePathDistance(pathToDestination) * SimulationUnits.UnitsToKmFactor;
                    Debug.Log($"[MISSION COORDINATOR] Percorso residuo con policy '{newPolicy}': {pathToDestination.Count} nodi, {tripDistanceKm:F2}km");
                }
                else
                {
                    tripDistanceKm = carBrain.GetRemainingPathDistance() * SimulationUnits.UnitsToKmFactor;
                    Debug.LogWarning($"[MISSION COORDINATOR] Fallback distanza percorso attuale: {tripDistanceKm:F2}km");
                }
                
                tripConsumption = carBattery.GetEstimatedConsumption(tripDistanceKm, multiplier);
            }
            else
            {
                // Fallback: usa la distanza del percorso attuale
                tripDistanceKm = carBrain.GetRemainingPathDistance() * SimulationUnits.UnitsToKmFactor;
                tripConsumption = carBattery.GetEstimatedConsumption(tripDistanceKm, multiplier);
            }

            float returnToChargerKm = 0f;
            if (currentMission != null && currentMission.destinationNode != null)
            {
                WaypointNode nearestCharger = FindNearestChargingStation(currentMission.destinationNode.transform.position);
                if (nearestCharger != null)
                {
                    var pathReturn = AStarPathfinder.FindPath(currentMission.destinationNode, nearestCharger);
                    if (pathReturn != null && pathReturn.Count > 0)
                    {
                        returnToChargerKm = CalculatePathDistance(pathReturn) * SimulationUnits.UnitsToKmFactor;
                    }
                    else
                    {
                        returnToChargerKm = Vector3.Distance(currentMission.destinationNode.transform.position, nearestCharger.transform.position) * SimulationUnits.UnitsToKmFactor * 1.3f;
                    }
                }
            }

            float returnConsumption = carBattery.GetEstimatedConsumption(returnToChargerKm, 0.7f);
            float requiredBattery = tripConsumption + returnConsumption + carBattery.minimumBatteryThreshold;

            if (carBattery.currentBattery < requiredBattery)
            {
                SendExplainabilityMessage($"Impossibile usare {newPolicy}: batteria insufficiente per completare la corsa e tornare alla colonnina.");
                Debug.LogWarning($"[MISSION COORDINATOR] Policy change {newPolicy} rejected. Required={requiredBattery:F1}% Current={carBattery.currentBattery:F1}% TripKm={tripDistanceKm:F2}km ReturnKm={returnToChargerKm:F2}km");
                yield break;
            }
        }

        // Applicazione della nuova policy
        currentMission.drivingPolicy = newPolicy;
        currentMission.policyConsumptionMultiplier = multiplier; 
        Debug.Log($"[MISSION COORDINATOR] Cambio policy: {newPolicy} (Mult: {multiplier}x)");

        if (missionController != null)
        {
            missionController.ChangeDrivingPolicy(newPolicy);
            
            float newEta = carBattery.EstimateTimeMinutes(tripDistanceKm);
            currentMission.estimatedEtaMin = newEta; // Update mission ETA estimate
            
            SendExplainabilityMessage($"✅ Modalità {newPolicy} attivata con successo. Il percorso è stato ricalcolato sulla base della policy scelta e potrebbe aver subito variazioni.", newEta, newPolicy);
            NotifyQueuePositions();
        }
    }

    private void HandleEndRide(FineCorsaRequest request)
    {
        if (currentMission == null || (currentState != CoordinatorState.DrivingToPickup && currentState != CoordinatorState.DrivingToDropoff))
        {
            SendEndRideResponse(request.session_id, false, "Nessuna corsa attiva");
            return;
        }

        if (request.session_id != currentMission.sessionId)
        {
            SendEndRideResponse(request.session_id, false, "Sessione non valida");
            return;
        }

        bool success = missionController.EndRideAtNearestStop(currentMission.passengerOnboard);
        if (success)
        {
            currentMission.endRideRequested = true;
            SetState(CoordinatorState.EndingRideToSafeStop, "Termino corsa");
            SendExplainabilityMessage("Richiesta fine corsa: mi fermo al punto sicuro più vicino.");
        }
        else
        {
            SendEndRideResponse(request.session_id, false, "Impossibile terminare la corsa ora");
        }
    }

    private void HandleBookingCancellation(string sessionId)
    {
        Log($"Ricevuta richiesta annullamento per session {sessionId}");

        // 1) Cancellazione di una prenotazione in attesa di conferma coda
        if (pendingQueueConfirmations.ContainsKey(sessionId))
        {
            pendingQueueConfirmations.Remove(sessionId);
            Log($"[MISSION COORDINATOR] Annullamento prenotazione in attesa di conferma coda (session {sessionId})");
            SendCancellationResponse(sessionId, true, null);
            return;
        }

        // 2) Cancellazione di una prenotazione già in coda
        if (TryRemoveMissionFromQueue(sessionId, out _))
        {
            Log($"[MISSION COORDINATOR] Annullamento prenotazione in coda (session {sessionId})");
            SendCancellationResponse(sessionId, true, null);
            NotifyQueuePositions();

            if (currentMission == null && (currentState == CoordinatorState.Idle || currentState == CoordinatorState.ReturningToStation))
            {
                TryStartNextFromQueue();
            }
            return;
        }
        
        // 3) Cancellazione della missione corrente (solo se c'è una missione attiva con quella sessione)
        if (currentMission == null || currentMission.sessionId != sessionId)
        {
            SendCancellationResponse(sessionId, false, "Nessuna prenotazione attiva");
            return;
        }
        
        // Non permettere la cancellazione se il passeggero è già a bordo
        if (currentMission.passengerOnboard)
        {
            SendCancellationResponse(sessionId, false, "Passeggero già a bordo");
            return;
        }
        
        // Cancella la missione
        Log($"Annullamento prenotazione per {currentMission.destinationName}");
        
        // Ferma la missione corrente
        if (missionController != null)
        {
            missionController.CancelCurrentMission();
        }
        
        // Svuota i dati della missione
        ClearCurrentMission();
        
        // Invia la conferma
        SendCancellationResponse(sessionId, true, null);
        
        // Prova a iniziare la prossima missione dalla coda, o torna alla ricarica se la coda è vuota
        TryStartNextFromQueue();
    }

    private bool TryRemoveMissionFromQueue(string sessionId, out MissionData removed)
    {
        removed = null;
        if (missionQueue.Count == 0) return false;

        bool found = false;
        var snapshot = missionQueue.ToArray();
        missionQueue.Clear();

        foreach (var m in snapshot)
        {
            if (!found && m != null && m.sessionId == sessionId)
            {
                removed = m;
                found = true;
                continue;
            }
            missionQueue.Enqueue(m);
        }

        return found;
    }

    private WaypointNode ResolveDestinationNode(Destinazione dest)
    {
        if (dest == null) return null;

        WaypointNode destination = null;
        if (!string.IsNullOrEmpty(dest.poi_id_unity) && POIRegistry.Instance != null)
        {
            destination = POIRegistry.Instance.GetPOIByUnityId(dest.poi_id_unity);
        }

        if (destination == null && !string.IsNullOrEmpty(dest.nome))
        {
            destination = FindPOIByName(dest.nome);
        }

        return destination;
    }

    private bool TryUpdateMissionEstimates(MissionData mission, Vector3 startPosition)
    {
        if (mission == null || mission.destinationNode == null) return false;

        WaypointNode startNode = AStarPathfinder.GetClosestNode(startPosition);
        if (startNode == null) return false;

        float distanceUnits = 0f;

        float pickupKm = 0f;
        float dropoffKm = 0f;

        if (mission.pickupNode != null)
        {
            List<WaypointNode> pathToPickup = AStarPathfinder.FindPath(startNode, mission.pickupNode);
            if (pathToPickup == null || pathToPickup.Count == 0) return false;
            float pUnits = CalculatePathDistance(pathToPickup);
            distanceUnits += pUnits;
            pickupKm = pUnits * SimulationUnits.UnitsToKmFactor;

            List<WaypointNode> pathToDropoff = AStarPathfinder.FindPath(mission.pickupNode, mission.destinationNode);
            if (pathToDropoff == null || pathToDropoff.Count == 0) return false;
            float dUnits = CalculatePathDistance(pathToDropoff);
            distanceUnits += dUnits;
            dropoffKm = dUnits * SimulationUnits.UnitsToKmFactor;
        }
        else
        {
            List<WaypointNode> pathToDropoff = AStarPathfinder.FindPath(startNode, mission.destinationNode);
            if (pathToDropoff == null || pathToDropoff.Count == 0) return false;
            float dUnits = CalculatePathDistance(pathToDropoff);
            distanceUnits += dUnits;
            dropoffKm = dUnits * SimulationUnits.UnitsToKmFactor;
        }

        float distanceKm = distanceUnits * SimulationUnits.UnitsToKmFactor;
        mission.estimatedDistanceKm = distanceKm;
        mission.estimatedEtaMin = carBattery.EstimateTimeMinutes(distanceKm);
        
        // Calcolo del consumo stimato
        float totalConsumption = 0f;
        if (pickupKm > 0) totalConsumption += carBattery.GetEstimatedConsumption(pickupKm, 0.7f); // Eco for pickup
        totalConsumption += carBattery.GetEstimatedConsumption(dropoffKm, mission.policyConsumptionMultiplier); // User Policy for dropoff
        
        // Aggiunge il consumo per il ritorno alla stazione di ricarica più vicina
        WaypointNode nearestCharger = FindNearestChargingStation(mission.destinationNode.transform.position);
        float returnToChargerKm = 0f;
        if (nearestCharger != null) 
        {
             // Stima diretta (distanza aerea * 1.3 per strada) per performance, o A* se necessario.
             // Usiamo A* tra nodi principali per sicurezza.
            var pathReturn = AStarPathfinder.FindPath(mission.destinationNode, nearestCharger);
            if (pathReturn != null)
            {
                returnToChargerKm = CalculatePathDistance(pathReturn) * SimulationUnits.UnitsToKmFactor;
            }
            else
            {
                // Fallback
                returnToChargerKm = Vector3.Distance(mission.destinationNode.transform.position, nearestCharger.transform.position) * SimulationUnits.UnitsToKmFactor * 1.3f;
            }
        }
        float returnConsumption = carBattery.GetEstimatedConsumption(returnToChargerKm, 0.7f); // Eco for return
        
        // Totale consumo stimato: pickup + dropoff + ritorno + margine di sicurezza (5%)
        mission.requiredBatteryLevel = Mathf.Min(carBattery.maxBattery, totalConsumption + returnConsumption + 5.0f); 
        
        // Correzione per requisiti di ricarica
        mission.requiresPreCharge = carBattery.currentBattery < mission.requiredBatteryLevel;
        mission.estimatedChargeMinutes = carBattery.GetTimeToCharge(mission.requiredBatteryLevel);

        if (debugLog && logDistanceCalibration)
        {
            string startName = mission.pickupNode != null ? mission.pickupNode.name : startNode.name;
            string endName = mission.destinationNode != null ? mission.destinationNode.name : "Unknown";
            Log($"[MISSION COORDINATOR] {startName} -> {endName}: {distanceUnits:F1} units = {distanceKm:F3} km (factor {SimulationUnits.UnitsToKmFactor:F4})");
        }

        return true;
    }

    private WaypointNode FindPOIByName(string name)
    {
        string nameLower = name.ToLower();
        foreach (var node in WaypointNode.AllNodes)
        {
            if (node.nodeType == WaypointNode.WaypointType.POI && node.name.ToLower().Contains(nameLower))
                return node;
        }
        return null;
    }

    private bool SelectRandomPedestrian(out PedestrianAI selected, out WaypointNode pickup)
    {
        selected = null; pickup = null;
        if (allPedestrians.Count == 0) return false;

        for (int i = 0; i < 15; i++)
        {
            var cand = allPedestrians[UnityEngine.Random.Range(0, allPedestrians.Count)];
            if (cand.IsOnSidewalk() && cand.personality == PedestrianAI.Personality.Calm)
            {
                var stop = GetClosestStoppingNode(cand.transform.position, 15f);
                if (stop != null) { selected = cand; pickup = stop; return true; }
            }
        }
        return false;
    }

    private bool SelectPedestrianByUserId(string userId, out PedestrianAI selected, out WaypointNode pickup)
    {
        selected = null;
        pickup = null;
        
        Debug.Log($"[MISSION COORDINATOR][DEBUG] SelectPedestrianByUserId chiamato col seguente ID: '{userId}'");
        
        if (string.IsNullOrEmpty(userId) || allPedestrians.Count == 0)
        {
            Debug.Log($"[MISSION COORDINATOR][DEBUG] userId è vuoto o nessun pedone disponibile, uso selezione casuale");
            return SelectRandomPedestrian(out selected, out pickup);
        }
        
        // Debug: Log di tutti i pedoni e i loro userId
        Debug.Log($"[MISSION COORDINATOR][DEBUG] Controllo {allPedestrians.Count} pedoni per userId match:");
        
        // Cerca il pedone con userId corrispondente
        foreach (var ped in allPedestrians)
        {
            if (ped.userId != userId)
            {
                continue; // Skip - userId non corrisponde
            }
            
            Debug.Log($"[MISSION COORDINATOR] MATCH FOUND! Il pedone '{ped.name}' ha userId '{userId}'");
            
            // Check personalità calma
            bool isCalm = ped.personality == PedestrianAI.Personality.Calm;
            //Debug.Log($"[MISSION COORDINATOR][VALIDATION] - Personalità: {ped.personality} (Calm={isCalm})");
            
            // Check se è sul marciapiede
            bool onSidewalk = ped.IsOnSidewalk();
            //Debug.Log($"[MISSION COORDINATOR][VALIDATION] - IsOnSidewalk: {onSidewalk}");
            
            // Check per stopping node
            var stop = GetClosestStoppingNode(ped.transform.position, 15f);
            //Debug.Log($"[MISSION COORDINATOR][VALIDATION] - Stopping node presente entro 15m: {(stop != null ? stop.name : "NULL")}");
            
            // Se il pedone è già in posizione valida, usalo subito
            if (onSidewalk && stop != null)
            {
                selected = ped;
                pickup = stop;
                Debug.Log($"[MISSION COORDINATOR] PEDESTRIAN READY! '{ped.name}' il pedone si trova già sul marciapiede vicino a '{stop.name}'");
                return true;
            }
            
            // RIPOSIZIONAMENTO AUTOMATICO
            Debug.Log($"[MISSION COORDINATOR][REPOSITIONING] Il pedone '{ped.name}' non si trova in posizione valida. Avvio riposizionamento automatico...");
            
            WaypointNode nearestStop = GetClosestStoppingNode(ped.transform.position, 100f);
            
            if (nearestStop == null)
            {
                Debug.LogError($"[MISSION COORDINATOR][REPOSITIONING] ERRORE: Nessun stopping node trovato!");
                continue;
            }
            
            Debug.Log($"[MISSION COORDINATOR][REPOSITIONING] Stopping node più vicino: '{nearestStop.name}' a {Vector3.Distance(ped.transform.position, nearestStop.transform.position):F2}m");
            
            Vector3 pickupPosition = FindSidewalkPositionNearNode(nearestStop, 5f);
            
            if (pickupPosition == Vector3.zero)
            {
                Debug.LogError($"[MISSION COORDINATOR][REPOSITIONING] ERRORE: Impossibile trovare marciapiede vicino al nodo '{nearestStop.name}'");
                continue;
            }
            
            Debug.Log($"[MISSION COORDINATOR][REPOSITIONING] Posizione di pickup valida trovata: {pickupPosition}");
            
            bool moveOk = ped.MoveToPickupLocation(pickupPosition);
            if (!moveOk)
            {
                Debug.LogWarning($"[MISSION COORDINATOR][REPOSITIONING] ERRORE: Impossibile muovere il pedone '{ped.name}' verso il pickup.");
                continue;
            }
            
            selected = ped;
            pickup = nearestStop;
            
            Debug.Log($"[MISSION COORDINATOR] RIPOSIZIONAMENTO AVVIATO! Il pedone '{ped.name}' si sta spostando verso il nodo '{nearestStop.name}'");
            return true;
        }
        
        //Fallback: nessun pedone trovato con userId, quindi selezione casuale
        Log($"✗ Nessun pedone trovato con userId '{userId}', uso selezione casuale");
        return SelectRandomPedestrian(out selected, out pickup);
    }

    private WaypointNode GetClosestStoppingNode(Vector3 pos, float radius)
    {
        WaypointNode best = null; 
        float minDst = radius * radius;
        foreach (var node in WaypointNode.AllNodes)
        {
            if (!node.isStoppingAllowed || node.isBlocked) continue;
            float d = (node.transform.position - pos).sqrMagnitude;
            if (d < minDst) { minDst = d; best = node; }
        }
        return best;
    }

    // Trova una posizione valida sul marciapiede vicino a un nodo di fermata.
    // Questo assicura che il pedone si trovi su una superficie calpestabile adatta al pickup del taxi.
    private Vector3 FindSidewalkPositionNearNode(WaypointNode node, float searchRadius)
    {
        if (node == null) return Vector3.zero;
        
        // Andiamo a cercare le zone pericolose per evitarle
        int dangerousArea = UnityEngine.AI.NavMesh.GetAreaFromName("Dangerous");
        int dangerousMask = dangerousArea != -1 ? (1 << dangerousArea) : 0;
        int allAreas = UnityEngine.AI.NavMesh.AllAreas;
        int sidewalkMask = allAreas & ~dangerousMask;
        
        UnityEngine.AI.NavMeshHit hit;
        if (UnityEngine.AI.NavMesh.SamplePosition(node.transform.position, out hit, searchRadius, sidewalkMask))
        {
            if (dangerousMask == 0 || (hit.mask & dangerousMask) == 0)
            {
                Debug.Log($"[MISSION COORDINATOR][HELPER] Trovata posizione sul marciapiede a {hit.position} (distanza dal nodo: {Vector3.Distance(node.transform.position, hit.position):F2}m)");
                return hit.position;
            }
        }
        
        // Se il campionamento diretto fallisce, proviamo a campionare in diverse direzioni intorno al nodo
        float[] angles = { 0f, 90f, 180f, 270f, 45f, 135f, 225f, 315f };
        foreach (float angle in angles)
        {
            Vector3 direction = Quaternion.Euler(0, angle, 0) * Vector3.forward;
            Vector3 samplePoint = node.transform.position + direction * (searchRadius * 0.5f);
            
            if (UnityEngine.AI.NavMesh.SamplePosition(samplePoint, out hit, searchRadius, sidewalkMask))
            {
                if (dangerousMask == 0 || (hit.mask & dangerousMask) == 0)
                {
                    Debug.Log($"[MISSION COORDINATOR][HELPER] Trovata posizione sul marciapiede a {hit.position} (angolo {angle}°, distanza dal nodo: {Vector3.Distance(node.transform.position, hit.position):F2}m)");
                    return hit.position;
                }
            }
        }
        
        Debug.LogWarning($"[MISSION COORDINATOR][HELPER] Impossibile trovare posizione sul marciapiede vicino al nodo '{node.name}' entro il raggio {searchRadius}m");
        return Vector3.zero;
    }

    private float CalculatePathDistance(List<WaypointNode> path)
    {
        float d = 0;
        for (int i = 0; i < path.Count - 1; i++) d += Vector3.Distance(path[i].transform.position, path[i + 1].transform.position);
        return d;
    }

    private bool IsMissionActive()
    {
        return currentState == CoordinatorState.EvaluatingBooking ||
               currentState == CoordinatorState.PreChargeForMission ||
               currentState == CoordinatorState.DrivingToPickup ||
               currentState == CoordinatorState.DrivingToDropoff ||
               currentState == CoordinatorState.EndingRideToSafeStop;
    }

    private void ClearCurrentMission()
    {
        currentMission = null;
        endRideResponseSent = false;
    }

    private void SetState(CoordinatorState newState, string statusMessage)
    {
        currentState = newState;
        string status = newState == CoordinatorState.Idle ? "idle" : "active";
        OnMissionStatusChanged?.Invoke(status, statusMessage);
        Log($"State -> {newState}");
    }

    private void Log(string msg)
    { 
        if (debugLog) 
            Debug.Log(msg);
    }

    // Helper per escape caratteri speciali per JSON
    private string EscapeForJson(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return text
            .Replace("\\", "\\\\")
            .Replace("\"", "'")
            .Replace("\n", " ")
            .Replace("\r", "")
            .Replace("\t", " ");
    }

    private void HandleRouteRecalculated(string reason)
    {
        string message;
        switch (reason)
        {
            case "weather_rain":
                message = "🌧️ Pioggia in arrivo! Navigazione aggiornata per condizioni scivolose.";
                break;
            case "weather_clear":
                message = "☀️ Meteo migliorato! Navigazione aggiornata alle condizioni ottimali.";
                break;
            case "roadblock":
                message = "🚧 Ho ricalcolato il percorso per evitare un blocco stradale.";
                break;
            case "policy":
                // Evita messaggi duplicati: la conferma di cambio policy viene già inviata
                // da ProcessPolicyChangeCoroutine().
                message = "";
                break;
            case "policy_forced_eco":
                message = "⚠️ Batteria insufficiente con la policy attuale! Ho attivato la modalità ECO per garantire l'arrivo.";
                break;
            default:
                message = "Ho ricalcolato il percorso.";
                break;
        }

        // Calcola nuovo ETA
        float eta = -1f;
        if (carBrain != null && carBattery != null)
        {
            float distUnits = carBrain.GetRemainingPathDistance();
            float distKm = distUnits * SimulationUnits.UnitsToKmFactor;
            eta = carBattery.EstimateTimeMinutes(distKm);

            // AUTO-ECO FALLBACK
            if (!carBattery.CanReachDestination(distKm))
            {
                // Calcola il percorso A* con i pesi ECO per avere la distanza corretta
                float ecoDistKm = distKm; // Fallback alla distanza attuale
                
                string weather = WeatherManager.Instance != null && WeatherManager.Instance.IsRaining ? "rain" : null;
                Dictionary<string, float> ecoMultipliers = KBService.Instance?.GetZoneMultipliersSync("Eco", weather);
                
                if (ecoMultipliers != null && currentMission?.destinationNode != null)
                {
                    WaypointNode currentNode = AStarPathfinder.GetClosestNode(carBrain.transform.position);
                    if (currentNode != null)
                    {
                        var ecoPath = AStarPathfinder.FindPath(currentNode, currentMission.destinationNode, ecoMultipliers);
                        if (ecoPath != null && ecoPath.Count > 0)
                        {
                            ecoDistKm = CalculatePathDistance(ecoPath) * SimulationUnits.UnitsToKmFactor;
                            Debug.Log($"[MISSION COORDINATOR] Percorso ECO calcolato: {ecoPath.Count} nodi, {ecoDistKm:F2}km (vs {distKm:F2}km policy attuale)");
                        }
                    }
                }
                
                
                float ecoMultiplier = 0.7f; // Default fallback
                if (KBService.Instance != null)
                {
                    ecoMultiplier = KBService.Instance.GetPolicyParametersSync("Eco").consumption_multiplier;
                }
                
                float ecoCons = carBattery.GetEstimatedConsumption(ecoDistKm, ecoMultiplier);
                if (carBattery.currentBattery >= (ecoCons + carBattery.minimumBatteryThreshold))
                {
                    // CHECK: se l'utente ha una policy obbligatoria (es. gravidanza), NON possiamo fare switch
                    if (currentMission != null && !string.IsNullOrEmpty(currentMission.requiredPolicy))
                    {
                        Debug.LogWarning($"[MISSION COORDINATOR] ⚠️ Batteria insufficiente ma policy '{currentMission.requiredPolicy}' è obbligatoria per sicurezza utente! Termino corsa al safe stop.");
                        SendExplainabilityMessage($"⚠️ Batteria non sufficiente per mantenere la modalità {currentMission.requiredPolicy} fino a destinazione. Per la tua sicurezza mi fermo alla prossima fermata sicura.", eta);
                        
                        if (missionController != null) 
                        {
                            missionController.EndRideAtNearestStop(currentMission.passengerOnboard);
                        }
                        return;
                    }
                    
                    Debug.LogWarning($"[MISSION COORDINATOR] Batteria insufficiente con policy corrente. Switch automatico a ECO. Distanza ECO: {ecoDistKm:F2}km");
                    message += " ⚠️ Batteria insufficiente: Passo a ECO per arrivare.";
                    if (missionController != null) missionController.ChangeDrivingPolicy("Eco");
                     
                    // SYNC: Aggiorna policy
                    if (currentMission != null)
                    {
                        currentMission.drivingPolicy = "Eco";
                    }
                     
                    // Invia il messaggio specifico immediatamente
                    SendExplainabilityMessage("Batteria insufficiente per la policy attuale. Passo a ECO per completare la corsa.", eta, "Eco");
                    NotifyQueuePositions();
                    return; 
                }
                else
                {
                    // BATTERIA INSUFFICIENTE ANCHE IN ECO!
                    // CHECK: se l'utente ha una policy obbligatoria, ferma al safe stop con messaggio specifico
                    if (currentMission != null && !string.IsNullOrEmpty(currentMission.requiredPolicy))
                    {
                        Debug.LogWarning($"[MISSION COORDINATOR] ⛔ Batteria insufficiente e policy '{currentMission.requiredPolicy}' obbligatoria! Termino corsa al safe stop.");
                        SendExplainabilityMessage($"⛔ Batteria non sufficiente per mantenere la modalità {currentMission.requiredPolicy} fino a destinazione. Per la tua sicurezza mi fermo alla prossima fermata sicura.", -1f);
                        
                        if (missionController != null) 
                        {
                            missionController.EndRideAtNearestStop(currentMission.passengerOnboard);
                        }
                        return;
                    }
                    
                    // Switch comunque a ECO per minimizzare il consumo
                    Debug.LogWarning("[MISSION COORDINATOR] ⛔ Batteria insufficiente anche in ECO!");
                    if (missionController != null) missionController.ChangeDrivingPolicy("Eco");
                     
                    // SYNC: Aggiorna anche la policy nella missione corrente
                    if (currentMission != null) currentMission.drivingPolicy = "Eco";
                     
                    // Comportamento dipende dalla causa del ricalcolo
                    if (reason == "destination_change")
                    {
                        // Cambio destinazione richiesto dall'utente
                        // Opzione 1: Tornare alla destinazione precedente
                        // Opzione 2: Fine corsa
                        string destChangeMsg = "⛔ Mi dispiace, la batteria non è sufficiente per raggiungere la nuova destinazione. " +
                                                "Posso riportarti alla destinazione precedente oppure puoi terminare la corsa qui.";
                        SendExplainabilityMessage(destChangeMsg, -1f, "Eco");
                    }
                    else
                    {
                        // Ricalcolo per barriera/meteo/traffico
                        // Ferma al punto sicuro più vicino, scusati, vai a ricaricare
                        string emergencyMsg = "⛔ Mi scuso per il disagio! A causa del nuovo percorso, la batteria non è sufficiente per completare la corsa. " +
                                                "Ti lascerò al punto sicuro più vicino.";
                        SendExplainabilityMessage(emergencyMsg, -1f, "Eco");
                         
                        // Avvia Fine Corsa Emergenza
                        if (missionController != null)
                        {
                            bool started = missionController.EndRideAtNearestStop(currentMission?.passengerOnboard ?? false);
                            if (started)
                            {
                                SetState(CoordinatorState.EndingRideToSafeStop, "Batteria insufficiente - fermata emergenza");
                            }
                        }
                    }
                    NotifyQueuePositions();
                    return;
                }
            }
        }

        if (reason == "policy_forced_eco")
        {
            SendExplainabilityMessage(message, eta, "Eco");
        }
        else
        {
            SendExplainabilityMessage(message, eta);
        }
        
        // AGGIORNAMENTO CODA: Eventi come pioggia, policy o blocchi cambiano il tempo residuo. Avvisiamo la coda
        NotifyQueuePositions();
    }

    private void SendExplainabilityMessage(string message, float etaMinutes = -1f, string policy = null)
    {
        if (connector == null || currentMission == null || string.IsNullOrEmpty(message)) return;

        string etaPayload = "";
        if (etaMinutes > 0)
        {
             etaPayload = $", \"eta_minutes\": {etaMinutes.ToString("F1").Replace(",", ".")}";
        }
        string policyPayload = "";
        if (!string.IsNullOrEmpty(policy))
        {
            policyPayload = $", \"policy\": \"{policy}\"";
        }

        // Escape caratteri speciali JSON
        string escapedMessage = message
            .Replace("\\", "\\\\")   // Escape backslashes
            .Replace("\"", "'")      // Replace quote con gli apostrofi
            .Replace("\n", " ")      // Replace ritorno a capo con spazio (per pulizia in chat)
            .Replace("\r", "")       // Remove carriage returns
            .Replace("\t", " ");     // Replace tabs con spazio
        string json = "{" +
            "\"type\": \"unity_message\"," +
            "\"session_id\": \"" + currentMission.sessionId + "\"," +
            "\"action\": \"explainability\"," +
            "\"payload\": {" +
                "\"message\": \"" + escapedMessage + "\"" +
                 etaPayload +
                 policyPayload +
            "}" +
        "}";
        connector.SendToBackend(json);
    }
    private void NotifyPassengerPickup(MissionData mission)
    {
        if (mission == null || mission.pickupNotified || connector == null) return;
        
        // Start coroutine per aspettare il calcolo del percorso
        StartCoroutine(NotifyPickupRoutine(mission));
    }

    private System.Collections.IEnumerator NotifyPickupRoutine(MissionData mission)
    {
        // Apsetta un frame per sicurezza
        yield return new WaitForSeconds(0.1f);

        // Ricalcola ETA basata sul percorso attuale
        float etaRaw = mission.estimatedEtaMin;
        if (missionController != null && missionController.carBrain != null && missionController.carBattery != null)
        {
             float distUnits = missionController.carBrain.GetRemainingPathDistance();
             float distKm = distUnits * SimulationUnits.UnitsToKmFactor;
             etaRaw = missionController.carBattery.EstimateTimeMinutes(distKm);
             Debug.Log($"[MISSION COORDINATOR] DEBUG ETA: Units={distUnits:F1}, Km={distKm:F3}, RawMin={etaRaw:F2} (Speed {missionController.carBattery.estimatedSpeedKmh})");
        }

        float eta = etaRaw;
        string json = "{" +
            "\"type\": \"unity_message\"," +
            "\"session_id\": \"" + mission.sessionId + "\"," +
            "\"action\": \"passenger_pickup\"," +
            "\"payload\": {" +
                "\"eta_minutes\": " + eta.ToString("F2").Replace(",", ".") + "," +
                "\"destination\": \"" + mission.destinationName + "\"" +
            "}" +
        "}";
        connector.SendToBackend(json);
        mission.pickupNotified = true;
    }

    private void SendConfirmResponse(string sid, float time, float dist, float bat)
    {
        var p = new ConfirmPayload { tempo_stimato_minuti = time, distanza_km = dist, batteria_attuale = bat };
        var m = new ConfirmMessage { type = "risposta_prenotazione", session_id = sid, esito = "confermato", payload = p };
        connector.SendJsonPayload(m);
    }

    private void SendErrorResponse(string sid, string err)
    {
        connector.SendToBackend($"{{\"type\":\"risposta_prenotazione\",\"session_id\":\"{sid}\",\"esito\":\"errore\",\"payload\":{{\"messaggio\":\"{EscapeForJson(err)}\"}}}}");
    }

    private void SendLowBatteryResponse(string sid, float bat, float wait)
    {
        var p = new LowBatteryPayload { batteria_attuale = bat, tempo_attesa_minuti = wait };
        var m = new LowBatteryMessage { type = "risposta_prenotazione", session_id = sid, esito = "batteria_scarica", payload = p };
        connector.SendJsonPayload(m);
    }

    private void SendQueueWaitRequest(string sid, int count, float wait)
    {
        var p = new QueueWaitPayload { corse_in_coda = count, tempo_attesa_minuti = wait };
        var m = new QueueWaitMessage { type = "risposta_prenotazione", session_id = sid, esito = "coda_attesa", payload = p };
        connector.SendJsonPayload(m);
    }

    private void NotifyQueuePositions()
    {
        if (connector == null || missionQueue.Count == 0) return;

        int position = 1;
        int queueIndex = 0;
        foreach (var mission in missionQueue)
        {
            // Calcolo dinamico per ogni missione nella coda
            float waitMinutes = CalculateQueueWaitTime(queueIndex);
            var payload = new QueueUpdatePayload { posizione_in_coda = position, tempo_stimato_minuti = waitMinutes };
            var msg = new QueueUpdateMessage { type = "queue_update", session_id = mission.sessionId, payload = payload };
            connector.SendJsonPayload(msg);

            Log($"Queue update -> session={mission.sessionId} pos={position} wait={waitMinutes:F1}m");
            position++;
            queueIndex++;
        }
    }

    private void SendRideCompletedResponse(string sid)
    {
        connector.SendToBackend($"{{\"type\":\"risposta_fine_corsa\",\"session_id\":\"{sid}\",\"esito\":\"confermato\"}}");
    }

    private void SendDestinationChangeResponse(string sid, bool success, string msg, float dist, float time, bool recharge, float rechargeTime)
    {
        string esito = success ? (recharge ? "confermato_ricarica_necessaria" : "confermato") : "errore";
        string payload = "";

        if (success)
        {
            payload = $"\"distanza_km\": {dist.ToString("F1").Replace(",", ".")}, \"tempo_stimato_minuti\": {time.ToString("F2").Replace(",", ".")}";
            if (recharge) payload += $", \"tempo_ricarica_minuti\": {rechargeTime.ToString("F0")}";

            if(!string.IsNullOrEmpty(msg))
            {
                string escapedMsg = EscapeForJson(msg);
                payload += $", \"messaggio\": \"{escapedMsg}\"";
            }
        }
        else
        {
            // Escape caratteri speciali JSON
            string escapedMsg = EscapeForJson(msg);
            payload = $"\"messaggio\": \"{escapedMsg}\"";
        }

        string json = $"{{\"type\": \"risposta_cambio_destinazione\", \"session_id\": \"{sid}\", \"esito\": \"{esito}\", \"payload\": {{{payload}}}}}";
        connector.SendToBackend(json);
    }

    private void SendEndRideResponse(string sid, bool success, string msg)
    {
        string esito = success ? "confermato" : "errore";
        string payload = success ? "{}" : $"{{\"messaggio\": \"{EscapeForJson(msg)}\"}}";
        string json = $"{{\"type\": \"risposta_fine_corsa\", \"session_id\": \"{sid}\", \"esito\": \"{esito}\", \"payload\": {payload}}}";
        connector.SendToBackend(json);
    }

    private void SendCancellationResponse(string sid, bool success, string msg)
    {
        string esito = success ? "confermato" : "errore";
        string payload = success ? "{}" : $"{{\"messaggio\": \"{EscapeForJson(msg)}\"}}";
        string json = $"{{\"type\": \"risposta_annullamento\", \"session_id\": \"{sid}\", \"esito\": \"{esito}\", \"payload\": {payload}}}";
        connector.SendToBackend(json);
    }

    private float EstimateChargingWaitMinutes(MissionData mission, Vector3 startPosition)
    {
        if (mission == null || carBattery == null) return 0f;

        float timeToCharge = carBattery.GetTimeToCharge(mission.requiredBatteryLevel);
        WaypointNode charger = FindNearestChargingStation(startPosition);
        if (charger == null)
        {
            return timeToCharge;
        }

        float distanceToChargerUnits = 0f;
        float distanceToPickupUnits = 0f;
        WaypointNode startNode = AStarPathfinder.GetClosestNode(startPosition);

        if (startNode != null)
        {
            var pathToCharge = AStarPathfinder.FindPath(startNode, charger);
            if (pathToCharge != null && pathToCharge.Count > 0)
            {
                distanceToChargerUnits = CalculatePathDistance(pathToCharge);
            }
        }

        if (distanceToChargerUnits <= 0f)
        {
            distanceToChargerUnits = Vector3.Distance(startPosition, charger.transform.position);
        }

        WaypointNode targetNode = mission.pickupNode != null ? mission.pickupNode : mission.destinationNode;
        if (targetNode != null)
        {
            var pathToTarget = AStarPathfinder.FindPath(charger, targetNode);
            if (pathToTarget != null && pathToTarget.Count > 0)
            {
                distanceToPickupUnits = CalculatePathDistance(pathToTarget);
            }
            else
            {
                distanceToPickupUnits = Vector3.Distance(charger.transform.position, targetNode.transform.position);
            }
        }

        float travelKm = (distanceToChargerUnits + distanceToPickupUnits) * SimulationUnits.UnitsToKmFactor;
        float travelMinutes = carBattery.EstimateTimeMinutes(travelKm);
        float totalMinutes = Mathf.Max(0f, travelMinutes + timeToCharge);

        return totalMinutes;
    }

    private float CalculateQueueWaitTime(int queueIndex)
    {
        float totalWait = 0f;
        float switchBuffer = 2.0f; 

        // 1. Tempo rimanente della missione corrente
        float baseWait = 0f;
        if (currentMission != null)
        {
            baseWait = EstimateRemainingCurrentMissionTime();
            totalWait += baseWait;
            totalWait += switchBuffer;
        }

        // 2. Tempo delle missioni in coda PRIMA di questa
        int currentIndex = 0;
        foreach (var m in missionQueue)
        {
            if (currentIndex >= queueIndex) break;
            
            // Debug check
            if (m.estimatedEtaMin < 0) Debug.LogError($"[MISSION_COOR] Found negative ETA in queue! Idx {currentIndex}: {m.estimatedEtaMin}");

            totalWait += m.estimatedEtaMin;
            if (m.requiresPreCharge) totalWait += m.estimatedChargeMinutes;
            totalWait += switchBuffer;
            
            currentIndex++;
        }

        float result = Mathf.Max(1.0f, totalWait);
        // Debug.Log($"[CalculateQueueWaitTime] QIdx:{queueIndex} -> Base(Cur+Buf):{baseWait + (currentMission!=null?switchBuffer:0):F1} + PrevMissions(Added):{totalWait - (baseWait + (currentMission!=null?switchBuffer:0)):F1} = {result:F1}");
        
        return result;
    }

    private float EstimateRemainingCurrentMissionTime()
    {
        if (currentMission == null) return 0f;

        // Se siamo in ricarica pre-missione
        if (currentState == CoordinatorState.PreChargeForMission || currentState == CoordinatorState.ReturningToStation || currentState == CoordinatorState.Charging)
        {
             // Stima pessimistica: tempo di ricarica residuo + tutta la missione
             return currentMission.estimatedChargeMinutes + currentMission.estimatedEtaMin;
        }
        
        // Se stiamo guidando (Pickup o Dropoff)
        if (currentState == CoordinatorState.DrivingToPickup || currentState == CoordinatorState.DrivingToDropoff)
        {
            // Calcolo distanza in linea d'aria * fattore correttivo (1.3) per rapidità
            // Non usiamo A* completo per performance, ma una stima basata sulla distanza
            float distToDest = 0f;
            
            Vector3 carPos = carBrain != null ? carBrain.transform.position : transform.position;

            if (currentState == CoordinatorState.DrivingToPickup && currentMission.pickupNode != null)
            {
                float distToPickup = Vector3.Distance(carPos, currentMission.pickupNode.transform.position);
                float distPickupToDrop = currentMission.estimatedDistanceKm / SimulationUnits.UnitsToKmFactor; // Ricaviamo units totali
                distToDest = distToPickup + distPickupToDrop;
            }
            else if (currentMission.destinationNode != null)
            {
                distToDest = Vector3.Distance(carPos, currentMission.destinationNode.transform.position);
            }
            
            float distKm = distToDest * SimulationUnits.UnitsToKmFactor * 1.3f; // 1.3 straight-line factor
            return carBattery.EstimateTimeMinutes(distKm);
        }

        // Fallback: Se la missione c'è ma siamo in uno stato di transizione (es. Idle, Evaluating)
        // Restituiamo la stima statica calcolata alla prenotazione invece di 0.
        if (currentMission != null)
        {
            return currentMission.estimatedEtaMin;
        }

        return 0f;
    }

    private WaypointNode FindNearestChargingStation(Vector3 position)
    {
        WaypointNode nearest = null;
        float minDist = float.MaxValue;
        foreach (var node in WaypointNode.AllNodes)
        {
            if (node.nodeType != WaypointNode.WaypointType.ChargingStation) continue;
            float dist = Vector3.Distance(position, node.transform.position);
            if (dist < minDist)
            {
                minDist = dist;
                nearest = node;
            }
        }
        return nearest;
    }

    // --- STRTTURE DATI ---
    [Serializable] public class BaseMessage { public string type; public string session_id; }
    [Serializable] public class RichiestaPrenotazione { public string session_id; public PayloadPrenotazione payload; }
    [Serializable] public class PayloadPrenotazione { public Destinazione destinazione; public string user_id; public string driving_policy; public string required_policy; }
    [Serializable] public class Destinazione { public string nome; public string poi_id_unity; }
    [Serializable] public class RispostaCodaAttesa { public string session_id; public PayloadCoda payload; }
    [Serializable] public class PayloadCoda { public bool accetta; }

    [Serializable] public class CambioDestinazioneRequest { public string type; public string session_id; public CambioDestinazionePayload payload; }
    [Serializable] public class CambioDestinazionePayload { public Destinazione nuova_destinazione; }
    [Serializable] public class FineCorsaRequest { public string type; public string session_id; }
    [Serializable] public class CambioPolicyRequest { public string type; public string session_id; public CambioPolicyPayload payload; }
    [Serializable] public class CambioPolicyPayload { public string nuova_policy; }

    [Serializable] public class ConfirmMessage { public string type; public string session_id; public string esito; public ConfirmPayload payload; }
    [Serializable] public class ConfirmPayload { public float tempo_stimato_minuti; public float distanza_km; public float batteria_attuale; }

    [Serializable] public class LowBatteryMessage { public string type; public string session_id; public string esito; public LowBatteryPayload payload; }
    [Serializable] public class LowBatteryPayload { public float batteria_attuale; public float tempo_attesa_minuti; }

    [Serializable] public class QueueWaitMessage { public string type; public string session_id; public string esito; public QueueWaitPayload payload; }
    [Serializable] public class QueueWaitPayload { public int corse_in_coda; public float tempo_attesa_minuti; }

    [Serializable] public class QueueConfirmMessage { public string type; public string session_id; public string esito; public QueueConfirmPayload payload; }
    [Serializable] public class QueueConfirmPayload { public int posizione_in_coda; public float tempo_stimato_minuti; }
    [Serializable] public class QueueUpdateMessage { public string type; public string session_id; public QueueUpdatePayload payload; }
    [Serializable] public class QueueUpdatePayload { public int posizione_in_coda; public float tempo_stimato_minuti; }

    public class MissionData
    {
        public string sessionId;
        public string userId;
        public WaypointNode destinationNode;
        public string destinationName;
        public WaypointNode pickupNode;
        public PedestrianAI passenger;
        public float estimatedDistanceKm;
        public float estimatedEtaMin;
        public bool requiresPreCharge;
        public float estimatedChargeMinutes;
        public float requiredBatteryLevel;
        public bool passengerOnboard;
        public bool endRideRequested;
        public bool bookingResponseSent;
        public string bookingResponseType;
        public bool pickupNotified;
        public string drivingPolicy;
        public string requiredPolicy; // Se non null, la policy non può essere cambiata (es. gravidanza)
        public float policyConsumptionMultiplier = 1.0f;

    }
}
