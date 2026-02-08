using UnityEngine;
using UnityEngine.Networking;
using System;
using System.Collections;
using System.Collections.Generic;

// Servizio per comunicare con la Knowledge Base (backend Python).
// Gestisce le query per ottenere i moltiplicatori delle zone per l'A* pesato.
public class KBService : MonoBehaviour
{
    public static KBService Instance { get; private set; }

    [Header("Backend Configuration")]
    // URL del backend Python
    public string backendUrl = "http://localhost:8000";

    [Header("Cache Settings")]
    // Tempo di scadenza della cache in secondi
    public float cacheExpirationSeconds = 300f; // 5 minuti
    // Numero massimo di entry nella cache (LRU)
    private const int MAX_CACHE_ENTRIES = 5;

    // Cache dei moltiplicatori per policy
    private Dictionary<string, CachedMultipliers> multiplierCache = new Dictionary<string, CachedMultipliers>();

    [System.Serializable]
    private class CachedMultipliers
    {
        public Dictionary<string, float> multipliers;
        public float timestamp;
    }

    [System.Serializable]
    public struct PolicyParameters
    {
        public float max_speed;  
        public float acceleration;
        public float brake_power;
        public float steering_speed; 
        public float consumption_multiplier; 
    }

    // Cache per i parametri fisici (Policy -> Params)
    private Dictionary<string, PolicyParameters> policyParamsCache = new Dictionary<string, PolicyParameters>();

    void Awake()
    {
        // Singleton pattern
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
        }

    }
    
    // Helper per ottenere l'ora simulata formattata (es. "8.5")
    private string GetCurrentSimulatedHour()
    {
        float h = 12.0f;
        if (TimeManager.Instance != null)
        {
            h = TimeManager.Instance.CurrentHour + (TimeManager.Instance.CurrentMinute / 60.0f);
        }
        return h.ToString("F1", System.Globalization.CultureInfo.InvariantCulture);
    }

    void Start()
    {
        // Avvia la sincronizzazione automatica POI-Zone
        StartCoroutine(SyncPoiZonesRoutine());
    }

    // Ottiene i moltiplicatori delle zone per una specifica policy.
    // Usa cache se disponibile e non scaduta.
    public void GetZoneMultipliers(string policyName, Action<Dictionary<string, float>> callback)
    {
        GetZoneMultipliers(policyName, null, callback);
    }

    // Ottiene i moltiplicatori delle zone per una specifica policy e condizione meteo.
    // Usa cache se disponibile e non scaduta.
    public void GetZoneMultipliers(string policyName, string weather, Action<Dictionary<string, float>> callback)
    {
        // Cache key include la situazione meteo e l'orario
        string hourStr = GetCurrentSimulatedHour();
        string cacheKey = $"{policyName}_{weather ?? "clear"}_{hourStr}";
        
        // Check cache
        if (multiplierCache.ContainsKey(cacheKey))
        {
            var cached = multiplierCache[cacheKey];
            if (Time.time - cached.timestamp < cacheExpirationSeconds)
            {
                Debug.Log($"[KBService] Using cached multipliers for '{cacheKey}'");
                callback?.Invoke(cached.multipliers);
                return;
            }
        }

        // Fetch dal backend
        StartCoroutine(FetchZoneMultipliers(policyName, weather, callback));
    }

    // Ottiene i parametri fisici per una policy (Speed, Accel, ecc.)
    public void GetPolicyParameters(string policyName, Action<PolicyParameters> callback)
    {
        // Check cache
        if (policyParamsCache.ContainsKey(policyName))
        {
            callback?.Invoke(policyParamsCache[policyName]);
            return;
        }

        StartCoroutine(FetchPolicyParameters(policyName, callback));
    }

    private IEnumerator FetchPolicyParameters(string policyName, Action<PolicyParameters> callback)
    {
        string url = $"{backendUrl}/api/policies/{UnityWebRequest.EscapeURL(policyName)}";
        Debug.Log($"[KBService] Fetching physics parameters from: {url}");

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 5;
            yield return request.SendWebRequest();

            PolicyParameters result = new PolicyParameters 
            { 
                max_speed = 40f, acceleration = 4f, brake_power = 10f, steering_speed = 6f, consumption_multiplier = 1.0f 
            };

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string json = request.downloadHandler.text;
                    
                    // Parsing manuale del JSON
                    result = ParsePolicyParamsJson(json);
                    
                    // Cache results
                    policyParamsCache[policyName] = result;
                    Debug.Log($"[KBService] Applied Physics for '{policyName}': MaxSpeed={result.max_speed}, Acc={result.acceleration}, Mult={result.consumption_multiplier}");
                }
                catch (Exception e)
                {
                    Debug.LogError($"[KBService] JSON parse error (Physics): {e.Message}");
                }
            }
            else
            {
                Debug.LogError($"[KBService] Physics Request failed: {request.error}");
            }

            callback?.Invoke(result);
        }
    }

    private PolicyParameters ParsePolicyParamsJson(string json)
    {
        PolicyParameters p = new PolicyParameters();
        p.max_speed = 10f; p.acceleration = 2f; p.brake_power = 10f; p.steering_speed = 6f; p.consumption_multiplier = 1.0f;

        try 
        {
            // Parsing per il JSON 
            string content = json.Trim();
            if (content.StartsWith("{")) content = content.Substring(1);
            if (content.EndsWith("}")) content = content.Substring(0, content.Length - 1);
            
            string[] pairs = content.Split(',');
            foreach (var pair in pairs)
            {
                string[] kv = pair.Split(':');
                if (kv.Length >= 2)
                {
                    string key = kv[0].Trim().Trim('"');
                    string valStr = kv[1].Trim();
                    
                    if (float.TryParse(valStr, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out float val))
                    {
                         if (key == "max_speed") p.max_speed = val;
                         if (key == "acceleration") p.acceleration = val;
                         if (key == "brake_power") p.brake_power = val;
                         if (key == "steering_speed") p.steering_speed = val;
                         if (key == "consumption_multiplier") p.consumption_multiplier = val;
                    }
                }
            }
            // Debug.Log($"[KBService] Parsed Physics: Speed={p.max_speed}, Acc={p.acceleration}, Cons={p.consumption_multiplier}");
        }
        catch (Exception e)
        {
            Debug.LogError($"[KBService] Error parsing physics JSON: {e.Message}. Content: {json}");
        }
        return p;
    }

    // Ottiene i moltiplicatori delle zone in modo sincrono (bloccante).
    public Dictionary<string, float> GetZoneMultipliersSync(string policyName, string weather = null)
    {
        string hourStr = GetCurrentSimulatedHour();
        string cacheKey = $"{policyName}_{weather ?? "clear"}_{hourStr}";

        // Check cache first
        if (multiplierCache.ContainsKey(cacheKey))
        {
            var cached = multiplierCache[cacheKey];
            if (Time.time - cached.timestamp < cacheExpirationSeconds)
            {
                return cached.multipliers;
            }
        }

        // Ritorna un dizionario vuoto in caso di cache mancante (non-blocking fallback)
        Debug.LogWarning($"[KBService] Cache miss for '{cacheKey}'. Returning empty multipliers and triggering fetch.");
        
        StartCoroutine(FetchZoneMultipliers(policyName, weather, null));
        
        return new Dictionary<string, float>();
    }

    // Ottiene i parametri fisici per una policy in modo sincrono (bloccante/cache).
    public PolicyParameters GetPolicyParametersSync(string policyName)
    {
        // Check cache
        if (policyParamsCache.ContainsKey(policyName))
        {
            return policyParamsCache[policyName];
        }

        // Return default values and trigger fetch
        Debug.LogWarning($"[KBService] Cache miss for policy params '{policyName}'. Returning default and triggering fetch.");
        StartCoroutine(FetchPolicyParameters(policyName, null));

        return new PolicyParameters 
        { 
            max_speed = 40f, acceleration = 4f, brake_power = 10f, steering_speed = 6f, consumption_multiplier = 1.0f 
        };
    }

    // Pre-carica i moltiplicatori per una policy (utile all'inizio della missione).
    public void PreloadMultipliers(string policyName)
    {
        GetZoneMultipliers(policyName, (multipliers) =>
        {
            Debug.Log($"[KBService] Preloaded {multipliers.Count} zone multipliers for policy '{policyName}'");
        });
    }

    // Invalida la cache per forzare un refresh.
    public void InvalidateCache()
    {
        multiplierCache.Clear();
        Debug.Log("[KBService] Cache invalidated");
    }

    // ========================================================================
    // SYNC POI-ZONES
    // ========================================================================

    private IEnumerator SyncPoiZonesRoutine()
    {
        yield return new WaitForSeconds(1.0f); // Attendi inizializzazione scena

        Debug.Log("[KBService] Starting POI-Zone sync (ID-based)...");

        // Ottieni riferimento al controller per accedere alla lista ordinata dei POI
        TaxiMissionController missionController = FindAnyObjectByType<TaxiMissionController>();
        
        if (missionController == null)
        {
            missionController = FindFirstObjectByType<TaxiMissionController>();
        }

        if (missionController == null || missionController.pointsOfInterest == null)
        {
            Debug.LogError("[KBService] TaxiMissionController or POI list not found. Sync skipped.");
            yield break;
        }

        List<WaypointNode> pois = missionController.pointsOfInterest;

        if (pois.Count == 0)
        {
            Debug.LogWarning("[KBService] No POIs found in MissionController to sync.");
            yield break;
        }

        System.Text.StringBuilder json = new System.Text.StringBuilder();
        json.Append("{\"mappings\":[");

        int count = 0;
        int syncedCount = 0;
        
        // Itera sui POI - usa poiId invece dell'indice per robustezza
        for (int i = 0; i < pois.Count; i++)
        {
            var poi = pois[i];
            
            // Salta POI senza ID configurato o senza zona
            if (poi == null || poi.poiId < 0 || poi.parentZone == null || string.IsNullOrEmpty(poi.parentZone.kbZoneId))
            {
                continue;
            }
            
            if (syncedCount > 0) json.Append(",");
            
            string zone = poi.parentZone.kbZoneId.Replace("\"", "\\\"");
            
            // DEBUG: Log dei primi 10 POI per debugging
            if (syncedCount < 10)
            {
                Debug.Log($"[KBService DEBUG] POI[{i}] id={poi.poiId}, name={poi.gameObject.name}, zone={zone}");
            }
            
            json.Append($"{{\"id_unity\":{poi.poiId},\"zone_id\":\"{zone}\"}}");
            syncedCount++;
        }
        json.Append("]}");

        if (syncedCount == 0)
        {
            Debug.Log("[KBService] No POIs with valid Zone ID found.");
            yield break;
        }

        string url = $"{backendUrl}/api/pois/sync_zones";
        Debug.Log($"[KBService] Syncing {syncedCount} POIs to backend: {url}");

        using (UnityWebRequest request = new UnityWebRequest(url, "POST"))
        {
            byte[] bodyRaw = System.Text.Encoding.UTF8.GetBytes(json.ToString());
            request.uploadHandler = new UploadHandlerRaw(bodyRaw);
            request.downloadHandler = new DownloadHandlerBuffer();
            request.SetRequestHeader("Content-Type", "application/json");

            yield return request.SendWebRequest();

            if (request.result == UnityWebRequest.Result.Success)
            {
                Debug.Log($"[KBService] POI Sync Success.");
            }
            else
            {
                Debug.LogError($"[KBService] POI Sync Failed: {request.error}");
            }
        }
    }

    private IEnumerator FetchZoneMultipliers(string policyName, string weather, Action<Dictionary<string, float>> callback)
    {
        string hourStr = GetCurrentSimulatedHour();
        string url = $"{backendUrl}/api/zones/multipliers?policy={UnityWebRequest.EscapeURL(policyName)}&hour={hourStr}";
        
        if (!string.IsNullOrEmpty(weather))
        {
            url += $"&weather={UnityWebRequest.EscapeURL(weather)}";
        }
        
        string cacheKey = $"{policyName}_{weather ?? "clear"}_{hourStr}";

        Debug.Log($"[KBService] Fetching multipliers from: {url}");

        using (UnityWebRequest request = UnityWebRequest.Get(url))
        {
            request.timeout = 5; // 5 secondi timeout

            yield return request.SendWebRequest();

            Dictionary<string, float> result = new Dictionary<string, float>();

            if (request.result == UnityWebRequest.Result.Success)
            {
                try
                {
                    string json = request.downloadHandler.text;
                    Debug.Log($"[KBService] Response: {json}");

                    // Parsing del JSON manuale (JsonUtility di Unity non supporta i dizionari)
                    result = ParseMultipliersJson(json);

                    if (result.Count > 0)
                    {
                        // LRU: rimuovi entry più vecchia se cache piena
                        if (multiplierCache.Count >= MAX_CACHE_ENTRIES && !multiplierCache.ContainsKey(cacheKey))
                        {
                            string oldestKey = null;
                            float oldestTime = float.MaxValue;
                            foreach (var kvp in multiplierCache)
                            {
                                if (kvp.Value.timestamp < oldestTime)
                                {
                                    oldestTime = kvp.Value.timestamp;
                                    oldestKey = kvp.Key;
                                }
                            }
                            if (oldestKey != null)
                            {
                                multiplierCache.Remove(oldestKey);
                                Debug.Log($"[KBService] Cache LRU: rimossa entry '{oldestKey}'");
                            }
                        }
                        
                        // Update cache
                        multiplierCache[cacheKey] = new CachedMultipliers
                        {
                            multipliers = result,
                            timestamp = Time.time
                        };

                        Debug.Log($"[KBService] Loaded {result.Count} multipliers for '{cacheKey}' (cache size: {multiplierCache.Count}/{MAX_CACHE_ENTRIES})");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogError($"[KBService] JSON parse error: {e.Message}");
                }
            }
            else
            {
                Debug.LogError($"[KBService] Request failed: {request.error}");
            }

            callback?.Invoke(result);
        }
    }

    // Parse manuale del JSON response.
    // Formato atteso: {"policy": "Comfort", "multipliers": {"centro_storico": 2.5, ...}}
    private Dictionary<string, float> ParseMultipliersJson(string json)
    {
        var result = new Dictionary<string, float>();

        try
        {
            // Trova la sezione "multipliers"
            int multipliersStart = json.IndexOf("\"multipliers\"");
            if (multipliersStart < 0) return result;

            // Trova l'inizio dell'oggetto multipliers
            int braceStart = json.IndexOf('{', multipliersStart);
            if (braceStart < 0) return result;

            // Trova la fine dell'oggetto multipliers
            int braceEnd = json.IndexOf('}', braceStart);
            if (braceEnd < 0) return result;

            // Estrai il contenuto dell'oggetto
            string multipliersContent = json.Substring(braceStart + 1, braceEnd - braceStart - 1);

            // Parse key-value pairs
            // Formato: "key": value, "key2": value2
            string[] pairs = multipliersContent.Split(',');

            foreach (string pair in pairs)
            {
                if (string.IsNullOrWhiteSpace(pair)) continue;

                string[] keyValue = pair.Split(':');
                if (keyValue.Length != 2) continue;

                // Pulisci la chiave (rimuovi virgolette e spazi)
                string key = keyValue[0].Trim().Trim('"');
                
                // Parse del valore float
                string valueStr = keyValue[1].Trim();
                if (float.TryParse(valueStr, System.Globalization.NumberStyles.Float, 
                    System.Globalization.CultureInfo.InvariantCulture, out float value))
                {
                    result[key] = value;
                }
            }
        }
        catch (Exception e)
        {
            Debug.LogError($"[KBService] ParseMultipliersJson error: {e.Message}");
        }

        return result;
    }
}
