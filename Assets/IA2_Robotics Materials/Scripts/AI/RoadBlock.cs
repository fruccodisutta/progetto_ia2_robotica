using UnityEngine;
using System.Collections;

/*
Classe helper per gestione delle barriere. 
Le barriere bloccano una certa strada, quindi il taxi è costretto a ripianificare il percorso da seguire. 
Tuttavia le potenziali barriere che possono spuntare sono diverse, per evitare di avere una città completamente bloccata dalle barriere,
queste spariscono dopo tot tempo 
*/

public class RoadBlock : MonoBehaviour
{
    [Header("Timer di sparizione")]
    public float vanishDelay;

    private bool isVanishing = false;

    // Ogni volta che l'oggetto viene attivato (SetActive(true)), resetta lo stato.
    void OnEnable()
    {
        isVanishing = false;
    }

    // Metodo chiamato dal Taxi quando rileva una barriera
    public void TriggerDisappearance()
    {
        if (isVanishing) return;
        
        isVanishing = true;
        StartCoroutine(VanishRoutine());
    }

    IEnumerator VanishRoutine()
    {
        Debug.Log($"[ROADBLOCK] Barriera individuata! Sparizione tra {vanishDelay} secondi.");
        
        // Aspettiamo il tempo stabilito per la sparizione
        yield return new WaitForSeconds(vanishDelay);

        Debug.Log("[ROADBLOCK] Barriera rimossa.");
        
        // Disattiviamo l'oggetto
        gameObject.SetActive(false);
    }
}