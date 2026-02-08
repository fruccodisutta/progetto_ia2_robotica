using UnityEngine;
using UnityEngine.AI;

[RequireComponent(typeof(NavMeshAgent))]
[RequireComponent(typeof(Animator))]
public class PedestrianAI : MonoBehaviour
{
    // Personalità del pedone: quelli impulsivi attraversano senza guardare, anche fuori dalle strisce pedonali 
    public enum Personality { Calm, Impulsive }
    // Stati del pedone (Finite State Machine)
    public enum AIState { Wandering, WaitingToCross, Crossing, Idle, HailingTaxi, MovingToPickup }
    
    public AIState currentState;

    [Header("Debug")]
    public bool showDebugInfo = true; 

    [Header("User Association")]
    //User ID da associare al pedone (uguale a quello presente su Neo4j)
    public string userId = "";

    [Header("Personalità (pedoni calmi di default)")]
    public Personality personality = Personality.Calm;
    //Distanza entro cui il pedone si spaventa delle auto
    public float riskTolerance; 

    [Header("Navigation")]
    public float wanderRadius;
    public float waitTime;

    [Header("Esitazione & Probabilità di attraversamento")]
    public float decisionInterval = 0.5f; 
    [Range(0f, 1f)] public float crossingProbability = 0.8f; 

    [Header("Animation Settings")]
    public float waveAnimationDuration = 3f; 
    private float currentWaveTimer = 0f;

    [Header("Sensors")]
    [SerializeField] private LayerMask carLayer; 

    // Componenti del pedone
    private NavMeshAgent agent;
    private Animator animator;
    // Maschere Navigazione rispetto alla NavMeshSurface
    private int jaywalkingMask; 
    private int allAreasMask;
    private int sidewalkMask; 

    // Memorizziamo dove il pedone vuole andare finché non abbiamo il permesso (ovvero maschera di navigazione attiva) per andarci
    private Vector3 pendingCrossingDestination; 
    // Timer di attesa per l'attraversamento 
    private float crossingCooldownTimer = 0f;

    // Target Taxi
    private Transform currentTaxiTarget;
    
    // Stato interno
    private float timer = 0;
    private float antiStuckTimer = 0;
    private bool isMoving = false;

    // Variabili per disegnare l'automobile intercettata 
    private GameObject detectedCarDebug = null;
    
    // Salviamo la stopping distance originale del pedone (per evitare che si fermi troppo lontano dalle auto)
    private float originalStoppingDistance;

    void Start()
    {
        agent = GetComponent<NavMeshAgent>();
        animator = GetComponent<Animator>();
        
        originalStoppingDistance = agent.stoppingDistance;
        
        agent.speed = (personality == Personality.Impulsive) ? 5f : 1.8f; 

        int jaywalkingAreaIndex = NavMesh.GetAreaFromName("Dangerous"); 
        // Filtro per le aree "calpestabili" dai pedoni
        if (jaywalkingAreaIndex != -1)
        {
            jaywalkingMask = 1 << jaywalkingAreaIndex; 
            allAreasMask = NavMesh.AllAreas;
            sidewalkMask = allAreasMask & ~jaywalkingMask; 
        }
        else
        {   
            // Debug
            // Fallback se l'area "Dangerous" non esiste nella NavMesh
            Debug.LogWarning($"[PedestrianAI] NavMesh area 'Dangerous' non trovata! Il pedone {name} userà tutte le aree calpestabili.");
            jaywalkingMask = 0;
            allAreasMask = NavMesh.AllAreas;
            sidewalkMask = NavMesh.AllAreas; 
        }

        agent.areaMask = sidewalkMask; 
        
        // Randomizza priorità per evitare deadlock tra pedoni (ovvero entrambi si bloccano a vicenda)
        // Quando due pedoni si avvicinano, quello con priorità più alta "vince" e l'altro cede
        agent.avoidancePriority = Random.Range(40, 61);
        
        SetState(AIState.Wandering);
    }

    void Update()
    {
        if (crossingCooldownTimer > 0) crossingCooldownTimer -= Time.deltaTime;
        HandleAnimation();
        HandleStateLogic();
    }

    void HandleAnimation()
    {
        bool currentlyMoving = agent.velocity.sqrMagnitude > 0.01f;

        if (agent.hasPath && agent.remainingDistance > agent.stoppingDistance && !agent.isStopped)
        {
            currentlyMoving = true;
        }

        // Se il pedone si sta muovendo, nel caso in cui è calmo allora cammina, se è impulsivo corre
        if (currentlyMoving != isMoving)
        {
            isMoving = currentlyMoving;
            
            string animTrigger = isMoving ? ((personality == Personality.Impulsive) ? "run" : "walk") : "idle";
            
            if (isMoving) animator.ResetTrigger("idle");
            else { animator.ResetTrigger("walk"); animator.ResetTrigger("run"); }

            animator.SetTrigger(animTrigger);
        }
    }

    void HandleStateLogic()
    {
        switch (currentState)
        {
            // Stato di wandering: ogni secondo il pedone sceglie una destinazione (random), quindi la raggiunge
            case AIState.Wandering:
                if (!agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.1f)
                {
                    timer += Time.deltaTime;
                    if (timer > waitTime)
                    {
                        FindNewDestination();
                        timer = 0;
                    }
                }
                break;

            // Stato di WaitingToCross: il pedone sceglie se attraversare in base al proprio carattere
            case AIState.WaitingToCross:
                antiStuckTimer += Time.deltaTime;

                // Timer per evitare il blocco del NavMeshAgent
                if (antiStuckTimer > 5.0f)
                {
                    Debug.LogWarning("TIMEOUT WAITING TO CROSS: Reset forzato.");
                    agent.areaMask = sidewalkMask; 
                    crossingCooldownTimer = 10.0f;
                    SetState(AIState.Wandering);
                    break; 
                }

                // Rotazione verso il punto di destinazione
                Vector3 lookDir = pendingCrossingDestination - transform.position;
                lookDir.y = 0;
                if(lookDir != Vector3.zero) 
                    transform.rotation = Quaternion.Slerp(transform.rotation, Quaternion.LookRotation(lookDir), Time.deltaTime * 5f);

                timer += Time.deltaTime;
                if (timer > decisionInterval)
                {
                    timer = 0; 
                    DecisionLogic(); 
                }
                break;

            // Stato di crossing
            case AIState.Crossing:
                timer += Time.deltaTime;

                // Controllo per capire se il pedone ha raggiunto l'altra parte della strada
                if (!agent.pathPending && agent.remainingDistance <= 0.1f) 
                {
                    // Il pedone ha raggiunto l'altra parte della strada - transizione a Wandering
                    agent.isStopped = true;
                    agent.ResetPath();
                    
                    crossingCooldownTimer = 5.0f;
                    antiStuckTimer = 0;
                    
                    SetState(AIState.Wandering);
                }
                break;
                
            // Stato HailingTaxi: una volta chiamato il taxi, il pedone si gira verso di esso e saluta
            case AIState.HailingTaxi:
                // Lasciamo che NavMeshAgent gestisca il movimento e HandleAnimation gestisca l'animazione camminata.
                if (!agent.isStopped && agent.hasPath && agent.remainingDistance > agent.stoppingDistance)
                {
                    return; 
                }

                if (currentTaxiTarget != null)
                {
                    Vector3 direction = currentTaxiTarget.position - transform.position;
                    direction.y = 0; 
                    if (direction != Vector3.zero)
                    {
                        Quaternion targetRotation = Quaternion.LookRotation(direction);
                        transform.rotation = Quaternion.Slerp(transform.rotation, targetRotation, Time.deltaTime * 5f);
                    }
                }

                currentWaveTimer += Time.deltaTime;
                if (currentWaveTimer > waveAnimationDuration)
                {
                    animator.SetTrigger("wave");
                    currentWaveTimer = 0f;    
                }
                break;
                
            // Stato MovingToPickup: il pedone si sta muovendo verso una posizione di pickup valida
            // Utile per evitare che il pedone chiami il taxi quando è in mezzo alla strada
            case AIState.MovingToPickup:
                if (!agent.pathPending && agent.remainingDistance <= agent.stoppingDistance + 0.5f)
                {
                    Log("Posizione di pickup raggiunta, pronto per il taxi");
                    agent.isStopped = true;
                    agent.ResetPath();

                    SetState(AIState.Idle);
                }
                break;
        }
    }

    void SetState(AIState newState)
    {
        if (currentState != newState)
        {
            currentState = newState;
            timer = 0;
            
            // Ripristina la maschera sidewalk quando si entra in Wandering DOPO un ritardo
            // Questo impedisce la teletrasporto dando al pedone il tempo di fermarsi completamente
            if (newState == AIState.Wandering && agent.areaMask != sidewalkMask)
            {
                Invoke(nameof(RestoreSidewalkMask), 0.1f);
            }
            
            // Gestione centralizzata dei timer
            // Reset anti-stuck timer quando si esce da stati legati all'attraversamento
            if (newState != AIState.WaitingToCross && newState != AIState.Crossing)
            {
                antiStuckTimer = 0;
            }
            
            if (newState != AIState.HailingTaxi)
            {
                currentWaveTimer = 0;
            }
        }
    }

    public void RequestCrossing(Vector3 destinationPoint)
    {
        // Se il pedone sta già attraversando, o se sta per attraversare ritorniamo
        if (currentState == AIState.Crossing || currentState == AIState.WaitingToCross) return;
        if (crossingCooldownTimer > 0) return;

        // Impostiamo la parte opposta della strada come destinazione
        pendingCrossingDestination = destinationPoint;
        
        agent.isStopped = true; 
        agent.ResetPath(); 
        antiStuckTimer = 0; 

        SetState(AIState.WaitingToCross);
    }

    void StartCrossing()
    {
        // Sblocchiamo la maschera, in questo modo i pedoni possono camminare anche in strada
        agent.areaMask = allAreasMask; 
        
        // Setttiamo la distanza a 0 per evitare che il pedone si fermi prima della destinazione
        agent.stoppingDistance = 0f;
        
        // Sblocchiamo il movimento
        agent.isStopped = false;

        // Calcoliamo il percorso verso l'altro lato
        // Avendo la maschera sbloccata, il path attraverserà la strada
        agent.SetDestination(pendingCrossingDestination);

        SetState(AIState.Crossing);
    }

    public void HailTaxi(Transform taxi)
    {
        agent.isStopped = true;
        agent.ResetPath();
        currentTaxiTarget = taxi;
        animator.SetTrigger("wave");
        currentWaveTimer = 0f;
        antiStuckTimer = 0; 
        SetState(AIState.HailingTaxi);
    }

    public bool MoveToPickupLocation(Vector3 targetPosition)
    {
        Log($"[PedestrianAI] Richiesto spostamento verso posizione di pickup: {targetPosition}");
        
        // Interrompe lo stato corrente
        agent.isStopped = false;
        agent.ResetPath();
        
        // Assicuriamoci che il pedone si muova solo sul marciapiede
        agent.areaMask = sidewalkMask;
        
        // Ripristina la stopping distance originale
        agent.stoppingDistance = originalStoppingDistance;
        
        // Velocità di camminata
        agent.speed = 1.8f;
        
        // Calcola il percorso verso la posizione di pickup
        if (agent.SetDestination(targetPosition))
        {
            Log($"[PedestrianAI] Percorso calcolato con successo. Distanza: {Vector3.Distance(transform.position, targetPosition):F2}m");
            SetState(AIState.MovingToPickup);
            return true;
        }

        Debug.LogWarning($"[PedestrianAI] ERRORE: Impossibile calcolare percorso verso {targetPosition}");
        // Fallback: resta nello stato
        return false;
    }

    // Il pedone sale sul taxi quando "sparisce" dalla scena
    public void BoardTaxi() { gameObject.SetActive(false); }

    // Il pedone scende dal taxi, riapparendo in un punto random sulla NavMeshSurface
    public void ExitTaxi(Vector3 dropOffPoint)
    {
        gameObject.SetActive(true);
        agent.Warp(dropOffPoint);
        
        agent.areaMask = sidewalkMask;
        
        agent.isStopped = true;
        agent.ResetPath();
        animator.SetTrigger("wave");
        Invoke(nameof(ReturnToWandering), 4.0f);
    }

    public void ReturnToWandering()
    {
        // Ripristina la maschera sidewalk
        agent.areaMask = sidewalkMask;
        agent.isStopped = false;
        antiStuckTimer = 0;
        
        // Ripristina la stopping distance originale
        agent.stoppingDistance = originalStoppingDistance;
        
        SetState(AIState.Wandering);
    }

    void RestoreSidewalkMask()
    {
        // Chiamato con delay dopo il completamento dell'attraversamento per evitare teletrasporto
        // A questo punto, l'agente si è completamente fermato ed è sicuro limitare la navigazione
        if (currentState == AIState.Wandering)
        {
            agent.areaMask = sidewalkMask;
        }
    }

    public void ApproachVehicle()
    {
        if (currentTaxiTarget == null) return;

        // Ora il pedone può camminare sulla strada per raggiungere la portiera
        agent.areaMask = NavMesh.AllAreas; 
        
        // Sblocchiamo il movimento
        agent.isStopped = false;
        agent.updateRotation = true;

        agent.speed = 1.8f; 

        // Invece di andare al centro (currentTaxiTarget.position), andiamo a destra.
        // Assumiamo che il passeggero salga dal lato marciapiede (destra dell'auto).
        Vector3 doorPosition = currentTaxiTarget.position + (currentTaxiTarget.right * 1.5f);
        
        // Proiettiamo sulla NavMesh per sicurezza (se il punto è troppo in aria o nel muro)
        NavMeshHit hitSidewalk;
        if (NavMesh.SamplePosition(doorPosition, out hitSidewalk, 2.0f, NavMesh.AllAreas))
        {
            agent.SetDestination(hitSidewalk.position);
        }
        else
        {
            // Fallback al centro se non trova la portiera
            agent.SetDestination(currentTaxiTarget.position);
        }

        // Impostiamo una stopping distance molto bassa per farlo "incollare" alla portiera
        agent.stoppingDistance = 1.5f;
        
        // Animazione: Assicuriamoci che cammini
        animator.ResetTrigger("idle");
        animator.ResetTrigger("wave");
    }

    void DecisionLogic()
    {
        // I pedoni impulsivi attraversano immediatamente senza controllare
        if (personality == Personality.Impulsive)
        {
            StartCrossing();
            return;
        }
        
        // I pedoni calmi controllano prima la presenza di automobili in avvicinamento
        if (IsCarApproaching(riskTolerance))
        {
            Log("Non attraverso, c'è un'automobile vicina!");
            
            // Imposta un cooldown per evitare il ripristino immediato da parte del CrosswalkTrigger
            // Questo risolve il problema di oscillazione degli stati Wandering e WaitingToCross
            crossingCooldownTimer = 5.0f;
            
            ReturnToWandering();
            return;
        }
        
        // La strada è libera - attraversamento con probabilità (permette esitazione naturale)
        if (Random.value <= crossingProbability)
        {
            StartCrossing();
        }
        // Altrimenti: continua ad aspettare (il pedone esita, riproverà dopo decisionInterval)
    }

    // Il pedone controlla se vi è una macchina nelle vicinanze, rispetto al proprio riskTolerance
    bool IsCarApproaching(float detectionRadius)
    {
        detectedCarDebug = null; 
        Collider[] cars = Physics.OverlapSphere(transform.position, detectionRadius, carLayer);
        foreach(var car in cars)
        {
            if (car.transform.root == transform.root) continue;
            Vector3 dirToPedestrian = (transform.position - car.transform.position).normalized;
            if (Vector3.Dot(car.transform.forward, dirToPedestrian) > 0.5f)
            {
                detectedCarDebug = car.gameObject; 
                return true; 
            }
        }
        return false;
    }

    // Metodi per trovare una nuova destinazione random nella scena
    public static Vector3 RandomNavSphere(Vector3 origin, float dist, int layermask)
    {
        Vector3 randDirection = Random.insideUnitSphere * dist;
        randDirection += origin;
        NavMeshHit navHit;
        if (NavMesh.SamplePosition(randDirection, out navHit, dist, layermask))
        {
            return navHit.position;
        }
        return origin;
    }

    void FindNewDestination()
    {
        //Controllo della destinazione prima che venga effettivamente impostata
        Vector3 newPos = RandomNavSphere(transform.position, wanderRadius, sidewalkMask);
        
        // Controllo se la destinazione è valida (abbastanza lontana dalla posizione corrente)
        if (Vector3.Distance(transform.position, newPos) > 1.0f)
        {
            agent.SetDestination(newPos);
        }
        else
        {
            // Fallback: proviamo a cercare una destinazione più lontana se non ne troviamo una valida
            newPos = RandomNavSphere(transform.position, wanderRadius * 2f, sidewalkMask);
            if (Vector3.Distance(transform.position, newPos) > 1.0f)
            {
                agent.SetDestination(newPos);
            }
            // Se ancora non riusciamo a trovare una destinazione valida, l'agente riproverà nel prossimo frame nello stato Wandering
        }
    }

    // Metodo Helper per MissionCoordinator: evita che il pedone che chiama il taxi si trova in mezzo alla strada durante la chiamata 
    public bool IsOnSidewalk()
    {
        // Se sta già attraversando o aspettando di attraversare, ritorno
        if (currentState == AIState.Crossing || currentState == AIState.WaitingToCross) return false;

        // Controllo NavMesh: cerchiamo il punto piÃ¹ vicino sul marciapiede
        NavMeshHit hitSidewalk;
        bool hasSidewalk = NavMesh.SamplePosition(transform.position, out hitSidewalk, 0.5f, sidewalkMask);
        if (!hasSidewalk) return false;
        if (jaywalkingMask != 0)
        {
            NavMeshHit hitDanger;
            bool hasDanger = NavMesh.SamplePosition(transform.position, out hitDanger, 0.5f, jaywalkingMask);
            if (hasDanger)
            {
                float dSide = Vector3.Distance(transform.position, hitSidewalk.position);
                float dDanger = Vector3.Distance(transform.position, hitDanger.position);
                // Se la maschera dell'hit contiene l'area pericolosa, il pedone è sulla strada
                if (dDanger <= dSide + 0.05f) return false;
            }
        }
        
        // Se il pedone non si trova in mezzo alla strada (quindi si trova sul marciapiede), ritorniamo true 
        return true;
    }

    // Metodo per debug tramite console di Unity
    void Log(string msg) { if(showDebugInfo) Debug.Log($"<color=cyan>[PEDESTRIAN {name}]</color>: {msg}"); }

    /* private void OnDrawGizmos()
    {
        #if UNITY_EDITOR
            UnityEditor.Handles.Label(transform.position + Vector3.up * 2, $"{currentState}");
        #endif

        if (detectedCarDebug != null)
        {
            Gizmos.color = Color.red;
            Gizmos.DrawLine(transform.position + Vector3.up, detectedCarDebug.transform.position + Vector3.up);
            Gizmos.DrawWireSphere(detectedCarDebug.transform.position, 1f);
        }
    } */
}
