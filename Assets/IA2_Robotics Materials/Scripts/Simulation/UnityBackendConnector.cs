using UnityEngine;
using System;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Collections.Concurrent;
using System.Collections.Generic;

public class UnityBackendConnector : MonoBehaviour
{
    [Header("WebSocket Configuration")]
    public string serverUrl = "ws://localhost:8000/ws";
    public bool autoReconnect = true;
    public float reconnectInterval = 5f;
    
    [Header("Debug")]
    public bool verboseLogging = true;
    
    [Header("Status")]
    [SerializeField] private bool isConnected = false;
    [SerializeField] private string lastMessageReceived = "";
    
    private ClientWebSocket webSocket;
    private CancellationTokenSource cancellationTokenSource;
    private ConcurrentQueue<string> receivedMessages = new ConcurrentQueue<string>();
    
    public event Action<string> OnMessageReceived;
    public event Action OnConnected;
    public event Action OnDisconnected;
    
    public static UnityBackendConnector Instance { get; private set; }
    private string sessionId;
    
    public bool IsConnected => isConnected;
    public string SessionId => sessionId;

    void Awake()
    {
        if (Instance == null)
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
        }
        else
        {
            Destroy(gameObject);
            return;
        }
        
        sessionId = "unity-" + Guid.NewGuid().ToString().Substring(0, 8);
        Log($"Session ID: {sessionId}");
    }
    
    // Usiamo la discard variable (_) per dire che è una chiamata Fire-and-Forget
    void Start() { _ = ConnectAsync(); }
    
    void Update()
    {
        while (receivedMessages.TryDequeue(out string message))
        {
            ProcessReceivedMessage(message);
        }
    }
    
    // Discard variable anche qui
    void OnDestroy() { _ = DisconnectAsync(); }
    void OnApplicationQuit() { _ = DisconnectAsync(); }
    
    public async void SendToBackend(string jsonMessage)
    {
        if (webSocket == null || webSocket.State != WebSocketState.Open) return;
        
        try
        {
            byte[] buffer = Encoding.UTF8.GetBytes(jsonMessage);
            await webSocket.SendAsync(new ArraySegment<byte>(buffer), WebSocketMessageType.Text, true, cancellationTokenSource.Token);
            if(verboseLogging) Debug.Log($"[WS SENT] {jsonMessage}");
        }
        catch (Exception ex) { LogError($"Errore invio: {ex.Message}"); }
    }

    public void SendJsonPayload<T>(T payloadObj)
    {
        string json = JsonUtility.ToJson(payloadObj);
        SendToBackend(json); 
    }
    
    // Wrapper asincroni con discard per chiamate manuali da bottone/inspector
    public async void Connect() { await ConnectAsync(); }
    public async void Disconnect() { await DisconnectAsync(); }

    private async Task ConnectAsync()
    {
        if (webSocket != null && webSocket.State == WebSocketState.Open) return;
        
        cancellationTokenSource = new CancellationTokenSource();
        webSocket = new ClientWebSocket();
        
        try
        {
            await webSocket.ConnectAsync(new Uri(serverUrl), cancellationTokenSource.Token);
            isConnected = true;
            Log($"[CONNECTED] {serverUrl}");
            OnConnected?.Invoke();
            _ = ReceiveLoopAsync(); // Fire and forget del loop di ricezione
        }
        catch (Exception ex)
        {
            LogError($"Connection Failed: {ex.Message}");
            isConnected = false;
            if (autoReconnect)
            {
                await Task.Delay((int)(reconnectInterval * 1000));
                _ = ConnectAsync(); // Riprova
            }
        }
    }
    
    private async Task DisconnectAsync()
    {
        if (webSocket == null) return;
        try
        {
            cancellationTokenSource?.Cancel();
            if (webSocket.State == WebSocketState.Open)
                await webSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Unity closing", CancellationToken.None);
            
            webSocket.Dispose();
            webSocket = null;
            isConnected = false;
            OnDisconnected?.Invoke();
        }
        catch { /* Ignora errori in chiusura */ }
    }
    
    private async Task ReceiveLoopAsync()
    {
        byte[] buffer = new byte[8192];
        StringBuilder messageBuilder = new StringBuilder();
        
        try
        {
            while (webSocket != null && webSocket.State == WebSocketState.Open)
            {
                var result = await webSocket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationTokenSource.Token);
                if (result.MessageType == WebSocketMessageType.Close) break;
                
                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                if (result.EndOfMessage)
                {
                    receivedMessages.Enqueue(messageBuilder.ToString());
                    messageBuilder.Clear();
                }
            }
        }
        catch { /* Connessione chiusa o annullata */ }
        finally { await HandleDisconnection(); }
    }
    
    private async Task HandleDisconnection()
    {
        isConnected = false;
        OnDisconnected?.Invoke();
        if (autoReconnect)
        {
            await Task.Delay((int)(reconnectInterval * 1000));
            _ = ConnectAsync();
        }
    }
    
    private void ProcessReceivedMessage(string message)
    {
        lastMessageReceived = message;
        if(verboseLogging) Log($"[RECEIVED] {message}");
        OnMessageReceived?.Invoke(message);
        
        try
        {
            var response = JsonUtility.FromJson<BackendResponse>(message);
            if (response != null && !string.IsNullOrEmpty(response.type))
            {
                if(response.type == "error") LogError($"[BACKEND ERROR] {response.message}");
            }
        }
        catch (Exception ex) { LogWarning($"JSON Parse Error: {ex.Message}"); }
    }
    
    private void Log(string msg) { if (verboseLogging) Debug.Log($"[WS] {msg}"); }
    private void LogWarning(string msg) { Debug.LogWarning($"[WS] {msg}"); }
    private void LogError(string msg) { Debug.LogError($"[WS] {msg}"); }

    [Serializable]
    public class BackendResponse
    {
        public string type;
        public string session_id;
        public string message;
    }
}