using UnityEngine;

//Classe che gestisce l'IA delle automobili del traffico

public class TrafficCarAI : MonoBehaviour
{
    //Stati dell'auto (Finite State Machine)
    public enum State { Driving, Blocked, Reversing, Waiting }

    [Header("Debug")]
    public bool showDebugGizmos = false;

    [Header("Navigazione")]
    public WaypointCircuit pathToCheck;
    public float reachThreshold = 2.0f;
    
    [Header("Motore")]
    public float maxSpeed;
    public float acceleration;
    public float brakePower;
    public float reverseSpeed;

    [Header("Sensore Volumetrico")]
    public LayerMask obstacleMask;
    public Vector3 sensorSize;
    public float detectionDistance; 
    public float stopDistance;    
    public Vector3 sensorOffset;

    [Header("Logica Anti-Stallo")]
    public float timeBeforeReverse;
    public float reverseDuration;
    public float waitAfterReverseDuration;
    private float waitTimer = 0f;

    [Header("V2I (Semafori Smart)")]
    private SmartTrafficLight currentTrafficLight = null;
    private bool isTrafficLightRed = false;

    //Quanto il sensore 'guarda verso la curva' invece che dritto. 0 = Dritto, 1 = guarda completamente il prossimo Waypoint
    [Range(0f, 1f)] public float curveLookAhead = 0.6f;
    //Il sensore sarà più lungo in base alla velocità dell'auto. Es: 0.5 significa che a 10m/s aggiunge 5m al raggio.
    public float speedDetectionFactor = 0.8f; 

    //Variabili di stato (utili per debug durante la simulazione)
    [SerializeField] private State currentState = State.Driving;
    private float currentSpeed = 0f;
    private int currentPointIndex = 0;
    private float stuckTimer = 0f;
    private float reversingTimer = 0f;

    void Start()
    {
        if (pathToCheck != null)
            currentPointIndex = pathToCheck.GetClosestWaypointIndex(transform.position);
    }

    void Update()
    {
        if (pathToCheck == null) return;

        switch (currentState)
        {
            case State.Driving:
                HandleDriving();
                break;
            case State.Blocked:
                HandleBlocked();
                break;
            case State.Reversing:
                HandleReversing();
                break;
            case State.Waiting:
                HandleWaiting();
                break;
        }
    }

    void HandleDriving()
    {

        // 1. Calcolo limiti velocità
        bool isPlayer = false;
        float currentDynamicDistance = detectionDistance + (currentSpeed * speedDetectionFactor);

        float distToObstacle = CheckObstacles(currentDynamicDistance, out isPlayer);

        float obstacleLimitSpeed = maxSpeed;

        if (distToObstacle < currentDynamicDistance)
        {
            if (distToObstacle < stopDistance)
            {
                obstacleLimitSpeed = 0f;
                // Logica di passaggio a stato Blocked
                if (currentSpeed < 0.5f) 
                { 
                    currentState = State.Blocked; 
                    stuckTimer = 0f; 
                    return; 
                }
            }
            else
            {
                // Frenata più aggressiva
                // Calcoliamo il fattore basandoci sulla distanza dinamica.
                // Questo controllo garantisce che inizi a rallentare molto prima se va veloce.
                float range = currentDynamicDistance - stopDistance;
                float factor = (distToObstacle - stopDistance) / range;
                
                // Curva quadratica invece che lineare: frena poco all'inizio, molto alla fine
                obstacleLimitSpeed = maxSpeed * Mathf.Pow(Mathf.Clamp01(factor), 2f);
            }
        }

        // Controllo del semaforo
        float trafficLightLimitSpeed = maxSpeed;

        if (isTrafficLightRed && currentTrafficLight != null)
        {
            Transform stopTarget = currentTrafficLight.stopLinePoint != null ? 
                                   currentTrafficLight.stopLinePoint : 
                                   currentTrafficLight.transform;

            //Se abbiamo superato la linea di stop, ignoriamo il semaforo
            if (IsStopLineBehind(stopTarget))
            {
                trafficLightLimitSpeed = maxSpeed; 
            }
            else
            {
                //Siamo ancora prima della linea
                float distToStopLine = Vector3.Distance(transform.position, stopTarget.position);
                float stopThreshold = 4.0f; // Distanza dal centro auto alla linea

                if (distToStopLine < stopThreshold)
                {
                    trafficLightLimitSpeed = 0f;
                }
                else
                {
                    // Rallenta dolcemente, iniziando a frenare 20 metri prima
                    float slowDownDist = 20.0f; 
                    float factor = Mathf.Clamp01((distToStopLine - stopThreshold) / slowDownDist);
                    trafficLightLimitSpeed = Mathf.Lerp(2.0f, maxSpeed, factor);
                }
            }
        }

        //Scegliamo il limite più basso tra ostacolo e semaforo
        float finalTargetSpeed = Mathf.Min(obstacleLimitSpeed, trafficLightLimitSpeed);

        MoveForward(finalTargetSpeed);
        SteerTowardsWaypoint();
    }

    // Ritorna TRUE se abbiamo superato la linea di stop del semaforo
    private bool IsStopLineBehind(Transform stopLine)
    {
        Vector3 dirToLine = stopLine.position - transform.position;
        
        // Prodotto scalare con la nostra direzione (Forward)
        float dot = Vector3.Dot(transform.forward, dirToLine);

        // Se dot < 0, l'angolo è > 90 gradi -> La linea è alle nostre spalle
        return dot < 0;
    }

    void HandleBlocked()
    {
        // Usiamo la distanza base quando siamo fermi
        float distToObstacle = CheckObstacles(detectionDistance, out bool isPlayer);
        
        if (distToObstacle > stopDistance * 1.2f) 
        { 
            currentState = State.Driving; 
            return; 
        }

        // Permettiamo la retromarcia se:
        // 1. Siamo bloccati dal Player, OPPURE
        // 2. Siamo bloccati da un'altra auto MA non c'è un semaforo rosso davanti
        bool shouldReverse = false;
        
        if (isPlayer && !isTrafficLightRed)
        {
            // Caso 1: Bloccati dal taxi e non c'è nessun semaforo rosso -> sempre retromarcia
            shouldReverse = true;
        }
        else if (!isTrafficLightRed)
        {
            // Caso 2: Bloccati da un'altra auto, nessun semaforo rosso
            shouldReverse = true;
        }

        // Se isTrafficLightRed == true, non andiamo in retromarcia (aspettiamo il verde)
        
        if (shouldReverse)
        {
            stuckTimer += Time.deltaTime;
            if (stuckTimer > timeBeforeReverse) 
            { 
                currentState = State.Reversing; 
                reversingTimer = 0f; 
            }
        }
    }

    void HandleReversing()
    {
        reversingTimer += Time.deltaTime;
        currentSpeed = Mathf.MoveTowards(currentSpeed, reverseSpeed, acceleration * Time.deltaTime);
        transform.Translate(-Vector3.forward * currentSpeed * Time.deltaTime);

        if (reversingTimer > reverseDuration)
        {
            currentSpeed = 0f;
            currentState = State.Waiting;
            waitTimer = 0f;
        }
    }

    void HandleWaiting()
    {
        currentSpeed = 0f;
        waitTimer += Time.deltaTime;
        if (waitTimer > waitAfterReverseDuration)
        {
            currentState = State.Driving;
            stuckTimer = 0f; 
        }
    }

    float CheckObstacles(float range, out bool isPlayer)
    {
        isPlayer = false;
        
        // Origine del sensore volumetrico dell'automobile
        Vector3 origin = transform.TransformPoint(sensorOffset);

        // Direzione verso il prossimo waypoint
        Vector3 directionToWaypoint = transform.forward; 
        if (pathToCheck != null && pathToCheck.curvedPath.Count > 0)
        {
            Vector3 targetPos = pathToCheck.curvedPath[currentPointIndex];
            Vector3 diff = (targetPos - transform.position);
            diff.y = 0; 
            if(diff != Vector3.zero) directionToWaypoint = diff.normalized;
        }

        Vector3 scanDirection = Vector3.Slerp(transform.forward, directionToWaypoint, curveLookAhead).normalized;

        // Check di prossimità rispetto agli ostacoli
        Collider[] closeHits = Physics.OverlapBox(origin, sensorSize * 0.6f, transform.rotation, obstacleMask);
        foreach(var col in closeHits)
        {
            if (col.transform.root == transform.root) continue; 
            
            if (col.CompareTag("Player")) isPlayer = true;
            
            if (showDebugGizmos) {
                ExtDebug.DrawBox(origin, sensorSize * 0.6f, transform.rotation, Color.magenta);
            }
            return 0f; 
        }

        // Check di distanza rispetto agli ostacoli
        RaycastHit hit;
        if (Physics.BoxCast(origin, sensorSize / 2, scanDirection, out hit, transform.rotation, range, obstacleMask))
        {
            if (hit.transform.root == transform) return float.MaxValue; 

            // Debug visuale del volume occupato (se c'è un ostacolo)
            if (showDebugGizmos)
            {
                DrawConnectedBoxCast(origin, sensorSize/2, scanDirection, hit.distance, transform.rotation, Color.red);
            }
            
            if (hit.collider.CompareTag("Player")) isPlayer = true;

            return hit.distance;
        }

        // Debug visuale del volume libero (se non ci sono ostacoli)
        if (showDebugGizmos)
        {
            DrawConnectedBoxCast(origin, sensorSize/2, scanDirection, range, transform.rotation, new Color(0, 1, 0, 0.4f));
        }
        
        return float.MaxValue;
    }

    void MoveForward(float targetSpeed)
    {
        if (currentSpeed < targetSpeed)
            currentSpeed += acceleration * Time.deltaTime;
        else
            currentSpeed -= brakePower * Time.deltaTime;
            
        currentSpeed = Mathf.Clamp(currentSpeed, 0, maxSpeed);

        transform.Translate(Vector3.forward * currentSpeed * Time.deltaTime);
        
        Vector3 targetPos = pathToCheck.curvedPath[currentPointIndex];
        targetPos.y = transform.position.y;
        if (Vector3.Distance(transform.position, targetPos) < reachThreshold)
            currentPointIndex = (currentPointIndex + 1) % pathToCheck.curvedPath.Count;
    }

    void SteerTowardsWaypoint()
    {
        if (currentSpeed < 0.1f) return;
        Vector3 targetPos = pathToCheck.curvedPath[currentPointIndex];
        Vector3 dir = targetPos - transform.position;
        dir.y = 0;
        if (dir != Vector3.zero)
        {
            Quaternion targetRot = Quaternion.LookRotation(dir);
            transform.rotation = Quaternion.Slerp(transform.rotation, targetRot, 5f * Time.deltaTime);
        }
    }

    public void ConnectToTrafficLight(SmartTrafficLight light)
    {
        if (currentTrafficLight != null) return;
        currentTrafficLight = light;
        currentTrafficLight.OnStateChanged += HandleTrafficSignal;
        HandleTrafficSignal(currentTrafficLight.CurrentState);
    }

    public void DisconnectFromTrafficLight()
    {
        if (currentTrafficLight != null)
        {
            currentTrafficLight.OnStateChanged -= HandleTrafficSignal;
            currentTrafficLight = null;
        }
        isTrafficLightRed = false; 
    }

    private void HandleTrafficSignal(SmartTrafficLight.LightState state)
    {
        if (state == SmartTrafficLight.LightState.Red || state == SmartTrafficLight.LightState.Yellow)
            isTrafficLightRed = true;
        else
            isTrafficLightRed = false;
    }
    
    /* void OnDrawGizmos()
    {
        if (!showDebugGizmos) return;
        Gizmos.color = Color.yellow;
        Vector3 origin = transform.TransformPoint(sensorOffset);
        Gizmos.DrawWireCube(origin + transform.forward * detectionDistance / 2, new Vector3(sensorSize.x, sensorSize.y, detectionDistance));
    }*/

    void DrawConnectedBoxCast(Vector3 origin, Vector3 halfExtents, Vector3 direction, float distance, Quaternion orientation, Color color)
    {
        // Calcoliamo la posizione finale
        Vector3 endPoint = origin + direction * distance;

        // Disegniamo i due Box (Partenza e Arrivo)
        ExtDebug.DrawBox(origin, halfExtents, orientation, color);
        ExtDebug.DrawBox(endPoint, halfExtents, orientation, color);

        // Calcoliamo i 4 angoli locali della faccia "frontale" del box
        // (Usiamo la rotazione dell'auto per orientarli correttamente)
        Vector3 p1 = new Vector3(halfExtents.x, halfExtents.y, halfExtents.z);
        Vector3 p2 = new Vector3(-halfExtents.x, halfExtents.y, halfExtents.z);
        Vector3 p3 = new Vector3(halfExtents.x, -halfExtents.y, halfExtents.z);
        Vector3 p4 = new Vector3(-halfExtents.x, -halfExtents.y, halfExtents.z);

        Vector3 p5 = new Vector3(halfExtents.x, halfExtents.y, -halfExtents.z);
        Vector3 p6 = new Vector3(-halfExtents.x, halfExtents.y, -halfExtents.z);
        Vector3 p7 = new Vector3(halfExtents.x, -halfExtents.y, -halfExtents.z);
        Vector3 p8 = new Vector3(-halfExtents.x, -halfExtents.y, -halfExtents.z);

        // Ruotiamo i punti
        p1 = orientation * p1; p2 = orientation * p2; p3 = orientation * p3; p4 = orientation * p4;
        p5 = orientation * p5; p6 = orientation * p6; p7 = orientation * p7; p8 = orientation * p8;

        // Disegniamo le linee che collegano Partenza e Arrivo
        Debug.DrawLine(origin + p1, endPoint + p1, color);
        Debug.DrawLine(origin + p2, endPoint + p2, color);
        Debug.DrawLine(origin + p3, endPoint + p3, color);
        Debug.DrawLine(origin + p4, endPoint + p4, color);
        
        Debug.DrawLine(origin + p5, endPoint + p5, color);
        Debug.DrawLine(origin + p6, endPoint + p6, color);
        Debug.DrawLine(origin + p7, endPoint + p7, color);
        Debug.DrawLine(origin + p8, endPoint + p8, color);
    }
}