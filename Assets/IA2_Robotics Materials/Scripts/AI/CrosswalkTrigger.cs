using UnityEngine;

public class CrosswalkTrigger : MonoBehaviour
{
    // Il punto esatto dove il pedone deve arrivare dall'altra parte della strada
    public Transform otherSidePoint;

    [Header("Tipo di Attraversamento")]
    public bool isDangerousZone = false;

    private float checkTimer = 0f;

    private void OnTriggerStay(Collider other)
    {
        // Controllo se c'è un pedone nella zona trigger
        // Frequenza di controllo: 5 volte al secondo (ogni 0.2s)
        checkTimer += Time.deltaTime;
        if (checkTimer < 0.2f) return;
        checkTimer = 0f;

        // 2. Logica di Ingaggio
        if (other.CompareTag("Pedestrian")) 
        {
            PedestrianAI ped = other.GetComponent<PedestrianAI>();
            if (ped != null)
            {
                // Filtro di Sicurezza: Se è una zona pericolosa e il pedone è prudente, l'attraversamento viene ignorato.
                // Le zone pericolose non sarebbero altro che gli attraversamenti fuori dalle strisce pedonali 
                if (isDangerousZone && ped.personality == PedestrianAI.Personality.Calm)
                {
                    return; 
                }

                // Tentativo di Attraversamento.
                ped.RequestCrossing(otherSidePoint.position);
            }
        }
    }

    // Disegna una linea che collega i due lati attraverso il trigger di attraversamento
    /* private void OnDrawGizmos()
    {
        if (otherSidePoint != null)
        {
            Gizmos.color = Color.yellow;
            Gizmos.DrawLine(transform.position, otherSidePoint.transform.position);
            
            Gizmos.color = Color.green;
            Gizmos.DrawWireSphere(otherSidePoint.position, 0.3f);
        }
    } */
}
