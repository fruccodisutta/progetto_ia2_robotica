using UnityEngine;
using TMPro;

// Script per visualizzare l'orario (fittizio) nella simulazione
public class ClockHUD : MonoBehaviour
{
    [Header("Display Settings")]
    public bool use24HourFormat = true;
    public bool showSeconds = false;

    [Header("References")]
    public TextMeshProUGUI timeText;

    private void Awake()
    {
        // Auto-assign se non settato
        if (timeText == null)
        {
            timeText = GetComponent<TextMeshProUGUI>();
        }

        if (timeText == null)
        {
            Debug.LogError("[ClockHUD] TextMeshProUGUI non trovato!");
            enabled = false;
            return;
        }
    }

    private void OnEnable()
    {
        // Attendi che TimeManager sia inizializzato
        if (TimeManager.Instance != null)
        {
            SubscribeToTimeManager();
            // Aggiorna subito con il tempo attuale
            UpdateDisplay(TimeManager.Instance.CurrentHour, TimeManager.Instance.CurrentMinute);
        }
        else
        {
            // Riprova nel prossimo frame
            Invoke(nameof(TrySubscribe), 0.1f);
        }
    }

    private void TrySubscribe()
    {
        if (TimeManager.Instance != null)
        {
            SubscribeToTimeManager();
            UpdateDisplay(TimeManager.Instance.CurrentHour, TimeManager.Instance.CurrentMinute);
        }
        else
        {
            Debug.LogWarning("[ClockHUD] TimeManager non trovato!");
        }
    }

    private void SubscribeToTimeManager()
    {
        TimeManager.Instance.OnMinuteChanged += UpdateDisplay;
    }

    private void OnDisable()
    {
        if (TimeManager.Instance != null)
        {
            TimeManager.Instance.OnMinuteChanged -= UpdateDisplay;
        }
    }

    private void UpdateDisplay(int hour, int minute)
    {
        if (timeText == null) return;

        string formattedTime;

        if (use24HourFormat)
        {
            // Formato 24h: HH:MM o HH:MM:SS
            if (showSeconds)
            {
                int second = TimeManager.Instance.CurrentSecond;
                formattedTime = $"{hour:D2}:{minute:D2}:{second:D2}";
            }
            else
            {
                formattedTime = $"{hour:D2}:{minute:D2}";
            }
        }
        else
        {
            // Formato 12h: HH:MM AM/PM
            int displayHour = hour == 0 ? 12 : (hour > 12 ? hour - 12 : hour);
            string ampm = hour >= 12 ? "PM" : "AM";
            
            if (showSeconds)
            {
                int second = TimeManager.Instance.CurrentSecond;
                formattedTime = $"{displayHour}:{minute:D2}:{second:D2} {ampm}";
            }
            else
            {
                formattedTime = $"{displayHour}:{minute:D2} {ampm}";
            }
        }

        timeText.text = formattedTime;
    }
}
