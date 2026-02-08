using UnityEngine;
using System.Collections;

[RequireComponent(typeof(BoxCollider))]
public class SmartTrafficLight : MonoBehaviour
{
    public enum LightState { Off, Green, Yellow, Red }
    public LightState CurrentState { get; private set; }

    public System.Action<LightState> OnStateChanged;

    [Header("Posizionamento della linea di stop delle automobili")]
    public Transform stopLinePoint;

    [Header("Controllo accensione del semaforo")]
    public bool isWorking = true; // Se false, il semaforo è spento/lampeggiante

    [Header("Timing")]
    public float greenDuration = 10f;
    public float yellowDuration = 3f;
    public float redDuration = 10f;
    public float startDelay = 0f;

    [Header("Vetro (spento) del semaforo per ogni stato")]
    public Renderer redMesh;
    public Renderer yellowMesh;
    public Renderer greenMesh;
    
    [Header("Materials utilizzati per simulare le luci del semaforo")]
    public Material lightOnRedMat;
    public Material lightOnYellowMat;
    public Material lightOnGreenMat;
    public Material lightOffMat;

    private Coroutine trafficRoutine;

    private void Start()
    {
        GetComponent<BoxCollider>().isTrigger = true;
        
        // Avvia il ciclo solo se il semaforo è funzionante
        if (isWorking)
        {
            trafficRoutine = StartCoroutine(TrafficCycle());
        }
        else
        {
            SetState(LightState.Off);
        }
    }

    // Metodo per cambiarlo a runtime (opzionale)
    public void SetStatus(bool active)
    {
        // Delego alla versione sicura per evitare coroutine duplicate
        SetWorkingStatus(active);
    }

    // Metodo pubblico per accendere/spegnere il semaforo dinamicamente
    public void SetWorkingStatus(bool working)
    {
        isWorking = working;
        if (isWorking)
        {
            if (trafficRoutine == null) trafficRoutine = StartCoroutine(TrafficCycle());
        }
        else
        {
            if (trafficRoutine != null) StopCoroutine(trafficRoutine);
            trafficRoutine = null;
            SetState(LightState.Off);
        }
    }

    IEnumerator TrafficCycle()
    {
        if (startDelay > 0) yield return new WaitForSeconds(startDelay);

        while (isWorking)
        {
            SetState(LightState.Red);
            yield return new WaitForSeconds(redDuration);
            
            SetState(LightState.Green);
            yield return new WaitForSeconds(greenDuration);

            SetState(LightState.Yellow);
            yield return new WaitForSeconds(yellowDuration);
        }
    }

    void SetState(LightState newState)
    {
        CurrentState = newState;
        UpdateVisuals();
        OnStateChanged?.Invoke(CurrentState);
    }

    void UpdateVisuals()
    {
        // 1. Reset: tutte le luci sono spente
        if (redMesh) redMesh.material = lightOffMat;
        if (yellowMesh) yellowMesh.material = lightOffMat;
        if (greenMesh) greenMesh.material = lightOffMat;

        // 2. Viene accesa solo quella giusta (se Off, non entra in nessun case)
        switch (CurrentState)
        {
            case LightState.Red: 
                if (redMesh) redMesh.material = lightOnRedMat; 
                break;
            case LightState.Yellow: 
                if (yellowMesh) yellowMesh.material = lightOnYellowMat; 
                break;
            case LightState.Green: 
                if (greenMesh) greenMesh.material = lightOnGreenMat; 
                break;
            // Case Off: non fare nulla (restano tutte spente)
            case LightState.Off:
                break;
        }
    }

    /* Vengono simulati dei semafori intelligenti con tecnologia 5G:
    nel momento in cui un'automobile entra nel raggio d'azione di un semaforo, allora questo comunica il suo stato */
    private void OnTriggerEnter(Collider other)
    {
        CarBrain car = other.GetComponentInParent<CarBrain>();
        if (car != null)
        {
            car.ConnectToTrafficLight(this);
        }

        TrafficCarAI npc = other.GetComponentInParent<TrafficCarAI>();
        if (npc != null)
        {
            npc.ConnectToTrafficLight(this);
        }
    }

    private void OnTriggerExit(Collider other)
    {
        CarBrain car = other.GetComponentInParent<CarBrain>();
        if (car != null)
        {
            car.DisconnectFromTrafficLight();
        }

        TrafficCarAI npc = other.GetComponentInParent<TrafficCarAI>();
        if (npc != null)
        {
            npc.DisconnectFromTrafficLight();
        }
    }

    /* private void OnDrawGizmos()
    {
        // 1. Disegna l'area del segnale WiFi (Il Trigger)
        BoxCollider signalZone = GetComponent<BoxCollider>();
        if (signalZone != null)
        {
            Gizmos.color = new Color(0, 1, 0, 0.1f); // Verde trasparente
            // Calcola la posizione globale del box collider
            Gizmos.matrix = transform.localToWorldMatrix;
            Gizmos.DrawCube(signalZone.center, signalZone.size);
            Gizmos.DrawWireCube(signalZone.center, signalZone.size);
            Gizmos.matrix = Matrix4x4.identity; // Reset matrice
        }

        // 2. Disegna la Linea di Stop Fisica
        if (stopLinePoint != null)
        {
            Gizmos.color = Color.red;
            // Disegna un cubo rosso solido nel punto di stop
            // Gizmos.DrawCube(stopLinePoint.position, new Vector3(6f, 0.2f, 0.5f)); 
            
            // Disegna una linea che collega il semaforo al punto di stop
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(transform.position, stopLinePoint.position);
        }
    } */
}
