using UnityEngine;

public class CarBattery : MonoBehaviour
{
    [Header("--- Energia & Batteria ---")]
    public float maxBattery = 100f;
    public float currentBattery = 100f;
    public float rechargeRate = 5f;
    public bool isCharging = false;

    [Header("--- Consumo basato su Distanza ---")]
    // Percentuale di batteria consumata per km
    public float consumptionPerKm = 15.0f;

    // Moltiplicatore consumo (aumenta lo scarico per km)
    public float consumptionMultiplier = 1.0f; 

    //Velocità media stimata in km/h per calcolo tempo
    public float estimatedSpeedKmh = 30f;
    
    //Soglia minima di batteria per accettare nuove corse
    public float minimumBatteryThreshold = 15f;

    public bool IsCharging => isCharging;
    public float Percentage => currentBattery / maxBattery;

    public void Awake()
    {
        // Forzamento dei parametri per tuning dall'editor di Unity
        if (consumptionPerKm < 14.0f || consumptionPerKm > 16.0f) 
        {
            consumptionPerKm = 15.0f;
        }
        if (consumptionMultiplier > 5.0f) consumptionMultiplier = 1.0f;
    }

    public void StartCharging()
    {
        isCharging = true;
    }

    public void StopCharging()
    {
        isCharging = false;
    }

    public bool HandleCharging()
    {
        if (isCharging)
        {
            currentBattery += rechargeRate * Time.deltaTime;
            currentBattery = Mathf.Clamp(currentBattery, 0, maxBattery);
            if (currentBattery >= maxBattery) isCharging = false;
            return true;
        }
        return false;
    }

    // Consuma batteria in base alla distanza percorsa.
    public bool ConsumeByDistance(float distanceKm)
    {
        if (currentBattery > 0)
        {
            float effectiveConsumptionPerKm = Mathf.Max(0f, consumptionPerKm * consumptionMultiplier);
            float consumption = distanceKm * effectiveConsumptionPerKm;
            currentBattery -= consumption;
            currentBattery = Mathf.Max(0, currentBattery);
            return currentBattery > 0;
        }
        return false;
    }
    
    // Stima l'autonomia residua in km.
    public float EstimateRangeKm()
    {
        float effectiveConsumptionPerKm = consumptionPerKm * consumptionMultiplier;
        if (effectiveConsumptionPerKm <= 0) return float.MaxValue;
        return currentBattery / effectiveConsumptionPerKm;
    }
    
    // Verifica se la batteria è sufficiente per raggiungere una destinazione.
    // Include un margine di sicurezza del 10%.
    public bool CanReachDestination(float distanceKm)
    {
        float requiredBattery = distanceKm * consumptionPerKm * consumptionMultiplier;
        float safetyMargin = requiredBattery * 0.05f; // 5% margine
        return currentBattery >= (requiredBattery + safetyMargin + minimumBatteryThreshold);
    }
    
    // Stima il tempo di percorrenza in minuti per una data distanza.
    public float EstimateTimeMinutes(float distanceKm)
    {
        if (estimatedSpeedKmh <= 0) return 0;
        return (distanceKm / estimatedSpeedKmh) * 60f;
    }
    
    // Calcola il tempo necessario per ricaricare la batteria al livello richiesto.
    public float GetTimeToCharge(float targetLevel = 80f)
    {
        if (currentBattery >= targetLevel) return 0;
        if (rechargeRate <= 0) return float.MaxValue;
        
        float needed = targetLevel - currentBattery;
        float timeSeconds = needed / rechargeRate;
        return timeSeconds / 60f; // Converti in minuti
    }
    
    // Calcola il consumo previsto per un percorso.
    public float GetEstimatedConsumption(float distanceKm)
    {
        return distanceKm * consumptionPerKm * consumptionMultiplier;
    }

    // Overload per stime predittive (usa un moltiplicatore specifico invece di quello attuale)
    public float GetEstimatedConsumption(float distanceKm, float overrideMultiplier)
    {
        return distanceKm * consumptionPerKm * overrideMultiplier;
    }
    
    // Verifica se il taxi dovrebbe andare a ricaricarsi.
    public bool NeedsCharging()
    {
        return currentBattery <= minimumBatteryThreshold;
    }

    /* Debug 
    #if UNITY_EDITOR
        private void OnValidate()
        {
            if (consumptionPerKm < 14.0f) 
            {
                consumptionPerKm = 15.0f;
                Debug.Log("[CarBattery] Aggiornato consumptionPerKm a 15.0f");
            }
            if (consumptionMultiplier == 10.0f) 
            {
                consumptionMultiplier = 1.0f;
                Debug.Log("[CarBattery] Aggiornato consumptionMultiplier a 1.0f");
            }
        }
    #endif */
}
