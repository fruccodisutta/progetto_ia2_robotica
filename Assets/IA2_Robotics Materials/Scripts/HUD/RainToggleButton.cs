using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Script per il bottone che avvia/ferma la pioggia
public class RainToggleButton : MonoBehaviour
{
    [Header("UI References")]
    public Button toggleButton;
    public TextMeshProUGUI buttonText;


    [Header("Visual Settings")]
    public Color rainingColor = new Color(0.3f, 0.5f, 1f, 1f);
    public Color clearColor = new Color(1f, 1f, 1f, 0.8f);

    [Header("Text Labels")]
    public string startRainText = "Start Rain";
    public string stopRainText = "Stop Rain";

    private void Awake()
    {
        if (toggleButton == null)
        {
            toggleButton = GetComponent<Button>();
        }

        if (toggleButton == null)
        {
            Debug.LogError("[RainToggleButton] Bottone non trovato!");
            enabled = false;
            return;
        }

        toggleButton.onClick.AddListener(OnButtonClicked);
    }

    private void OnEnable()
    {
        if (WeatherManager.Instance != null)
        {
            SubscribeToWeatherManager();
            UpdateButtonVisuals(WeatherManager.Instance.IsRaining);
        }
        else
        {
            Invoke(nameof(TrySubscribe), 0.1f);
        }
    }

    private void TrySubscribe()
    {
        if (WeatherManager.Instance != null)
        {
            SubscribeToWeatherManager();
            UpdateButtonVisuals(WeatherManager.Instance.IsRaining);
        }
        else
        {
            Debug.LogWarning("[RainToggleButton] WeatherManager non trovato! Il bottone non funzionerà.");
        }
    }

    private void SubscribeToWeatherManager()
    {
        WeatherManager.Instance.OnWeatherChanged += UpdateButtonVisuals;
    }

    private void OnDisable()
    {
        if (WeatherManager.Instance != null)
        {
            WeatherManager.Instance.OnWeatherChanged -= UpdateButtonVisuals;
        }
    }

    public void OnButtonClicked()
    {
        if (WeatherManager.Instance != null)
        {
            WeatherManager.Instance.ToggleRain();
        }
        else
        {
            Debug.LogError("[RainToggleButton] WeatherManager non disponibile!");
        }
    }

    private void UpdateButtonVisuals(bool isRaining)
    {
        // Aggiorna testo
        if (buttonText != null)
        {
            buttonText.text = isRaining ? stopRainText : startRainText;
        }

        // Cambia colore del bottone
        ColorBlock colors = toggleButton.colors;
        colors.normalColor = isRaining ? rainingColor : clearColor;
        toggleButton.colors = colors;
    }

    private void OnDestroy()
    {
        if (toggleButton != null)
        {
            toggleButton.onClick.RemoveListener(OnButtonClicked);
        }
    }
}
