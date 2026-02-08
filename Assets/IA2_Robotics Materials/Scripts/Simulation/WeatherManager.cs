using UnityEngine;
using System;

// Singleton per la gestione del meteo nella simulazione.
// Sistema di pioggia casuale con durate configurabili e cooldown.
public class WeatherManager : MonoBehaviour
{
    public static WeatherManager Instance { get; private set; }

    [Header("Rain Particle System")]
    //Sistema particellare della pioggia
    public ParticleSystem rainParticleSystem;
    
    //Transform da seguire. Per semplicità la pioggia segue il tetto del taxi (come la "nuvola di Fantozzi")
    //Permette sia astrazione che di alleggerire il carico su cpu/gpu
    public Transform followTarget;
    
    //Offset Y sopra il target (altezza pioggia)
    public float followHeightOffset = 10f;
    
    //Offset XZ dal target (in caso di spostamenti laterali)
    public Vector2 followXZOffset = Vector2.zero;

    //Durata minima della pioggia in secondi
    public float minRainDuration = 30f;
    
    //Durata massima della pioggia in secondi
    public float maxRainDuration = 120f;

    [Header("Cooldown Settings")]
    //Tempo minimo di bel tempo dopo la pioggia
    public float cooldownAfterRain = 60f;

    [Header("Random Rain Settings")]
    //Intervallo per check random pioggia
    public float rainCheckInterval = 30f;
    
    //Probabilità di pioggia ad ogni check (0-1, default 0.1 = 10%)
    [Range(0f, 1f)]
    public float rainProbability = 0.1f;

    [Header("Manual Control")]
    //Attiva/disattiva la pioggia manualmente
    public bool enableRandomRain = true;

    [Header("Debug")]
    public bool showDebugLogs = false;

    // State interno
    private bool isRaining = false;
    private bool isInCooldown = false;
    private float rainDurationTimer = 0f;
    private float cooldownTimer = 0f;
    private float rainCheckTimer = 0f;

    public bool IsRaining => isRaining;
    public bool IsInCooldown => isInCooldown;
    public float RemainingRainTime => isRaining ? rainDurationTimer : 0f;
    public float RemainingCooldownTime => isInCooldown ? cooldownTimer : 0f;

    // Eventi
    public event Action<bool> OnWeatherChanged; // true = rain started, false = rain stopped

    private void Awake()
    {
        // Singleton pattern
        if (Instance != null && Instance != this)
        {
            Destroy(gameObject);
            return;
        }

        Instance = this;
        DontDestroyOnLoad(gameObject); 

        if (rainParticleSystem == null)
        {
            Debug.LogError("[WeatherManager] Sistema particellare non assegnato.");
        }

        if (showDebugLogs)
        {
            Debug.Log("[WeatherManager] Inizializzato. Pioggia casuale abilitata: " + enableRandomRain);
        }
    }

    private void Update()
    {
        // Gestione cooldown
        if (isInCooldown)
        {
            cooldownTimer -= Time.deltaTime;
            if (cooldownTimer <= 0)
            {
                isInCooldown = false;
                if (showDebugLogs)  
                    Debug.Log("[WeatherManager] Cooldown terminato. Potrebbe iniziare a piovere.");
            }
            return;
        }

        // Gestione pioggia attiva
        if (isRaining)
        {
            rainDurationTimer -= Time.deltaTime;
            if (rainDurationTimer <= 0)
            {
                StopRain();
            }
        }

        // Check random rain (solo se abilitato e non in cooldown)
        else if (enableRandomRain)
        {
            rainCheckTimer += Time.deltaTime;
            if (rainCheckTimer >= rainCheckInterval)
            {
                rainCheckTimer = 0f;
                CheckRandomRain();
            }
        }
        
        // Aggiorna posizione particle system per seguire il taxi
        UpdateParticleSystemPosition();
    }

    // Check casuale per attivare la pioggia
    private void CheckRandomRain()
    {
        if (UnityEngine.Random.value < rainProbability)
        {
            StartRain();
        }
    }

    // Avvia la pioggia con durata casuale
    private void StartRain()
    {
        if (isRaining) return;

        float duration = UnityEngine.Random.Range(minRainDuration, maxRainDuration);
        StartRainWithDuration(duration);
    }

    // Avvia la pioggia con durata specifica
    private void StartRainWithDuration(float duration)
    {
        isRaining = true;
        rainDurationTimer = duration;
        isInCooldown = false;

        // Attiva particle system
        if (rainParticleSystem != null)
        {
            rainParticleSystem.Play();
        }

        if (showDebugLogs)
            Debug.Log($"[WeatherManager] Pioggia iniziata. Durata: {duration:F1} secondi");

        OnWeatherChanged?.Invoke(true);
    }

    // Ferma la pioggia e avvia cooldown
    private void StopRain()
    {
        if (!isRaining) return;

        isRaining = false;
        rainDurationTimer = 0f;

        // Disattiva particle system
        if (rainParticleSystem != null)
        {
            rainParticleSystem.Stop();
        }

        // Avvia cooldown
        isInCooldown = true;
        cooldownTimer = cooldownAfterRain;

        if (showDebugLogs)
            Debug.Log($"[WeatherManager] Pioggia terminata. Cooldown: {cooldownAfterRain:F1} secondi");

        OnWeatherChanged?.Invoke(false);
    }

    // Toggle pioggia manualmente (chiamato da UI button)
    // Bypassa il cooldown per controllo manuale
    public void ToggleRain()
    {
        if (isRaining)
        {
            // Ferma pioggia manualmente
            StopRainManually();
        }
        else
        {
            // Avvia pioggia manualmente
            StartRainManually();
        }
    }

    // Avvia la pioggia manualmente (bypassa cooldown)
    public void StartRainManually()
    {
        if (isRaining)
        {
            if (showDebugLogs)
                Debug.Log("[WeatherManager] Pioggia già attiva. Ignoro avvio manuale.");
            return;
        }

        isInCooldown = false;
        cooldownTimer = 0f;

        float duration = (minRainDuration + maxRainDuration) / 2f;
        StartRainWithDuration(duration);

        if (showDebugLogs)
            Debug.Log("[WeatherManager] Pioggia avviata manualmente.");
    }

    // Ferma la pioggia manualmente (NO cooldown)
    public void StopRainManually()
    {
        if (!isRaining)
        {
            if (showDebugLogs)
                Debug.Log("[WeatherManager] Pioggia non attiva. Ignoro stop manuale.");
            return;
        }

        isRaining = false;
        rainDurationTimer = 0f;

        if (rainParticleSystem != null)
        {
            rainParticleSystem.Stop();
        }

        isInCooldown = false;
        cooldownTimer = 0f;

        if (showDebugLogs)
            Debug.Log("[WeatherManager] Pioggia fermata manualmente (no cooldown).");

        OnWeatherChanged?.Invoke(false);
    }

    // Forza lo stato della pioggia
    public void ForceRain(bool enable)
    {
        if (enable)
        {
            StartRainManually();
        }
        else
        {
            StopRainManually();
        }
    }

    // Aggiorna la posizione del particle system per seguire il target
    private void UpdateParticleSystemPosition()
    {
        if (rainParticleSystem == null) return;
        if (followTarget == null) return;

        // Calcola nuova posizione: sopra il target con offset
        Vector3 targetPosition = followTarget.position;
        targetPosition.y += followHeightOffset;
        targetPosition.x += followXZOffset.x;
        targetPosition.z += followXZOffset.y;

        rainParticleSystem.transform.position = targetPosition;
    }

    // Abilita/disabilita sistema random rain
    public void SetRandomRainEnabled(bool enabled)
    {
        enableRandomRain = enabled;
        
        if (showDebugLogs)
            Debug.Log($"[WeatherManager] Random rain {(enabled ? "enabled" : "disabled")}");
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }
}