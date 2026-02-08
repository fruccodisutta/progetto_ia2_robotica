using UnityEngine;
using System;

// Singleton per la gestione del tempo simulato nella simulazione.
// 24 ore simulazione = realMinutesPerDay minuti reali (default: 20 min)
// Start time configurabile (default: 8:00 AM)
public class TimeManager : MonoBehaviour
{
    public static TimeManager Instance { get; private set; }

    [Header("Time Configuration")]
    public float realMinutesPerDay = 20f;
    
    //Ora di inizio simulazione (0-23)
    [Range(0, 23)]
    public int startHour = 8;
    
    //Minuto di inizio simulazione (0-59)
    [Range(0, 59)]
    public int startMinute = 0;

    [Header("Control")]
    //Metti in pausa il tempo simulato
    public bool isPaused = false;

    [Header("Debug")]
    //Mostra log debug per cambiamenti orario
    public bool showDebugLogs = false;

    // State interno
    private float currentTimeInMinutes; // Tempo totale in minuti dall'inizio del giorno (0-1440)
    private int lastMinute = -1;
    private int lastHour = -1;

    // Eventi pubblici per notificare cambiamenti
    public event Action<int, int> OnMinuteChanged; // (hour, minute)
    public event Action<int> OnHourChanged; // (hour)
    public event Action OnDayChanged; // Quando passa un giorno completo

    // Proprietà pubbliche per accesso globale
    public int CurrentHour => Mathf.FloorToInt(currentTimeInMinutes / 60f) % 24;
    public int CurrentMinute => Mathf.FloorToInt(currentTimeInMinutes % 60f);
    public int CurrentSecond => Mathf.FloorToInt((currentTimeInMinutes * 60f) % 60f);
    
    // 0 = mezzanotte, 0.5 = mezzogiorno, 1 = mezzanotte successiva
    public float TimeOfDay => (currentTimeInMinutes / 1440f) % 1f;
    
    // Tempo formattato come stringa HH:MM
    public string FormattedTime => $"{CurrentHour:D2}:{CurrentMinute:D2}";
    
    // Tempo formattato con secondi HH:MM:SS (per debug)
    public string FormattedTimeWithSeconds => $"{CurrentHour:D2}:{CurrentMinute:D2}:{CurrentSecond:D2}";

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
        
        // Inizializza tempo di partenza
        currentTimeInMinutes = (startHour * 60f) + startMinute;
        lastHour = CurrentHour;
        lastMinute = CurrentMinute;
        
        if (showDebugLogs)
        {
            Debug.Log($"[TimeManager] Inizializzato. Orario di inizio: {FormattedTime}");
            Debug.Log($"[TimeManager] Tempo simulato = 1 secondo reale equivale a {GetSimulatedSecondsPerRealSecond():F2} secondi simulati");
            Debug.Log($"[TimeManager] Durata giornata: {realMinutesPerDay} minuti reali");
        }
    }

    private void Update()
    {
        if (isPaused) return;

        // Calcola quanti secondi simulati passano per ogni secondo reale
        float simulatedSecondsPerRealSecond = GetSimulatedSecondsPerRealSecond();
        
        // Incrementa il tempo in minuti
        float deltaMinutes = (Time.deltaTime * simulatedSecondsPerRealSecond) / 60f;
        currentTimeInMinutes += deltaMinutes;

        // Wrap around a 24 ore (1440 minuti)
        if (currentTimeInMinutes >= 1440f)
        {
            currentTimeInMinutes -= 1440f;
            OnDayChanged?.Invoke();
        }

        int currentMin = CurrentMinute;
        int currentHr = CurrentHour;
        
        if (currentMin != lastMinute)
        {
            OnMinuteChanged?.Invoke(currentHr, currentMin);
            lastMinute = currentMin;
        }

        if (currentHr != lastHour)
        {
            OnHourChanged?.Invoke(currentHr);
            lastHour = currentHr;
        }
    }

    private float GetSimulatedSecondsPerRealSecond()
    {
        if (realMinutesPerDay <= 0)
        {
            realMinutesPerDay = 20f;
        }
        
        return (24f * 60f * 60f) / (realMinutesPerDay * 60f);
    }

    // Imposta il tempo simulato a un orario specifico
    public void SetTime(int hour, int minute)
    {
        hour = Mathf.Clamp(hour, 0, 23);
        minute = Mathf.Clamp(minute, 0, 59);
        
        currentTimeInMinutes = (hour * 60f) + minute;
        lastHour = CurrentHour;
        lastMinute = CurrentMinute;
    }

    // Pausa/Resume il tempo simulato
    public void SetPaused(bool paused)
    {
        isPaused = paused;
    }

    // Modifica la velocità del tempo (opzionale)
    public void SetDayDuration(float newRealMinutesPerDay)
    {
        if (newRealMinutesPerDay <= 0)
        {
            return;
        }
        
        realMinutesPerDay = newRealMinutesPerDay;
    }

    // Verifica se è giorno (6:00 - 18:00) o notte
    public bool IsDaytime()
    {
        return CurrentHour >= 6 && CurrentHour < 18;
    }

    // Verifica se siamo in orario di punta (7-9 AM, 17-19 PM)
    public bool IsRushHour()
    {
        return (CurrentHour >= 7 && CurrentHour < 9) || (CurrentHour >= 17 && CurrentHour < 19);
    }

    private void OnDestroy()
    {
        if (Instance == this)
        {
            Instance = null;
        }
    }
}
