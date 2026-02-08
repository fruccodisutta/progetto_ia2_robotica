using UnityEngine;
using UnityEngine.UI;
using TMPro;

// Script per visualizzare la batteria del taxi
public class TaxiBatteryHUD : MonoBehaviour
{
    [Header("Riferimenti Logica")]
    public CarBattery carController;

    [Header("Componenti UI")]
    public Image batteryFillImage;       
    public Image batteryIconImage;       
    public TextMeshProUGUI percentageText; 

    [Header("Assets Icone")]
    public Sprite iconHigh;      
    public Sprite iconMedium;    
    public Sprite iconLow;       
    public Sprite iconCharging;  

    [Header("Impostazioni Colore (Opzionale)")]
    public bool changeColorBasedOnLevel = false;
    public Color highColor = Color.green;
    public Color lowColor = Color.red;

    void Update()
    {
        if (carController == null) return;

        // Ottieni dati grezzi
        float current = carController.currentBattery;
        float max = carController.maxBattery;
        float pct = Mathf.Clamp01(current / max); 

        // Aggiorna la Barra
        if (batteryFillImage != null)
        {
            batteryFillImage.fillAmount = pct;

            // Logica colore dinamico
            if (changeColorBasedOnLevel)
            {
                // Lerp interpolare dal rosso al verde in base alla percentuale
                batteryFillImage.color = Color.Lerp(lowColor, highColor, pct);
            }
        }

        // Aggiorna il Testo
        if (percentageText != null)
        {
            percentageText.text = (pct * 100f).ToString("F0") + "%";
        }

        // Gestione Icone (Stati)
        if (batteryIconImage != null)
        {
            UpdateBatteryIcon(pct);
        }
    }

    void UpdateBatteryIcon(float percentage)
    {
        // Priorità allo stato di Ricarica
        if (carController.isCharging)
        {
            if (batteryIconImage.sprite != iconCharging)
                batteryIconImage.sprite = iconCharging;
            return;
        }

        // Logica a soglie per i livelli
        if (percentage > 0.6f) 
        {
            if (batteryIconImage.sprite != iconHigh)
                batteryIconImage.sprite = iconHigh;
        }
        else if (percentage > 0.2f) 
        {
            if (batteryIconImage.sprite != iconMedium)
                batteryIconImage.sprite = iconMedium;
        }
        else // Sotto il 20%
        {
            if (batteryIconImage.sprite != iconLow)
                batteryIconImage.sprite = iconLow;
        }
    }
}