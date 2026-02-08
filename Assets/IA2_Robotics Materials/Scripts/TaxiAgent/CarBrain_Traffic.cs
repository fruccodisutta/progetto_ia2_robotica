using UnityEngine;

[RequireComponent(typeof(CarBrain))]
public class CarBrain_Traffic : MonoBehaviour
{
    // Stato del semaforo
    public bool IsRed { get; private set; }
    public bool IsYellow { get; private set; }
    public bool IsGreen { get; private set; }
    
    private SmartTrafficLight currentTrafficLight = null;
    private CarBrain brain; 

    public void Initialize(CarBrain carBrain)
    {
        this.brain = carBrain;
    }

    // LOGICA DI CONNESSIONE
    public void ConnectToLight(SmartTrafficLight light)
    {
        if (currentTrafficLight != null) return; 

        // Filtro Frontale (viene effetuato il prodotto scalare tra la posizione del semaforo e la direzione del veicolo)
        // Se il risultato è negativo, il semaforo è dietro di noi e non ci interessa
        // Questo controllo viene fatto per evitare che il veicolo si fermi per un semaforo che non gli riguarda
        Vector3 directionToLight = light.transform.position - brain.transform.position;
        float dot = Vector3.Dot(brain.transform.forward, directionToLight.normalized);

        if (dot < 0f) return; // Dietro di noi

        currentTrafficLight = light;
        currentTrafficLight.OnStateChanged += HandleTrafficSignal;
        HandleTrafficSignal(currentTrafficLight.CurrentState);
    }

    public void DisconnectFromLight()
    {
        if (currentTrafficLight != null)
        {
            currentTrafficLight.OnStateChanged -= HandleTrafficSignal;
            currentTrafficLight = null;
        }
        IsRed = false; 
        IsYellow = false; 
        IsGreen = false;
    }

    // GESTIONE EVENTI
    private void HandleTrafficSignal(SmartTrafficLight.LightState state)
    {
        IsRed = false; IsYellow = false; IsGreen = false;

        switch (state)
        {
            case SmartTrafficLight.LightState.Red: IsRed = true; break;
            case SmartTrafficLight.LightState.Yellow: IsYellow = true; break;
            case SmartTrafficLight.LightState.Green: IsGreen = true; break;
        }
    }

    // LOGICA DI FRENATA
    public bool GetTrafficLightSpeedLimit(out float limitSpeed, out bool isIntentionalStop)
    {
        limitSpeed = -1f; 
        isIntentionalStop = false;

        if ((IsRed || IsYellow) && currentTrafficLight != null)
        {
            Transform stopTarget = currentTrafficLight.stopLinePoint != null ? 
                                   currentTrafficLight.stopLinePoint : 
                                   currentTrafficLight.transform;

            // Se abbiamo già superato la linea di stop, non dobbiamo fermarci anche se è rosso.
            // Altrimenti l'auto inchioda in mezzo all'incrocio.
            if (IsStopLineBehind(stopTarget))
            {
                // Linea superata -> Nessun limite, libera l'incrocio!
                return false; 
            }

            float distToStopLine = Vector3.Distance(brain.transform.position, stopTarget.position);
            
            float stopThreshold = 2.8f; 

            if (distToStopLine <= stopThreshold)
            {
                isIntentionalStop = true;
                limitSpeed = 0f;
                return true;
            }
            else
            {
                float approachLimit = 5.0f; 
                float approachFactor = Mathf.Clamp01((distToStopLine - stopThreshold) / 25.0f);
                limitSpeed = Mathf.Lerp(3.0f, approachLimit, approachFactor);
                return true;
            }
        }
        return false;
    }

    // Metodo Helper per verificare se la linea è dietro di noi
    private bool IsStopLineBehind(Transform stopLine)
    {
        Vector3 dirToLine = stopLine.position - brain.transform.position;
        float dot = Vector3.Dot(brain.transform.forward, dirToLine);
        return dot < 0; 
    }
}