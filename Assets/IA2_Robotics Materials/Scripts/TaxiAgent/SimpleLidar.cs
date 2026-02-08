using UnityEngine;

public class SimpleLidar : MonoBehaviour
{
    [Header("Dependencies")]
    public CarMotor carMotor;

    [Header("Hardware Settings")]
    public float maxRange;       
    public float fieldOfView;    
    public int raysCount;
    public LayerMask obstacleLayer;    
    
    [Header("Software Configuration")]
    public float scanLaneWidth = 3.6f; // La larghezza della corsia che ci interessa monitorare

    // Output pubblici
    public ScanResult FrontSector { get; private set; } 
    public ScanResult LeftSector { get; private set; }
    
    // Settore specifico per pedoni (più largo)
    public ScanResult PedestrianSector { get; private set; } 

    // Cache
    private ScanResult _tempFront;
    private ScanResult _tempLeft;
    private ScanResult _tempPedestrian;

    // Struttura dati per passare informazioni complete al Controller
    public struct ScanResult
    {
        public float Distance;
        public GameObject ObjectDetected;
        public Vector3 LocalPoint; // Utile per debug

        public bool IsValid => ObjectDetected != null;
        public void Reset(float range) { Distance = range; ObjectDetected = null; }
    }

    void Start()
    {
        if (carMotor == null) carMotor = GetComponent<CarMotor>();
    }

    void Update()
    {
        PerformLidarScan();
    }

    void PerformLidarScan()
    {
        _tempFront.Reset(maxRange);
        _tempLeft.Reset(maxRange);
        _tempPedestrian.Reset(maxRange);

        int rayCount = Mathf.Max(1, raysCount);
        float startAngle = -fieldOfView / 2f;
        float angleStep = (rayCount == 1) ? 0f : (fieldOfView / (rayCount - 1));
        Vector3 sensorPos = transform.position;
        float halfLane = scanLaneWidth / 2f;
        float pedestrianSafetyHalfWidth = halfLane + 1.85f;
        
        // Fallback nel caso in cui non viene trovato il CarMotor
        float steeringRad = -1.0f;
        float wheelBase = -1.0f;

        if (carMotor != null)
        {
            steeringRad = carMotor.CurrentSteerAngle * Mathf.Deg2Rad;
            wheelBase = carMotor.wheelBase;
        }

        for (int i = 0; i < rayCount; i++)
        {
            float currentAngle = startAngle + (angleStep * i);
            Quaternion rotation = Quaternion.Euler(0, currentAngle, 0);
            Vector3 rayDirection = transform.rotation * rotation * Vector3.forward;

            RaycastHit hit;
            Color rayColor = Color.green; 
            Vector3 endPoint = sensorPos + rayDirection * maxRange;

            if (Physics.Raycast(sensorPos, rayDirection, out hit, maxRange, obstacleLayer))
            {
                endPoint = hit.point;
                Vector3 localHit = transform.InverseTransformPoint(hit.point);

                // PREDIZIONE TRAIETTORIA
                // Calcoliamo dove sarà l'auto quando avrà raggiunto la distanza Z dell'ostacolo.
                // Formula approssimata dell'arco di Ackermann: Offset = (z^2 * tan(delta)) / 2L
                float curveOffset = 0f;
                // Evitiamo calcoli se andiamo dritti o quasi
                if (Mathf.Abs(steeringRad) > 0.01f) 
                {
                    // Nota: Usiamo localHit.z (distanza frontale) al quadrato
                    curveOffset = ((localHit.z * localHit.z * Mathf.Tan(steeringRad)) / (2 * wheelBase));
                }

                // "Raddrizziamo" virtualmente l'ostacolo sottraendo la curva prevista.
                // Se l'ostacolo è sulla traiettoria curva, effectiveX sarà vicino a 0.
                float effectiveX = localHit.x - curveOffset;

                // Ora usiamo effectiveX invece di localHit.x per tutti i controlli
                rayColor = Color.yellow; 

                // PEDONI
                if (hit.collider.CompareTag("Pedestrian"))
                {
                    // Usiamo effectiveX: Il pedone è sulla mia FUTURA traiettoria?
                    if (Mathf.Abs(effectiveX) <= pedestrianSafetyHalfWidth)
                    {
                        rayColor = Color.magenta;
                        if (hit.distance < _tempPedestrian.Distance)
                        {
                            _tempPedestrian.Distance = hit.distance;
                            _tempPedestrian.ObjectDetected = hit.collider.gameObject;
                            _tempPedestrian.LocalPoint = localHit; // Salviamo comunque il punto reale
                        }
                    }
                }

                // FRONTALE (Corsia + Sicurezza Incroci)
                float detectionWidth = halfLane;

                // Se l'oggetto è un'auto, allarghiamo il campo visivo
                // Questo permette di vedere le auto che stanno entrando nell'incrocio
                if (hit.collider.CompareTag("TrafficCar"))
                {
                    detectionWidth = halfLane + 2.4f;
                }

                if (Mathf.Abs(effectiveX) <= detectionWidth)
                {
                    if (rayColor != Color.magenta) rayColor = Color.red; 

                    if (hit.distance < _tempFront.Distance)
                    {
                        _tempFront.Distance = hit.distance;
                        _tempFront.ObjectDetected = hit.collider.gameObject;
                        _tempFront.LocalPoint = localHit;
                    }
                }
                
                // SORPASSO (Sinistra)
                // Per il sorpasso, controlliamo cosa succede a sinistra della traiettoria prevista
                // effectiveX < -halfLane significa "A sinistra della mia curva"
                else if (effectiveX < -halfLane && effectiveX > -halfLane - 6.0f)
                {
                    if (hit.distance < _tempLeft.Distance)
                    {
                        _tempLeft.Distance = hit.distance;
                        _tempLeft.ObjectDetected = hit.collider.gameObject;
                        _tempLeft.LocalPoint = localHit;
                    }
                }
            }
            
            Debug.DrawLine(sensorPos, endPoint, rayColor);
        }

        FrontSector = _tempFront;
        LeftSector = _tempLeft;
        PedestrianSector = _tempPedestrian;
    }
}
