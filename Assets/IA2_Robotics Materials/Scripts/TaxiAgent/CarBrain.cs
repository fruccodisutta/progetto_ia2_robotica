using UnityEngine;
using System.Collections.Generic;

[RequireComponent(typeof(CarMotor))]
[RequireComponent(typeof(CarBattery))]
[RequireComponent(typeof(CarBrain_Traffic))]
[RequireComponent(typeof(CarBrain_Navigation))]
[RequireComponent(typeof(CarBrain_Perception))]
public class CarBrain : MonoBehaviour
{
    // Riferimenti ai Sottosistemi
    private CarMotor motor;
    private CarBattery battery;

    [Header("--- Sensori LiDAR ---")]
    public SimpleLidar frontLidar; 
    public SimpleLidar rearLidar;

    // Moduli
    private CarBrain_Traffic trafficModule;
    private CarBrain_Navigation navModule;
    private CarBrain_Perception perceptionModule;

    // Esposizione Evento (Proxy verso il modulo percezione per non interferire con MissionController)
    public System.Action<WaypointNode> OnRoadBlockDetected 
    {
        get => perceptionModule.OnRoadBlockDetected;
        set => perceptionModule.OnRoadBlockDetected = value;
    }

    [Header("--- Anti-Stuck (Logic) ---")]
    public float stuckTimeThreshold; 
    public float reverseDuration = 2.0f;    
    public float reverseSpeed = -3.0f; 
    public float rearSafetyDistance = 1.5f;
    private float currentStuckTimer = 0f;
    private float currentReverseTimer = 0f;
    private bool isReversing = false;

    [Header("--- Dinamica in Curva ---")]
    [Range(0.1f, 1.0f)] public float minCornerSpeedFactor = 0.6f;

    private float missionOffset = 0f;

    [Header("--- Consumo Batteria ---")]
    private Vector3 lastPosition;

    [Header("--- City Zones ---")]
    // Zona corrente dove si trova il taxi
    public CityZone currentZone = null;
    
    // Log debug per transizioni di zona
    public bool logZoneTransitions = true;

    void Awake()
    {
        motor = GetComponent<CarMotor>();
        battery = GetComponent<CarBattery>();
        
        // Validazione Inspector
        if (frontLidar == null) Debug.LogError("[CAR BRAIN] Assegna il FrontLidar nell'Inspector!");
        if (rearLidar == null) Debug.LogWarning("[CAR BRAIN] RearLidar non assegnato. La retromarcia sarà cieca.");

        // Setup Moduli
        trafficModule = GetComponent<CarBrain_Traffic>();
        navModule = GetComponent<CarBrain_Navigation>();
        perceptionModule = GetComponent<CarBrain_Perception>();

        // Inizializzazione dipendenze incrociate
        trafficModule.Initialize(this);
        navModule.Initialize(this, motor);
        perceptionModule.Initialize(this, motor, frontLidar, navModule);

        lastPosition = transform.position;
    }

    void FixedUpdate()
    {
        // 1. Gestione Batteria & Ricarica (consumo basato su distanza)
        Vector3 currentPosition = transform.position;
        float distanceUnits = Vector3.Distance(currentPosition, lastPosition);

        if (battery.HandleCharging())
        {
            motor.ApplyKinematics(0f, false);
            lastPosition = currentPosition;
            return;
        }

        if (distanceUnits > 0.001f)
        {
            if (!battery.ConsumeByDistance(distanceUnits * SimulationUnits.UnitsToKmFactor))
            {
                motor.ApplyKinematics(0f, false);
                lastPosition = currentPosition;
                return;
            }
        }
        else if (battery.currentBattery <= 0f)
        {
            motor.ApplyKinematics(0f, false);
            lastPosition = currentPosition;
            return;
        }

        lastPosition = currentPosition;

        // 2. Aggiornamento Timer Percezione
        perceptionModule.UpdateTimers(Time.deltaTime);

        // 3. Logica Retromarcia (Anti-Stuck)
        if (isReversing)
        {
            HandleReversingState();
            return;
        }

        // 4. Decision Logic (Pipeline)
        if (!navModule.HasPath() || navModule.IsPathComplete) { motor.ApplyKinematics(0f, false); return; }

        float targetSpeed = motor.maxSpeed;
        bool isIntentionalStop = false;
        
        // A. Analisi Semafori
        float trafficSpeedLimit = -1f;
        bool trafficSaysStop = false;
        bool isTrafficRestricted = trafficModule.GetTrafficLightSpeedLimit(out trafficSpeedLimit, out trafficSaysStop);
        
        if (trafficSaysStop) isIntentionalStop = true;
        if (trafficSpeedLimit >= 0) targetSpeed = Mathf.Min(targetSpeed, trafficSpeedLimit);

        // B. Analisi Curve (Predittiva e Reattiva)
        // Solo se non siamo già fermi al semaforo
        if (!trafficSaysStop)
        {
            float predictiveFactor = navModule.GetPredictiveCornerFactor();
            float predictiveSpeed = Mathf.Lerp(navModule.approachSpeed, motor.maxSpeed, predictiveFactor);
            
            float steerFactor = Mathf.Clamp01(Mathf.Abs(motor.CurrentSteerAngle) / motor.maxSteerAngle);
            float reactiveSpeed = motor.maxSpeed * Mathf.Lerp(1.0f, minCornerSpeedFactor, steerFactor);
            
            targetSpeed = Mathf.Min(targetSpeed, Mathf.Min(predictiveSpeed, reactiveSpeed));
        }

        // C. Analisi Percezione (Pedoni, Ostacoli, Sorpassi)
        // Passiamo 'isTrafficRestricted' per evitare sorpassi col rosso
        bool perceptionStop = false;
        float envSpeed = perceptionModule.CalculateSpeedBasedOnEnvironment(targetSpeed, isTrafficRestricted, out perceptionStop);
        
        if (perceptionStop) isIntentionalStop = true;
        targetSpeed = envSpeed;

        // 5. Controllo Stallo (Anti-Stuck)
        CheckForStuck(isIntentionalStop, isTrafficRestricted);

        // Gestione Priorità Offset
        // Se la percezione dice 0 (nessun ostacolo/sorpasso), usiamo l'offset della missione (accostamento).
        // Se la percezione dice != 0 (es. c'è un ostacolo e devo sorpassare), la percezione VINCE per sicurezza.
        float finalOffset = perceptionModule.targetOffset;
        
        if (Mathf.Abs(finalOffset) < 0.1f) 
        {
            finalOffset = missionOffset;
        }

        navModule.SetTargetOffset(finalOffset);
        navModule.UpdateSteering();

        // 7. Applicazione Motore Finale
        motor.ApplyKinematics(targetSpeed, false);
    }

    public void StopNavigation()
    {
        navModule.ClearPath();
        motor.ApplyKinematics(0f, false);
    }

    // --- LOGICA ANTI-STUCK ---
    void CheckForStuck(bool isIntentionalStop, bool isTrafficRestricted)
    {
        // Condizioni per considerare lo stop NON intenzionale (stuck):
        // - Velocità quasi zero (< 0.5 m/s)
        // - NON è uno stop intenzionale (pedone, roadblock, stop line al semaforo)
        // - NON siamo connessi a un semaforo rosso/giallo (isTrafficRestricted)
        //
        // Se tutte queste condizioni sono vere, significa che siamo bloccati
        // da qualcosa che non dovrebbe bloccarci (es: auto che si ferma in curva)
        // e possiamo provare la retromarcia.
        //
        // IMPORTANTE: isTrafficRestricted è TRUE quando siamo connessi a un semaforo
        // rosso/giallo con la stop line ancora davanti, quindi BLOCCA la retromarcia
        // anche se siamo in coda dietro altre auto.
        bool isStuck = Mathf.Abs(motor.CurrentSpeed) < 0.5f && 
                       !isIntentionalStop && 
                       !isTrafficRestricted;

        if (isStuck)
        {
            currentStuckTimer += Time.deltaTime;
            if (currentStuckTimer > stuckTimeThreshold)
            {
                Debug.Log($"[CAR BRAIN] Bloccato! Avvio retromarcia. Timer: {currentStuckTimer:F1}s");
                isReversing = true;
                currentStuckTimer = 0f;
            }
        }
        else
        {
            currentStuckTimer = 0f;
        }
    }

    void HandleReversingState()
    {
        currentReverseTimer += Time.deltaTime;
        
        // 1. Raddrizza lo sterzo
        float newSteer = Mathf.Lerp(motor.CurrentSteerAngle, 0f, motor.steeringSpeed * Time.deltaTime);
        motor.SetSteerAngle(newSteer);

        // 2. Controllo Lidar Posteriore (Safety)
        bool panicStop = false;
        if (rearLidar != null)
        {
            // Usiamo FrontSector perché il lidar è ruotato di 180 gradi, 
            // quindi il suo "Front" è il "Back" dell'auto.
            var rearScan = rearLidar.FrontSector;
            
            if (rearScan.IsValid && rearScan.Distance < rearSafetyDistance)
            {
                Debug.LogWarning("[CAR BRAIN] Ostacolo posteriore rilevato! Stop retromarcia.");
                panicStop = true;
            }
        }

        if (panicStop)
        {
            motor.ApplyKinematics(0f, false); // Freno
        }
        else
        {
            motor.ApplyKinematics(reverseSpeed, true); // Retromarcia
        }

        // 3. Timeout Retromarcia
        if (currentReverseTimer > reverseDuration)
        {
            isReversing = false;
            currentReverseTimer = 0f;
            currentStuckTimer = 0f;
        }
    }

    // --- API PUBBLICHE ---

    public void SetMissionOffset(float offset)
    {
        missionOffset = offset;
    }

    public void SetCurrentPassenger(Transform passenger)
    {
        perceptionModule.SetPassengerTarget(passenger);
    }

    // Indica che ci stiamo avvicinando al pickup
    public void SetApproachingPickup(bool approaching)
    {
        perceptionModule.SetApproachingPickup(approaching);
    }
    
    public void SetNewPath(List<Transform> pathPoints) => navModule.SetPath(pathPoints);
    
    public void PathRefreshed() => perceptionModule.SetWaitingForPath(false);
    
    public void StartCharging() => battery.StartCharging();

    public bool HasFinishedPath => navModule.IsPathComplete;

    public float GetRemainingPathDistance() => navModule.CalculateRemainingPathDistance();

    // Proxy per V2I (usati dai Trigger del Semaforo)
    public void ConnectToTrafficLight(SmartTrafficLight light) => trafficModule.ConnectToLight(light);
    public void DisconnectFromTrafficLight() => trafficModule.DisconnectFromLight();

    // ========== CITY ZONE DETECTION ==========
    
    // Chiamato quando entriamo in una CityZone
    void OnTriggerEnter(Collider other)
    {
        // Controlla se il collider appartiene a una CityZone
        CityZone zone = other.GetComponent<CityZone>();
        
        if (zone != null)
        {
            // Aggiorna la zona corrente
            CityZone previousZone = currentZone;
            currentZone = zone;
            
            // Debug logging
            if (logZoneTransitions)
            {
                if (previousZone != null)
                {
                    Debug.Log($"<color=cyan>[TAXI ZONE]</color> Transizione: <color=yellow>{previousZone.zoneName}</color> -> <color=lime>{zone.zoneName}</color> ({zone.zoneType})");
                }
                else
                {
                    Debug.Log($"<color=cyan>[TAXI ZONE]</color> Entrato in zona: <color=lime>{zone.zoneName}</color> ({zone.zoneType})");
                }
            }
        }
    }
    
    // Chiamato quando usciamo da una CityZone
    void OnTriggerExit(Collider other)
    {
        // Controlla se il collider appartiene a una CityZone
        CityZone zone = other.GetComponent<CityZone>();
        
        if (zone != null && zone == currentZone)
        {
            // Debug logging
            if (logZoneTransitions)
            {
                Debug.Log($"<color=cyan>[TAXI ZONE]</color> Uscito da zona: <color=orange>{zone.zoneName}</color> ({zone.zoneType})");
            }
            
            // Svuota la zona corrente (il taxi è ora fuori da tutte le zone)
            currentZone = null;
        }
    }
}

public static class ExtDebug
{
    public static void DrawBox(Vector3 center, Vector3 halfExtents, Quaternion orientation, Color color)
    {
        // Disegno semplice con linee di debug
        Matrix4x4 matrix = Matrix4x4.TRS(center, orientation, Vector3.one);
        Vector3 point1 = matrix.MultiplyPoint(new Vector3(-halfExtents.x, -halfExtents.y, -halfExtents.z));
        Vector3 point2 = matrix.MultiplyPoint(new Vector3(halfExtents.x, -halfExtents.y, -halfExtents.z));
        Vector3 point3 = matrix.MultiplyPoint(new Vector3(halfExtents.x, -halfExtents.y, halfExtents.z));
        Vector3 point4 = matrix.MultiplyPoint(new Vector3(-halfExtents.x, -halfExtents.y, halfExtents.z));
        
        Debug.DrawLine(point1, point2, color);
        Debug.DrawLine(point2, point3, color);
        Debug.DrawLine(point3, point4, color);
        Debug.DrawLine(point4, point1, color);
    }

    public static void DrawSphereCast(Vector3 origin, float radius, Vector3 direction, float distance, Color color)
    {
        Vector3 endPoint = origin + direction * distance;
        Debug.DrawLine(origin, endPoint, color);
        
        Vector3 up = Vector3.up * radius;
        Vector3 right = Vector3.right * radius;
        
        Debug.DrawLine(origin - up, origin + up, color);
        Debug.DrawLine(origin - right, origin + right, color);
        
        Debug.DrawLine(endPoint - up, endPoint + up, color);
        Debug.DrawLine(endPoint - right, endPoint + right, color);
    }
}
