
#Main FastAPI application for Taxi Backend.

#WebSocket endpoint per comunicazione con Unity.


import json
import re
from contextlib import asynccontextmanager
from typing import Any

from fastapi import FastAPI, WebSocket, WebSocketDisconnect
from fastapi.middleware.cors import CORSMiddleware

from app.config import get_settings
from app.schemas import (
    UserMessage,
    Trigger,
    UIAction,
    AssistantResponse,
    IncomingMessage,
    SessionMode,
)
from app.session_store import session_store
from app.neo4j.driver import neo4j_driver
from app.llm.agent import get_llm_client
from app.services.policy import policy_service
from app.neo4j.seed2 import get_user_conditions, get_effective_policy
from app.neo4j.seed2 import get_user_conditions, get_effective_policy
from app.utils.logging import setup_logging, get_logger
from app.utils.formatting import format_duration_minutes

# Setup logging
setup_logging()
logger = get_logger(__name__)


# =============================================================================
# LIFECYCLE
# =============================================================================

@asynccontextmanager
async def lifespan(app: FastAPI):
    """Gestisce startup e shutdown dell'applicazione."""
    # Startup
    logger.info("Starting Taxi Backend...")
    
    try:
        await neo4j_driver.connect()
        logger.info("Neo4j connected")
    except Exception as e:
        logger.warning(f"Neo4j connection failed: {e}. Running in degraded mode.")
    
    yield
    
    # Shutdown
    logger.info("Shutting down Taxi Backend...")
    await neo4j_driver.disconnect()


# =============================================================================
# APP
# =============================================================================

app = FastAPI(
    title="Taxi Backend",
    description="Backend per simulazione taxi autonomo Unity",
    version="1.0.0",
    lifespan=lifespan,
)

# CORS
app.add_middleware(
    CORSMiddleware,
    allow_origins=["*"],
    allow_credentials=True,
    allow_methods=["*"],
    allow_headers=["*"],
)

# Static files for test-client
from fastapi.staticfiles import StaticFiles
from pathlib import Path

# Mount test-client directory on /app
test_client_dir = Path(__file__).parent.parent / "test-client"
if test_client_dir.exists():
    app.mount("/app", StaticFiles(directory=str(test_client_dir), html=True), name="test-client")


# =============================================================================
# ENDPOINTS
# =============================================================================

@app.get("/")
async def root():
    """Health check endpoint."""
    return {"status": "ok", "service": "taxi-backend"}


@app.get("/health")
async def health():
    return {"status": "ok", "db": neo4j_driver.driver is not None}


@app.get("/api/policies/{policy_name}")
async def get_policy_parameters(policy_name: str):
    """Restituisce i parametri fisici della policy richiesta (da Neo4j)."""
    # Normalizza input (es. "sport" -> "Sport")
    normalized_name = policy_name.capitalize()
    
    query = """
    MATCH (p:Policy {name: $name})
    RETURN p.max_speed AS max_speed,
           p.acceleration AS acceleration,
           p.brake_power AS brake_power,
           p.steering_speed AS steering_speed,
           p.consumption_multiplier AS consumption_multiplier
    """
    
    try:
        results = await neo4j_driver.execute_query(query, {"name": normalized_name})
        if not results:
            # Fallback se la policy non esiste o non ha parametri: return defaults (Comfort)
            return {
                "max_speed": 40.0,
                "acceleration": 4.0,
                "brake_power": 10.0,
                "steering_speed": 40.0,
                "consumption_multiplier": 1.0,
                "source": "default_fallback"
            }
            
        record = results[0]
        return {
            "max_speed": record.get("max_speed", 40.0),
            "acceleration": record.get("acceleration", 4.0),
            "brake_power": record.get("brake_power", 10.0),
            "steering_speed": record.get("steering_speed", 40.0),
            "consumption_multiplier": record.get("consumption_multiplier", 1.0),
            "source": "neo4j"
        }
    except Exception as e:
        logger.error(f"Error fetching policy params: {e}")
        # Default in caso di errore DB
        return {
            "max_speed": 40.0,
            "acceleration": 4.0,
            "brake_power": 10.0,
            "steering_speed": 40.0,
            "source": "error_default"
        }
async def health():
    """Detailed health check."""
    neo4j_ok = await neo4j_driver.health_check()
    return {
        "status": "ok" if neo4j_ok else "degraded",
        "neo4j": "connected" if neo4j_ok else "disconnected"
    }


# =============================================================================
# POI SEARCH ENDPOINTS
# =============================================================================

from app.neo4j.repo import neo4j_repo


@app.get("/api/pois/search")
async def search_pois(q: str = "", limit: int = 5, user_id: str | None = None):
    """
    POI autocomplete search for the booking screen.
    
    Args:
        q: Search query (partial POI name)
        limit: Max results to return (default: 5)
        
    Returns:
        List of matching POIs with id, name, category, rating, id_unity
    """
    if len(q) < 2:
        return {"pois": []}
    
    results = await neo4j_repo.search_pois_autocomplete(q, limit)

    if user_id:
        home = await neo4j_repo.get_user_home(user_id)
        home_id = home.get("id") if home else None
        query_lower = q.lower()
        is_home_query = "casa" in query_lower or "home" in query_lower

        if is_home_query:
            return {"pois": [home] if home else []}

        filtered = []
        for poi in results:
            category = poi.get("category")
            poi_id = poi.get("id")
            if category == "Residenziale" and poi_id != home_id:
                continue
            filtered.append(poi)
        return {"pois": filtered}

    return {"pois": results}


@app.get("/api/pois/{poi_id}")
async def get_poi(poi_id: str):
    """Get a specific POI by ID."""
    poi = await neo4j_repo.get_poi_by_id(poi_id)
    if poi:
        return poi
    return {"error": "POI not found"}, 404



@app.post("/api/pois/sync_zones")
async def sync_poi_zones(data: dict[str, Any]):
    """
    Sincronizza la mappatura POI -> Zona ricevuta da Unity.
    Usa id_unity (indice nella lista POI) per il match robusto.
    """
    mappings = data.get("mappings", [])
    if not mappings:
        return {"status": "skipped", "message": "No mappings provided"}
    
    logger.info(f"[SYNC] Received {len(mappings)} POI-Zone mappings from Unity (using id_unity)")
    
    # DEBUG: Stampa i primi 10 mapping per vedere la Fabbrica (ID 6)
    for i, mapping in enumerate(mappings[:10]):
        logger.info(f"[SYNC DEBUG] [{i}] id_unity={mapping.get('id_unity')}, zone_id={mapping.get('zone_id')}")
    
    try:
        # Prima cancella tutte le vecchie relazioni LOCATED_IN per i POI che stiamo sincronizzando
        query_cleanup = """
        UNWIND $mappings AS item
        MATCH (p:PuntoInteresse {id_unity: item.id_unity})-[old:LOCATED_IN]->()
        DELETE old
        """
        await neo4j_driver.execute_write(query_cleanup, {"mappings": mappings})
        
        # Poi crea le nuove relazioni
        query = """
        UNWIND $mappings AS item
        MATCH (z:Zone {id: item.zone_id})
        MATCH (p:PuntoInteresse {id_unity: item.id_unity})
        MERGE (p)-[:LOCATED_IN]->(z)
        """
        
        await neo4j_driver.execute_write(query, {"mappings": mappings})
        
        logger.info(f"[SYNC] Successfully linked {len(mappings)} POIs to Zones")
        return {"status": "ok", "synced": len(mappings)}
        
    except Exception as e:
        logger.error(f"[SYNC] Error syncing zones: {e}")
        return {"status": "error", "message": str(e)}


# =============================================================================
# ZONE MULTIPLIERS ENDPOINTS (per A* pesato)
# =============================================================================

@app.get("/api/zones/multipliers")
async def get_zone_multipliers(policy: str = "Comfort", weather: str | None = None, hour: float = 12.0):
    """
    Ottiene i moltiplicatori di tutte le zone per una specifica policy, meteo e orario.
    
    Args:
        policy: Nome della policy (Comfort, Sport, Eco)
        weather: Condizione meteo (es. "rain")
        hour: Orario simulato (0-24) per regole temporali
        
    Returns:
        Dict con zone_id -> multiplier
    """
    from app.neo4j.seed2 import get_zone_multipliers_with_context

    try:
        # Passiamo anche l'orario alla funzione
        multipliers = await get_zone_multipliers_with_context(policy, weather, hour)
        
        weather_str = weather if weather else "clear"
        logger.info(f"[ZONES] Multipliers for policy '{policy}' + weather '{weather_str}' + hour {hour:.1f}: {multipliers}")
        
        return {
            "policy": policy,
            "weather": weather,
            "hour": hour,
            "multipliers": multipliers
        }
    except Exception as e:
        logger.error(f"[ZONES] Error getting multipliers: {e}")
        return {
            "policy": policy,
            "multipliers": {},
            "error": str(e)
        }


@app.get("/api/zones")
async def get_all_zones():
    """
    Ottiene tutte le zone definite nella KB.
    
    Returns:
        Lista di zone con id, name, surface, type
    """
    query = """
    MATCH (z:Zone)
    RETURN z.id AS id, z.name AS name, z.surface AS surface, z.type AS type
    ORDER BY z.name
    """
    
    try:
        results = await neo4j_driver.execute_query(query, {})
        return {"zones": results}
    except Exception as e:
        logger.error(f"[ZONES] Error getting zones: {e}")
        return {"zones": [], "error": str(e)}


# =============================================================================
# MUSIC ENDPOINTS
# =============================================================================

from fastapi.responses import FileResponse
from app.services.music_service import music_service


@app.get("/music/genres")
async def get_music_genres():
    """Ottiene i generi musicali disponibili."""
    return {"genres": music_service.get_available_genres()}


@app.get("/music/{genre}")
async def stream_music(genre: str):
    """Stream file audio per genere."""
    file_path = music_service.get_music_file_path(genre)
    if file_path and file_path.exists():
        return FileResponse(
            path=file_path,
            media_type="audio/mpeg",
            filename=f"{genre}.mp3"
        )
    return {"error": f"Genre '{genre}' not found"}, 404


@app.get("/music/state/{session_id}")
async def get_music_state(session_id: str):
    """Ottiene lo stato della musica per una sessione."""
    return session_store.get_music_state(session_id)


@app.post("/music/control")
async def control_music(data: dict[str, Any]):
    """Controlla la musica (play/stop/pause)."""
    session_id = data.get("session_id")
    action = data.get("action")  # play, stop, pause, resume
    genre = data.get("genre")
    
    if not session_id or not action:
        return {"error": "Missing session_id or action"}
    
    if action == "play" and genre:
        normalized = music_service.normalize_genre(genre)
        if normalized:
            session_store.start_music(session_id, normalized)
            return {"status": "playing", "genre": normalized, "url": f"/music/{normalized}"}
        return {"error": f"Unknown genre: {genre}"}
    elif action == "stop":
        session_store.stop_music(session_id)
        return {"status": "stopped"}
    elif action == "pause":
        session_store.pause_music(session_id)
        return {"status": "paused"}
    elif action == "resume":
        session_store.resume_music(session_id)
        state = session_store.get_music_state(session_id)
        return {"status": "playing", "genre": state["genre"]}
    
    return {"error": f"Unknown action: {action}"}


@app.post("/test")
async def test_message(data: dict[str, Any]):
    """
    Endpoint HTTP per testing senza WebSocket.
    
    Accetta gli stessi payload di WebSocket e ritorna la risposta.
    """
    logger.info(f"Test endpoint received: {data}")
    try:
        response = await handle_message(data)
        logger.info(f"Test endpoint response: {response}")
        return response
    except Exception as e:
        logger.error(f"Test endpoint error: {e}")
        return {"type": "error", "message": str(e)}


@app.get("/llm/models")
async def get_available_models():
    """Lista i modelli LLM disponibili."""
    from app.config import OPENROUTER_FREE_MODELS
    
    settings = get_settings()
    return {
        "current_provider": settings.llm_provider,
        "current_model": settings.openrouter_model if settings.llm_provider == "openrouter" else settings.ollama_model,
        "openrouter_models": OPENROUTER_FREE_MODELS,
        "ollama_model": settings.ollama_model
    }


@app.post("/llm/set-model")
async def set_llm_model(data: dict[str, str]):
    """
    Cambia il provider/modello LLM a runtime.
    
    Body:
        provider: "ollama" o "openrouter"
        model: nome del modello (opzionale per openrouter)
        api_key: API key per OpenRouter (opzionale se già configurato)
    """
    from app.llm.agent import reset_llm_client
    
    provider = data.get("provider", "ollama")
    model = data.get("model")
    api_key = data.get("api_key")
    
    settings = get_settings()
    
    # Aggiorna settings (nota: queste modifiche sono temporanee in memoria)
    import app.config as config_module
    
    if provider == "openrouter":
        if api_key:
            settings.openrouter_api_key = api_key
        if model:
            settings.openrouter_model = model
        settings.llm_provider = "openrouter"
    else:
        if model:
            settings.ollama_model = model
        settings.llm_provider = "ollama"
    
    # Resetta il client per ricaricare con nuove impostazioni
    await reset_llm_client()
    
    # Inizializza nuovo client
    client = await get_llm_client()
    
    return {
        "success": True,
        "provider": settings.llm_provider,
        "model": settings.openrouter_model if provider == "openrouter" else settings.ollama_model,
        "client_type": type(client).__name__
    }



# =============================================================================
# CONNECTION MANAGER - Gestione connessioni WebSocket
# =============================================================================

class ConnectionManager:
    """
    Gestisce le connessioni WebSocket distinguendo tra:
    - unity_connection: la connessione di Unity
    - chat_clients: le connessioni dei client chat (mobile.html)
    """
    
    def __init__(self):
        self.unity_connection: WebSocket | None = None
        self.chat_clients: dict[str, WebSocket] = {}  # session_id -> WebSocket
    
    async def register_unity(self, websocket: WebSocket):
        """Registra la connessione Unity."""
        self.unity_connection = websocket
        logger.info("[CONNECTION] Unity client registered")
    
    async def unregister_unity(self):
        """Rimuove la connessione Unity."""
        self.unity_connection = None
        logger.info("[CONNECTION] Unity client disconnected")
    
    async def register_chat_client(self, session_id: str, websocket: WebSocket):
        """Registra un client chat."""
        self.chat_clients[session_id] = websocket
        logger.info(f"[CONNECTION] Chat client registered: {session_id}")
    
    async def unregister_chat_client(self, session_id: str):
        """Rimuove un client chat."""
        if session_id in self.chat_clients:
            del self.chat_clients[session_id]
            logger.info(f"[CONNECTION] Chat client disconnected: {session_id}")
    
    async def send_to_unity(self, message: dict) -> bool:
        """Invia un messaggio a Unity."""
        if self.unity_connection is None:
            logger.warning("[CONNECTION] No Unity connection available")
            return False
        
        try:
            await self.unity_connection.send_text(json.dumps(message, ensure_ascii=False))
            logger.info(f"[CONNECTION] Sent to Unity: {message.get('type', 'unknown')}")
            return True
        except Exception as e:
            logger.error(f"[CONNECTION] Failed to send to Unity: {e}")
            return False
    
    async def send_to_chat_client(self, session_id: str, message: dict) -> bool:
        """Invia un messaggio a un client chat specifico."""
        if session_id not in self.chat_clients:
            logger.warning(f"[CONNECTION] Chat client not found: {session_id}")
            return False
        
        try:
            ui_options = message.get("ui_options")
            if isinstance(ui_options, list) and len(ui_options) > 0:
                session_store.update_session(session_id, last_ui_options=ui_options)

            await self.chat_clients[session_id].send_text(json.dumps(message, ensure_ascii=False))
            logger.info(f"[CONNECTION] Sent to chat client {session_id}: {message.get('type', 'unknown')}")
            return True
        except Exception as e:
            logger.error(f"[CONNECTION] Failed to send to chat client {session_id}: {e}")
            return False
    
    async def broadcast_to_chat_clients(self, message: dict):
        """Invia un messaggio a tutti i client chat."""
        for session_id in self.chat_clients:
            await self.send_to_chat_client(session_id, message)
    
    def is_unity_connected(self) -> bool:
        """Verifica se Unity è connesso."""
        return self.unity_connection is not None


# Istanza globale del connection manager
connection_manager = ConnectionManager()


# =============================================================================
# WEBSOCKET
# =============================================================================


@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    """
    WebSocket endpoint per comunicazione con Unity e chat clients.
    
    Distingue tra:
    - Unity: invia unity_message con action=ping come primo messaggio
    - Chat clients: inviano altri tipi di messaggi
    """
    await websocket.accept()
    logger.info("WebSocket connection accepted")
    
    is_unity = False
    client_session_id = None
    
    try:
        while True:
            # Ricevi messaggio
            data = await websocket.receive_text()
            logger.debug(f"Received: {data}")
            
            try:
                # Parse messaggio
                message_data = json.loads(data)
                msg_type = message_data.get("type", "")
                session_id = message_data.get("session_id", "")
                
                # === IDENTIFICAZIONE CLIENT ===
                # Unity si identifica con unity_message + action=ping
                if msg_type == "unity_message":
                    action = message_data.get("action", "")
                    if action == "ping" and not is_unity:
                        is_unity = True
                        await connection_manager.register_unity(websocket)
                        logger.info(f"[WS] Identified as Unity client: {session_id}")
                
                # Chat client si identifica con session_id
                elif not is_unity and session_id and session_id not in connection_manager.chat_clients:
                    client_session_id = session_id
                    await connection_manager.register_chat_client(session_id, websocket)
                    logger.info(f"[WS] Identified as chat client: {session_id}")
                
                # === GESTIONE MESSAGGI ===
                response = await handle_message(message_data, is_unity)
                
                # Aggiorna opzioni attive per sessione chat (se presenti)
                if not is_unity and session_id:
                    ui_options = response.get("ui_options")
                    if isinstance(ui_options, list) and len(ui_options) > 0:
                        session_store.update_session(session_id, last_ui_options=ui_options)

                # Invia risposta al mittente
                response_json = json.dumps(response, ensure_ascii=False)
                await websocket.send_text(response_json)
                logger.debug(f"Sent: {response_json}")
                
            except json.JSONDecodeError as e:
                logger.error(f"Invalid JSON: {e}")
                error_response = {
                    "type": "error",
                    "message": "Invalid JSON format"
                }
                await websocket.send_text(json.dumps(error_response))
                
            except Exception as e:
                logger.error(f"Error handling message: {e}")
                import traceback
                traceback.print_exc()
                error_response = {
                    "type": "error",
                    "message": f"Internal error: {str(e)}"
                }
                await websocket.send_text(json.dumps(error_response))
                
    except WebSocketDisconnect:
        logger.info("WebSocket disconnected")
        if is_unity:
            await connection_manager.unregister_unity()
        elif client_session_id:
            await connection_manager.unregister_chat_client(client_session_id)



# =============================================================================
# MESSAGE HANDLERS
# =============================================================================

async def handle_message(data: dict[str, Any], is_unity: bool = False) -> dict[str, Any]:
    """
    Gestisce un messaggio in ingresso e genera la risposta.
    
    Args:
        data: Messaggio JSON parsato
        is_unity: True se il messaggio arriva da Unity
        
    Returns:
        Risposta strutturata
    """
    msg_type = data.get("type", "")
    session_id = data.get("session_id", "unknown")
    
    logger.info(f"Handling message type={msg_type} session={session_id} from_unity={is_unity}")
    
    if msg_type == "unity_message":
        return await handle_unity_message(data)
    
    elif msg_type == "user_message":
        return await handle_user_message(data)
    
    elif msg_type == "pre_ride_message":
        return await handle_pre_ride_message(data)
    
    elif msg_type == "trigger":
        return await handle_trigger(data)
    
    elif msg_type == "ui_action":
        return await handle_ui_action(data)
    
    # === BOOKING FLOW: Richieste dalla Chat UI ===
    elif msg_type == "richiesta_prenotazione":
        # Richiesta prenotazione dalla chat - forward a Unity e attendi risposta
        return await handle_booking_request(data)
    
    elif msg_type == "risposta_coda_attesa":
        # Risposta utente alla coda - forward a Unity
        return await handle_user_queue_response(data)
    
    elif msg_type == "annulla_prenotazione":
        # Richiesta annullamento prenotazione dalla chat - forward a Unity
        return await handle_booking_cancellation(data)
    
    # === BOOKING FLOW: Risposte da Unity ===
    elif msg_type == "risposta_prenotazione":
        # Risposta da Unity - inoltra al chat client
        return await handle_booking_response(data)
    
    elif msg_type == "conferma_coda":
        return await handle_queue_confirmation(data)

    elif msg_type == "queue_update":
        return await handle_queue_update(data)
    
    # === DESTINATION CHANGE & END RIDE ===
    elif msg_type == "cambio_destinazione":
        # Richiesta cambio destinazione dalla chat - forward a Unity
        return await handle_destination_change_request(data)
    
    elif msg_type == "fine_corsa":
        # Richiesta fine corsa dalla chat - forward a Unity
        return await handle_end_ride_request(data)
    
    elif msg_type == "risposta_cambio_destinazione":
        # Risposta cambio destinazione da Unity - inoltra al chat client
        return await handle_destination_change_response(data)
    
    elif msg_type == "risposta_fine_corsa":
        # Risposta fine corsa da Unity - inoltra al chat client
        return await handle_end_ride_response(data)
    
    elif msg_type == "risposta_annullamento":
        # Risposta annullamento da Unity - inoltra al chat client
        return await handle_cancellation_response(data)
    
    else:
        return {
            "type": "error",
            "session_id": session_id,
            "message": f"Unknown message type: {msg_type}"
        }





async def _process_side_effects(response: dict[str, Any], session_id: str):
    """
    Elabora comandi nella risposta per side-effects (es. notifica Unity).
    Indispensabile per comandi generati da PolicyService che devono raggiungere Unity.
    """
    commands = response.get("commands", [])
    
    for cmd in commands:
        cmd_type = cmd.get("type")
        payload = cmd.get("payload", {})
        
        # REROUTE_TO -> Invia cambio destinazione a Unity
        if cmd_type == "REROUTE_TO":
            # VALIDAZIONE: Verifica che ci sia una corsa attiva
            if not session_store.is_ride_active(session_id):
                logger.warning(f"[MAIN] REROUTE_TO ignorato: nessuna corsa attiva per session {session_id}")
                continue
            
            poi_id = payload.get("poi_id")
            poi_name = payload.get("name")
            id_unity = payload.get("id_unity")
            
            # Validation: ensure id_unity is present
            if id_unity is None:
                logger.warning(f"[MAIN] REROUTE_TO missing id_unity for POI {poi_name} ({poi_id}). Attempting fetch...")
                try:
                    from app.neo4j.repo import neo4j_repo
                    poi_data = await neo4j_repo.get_poi_by_id(poi_id)
                    if poi_data and "id_unity" in poi_data:
                        id_unity = poi_data["id_unity"]
                        logger.info(f"[MAIN] Recovered id_unity: {id_unity}")
                    else:
                        logger.error(f"[MAIN] Failed to recover id_unity for POI {poi_id}")
                except Exception as e:
                    logger.error(f"[MAIN] Error fetching POI data: {e}")

            # Costruisci payload per Unity
            unity_payload = {
                "nuova_destinazione": {
                    "poi_id": poi_id,
                    "nome": poi_name,
                    "poi_id_unity": id_unity
                }
            }
            
            unity_msg = {
                "type": "cambio_destinazione",
                "session_id": session_id,
                "payload": unity_payload
            }
            
            if connection_manager.is_unity_connected():
                logger.info(f"[MAIN] Sending REROUTE_TO to Unity: {json.dumps(unity_msg, ensure_ascii=False)}")
                await connection_manager.send_to_unity(unity_msg)
                logger.info(f"[MAIN] REROUTE_TO (side-effect) inoltrato a Unity per session {session_id}")
            else:
                logger.warning(f"[MAIN] REROUTE_TO ignorato: Unity non connesso")


async def handle_user_message(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce un messaggio testuale dall'utente.
    
    ARCHITETTURA LLM-FIRST:
    1. Negation detection - blocca azioni non volute
    2. Simple confirmations/rejections - gestione rapida
    3. Help requests - mostra capabilities
    4. Greetings - risposta cortese
    5. LLM tool classification - PRINCIPALE
    6. Conversational fallback
    
    Pattern matching è stato RIMOSSO per evitare falsi positivi.
    """
    import time
    from app.llm.intent_classifier import intent_classifier
    from app.utils.text import analyze_text, is_greeting, is_help_request
    from app.utils.timing import RequestTimer
    
    # Start timing
    request_start = time.perf_counter()
    timer = RequestTimer("handle_user_message")
    
    msg = UserMessage(**data)
    
    # Aggiorna sessione
    session_store.update_session(
        session_id=msg.session_id,
        user_id=msg.user_id,
        ride_id=msg.ride_id,
        city=msg.city,
        taxi_x=msg.taxi.x,
        taxi_y=msg.taxi.y,
    )
    
    session = session_store.get_session(msg.session_id)
    user_id = session.user_id or msg.user_id or "unknown"
    text = msg.text.strip()
    
    logger.info(f"\n{'='*70}")
    logger.info(f"📨 INCOMING USER MESSAGE")
    logger.info(f"{'='*70}")
    logger.info(f"  Session: {msg.session_id}")
    logger.info(f"  User: {user_id}")
    logger.info(f"  Text: '{text}'")
    logger.info(f"  Mode: {session.mode}")
    logger.info(f"  Music: playing={session.music_playing}, genre={session.music_genre}")
    logger.info(f"  Active POI suggestions: {len(session.last_poi_suggestions)}")
    logger.info(f"{'='*70}")

    
    # =========================================================================
    # 0. ANALISI TESTO (negazioni, conferme, rifiuti)
    # =========================================================================
    analysis = analyze_text(text)
    
    logger.info(f"[ANALYSIS] negation={analysis.has_negation}, confirm={analysis.is_simple_confirmation}, reject={analysis.is_simple_rejection}")
    
    # =========================================================================
    # 1. SIMPLE REJECTIONS (no, annulla, lascia stare)
    # =========================================================================
    if analysis.is_simple_rejection:
        logger.info("Simple rejection detected")
        # Pulisci stato
        session_store.update_session(msg.session_id, last_poi_suggestions=[])
        session_store.clear_pending_question(msg.session_id)
        
        return {
            "type": "assistant_response",
            "session_id": msg.session_id,
            "message": "Va bene, nessun problema! Se hai bisogno di qualcosa, sono qui. 🚕",
            "ui_options": [],
            "commands": []
        }
    
    # =========================================================================
    # 2. SIMPLE CONFIRMATIONS (ok, sì, perfetto, grazie)
    # =========================================================================
    if analysis.is_simple_confirmation:
        logger.info("Simple confirmation detected")
        return {
            "type": "assistant_response",
            "session_id": msg.session_id,
            "message": "👍 Ricevuto! Continuiamo verso la destinazione.",
            "ui_options": [],
            "commands": []
        }
    
    # =========================================================================
    # 3. HELP REQUEST (aiuto, cosa puoi fare)
    # =========================================================================
    if is_help_request(text):
        logger.info("Help request detected")
        return {
            "type": "assistant_response",
            "session_id": msg.session_id,
            "message": "🚕 **Ecco cosa posso fare per te:**\n\n"
                      "🍕 **Cibo** - Dimmi se hai fame e ti trovo un posto dove mangiare\n"
                      "🍺 **Bevande** - Bar, caffè, cocktail\n"
                      "💊 **Farmacia** - Se ti senti male\n"
                      "🛍️ **Shopping** - Negozi, regali, spesa\n"
                      "🎵 **Musica** - Metti, ferma, cambia genere\n"
                      "🚗 **Guida** - Veloce, lenta, normale\n"
                      "🗺️ **Tour** - Ti mostro i posti interessanti\n\n"
                      "Dimmi cosa ti serve!",
            "ui_options": [
                {"id": "quick:fame", "label": "🍕 Ho fame"},
                {"id": "quick:sete", "label": "🍺 Ho sete"},
                {"id": "ask_music", "label": "🎵 Metti musica"},
            ],
            "commands": []
        }
    
    # =========================================================================
    # 4. GREETINGS (ciao, salve)
    # =========================================================================
    if is_greeting(text):
        logger.info("Greeting detected")
        return {
            "type": "assistant_response",
            "session_id": msg.session_id,
            "message": "Ciao! 👋 Come posso aiutarti durante il viaggio?",
            "ui_options": [
                {"id": "quick:fame", "label": "🍕 Ho fame"},
                {"id": "quick:sete", "label": "🍺 Ho sete"},
                {"id": "ask_music", "label": "🎵 Metti musica"},
            ],
            "commands": []
        }
    
    # =========================================================================
    # 5. NEGATION DETECTION - Se c'è negazione, gestisci con cautela
    # =========================================================================
    if analysis.has_negation:
        logger.info(f"Negation detected in: '{text}'")
        # Non eseguire azioni automatiche, chiedi chiarimenti
        return {
            "type": "assistant_response",
            "session_id": msg.session_id,
            "message": "Ho capito. C'è qualcos'altro che posso fare per te?",
            "ui_options": [
                {"id": "quick:fame", "label": "🍕 Ho fame"},
                {"id": "quick:sete", "label": "🍺 Ho sete"},
                {"id": "ask_music", "label": "🎵 Musica"},
                {"id": "cancel", "label": "❌ Niente, grazie"},
            ],
            "commands": []
        }
    
    # =========================================================================
    # 5b. OPTION MATCHING (se ci sono opzioni attive)
    # =========================================================================
    if session.last_ui_options:
        match_result = await intent_classifier.match_option(text, session.last_ui_options)
        if match_result.action == "select" and match_result.selected_id:
            logger.info(f"[OPTION_MATCH] Selected {match_result.selected_id}")
            session_store.update_session(msg.session_id, last_ui_options=[], last_poi_suggestions=[])
            return await handle_ui_action({
                "type": "ui_action",
                "session_id": msg.session_id,
                "action_id": match_result.selected_id,
                "payload": {}
            })
        elif match_result.action == "cancel":
            logger.info("[OPTION_MATCH] User cancelled options")
            session_store.update_session(msg.session_id, last_ui_options=[], last_poi_suggestions=[])
            return await handle_ui_action({
                "type": "ui_action",
                "session_id": msg.session_id,
                "action_id": "cancel",
                "payload": {}
            })

    # =========================================================================
    # 6. LLM TOOL CLASSIFICATION (PRINCIPALE)
    # =========================================================================
    from app.tools_registry import tool_registry
    
    # Costruisci contesto ricco per l'LLM
    context_parts = []
    if session.music_playing:
        if session.music_paused:
            context_parts.append(f"Musica: PAUSA ({session.music_genre})")
        else:
            context_parts.append(f"Musica: IN RIPRODUZIONE ({session.music_genre}), volume: {session.music_volume}/10")
    else:
        context_parts.append("Musica: SPENTA")
    
    if session.last_poi_suggestions:
        context_parts.append(f"POI suggeriti attivi: {len(session.last_poi_suggestions)}")
    
    context_parts.append(f"Modalità: {session.mode}")
    context_str = " | ".join(context_parts)
    
    tools_prompt = tool_registry.build_tools_prompt(session)
    logger.info(f"[MAIN] Calling LLM classify_with_tools with context: {context_str}")
    
    tool_result = await intent_classifier.classify_with_tools(
        message=text,
        tools_prompt=tools_prompt,
        context_info=context_str
    )
    
    logger.info(f"[MAIN] LLM result: tool_id={tool_result.tool_id}, confidence={tool_result.confidence}")
    
    # Soglia di confidenza più alta per maggiore precisione
    if tool_result.tool_id and tool_result.confidence >= 0.7:
        logger.info(f"[MAIN] Executing tool: {tool_result.tool_id}")
        response = await _execute_tool(
            tool_id=tool_result.tool_id,
            session_id=msg.session_id,
            user_id=user_id,
            message=text,
            session=session,
            params=tool_result.params
        )
        
        # Processa side-effects (es. notifica Unity)
        await _process_side_effects(response, msg.session_id)
        return response
    
    # =========================================================================
    # 6b. DIRECT POLICY INTENT (heuristic fallback)
    # =========================================================================
    from app.schemas import SessionMode
    if session.mode == SessionMode.NORMAL:
        try:
            from app.tools.taxi_tools import detect_policy_from_text
            direct_policy = detect_policy_from_text(text)
            if direct_policy:
                logger.info(f"[POLICY] Direct policy intent detected: {direct_policy}")
                response = await _execute_tool(
                    tool_id="change_driving_policy",
                    session_id=msg.session_id,
                    user_id=user_id,
                    message=text,
                    session=session,
                    params={"policy": direct_policy}
                )
                await _process_side_effects(response, msg.session_id)
                return response
        except Exception as e:
            logger.warning(f"[POLICY] Direct intent detection failed: {e}")

    # =========================================================================
    # 7. POI SELECTION (se ci sono suggerimenti attivi)
    # =========================================================================
    if session.last_poi_suggestions:
        logger.info(f"Active POI options: {session.last_poi_suggestions}")
        
        from app.neo4j.repo import neo4j_repo
        
        options = []
        for poi_id in session.last_poi_suggestions:
            poi = await neo4j_repo.get_poi_by_id(poi_id)
            if poi:
                options.append({
                    "id": f"poi:{poi_id}",
                    "label": poi.get("name", poi_id)
                })
        options.append({"id": "cancel", "label": "Annulla"})
        
        match_result = await intent_classifier.match_option(text, options)
        
        if match_result.action == "select" and match_result.selected_id:
            logger.info(f"User selected option: {match_result.selected_id}")
            poi_id = match_result.selected_id.replace("poi:", "")
            
            result = await policy_service.handle_poi_selection(
                session_id=msg.session_id,
                user_id=user_id,
                poi_id=poi_id
            )
            session_store.update_session(msg.session_id, last_poi_suggestions=[])
            
            response = {"type": "assistant_response", "session_id": msg.session_id, **result}
            
            # Processa side-effects
            await _process_side_effects(response, msg.session_id)
            return response
        
        elif match_result.action == "cancel":
            logger.info("User cancelled POI selection")
            session_store.update_session(msg.session_id, last_poi_suggestions=[])
            return {
                "type": "assistant_response",
                "session_id": msg.session_id,
                "message": "Va bene! Continuiamo verso la destinazione. 🚕",
                "ui_options": [],
                "commands": []
            }
    
    # =========================================================================
    # 8. NEED CLASSIFICATION (fallback LLM)
    # =========================================================================
    classify_result = await intent_classifier.classify_need(
        message=text,
        context={"city": msg.city, "user_id": user_id}
    )
    
    if classify_result.need and classify_result.confidence >= 0.65:
        logger.info(f"Detected need: {classify_result.need} (conf={classify_result.confidence:.2f})")
        session_store.update_session(msg.session_id, last_poi_suggestions=[])
        
        return await _handle_need_request(
            session_id=msg.session_id,
            user_id=user_id,
            need=classify_result.need,
            subcategory=classify_result.subcategory
        )
    
    # =========================================================================
    # 9. FALLBACK CONVERSAZIONALE
    # =========================================================================
    logger.info("No intent detected, generating conversational response")
    
    response_text = await intent_classifier.get_conversational_response(text)
    
    return {
        "type": "assistant_response",
        "session_id": msg.session_id,
        "message": response_text,
        "ui_options": [
            {"id": "quick:fame", "label": "🍕 Ho fame"},
            {"id": "quick:sete", "label": "🍺 Ho sete"},
            {"id": "quick:malessere", "label": "💊 Sto male"},
        ],
        "commands": []
    }


async def handle_pre_ride_message(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce un messaggio nella schermata di prenotazione (pre-ride).
    
    In questa modalità, solo i tool POI sono disponibili.
    Usato per la chat intelligente nella schermata di booking.
    """
    from app.llm.intent_classifier import intent_classifier
    from app.schemas import SessionMode
    
    session_id = data.get("session_id", "unknown")
    user_id = data.get("user_id", "unknown")
    city = data.get("city", "Palermo")
    text = data.get("text", "").strip()
    
    logger.info(f"Processing pre-ride message: '{text[:50]}...'")
    
    # Set pre-ride mode for this session
    session_store.update_session(
        session_id=session_id,
        user_id=user_id,
        city=city,
        mode=SessionMode.PRE_RIDE
    )
    
    session = session_store.get_session(session_id)
    
    # Check for greetings
    from app.utils.text import is_greeting, is_help_request
    
    if is_greeting(text):
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "Ciao! 👋 Dimmi dove vuoi andare. Puoi dirmi il nome del posto o descrivermi cosa cerchi!",
            "ui_options": [
                {"id": "quick:fame", "label": "🍕 Ho fame"},
                {"id": "quick:sete", "label": "🍺 Ho sete"},
                {"id": "quick:shopping", "label": "🛍️ Shopping"},
            ],
            "commands": [],
            "selected_poi": None
        }
    
    if is_help_request(text):
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "🚕 Dimmi dove vuoi andare! Puoi:\n\n"
                      "• Cercare un posto per nome (es. 'FastFood Express')\n"
                      "• Dirmi cosa ti serve (es. 'ho fame', 'voglio una pizza')\n"
                      "• Chiedere un posto per un bisogno (es. 'un bar per un aperitivo')",
            "ui_options": [
                {"id": "quick:fame", "label": "🍕 Cibo"},
                {"id": "quick:sete", "label": "🍺 Bevande"},
                {"id": "quick:shopping", "label": "🛍️ Shopping"},
            ],
            "commands": [],
            "selected_poi": None
        }
    
    # Se ci sono opzioni attive, prova a matchare la scelta
    if session.last_ui_options:
        match_result = await intent_classifier.match_option(text, session.last_ui_options)
        if match_result.action == "select" and match_result.selected_id:
            logger.info(f"[OPTION_MATCH][PRE_RIDE] Selected {match_result.selected_id}")
            session_store.update_session(session_id, last_ui_options=[], last_poi_suggestions=[])
            return await handle_ui_action({
                "type": "ui_action",
                "session_id": session_id,
                "action_id": match_result.selected_id,
                "payload": {}
            })
        elif match_result.action == "cancel":
            logger.info("[OPTION_MATCH][PRE_RIDE] User cancelled options")
            session_store.update_session(session_id, last_ui_options=[], last_poi_suggestions=[])
            return await handle_ui_action({
                "type": "ui_action",
                "session_id": session_id,
                "action_id": "cancel",
                "payload": {}
            })

    # Use LLM with POI-only tools (enforced by PRE_RIDE mode)
    from app.tools_registry import tool_registry
    
    tools_prompt = tool_registry.build_tools_prompt(session)
    logger.info(f"[PRE_RIDE] Tools prompt: {tools_prompt[:200]}...")
    
    tool_result = await intent_classifier.classify_with_tools(
        message=text,
        tools_prompt=tools_prompt,
        context_info="Modalità: pre_ride (selezione destinazione)"
    )
    
    logger.info(f"[PRE_RIDE] LLM result: tool_id={tool_result.tool_id}, confidence={tool_result.confidence}")
    
    if tool_result.tool_id and tool_result.confidence >= 0.6:
        result = await _execute_tool(
            tool_id=tool_result.tool_id,
            session_id=session_id,
            user_id=user_id,
            message=text,
            session=session,
            params=tool_result.params
        )
        
        # Check if a POI was selected (has REROUTE_TO command)
        selected_poi = None
        for cmd in result.get("commands", []):
            if cmd.get("type") == "REROUTE_TO":
                selected_poi = cmd.get("payload", {})
                break
        
        result["selected_poi"] = selected_poi
        return result
    
    # Fallback: need classification
    classify_result = await intent_classifier.classify_need(
        message=text,
        context={"city": city, "user_id": user_id}
    )
    
    if classify_result.need and classify_result.confidence >= 0.5:
        result = await _handle_need_request(
            session_id=session_id,
            user_id=user_id,
            need=classify_result.need,
            subcategory=classify_result.subcategory
        )
        result["selected_poi"] = None
        return result
    
    # No match - ask for clarification
    return {
        "type": "assistant_response",
        "session_id": session_id,
        "message": "🤔 Non ho capito bene. Dimmi dove vuoi andare o cosa stai cercando!",
        "ui_options": [
            {"id": "quick:fame", "label": "🍕 Ho fame"},
            {"id": "quick:sete", "label": "🍺 Ho sete"},
            {"id": "quick:malessere", "label": "💊 Sto male"},
            {"id": "quick:divertimento", "label": "🎉 Divertimento"},
            {"id": "quick:shopping", "label": "🛍️ Shopping"},
        ],
        "commands": [],
        "selected_poi": None
    }


# =============================================================================
# UNITY MESSAGE HANDLER
# =============================================================================

async def handle_unity_message(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce messaggi provenienti dalla simulazione Unity.
    
    Supporta le seguenti azioni:
    - ping: Test di connessione, risponde con pong
    - status: Riceve stato del taxi (posizione, batteria, stato missione)
    - destination: Riceve info sulla destinazione corrente
    - mission_complete: Notifica completamento missione
    
    Args:
        data: Messaggio JSON con campi type, session_id, action, payload
        
    Returns:
        Risposta strutturata per Unity
    """
    session_id = data.get("session_id", "unknown")
    action = data.get("action", "")
    payload = data.get("payload", {})
    
    logger.info(f"[UNITY] Received action={action} session={session_id}")
    logger.info(f"[UNITY] Payload: {payload}")
    
    # Handle different actions from Unity
    if action == "ping":
        logger.info(f"[UNITY] Ping received from {session_id}")
        return {
            "type": "unity_response",
            "session_id": session_id,
            "action": "pong",
            "message": "Backend connected! WebSocket active.",
            "payload": {
                "server_time": str(__import__('datetime').datetime.now().isoformat()),
                "status": "ok"
            }
        }
    
    elif action == "status":
        # Riceve stato del taxi
        status = payload.get("status", "unknown")
        battery = payload.get("battery", 0)
        position = payload.get("position", {})
        
        logger.info(f"[UNITY] Taxi Status: {status}, Battery: {battery}%, Pos: {position}")
        
        return {
            "type": "unity_response",
            "session_id": session_id,
            "action": "status_ack",
            "message": f"Status received: {status}",
            "payload": {
                "received": True,
                "battery_warning": battery < 20
            }
        }
    
    elif action == "destination":
        # Riceve info sulla destinazione
        poi_name = payload.get("poi_name", "")
        x = payload.get("x", 0)
        y = payload.get("y", 0)
        
        logger.info(f"[UNITY] Destination set: {poi_name} at ({x}, {y})")
        
        return {
            "type": "unity_response",
            "session_id": session_id,
            "action": "destination_ack",
            "message": f"Navigating to {poi_name}",
            "payload": {
                "poi_name": poi_name,
                "confirmed": True
            }
        }
    
    elif action == "mission_complete":
        # Missione completata
        mission_type = payload.get("mission_type", "passenger")
        success = payload.get("success", True)
        
        logger.info(f"[UNITY] Mission complete: {mission_type}, Success: {success}")
        
        return {
            "type": "unity_response",
            "session_id": session_id,
            "action": "mission_ack",
            "message": "Mission completed! Ready for next passenger.",
            "payload": {
                "next_action": "idle",
                "success": success
            }
        }
    
    elif action == "passenger_pickup":
        # Passeggero raccolto
        passenger_name = payload.get("passenger_name", "Unknown")
        
        logger.info(f"[UNITY] Passenger pickup: {passenger_name}")

        ride_started = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "🚕 Il taxi è arrivato, la corsa sta iniziando.",
            "ui_options": [],
            "commands": [],
            "booking_status": "ride_started",
            "eta_minutes": payload.get("eta_minutes"),
            "destination": payload.get("destination")
        }

        if session_id in connection_manager.chat_clients:
            await connection_manager.send_to_chat_client(session_id, ride_started)
            logger.info(f"[UNITY] Booking status 'ride_started' inoltrato a chat client {session_id}")
        
        return {
            "type": "unity_response",
            "session_id": session_id,
            "action": "pickup_ack",
            "message": f"Welcome aboard, {passenger_name}!",
            "payload": {
                "confirmed": True
            }
        }

    elif action == "explainability":
        message = payload.get("message", "").strip()
        eta_minutes = payload.get("eta_minutes")
        payload_policy = payload.get("policy")

        # Rileva cambio policy forzato da Unity (es. batteria bassa -> ECO) - aggiorna solo la policy
        if "Passo a ECO" in message or "ECO per" in message:
            session = session_store.get_session(session_id)
            if session.driving_policy != "Eco":
                session.driving_policy = "Eco"
                logger.info(f"[UNITY] Policy aggiornata a Eco nella sessione {session_id} (forzato da Unity)")

        # Se Unity invia la policy esplicita nel payload, usala come source of truth.
        if payload_policy:
            new_policy = payload_policy.capitalize()
            session = session_store.get_session(session_id)
            if session.driving_policy != new_policy:
                session.driving_policy = new_policy
                logger.info(f"[UNITY] Policy aggiornata a {new_policy} nella sessione {session_id} (payload)")

        # Rileva cambio policy confermato da Unity
        policy_match = re.search(r"Policy cambiata in\s+([A-Za-z]+)", message, re.IGNORECASE)
        if policy_match:
            new_policy = policy_match.group(1).capitalize()
            session = session_store.get_session(session_id)
            session.driving_policy = new_policy
            logger.info(f"[UNITY] Policy aggiornata a {new_policy} nella sessione {session_id} (confermata da Unity)")

        if eta_minutes is not None:
            message += f"\n\n⏱️ Nuovo tempo stimato: {format_duration_minutes(eta_minutes)}"

        if message and session_id in connection_manager.chat_clients:
            response = {
                "type": "assistant_response",
                "session_id": session_id,
                "message": message,
                "ui_options": [],
                "commands": [],
                "message_type": "explainability"
            }
            await connection_manager.send_to_chat_client(session_id, response)
            logger.info(f"[UNITY] Explainability inoltrato a chat client {session_id}")

        return {
            "type": "unity_response",
            "session_id": session_id,
            "action": "explainability_ack",
            "message": "Explainability received",
            "payload": {"received": True}
        }
    
    else:
        # Azione sconosciuta - echo back per debug
        logger.warning(f"[UNITY] Unknown action: {action}")
        return {
            "type": "unity_response",
            "session_id": session_id,
            "action": "echo",
            "message": f"Received unknown action: {action}",
            "payload": payload
        }


# =============================================================================
# BOOKING FLOW HANDLERS
# =============================================================================

# Store per le prenotazioni in attesa (session_id -> booking_state)
pending_bookings: dict[str, dict] = {}


async def handle_booking_request(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce una richiesta di prenotazione dalla chat UI.
    
    Il messaggio viene INOLTRATO a Unity tramite il ConnectionManager.
    Unity MissionCoordinator lo riceverà e invierà risposta_prenotazione.
    """
    session_id = data.get("session_id", "unknown")
    payload = data.get("payload", {})
    destinazione = payload.get("destinazione", {})
    user_id = payload.get("user_id", "")
    requested_policy = payload.get("driving_policy", "Comfort")
    
    logger.info(f"[BOOKING] Richiesta prenotazione: {destinazione.get('nome')} session={session_id}")
    logger.info(f"[BOOKING] POI ID Unity: {destinazione.get('poi_id_unity')}")
    logger.info(f"[BOOKING] Policy richiesta: {requested_policy}, User: {user_id}")
    
    # === CONTROLLO CONDIZIONI UTENTE ===
    policy_override_message = None
    effective_policy = requested_policy
    
    try:
        user_conditions = await get_user_conditions(user_id)
        if user_conditions:
            logger.info(f"[BOOKING] Condizioni utente {user_id}: {user_conditions}")
            effective_policy, override_reason = get_effective_policy(requested_policy, user_conditions)
            
            if override_reason:
                # Policy forzata per sicurezza - prepara messaggio explainability
                logger.info(f"[BOOKING] Policy forzata: {effective_policy} (motivo: {override_reason})")
                policy_override_message = override_reason
                
                # Aggiorna la policy nel payload che verrà inviato a Unity
                payload["driving_policy"] = effective_policy
                # Indica a Unity che questa policy è OBBLIGATORIA e non può essere cambiata
                payload["required_policy"] = effective_policy
                data["payload"] = payload
    except Exception as e:
        logger.warning(f"[BOOKING] Errore nel controllo condizioni: {e}")
    
    # Store pending booking
    pending_bookings[session_id] = {
        "state": "waiting_unity_response",
        "destinazione": destinazione,
        "user_id": user_id,
        "effective_policy": effective_policy,
        "policy_override_message": policy_override_message,
        "timestamp": __import__("time").time()
    }
    
    # === INOLTRA A UNITY ===
    if connection_manager.is_unity_connected():
        # Inoltra il messaggio esatto a Unity
        forwarded = await connection_manager.send_to_unity(data)
        if forwarded:
            logger.info(f"[BOOKING] Richiesta inoltrata a Unity con policy: {effective_policy}")
        else:
            logger.warning(f"[BOOKING] Fallito inoltro a Unity")
    else:
        logger.warning(f"[BOOKING] Unity non connesso! La richiesta non può essere elaborata.")
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "⚠️ **Il taxi non è connesso**\n\nIl simulatore Unity non è attivo. Avvia Unity e riprova.",
            "ui_options": [],
            "commands": [],
            "booking_status": "error"
        }
    
    # Ritorniamo un acknowledgment per confermare la ricezione
    # La risposta vera arriverà da Unity (risposta_prenotazione)
    response = {
        "type": "booking_acknowledged",
        "session_id": session_id,
        "message": "Richiesta inviata al taxi, attendi la risposta...",
        "booking_status": "waiting"
    }
    
    # Aggiungi messaggio explainability se policy è stata forzata
    if policy_override_message:
        response["policy_override"] = {
            "effective_policy": effective_policy,
            "message": policy_override_message
        }
    
    return response





async def handle_user_queue_response(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce la risposta dell'utente alla coda di attesa.
    
    Inoltra la risposta a Unity tramite WebSocket.
    Unity MissionCoordinator risponderà con conferma_coda.
    """
    session_id = data.get("session_id", "unknown")
    payload = data.get("payload", {})
    accetta = payload.get("accetta", False)
    
    logger.info(f"[BOOKING] Risposta utente coda: accetta={accetta} session={session_id}")
    
    # Aggiorna stato booking
    if session_id in pending_bookings:
        pending_bookings[session_id]["queue_accepted"] = accetta
    
    # === INOLTRA A UNITY ===
    if connection_manager.is_unity_connected():
        await connection_manager.send_to_unity(data)
        logger.info(f"[BOOKING] Risposta coda inoltrata a Unity")
    
    # Ritorniamo un acknowledgment
    return {
        "type": "queue_response_acknowledged",
        "session_id": session_id,
        "message": "Elaborazione in corso..." if accetta else "Annullamento in corso...",
        "booking_status": "processing"
    }




async def handle_booking_response(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce la risposta di Unity a una richiesta di prenotazione.
    
    IMPORTANTE: Questa risposta arriva da Unity e deve essere inoltrata
    al chat client corrispondente tramite session_id.
    
    Esiti possibili:
    - confermato: Prenotazione accettata
    - batteria_scarica: Taxi deve ricaricare
    - coda_attesa: Taxi occupato, chiede conferma utente
    """
    session_id = data.get("session_id", "unknown")
    esito = data.get("esito", "")
    payload = data.get("payload", {})
    
    logger.info(f"[BOOKING] Risposta da Unity: esito={esito} session={session_id}")
    logger.info(f"[BOOKING] Payload: {payload}")
    
    # Prepara la risposta per il chat client
    response = None
    
    if esito == "confermato":
        tempo = payload.get("tempo_stimato_minuti", 5)
        distanza = payload.get("distanza_km", 0)
        batteria = payload.get("batteria_attuale", 100)
        tempo_rounded = max(1, int(round(tempo)))
        tempo_display = format_duration_minutes(tempo)
        
        message = f"⏱️ Tempo stimato: {tempo_display}\n"
        message += f"📍 Distanza: {distanza:.1f} km\n"
        message += f"🔋 Batteria taxi: {batteria:.0f}%"
        
        # Recupera policy override dal pending booking
        booking_info = pending_bookings.get(session_id, {})
        policy_override_msg = booking_info.get("policy_override_message")
        effective_policy = booking_info.get("effective_policy")
        
        logger.info(f"[BOOKING] pending_bookings for {session_id}: {booking_info}")
        logger.info(f"[BOOKING] policy_override_msg: {policy_override_msg}, effective_policy: {effective_policy}")

        if effective_policy:
            session = session_store.get_session(session_id)
            session.driving_policy = effective_policy
            logger.info(f"[BOOKING] Policy sessione aggiornata a {effective_policy} per {session_id}")
        
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": message,
            "ui_options": [],
            "commands": [],
            "booking_status": "confirmed",
            "eta_minutes": tempo_rounded,
            "distance_km": distanza,
            "battery_pct": batteria
        }
        
        # Aggiungi policy override se presente
        if policy_override_msg:
            response["policy_override"] = {
                "effective_policy": effective_policy,
                "message": policy_override_msg
            }
        
        # ATTIVA LA CORSA nella sessione backend
        booking_info = pending_bookings.get(session_id, {})
        dest_info = booking_info.get("destinazione", {})
        session_store.start_ride(
            session_id=session_id,
            destination_name=dest_info.get("nome", "Unknown"),
            destination_id=dest_info.get("poi_id_unity", "")
        )
        # Cleanup pending booking
        pending_bookings.pop(session_id, None)
        
        # Inoltra al chat client
        if session_id in connection_manager.chat_clients:
            await connection_manager.send_to_chat_client(session_id, response)
            logger.info(f"[BOOKING] Risposta 'confermato' inoltrata a chat client {session_id}")
        
        return response

    
    elif esito == "batteria_scarica":
        tempo_attesa = payload.get("tempo_attesa_minuti", 15)
        batteria = payload.get("batteria_attuale", 0)
        messaggio = payload.get("messaggio", "Devo ricaricare la batteria")
        tempo_display = format_duration_minutes(tempo_attesa)
        
        message = f"🔋 **{messaggio}**\n\n"
        message += f"⏳ Tempo totale (percorso + ricarica): **{tempo_display}**\n"
        message += f"🔌 Batteria attuale: **{batteria:.0f}%**\n\n"
        message += "Il taxi si sta dirigendo alla stazione di ricarica."
        
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": message,
            "ui_options": [],
            "commands": [],
            "booking_status": "charging"
        }
        
        # Inoltra al chat client
        if session_id in connection_manager.chat_clients:
            await connection_manager.send_to_chat_client(session_id, response)
            logger.info(f"[BOOKING] Risposta 'batteria_scarica' inoltrata a chat client {session_id}")
        
        return response

    
    elif esito == "coda_attesa":
        corse_in_coda = payload.get("corse_in_coda", 1)
        tempo_attesa = payload.get("tempo_attesa_minuti", 10)
        if "messaggio" in payload and payload.get("messaggio") is not None:
            messaggio = payload.get("messaggio")
        else:
            messaggio = "Sono occupato con 1 corsa" if corse_in_coda == 1 else f"Sono occupato con {corse_in_coda} corse"

        if corse_in_coda == 1 and isinstance(messaggio, str):
            messaggio = messaggio.replace("1 corse", "1 corsa")
        tempo_display = format_duration_minutes(tempo_attesa)
        
        # Aggiorna stato preservando i dati originali (come policy_override_message)
        if session_id in pending_bookings:
            pending_bookings[session_id].update({
                "state": "waiting_queue_response",
                "corse_in_coda": corse_in_coda,
                "tempo_attesa": tempo_attesa
            })
        else:
            pending_bookings[session_id] = {
                "state": "waiting_queue_response",
                "corse_in_coda": corse_in_coda,
                "tempo_attesa": tempo_attesa
            }
        
        message = f"👥 Corse in coda: **{corse_in_coda}**\n"
        message += f"⏱️ Tempo stimato di attesa: **{tempo_display}**\n\n"
        message += "Vuoi attendere?"
        
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": message,
            "ui_options": [
                {"id": "queue_accept", "label": "✅ Sì, attendo"},
                {"id": "queue_reject", "label": "❌ No, annulla"}
            ],
            "commands": [],
            "booking_status": "waiting_queue_response"
        }
        
        # Inoltra al chat client
        if session_id in connection_manager.chat_clients:
            await connection_manager.send_to_chat_client(session_id, response)
            logger.info(f"[BOOKING] Risposta 'coda_attesa' inoltrata a chat client {session_id}")
        
        return response

    
    else:
        logger.warning(f"[BOOKING] Esito sconosciuto: {esito}")
        return {
            "type": "error",
            "session_id": session_id,
            "message": f"Risposta prenotazione non riconosciuta: {esito}"
        }


async def handle_queue_confirmation(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce la conferma/rifiuto della coda da parte di Unity.
    Inoltra al chat client.
    """
    session_id = data.get("session_id", "unknown")
    esito = data.get("esito", "")
    payload = data.get("payload", {})
    
    logger.info(f"[BOOKING] Conferma coda: esito={esito} session={session_id}")
    
    if esito == "accettato":
        # NON rimuovere pending_bookings qui - contiene policy_override_message
        # che servirà quando arriverà la conferma finale
        posizione = payload.get("posizione_in_coda", 1)
        tempo = payload.get("tempo_stimato_minuti", 10)
        tempo_display = format_duration_minutes(tempo)
        
        message = f"✅ **Sei in coda!**\n\n"
        message += f"📍 Posizione: **#{posizione}**\n"
        message += f"⏱️ Tempo stimato: **{tempo_display}**\n\n"
        message += "Ti avviseremo quando il taxi sarà in arrivo."
        
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": message,
            "ui_options": [
                {"id": "cancel_queue", "label": "❌ Annulla prenotazione"}
            ],
            "commands": [],
            "booking_status": "queued",
            "queue_position": posizione,
            "queue_eta_minutes": tempo
        }
        
        # Inoltra al chat client
        if session_id in connection_manager.chat_clients:
            await connection_manager.send_to_chat_client(session_id, response)
            logger.info(f"[BOOKING] Conferma coda 'accettato' inoltrata a chat client {session_id}")
        
        return response
    
    else:  # rifiutato o altro
        # Pulisci pending_bookings quando la coda è rifiutata
        pending_bookings.pop(session_id, None)
        
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "❌ **Prenotazione annullata.**\n\nPuoi effettuare una nuova prenotazione quando vuoi.",
            "ui_options": [],
            "commands": [],
            "booking_status": "cancelled"
        }
        
        # Inoltra al chat client
        if session_id in connection_manager.chat_clients:
            await connection_manager.send_to_chat_client(session_id, response)
            logger.info(f"[BOOKING] Conferma coda 'rifiutato' inoltrata a chat client {session_id}")
        
        return response


async def handle_queue_update(data: dict[str, Any]) -> dict[str, Any]:
    """
    Aggiorna la posizione in coda di un utente.
    Inoltra al chat client per aggiornare overlay senza reimpostare lo stato.
    """
    session_id = data.get("session_id", "unknown")
    payload = data.get("payload", {})
    posizione = payload.get("posizione_in_coda")
    tempo = payload.get("tempo_stimato_minuti")
    tempo_display = format_duration_minutes(tempo) if tempo is not None else None

    logger.info(f"[BOOKING] Queue update: session={session_id} pos={posizione} tempo={tempo}")

    message_lines = []
    if posizione is not None:
        message_lines.append(f"📍 Posizione: **#{posizione}**")
    if tempo_display:
        message_lines.append(f"⏱️ Tempo stimato: **{tempo_display}**")

    response = {
        "type": "assistant_response",
        "session_id": session_id,
        "message": "\n".join(message_lines),
        "ui_options": [],
        "commands": [],
        "booking_status": "queue_update",
        "queue_position": posizione,
        "queue_eta_minutes": tempo
    }

    if session_id in connection_manager.chat_clients:
        await connection_manager.send_to_chat_client(session_id, response)
        logger.info(f"[BOOKING] Queue update inoltrato a chat client {session_id}")

    return response


# =============================================================================
# DESTINATION CHANGE & END RIDE HANDLERS
# =============================================================================

async def handle_destination_change_request(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce una richiesta di cambio destinazione durante la corsa.
    
    Inoltra la richiesta a Unity che ricalcolerà il percorso.
    """
    session_id = data.get("session_id", "unknown")
    payload = data.get("payload", {})
    new_destination = payload.get("nuova_destinazione", {})
    
    logger.info(f"[REROUTE] Richiesta cambio destinazione: {new_destination}")
    
    # Costruisci messaggio per Unity
    unity_request = {
        "type": "cambio_destinazione",
        "session_id": session_id,
        "payload": {
            "nuova_destinazione": new_destination
        }
    }
    
    # Invia a Unity
    if connection_manager.is_unity_connected():
        logger.info(f"[REROUTE] Sending to Unity: {json.dumps(unity_request, ensure_ascii=False)}")
        await connection_manager.send_to_unity(unity_request)
        logger.info(f"[REROUTE] Richiesta cambio destinazione inoltrata a Unity")
        
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "",
            "ui_options": [],
            "commands": []
        }
    else:
        logger.warning("[REROUTE] Unity non connesso")
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "❌ Impossibile cambiare destinazione: veicolo non connesso.",
            "ui_options": [],
            "commands": []
        }


async def handle_end_ride_request(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce una richiesta di fine corsa anticipata.
    
    Inoltra a Unity che fermerà il taxi in un punto sicuro.
    """
    session_id = data.get("session_id", "unknown")
    
    logger.info(f"[END_RIDE] Richiesta fine corsa session={session_id}")
    
    # Costruisci messaggio per Unity
    unity_request = {
        "type": "fine_corsa",
        "session_id": session_id
    }
    
    # Invia a Unity
    if connection_manager.is_unity_connected():
        await connection_manager.send_to_unity(unity_request)
        logger.info(f"[END_RIDE] Richiesta fine corsa inoltrata a Unity")
        
        # Non inviamo messaggio al client qui - aspettiamo la risposta di Unity
        # che verrà inoltrata tramite handle_end_ride_response
        return {
            "type": "ack",
            "session_id": session_id,
            "status": "processing"
        }
    else:
        logger.warning("[END_RIDE] Unity non connesso")
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "❌ Impossibile terminare la corsa: veicolo non connesso.",
            "ui_options": [],
            "commands": []
        }


async def handle_destination_change_response(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce la risposta di Unity a una richiesta di cambio destinazione.
    
    Esiti possibili:
    - confermato: Cambio effettuato con nuovo ETA
    - confermato_ricarica_necessaria: Cambio effettuato ma serve ricarica
    - errore: Impossibile cambiare destinazione
    """
    session_id = data.get("session_id", "unknown")
    esito = data.get("esito", "errore")
    payload = data.get("payload", {})
    
    logger.info(f"[REROUTE] Risposta da Unity: esito={esito}, payload={payload}")
    logger.debug(f"[REROUTE] Full response data: {json.dumps(data, ensure_ascii=False)}")
    
    if esito == "confermato":
        distanza = payload.get("distanza_km", 0)
        tempo = payload.get("tempo_stimato_minuti", 0)
        
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": f"✅ **Destinazione aggiornata!**\n\n"
                      f"📍 Nuovo percorso: {distanza:.1f} km\n"
                      f"⏱️ Tempo stimato: {format_duration_minutes(tempo)}",
            "ui_options": [],
            "commands": [],
            "booking_status": "rerouted"
        }
    
    elif esito == "confermato_ricarica_necessaria":
        distanza = payload.get("distanza_km", 0)
        tempo = payload.get("tempo_stimato_minuti", 0)
        tempo_ricarica = payload.get("tempo_ricarica_minuti", 0)
        
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": f"⚠️ **Destinazione aggiornata, ma serve una ricarica.**\n\n"
                      f"📍 Nuovo percorso: {distanza:.1f} km\n"
                      f"⏱️ Tempo stimato (con ricarica): ~{format_duration_minutes(tempo + tempo_ricarica)}\n\n"
                      f"Se preferisci, puoi terminare la corsa qui.",
            "ui_options": [
                {"id": "continue_with_charge", "label": "✅ Continua con ricarica"},
                {"id": "end_ride_now", "label": "🛑 Fermati qui"}
            ],
            "commands": [],
            "booking_status": "needs_recharge"
        }
    
    else:  # errore
        messaggio = payload.get("messaggio", "Errore sconosciuto")
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": f"❌ **Impossibile cambiare destinazione.**\n\n{messaggio}\n\n📍 Procedo verso la destinazione precedentemente concordata. Se preferisci terminare la corsa qui, usa il pulsante 'Fine corsa'.",
            "ui_options": [
                {"id": "end_ride_now", "label": "🛑 Fine corsa"}
            ],
            "commands": [],
            "booking_status": "error"
        }
    
    # Inoltra al chat client
    if session_id in connection_manager.chat_clients:
        await connection_manager.send_to_chat_client(session_id, response)
        logger.info(f"[REROUTE] Risposta inoltrata a chat client {session_id}")
    else:
        logger.warning(f"[REROUTE] Chat client NON trovato per session_id={session_id}. Clients registrati: {list(connection_manager.chat_clients.keys())}")
    
    # Ritorna ack a Unity
    return {
        "type": "unity_response",
        "session_id": session_id,
        "action": "destination_change_ack",
        "message": "Destination change response processed",
        "payload": {"esito": esito}
    }


async def handle_end_ride_response(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce la risposta di Unity a una richiesta di fine corsa.
    
    Esiti possibili:
    - confermato: Corsa terminata con successo
    - errore: Impossibile terminare la corsa
    """
    session_id = data.get("session_id", "unknown")
    esito = data.get("esito", "errore")
    payload = data.get("payload", {})
    
    logger.info(f"[END_RIDE] Risposta da Unity: esito={esito}, payload={payload}")
    
    if esito == "confermato":
        # TERMINA LA CORSA nella sessione backend
        session_store.end_ride(session_id)
        
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "✅ Corsa terminata con successo!\n"
                      "🙏 Grazie per aver viaggiato con noi!",
            "ui_options": [],
            "commands": [{"type": "redirect_to_main", "delay_ms": 8000}],
            "booking_status": "ended"
        }
    
    else:  # errore
        messaggio = payload.get("messaggio", "Errore sconosciuto")
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": f"❌ **Impossibile terminare la corsa ora.**\n\n{messaggio}",
            "ui_options": [],
            "commands": [],
            "booking_status": "error"
        }
    
    # Inoltra al chat client
    if session_id in connection_manager.chat_clients:
        await connection_manager.send_to_chat_client(session_id, response)
        logger.info(f"[END_RIDE] Risposta inoltrata a chat client {session_id}")
    
    return response


async def handle_booking_cancellation(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce la richiesta di annullamento prenotazione dall'utente.
    
    Inoltra la richiesta a Unity che cancellerà la missione e tornerà alla stazione.
    """
    session_id = data.get("session_id", "unknown")
    
    logger.info(f"[CANCELLATION] Richiesta annullamento prenotazione session={session_id}")
    
    # Remove from pending bookings
    pending_bookings.pop(session_id, None)
    
    # Forward cancellation to Unity
    unity_request = {
        "type": "annulla_prenotazione",
        "session_id": session_id
    }
    
    if connection_manager.is_unity_connected():
        await connection_manager.send_to_unity(unity_request)
        logger.info(f"[CANCELLATION] Richiesta annullamento inoltrata a Unity")
        
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "Annullamento in corso...",
            "ui_options": [],
            "commands": [],
            "booking_status": "cancelling"
        }
    else:
        logger.warning("[CANCELLATION] Unity non connesso")
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "❌ **Prenotazione annullata.**\n\nPuoi effettuare una nuova prenotazione quando vuoi.",
            "ui_options": [],
            "commands": [],
            "booking_status": "cancelled"
        }


async def handle_cancellation_response(data: dict[str, Any]) -> dict[str, Any]:
    """
    Gestisce la risposta di Unity a una richiesta di annullamento prenotazione.
    """
    session_id = data.get("session_id", "unknown")
    esito = data.get("esito", "errore")
    
    logger.info(f"[CANCELLATION] Risposta da Unity: esito={esito}")
    
    if esito == "confermato":
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "❌ **Prenotazione annullata.**\n\nPuoi effettuare una nuova prenotazione quando vuoi.",
            "ui_options": [],
            "commands": [],
            "booking_status": "cancelled"
        }
    else:
        response = {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "⚠️ Impossibile annullare la prenotazione.",
            "ui_options": [],
            "commands": [],
            "booking_status": "error"
        }
    
    # Forward to chat client
    if session_id in connection_manager.chat_clients:
        await connection_manager.send_to_chat_client(session_id, response)
        logger.info(f"[CANCELLATION] Risposta inoltrata a chat client {session_id}")
    
    return response



def build_booking_request(session_id: str, user_id: str, poi_name: str, poi_id_unity: str) -> dict[str, Any]:
    """
    Costruisce un messaggio di richiesta prenotazione da inviare a Unity.
    
    Args:
        session_id: ID della sessione
        user_id: ID dell'utente
        poi_name: Nome del POI di destinazione
        poi_id_unity: ID del POI in Unity (placeholder per mapping futuro)
    
    Returns:
        Messaggio JSON pronto per l'invio via WebSocket
    """
    return {
        "type": "richiesta_prenotazione",
        "session_id": session_id,
        "payload": {
            "destinazione": {
                "nome": poi_name,
                "poi_id_unity": poi_id_unity
            },
            "user_id": user_id
        }
    }


def build_queue_response(session_id: str, accetta: bool) -> dict[str, Any]:
    """
    Costruisce la risposta dell'utente alla coda di attesa.
    
    Args:
        session_id: ID della sessione
        accetta: True se l'utente accetta di attendere
    
    Returns:
        Messaggio JSON pronto per l'invio via WebSocket
    """
    return {
        "type": "risposta_coda_attesa",
        "session_id": session_id,
        "payload": {
            "accetta": accetta
        }
    }


def _detect_need(text: str) -> str | None:
    """Rileva il bisogno dal testo dell'utente."""
    need_patterns = {
        "Fame": ["fame", "mangiare", "pranzo", "cena", "colazione", "pizza", "ristorante", "panino"],
        "Sete": ["sete", "bere", "bar", "caffè", "caffe", "birra", "drink"],
        "Malessere": ["male", "malessere", "farmacia", "medicina", "dottore", "ospedale", "dentista"],
        "Divertimento": ["divertirmi", "divertimento", "cinema", "teatro", "disco", "discoteca", "stadio"],
        "Shopping": ["shopping", "comprare", "negozio", "negozi", "centro commerciale"],
    }
    
    for need, keywords in need_patterns.items():
        if any(kw in text for kw in keywords):
            return need
    return None


def _extract_genre_from_message(message: str) -> str | None:
    """Estrae il genere musicale dal messaggio dell'utente."""
    text = message.lower()
    genre_map = {
        "jazz": "Jazz",
        "rock": "Rock",
        "pop": "Pop",
        "classica": "Classica",
        "hip hop": "HipHop",
        "hiphop": "HipHop",
        "hip-hop": "HipHop",
        "elettronica": "Elettronica",
        "electronic": "Elettronica",
    }
    for pattern, genre in genre_map.items():
        if pattern in text:
            return genre
    return None


def _extract_volume_from_message(message: str) -> int | None:
    """Estrae il livello di volume dal messaggio dell'utente."""
    import re
    text = message.lower()
    
    # Pattern: "volume a 8", "volume al 8", "metti il volume a 10"
    match = re.search(r'volume\s*(?:a|al)?\s*(\d+)', text)
    if match:
        vol = int(match.group(1))
        # Se è una percentuale (es. 80%), converti
        if vol > 10:
            vol = max(1, min(10, round(vol / 10)))
        return max(1, min(10, vol))
    
    # Pattern: "a 8", "al 10" (se nel contesto volume)
    match = re.search(r'\b(?:a|al)\s*(\d+)\b', text)
    if match:
        vol = int(match.group(1))
        if vol > 10:
            vol = max(1, min(10, round(vol / 10)))
        return max(1, min(10, vol))
    
    # Pattern: solo numero "8", "10"
    match = re.search(r'\b(\d+)\b', text)
    if match:
        vol = int(match.group(1))
        if 1 <= vol <= 10:
            return vol
        elif vol > 10:
            return max(1, min(10, round(vol / 10)))
    
    return None


async def _execute_tool(
    tool_id: str,
    session_id: str,
    user_id: str,
    message: str,
    session,
    params: dict | None = None
) -> dict[str, Any]:
    """
    Esegue un tool in base al suo ID usando il nuovo sistema execute().
    
    Costruisce ToolContext e delega al tool.
    """
    from app.tools_registry import tool_registry
    from app.schemas import ToolContext
    
    params = params or {}
    
    # Estrai parametri dal messaggio se non già presenti
    if tool_id in ("music_play", "change_genre") and "genre" not in params:
        genre = _extract_genre_from_message(message)
        if genre:
            params["genre"] = genre
    
    if tool_id == "volume_set" and "volume" not in params:
        volume = _extract_volume_from_message(message)
        if volume:
            params["volume"] = volume
    
    # Costruisci contesto
    ctx = ToolContext(
        session_id=session_id,
        user_id=user_id,
        message=message,
        city=session.city,
        taxi_x=session.taxi_x,
        taxi_y=session.taxi_y,
        params=params,
        music_playing=session.music_playing,
        music_genre=session.music_genre,
        music_paused=session.music_paused,
        music_volume=session.music_volume,
    )
    
    # Esegui tool
    result = await tool_registry.execute_tool(tool_id, ctx)
    
    if result:
        return result.to_response(session_id)
    
    # Fallback
    return {
        "type": "assistant_response",
        "session_id": session_id,
        "message": f"Tool '{tool_id}' non trovato.",
        "ui_options": [],
        "commands": []
    }


async def _handle_music_intent(
    session_id: str,
    user_id: str,
    action: str,
    genre: str | None = None,
    volume_action: str | None = None,
    volume_value: int | None = None
) -> dict[str, Any]:
    """
    Gestisce intent musica (play/stop/pause/volume/resume/change_genre).
    
    Controlla stato corrente e gestisce errori (già accesa/spenta).
    """
    from app.schemas import Command, CommandType, UIOption
    from app.neo4j.repo import neo4j_repo
    
    music_state = session_store.get_music_state(session_id)
    is_playing = music_state["playing"] and not music_state["paused"]
    current_genre = music_state["genre"]
    current_volume = music_state["volume"]
    
    # VOLUME
    if action == "volume":
        if volume_action == "up":
            new_vol = session_store.adjust_volume(session_id, 2)
            return {
                "type": "assistant_response",
                "session_id": session_id,
                "message": f"🔊 Volume alzato a {new_vol}/10",
                "ui_options": [],
                "commands": [{"type": "SET_VOLUME", "payload": {"volume": new_vol}}]
            }
        elif volume_action == "down":
            new_vol = session_store.adjust_volume(session_id, -2)
            return {
                "type": "assistant_response",
                "session_id": session_id,
                "message": f"🔉 Volume abbassato a {new_vol}/10",
                "ui_options": [],
                "commands": [{"type": "SET_VOLUME", "payload": {"volume": new_vol}}]
            }
        elif volume_action == "set" and volume_value:
            new_vol = session_store.set_volume(session_id, volume_value)
            return {
                "type": "assistant_response",
                "session_id": session_id,
                "message": f"🔊 Volume impostato a {new_vol}/10",
                "ui_options": [],
                "commands": [{"type": "SET_VOLUME", "payload": {"volume": new_vol}}]
            }
    
    # RESUME
    if action == "resume":
        if not music_state["playing"]:
            return {
                "type": "assistant_response",
                "session_id": session_id,
                "message": "Non c'è musica da riprendere. Vuoi che la metta?",
                "ui_options": [
                    {"id": "ask_music", "label": "Sì, metti musica 🎵"},
                    {"id": "cancel", "label": "No grazie"}
                ],
                "commands": []
            }
        
        session_store.resume_music(session_id)
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": f"▶️ Riprendo {current_genre}!",
            "ui_options": [],
            "commands": [{"type": "RESUME_MUSIC", "payload": {}}]
        }
    
    # CHANGE GENRE
    if action == "change_genre":
        session_store.update_session(session_id, pending_question="music_genre")
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "🎵 Che genere preferisci?",
            "ui_options": [
                UIOption(id="genre:Pop", label="Pop").model_dump(),
                UIOption(id="genre:Rock", label="Rock").model_dump(),
                UIOption(id="genre:Jazz", label="Jazz").model_dump(),
                UIOption(id="genre:Classica", label="Classica").model_dump(),
                UIOption(id="genre:HipHop", label="Hip Hop").model_dump(),
                UIOption(id="genre:Elettronica", label="Elettronica").model_dump(),
            ],
            "commands": []
        }
    
    # STOP
    if action == "stop":
        if not music_state["playing"]:
            return {
                "type": "assistant_response",
                "session_id": session_id,
                "message": "🔇 La musica è già spenta. Vuoi che la accenda?",
                "ui_options": [
                    {"id": "ask_music", "label": "Sì, metti musica 🎵"},
                    {"id": "cancel", "label": "No grazie"}
                ],
                "commands": []
            }
        
        session_store.stop_music(session_id)
        command = Command(type=CommandType.STOP_MUSIC, payload={})
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "🔇 Musica fermata. Se vuoi riascoltarla, dimmelo!",
            "ui_options": [],
            "commands": [command.model_dump()]
        }
    
    # PAUSE
    if action == "pause":
        if not music_state["playing"]:
            return {
                "type": "assistant_response",
                "session_id": session_id,
                "message": "La musica non è in riproduzione.",
                "ui_options": [],
                "commands": []
            }
        
        session_store.pause_music(session_id)
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "⏸️ Musica in pausa.",
            "ui_options": [
                {"id": "resume_music", "label": "▶️ Riprendi"}
            ],
            "commands": [{"type": "PAUSE_MUSIC", "payload": {}}]
        }
    
    # PLAY
    if action == "play":
        # Se già in riproduzione, chiedi se cambiare
        if is_playing:
            msg = f"🎵 La musica è già accesa ({current_genre}). "
            if genre and genre.lower() != current_genre.lower():
                # L'utente vuole un genere diverso, cambialo
                normalized = music_service.normalize_genre(genre)
                if normalized:
                    session_store.start_music(session_id, normalized)
                    command = Command(
                        type=CommandType.PLAY_MUSIC,
                        payload={"genre": normalized, "url": f"/music/{normalized}"}
                    )
                    return {
                        "type": "assistant_response",
                        "session_id": session_id,
                        "message": f"🎵 Cambio a {normalized}!",
                        "ui_options": [],
                        "commands": [command.model_dump()]
                    }
            
            return {
                "type": "assistant_response",
                "session_id": session_id,
                "message": msg + "Vuoi cambiare genere?",
                "ui_options": [
                    {"id": "change_music", "label": "Cambia genere"},
                    {"id": "music_ok", "label": "Va bene così"}
                ],
                "commands": []
            }
        
        # Se ha preferenza e non ha specificato genere, usa preferenza
        if not genre:
            pref_genre = await neo4j_repo.get_music_preference(user_id)
            if pref_genre:
                genre = pref_genre
        
        # Se ancora non c'è genere, chiedi
        if not genre:
            session_store.update_session(session_id, pending_question="music_genre")
            return {
                "type": "assistant_response",
                "session_id": session_id,
                "message": "🎵 Che genere preferisci?",
                "ui_options": [
                    UIOption(id="genre:Pop", label="Pop").model_dump(),
                    UIOption(id="genre:Rock", label="Rock").model_dump(),
                    UIOption(id="genre:Jazz", label="Jazz").model_dump(),
                    UIOption(id="genre:Classica", label="Classica").model_dump(),
                    UIOption(id="genre:HipHop", label="Hip Hop").model_dump(),
                    UIOption(id="genre:Elettronica", label="Elettronica").model_dump(),
                    UIOption(id="no_music", label="No grazie").model_dump(),
                ],
                "commands": []
            }
        
        # Avvia musica
        normalized = music_service.normalize_genre(genre)
        if not normalized:
            return {
                "type": "assistant_response",
                "session_id": session_id,
                "message": f"Non conosco il genere '{genre}'. Prova con Pop, Rock, Jazz, Classica...",
                "ui_options": [],
                "commands": []
            }
        
        session_store.start_music(session_id, normalized)
        command = Command(
            type=CommandType.PLAY_MUSIC,
            payload={"genre": normalized, "url": f"/music/{normalized}"}
        )
        
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": f"🎵 Avvio {normalized}! Buon ascolto!",
            "ui_options": [],
            "commands": [command.model_dump()]
        }
    
    # Fallback
    return {
        "type": "assistant_response",
        "session_id": session_id,
        "message": "Non ho capito cosa vuoi fare con la musica.",
        "ui_options": [],
        "commands": []
    }


async def _handle_need_request(
    session_id: str,
    user_id: str,
    need: str,
    subcategory: str | None = None
) -> dict[str, Any]:
    """Gestisce richiesta di bisogno - cerca POI appropriati."""
    from app.llm.tools import tool_recommend_pois
    
    result = await tool_recommend_pois(
        user_id=user_id,
        need=need
    )
    
    pois = result.get("pois", [])
    has_preferences = result.get("has_preferences", False)
    
    if not pois:
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": f"Mi dispiace, non ho trovato luoghi per '{need}' nelle vicinanze.",
            "ui_options": [],
            "commands": []
        }
    
    # Salva POI IDs nella sessione
    poi_ids = [p["poi_id"] for p in pois]
    session_store.update_session(session_id, last_poi_suggestions=poi_ids)
    
    # Costruisci risposta con bottoni (senza lista testuale duplicata)
    need_emoji = {"Fame": "🍕", "Sete": "🍺", "Malessere": "💊", "Divertimento": "🎉", "Shopping": "🛍️"}
    
    # Messaggio breve di intro
    if has_preferences:
        message = f"{need_emoji.get(need, '📍')} In base alle tue preferenze, ecco cosa ho trovato:"
    else:
        message = f"{need_emoji.get(need, '📍')} Ecco cosa ho trovato:"
    
    # Costruisci i bottoni con le info integrate (nome + rating)
    ui_options = []
    
    for poi in pois:
        rating = poi.get('rating', 0) or 0
        from_pref = poi.get('from_preference', False)
        
        # Stellina per i preferiti
        pref_mark = "⭐ " if from_pref else ""
        # Label del bottone: nome + rating
        button_label = f"{pref_mark}{poi['name']} ({rating:.1f}⭐)"
        ui_options.append({"id": f"poi:{poi['poi_id']}", "label": button_label})
    
    ui_options.append({"id": "cancel", "label": "❌ Annulla"})
    
    return {
        "type": "assistant_response",
        "session_id": session_id,
        "message": message,
        "ui_options": ui_options,
        "commands": []
    }


async def _handle_poi_selection_by_name(
    session_id: str,
    user_id: str,
    text: str
) -> dict[str, Any] | None:
    """
    Cerca di riconoscere un nome POI nel testo e gestisce la selezione.
    Ritorna None se non trova un POI.
    """
    from app.neo4j.repo import neo4j_repo
    from app.schemas import Command, CommandType
    
    # Prova a cercare il POI per nome nel database
    poi = await neo4j_repo.get_poi_by_name(text)
    
    if poi:
        # Trovato! Mostra conferma invece di navigare direttamente
        session_store.update_session(session_id, last_poi_suggestions=[poi["id"]])
        
        rating = poi.get("rating", 0)
        label = f"{poi['name']} ⭐{rating:.1f}" if rating else poi["name"]
        
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": "📍 Ho trovato questo posto:",
            "ui_options": [
                {"id": f"poi:{poi['id']}", "label": label},
                {"id": "cancel", "label": "❌ Annulla"}
            ],
            "commands": []
        }
    
    return None


async def handle_trigger(data: dict[str, Any]) -> dict[str, Any]:
    """Gestisce un trigger da Unity."""
    trigger = Trigger(**data)
    
    logger.info(f"Handling trigger: {trigger.name}")
    
    # Routing trigger
    if trigger.name == "ASK_MUSIC":
        session = session_store.get_session(trigger.session_id)
        user_id = session.user_id or "unknown"
        
        result = await policy_service.handle_music_trigger(
            session_id=trigger.session_id,
            user_id=user_id
        )
        
        return {
            "type": "assistant_response",
            "session_id": trigger.session_id,
            **result
        }
    
    
    elif trigger.name == "ARRIVED_PICKUP":
        return {
            "type": "assistant_response",
            "session_id": trigger.session_id,
            "message": "Benvenuto a bordo! Dove la porto oggi?",
            "ui_options": [],
            "commands": []
        }
    
    elif trigger.name == "RIDE_START":
        # Payload: user_id, city, destination, eta_minutes
        payload = trigger.payload
        user_id = payload.get("user_id", "unknown")
        city = payload.get("city", "")
        destination = payload.get("destination", "destinazione")
        eta_minutes = payload.get("eta_minutes", 10)
        
        # Aggiorna sessione
        session_store.update_session(
            session_id=trigger.session_id,
            user_id=user_id,
            ride_id=trigger.ride_id,
            city=city
        )
        
        result = await policy_service.handle_ride_start(
            session_id=trigger.session_id,
            user_id=user_id,
            city=city,
            destination=destination,
            eta_minutes=eta_minutes
        )
        
        return {
            "type": "assistant_response",
            "session_id": trigger.session_id,
            **result
        }
    
    elif trigger.name == "RIDE_END" or trigger.name == "END_RIDE":
        # Termina corsa anticipatamente su richiesta utente
        logger.info(f"[TRIGGER] RIDE_END ricevuto per session {trigger.session_id}")
        
        # Simula una richiesta fine corsa
        request_data = {
            "session_id": trigger.session_id,
            "payload": {}
        }
        return await handle_end_ride_request(request_data)
    
    elif trigger.name == "END_RIDE_OLD":
        # Termina corsa - pulisci sessione
        session_store.clear_session(trigger.session_id)
        return {
            "type": "assistant_response",
            "session_id": trigger.session_id,
            "message": "Grazie per aver viaggiato con noi! Arrivederci e buona giornata! 👋",
            "ui_options": [],
            "commands": [{"type": "END_RIDE", "payload": {}}]
        }
    
    else:
        return {
            "type": "assistant_response",
            "session_id": trigger.session_id,
            "message": f"Trigger ricevuto: {trigger.name}",
            "ui_options": [],
            "commands": []
        }


async def handle_ui_action(data: dict[str, Any]) -> dict[str, Any]:
    """Gestisce un'azione UI (click bottone)."""
    action = UIAction(**data)
    
    logger.info(f"Handling UI action: {action.action_id}")
    
    session = session_store.get_session(action.session_id)
    user_id = session.user_id or "unknown"

    # Una selezione UI chiude eventuali opzioni attive
    if session.last_ui_options:
        session_store.update_session(action.session_id, last_ui_options=[])
    
    # POI selection
    if action.action_id == "poi_select" or action.action_id.startswith("poi:"):
        poi_id = action.payload.get("poi_id") or action.action_id.replace("poi:", "")
        
        result = await policy_service.handle_poi_selection(
            session_id=action.session_id,
            user_id=user_id,
            poi_id=poi_id
        )
        
        response = {
            "type": "assistant_response",
            "session_id": action.session_id,
            **result
        }
        
        # Processa side-effects (notifica Unity)
        await _process_side_effects(response, action.session_id)
        
        return response
    
    # Genre selection
    elif action.action_id.startswith("genre:"):
        genre = action.action_id.replace("genre:", "")
        
        result = await policy_service.handle_genre_selection(
            session_id=action.session_id,
            user_id=user_id,
            genre=genre
        )
        
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            **result
        }

    # Policy change selection (from UI buttons)
    elif action.action_id.startswith("policy:"):
        new_policy = action.action_id.replace("policy:", "").capitalize()
        if new_policy not in ["Sport", "Comfort", "Eco"]:
            return {
                "type": "assistant_response",
                "session_id": action.session_id,
                "message": f"⚠️ Modalità '{new_policy}' non riconosciuta. Scegli tra Sport, Comfort o Eco.",
                "ui_options": [],
                "commands": []
            }

        # Controllo condizioni utente (policy forzata)
        try:
            user_conditions = await get_user_conditions(user_id)
            if user_conditions:
                _, override_reason = get_effective_policy("Sport", user_conditions)
                if override_reason:
                    return {
                        "type": "assistant_response",
                        "session_id": action.session_id,
                        "message": f"🛋️ {override_reason}\n\nLa modalità Comfort non può essere cambiata per garantire la tua sicurezza.",
                        "ui_options": [],
                        "commands": []
                    }
        except Exception as e:
            logger.warning(f"[UI_ACTION] Errore controllo condizioni: {e}")

        # Policy già attiva
        if session.driving_policy.lower() == new_policy.lower():
            return {
                "type": "assistant_response",
                "session_id": action.session_id,
                "message": f"ℹ️ Modalità {session.driving_policy} già attiva.",
                "ui_options": [],
                "commands": []
            }

        # NOTA: Il check policy_locked è stato rimosso. Unity fa sempre il check batteria.

        # Invia il comando a Unity
        unity_msg = {
            "type": "cambio_policy",
            "session_id": action.session_id,
            "payload": {
                "nuova_policy": new_policy
            }
        }

        if connection_manager.is_unity_connected():
            await connection_manager.send_to_unity(unity_msg)
            logger.info(f"[UI_ACTION] Sent policy change to Unity: {new_policy}")
            return {
                "type": "assistant_response",
                "session_id": action.session_id,
                "message": f"Richiesta inviata: sto verificando se posso passare a {new_policy}.",
                "ui_options": [],
                "commands": []
            }
        else:
            logger.warning("[UI_ACTION] Unity not connected, cannot change policy")
            return {
                "type": "assistant_response",
                "session_id": action.session_id,
                "message": "⚠️ Il taxi non è connesso. Riprova tra poco.",
                "ui_options": [],
                "commands": []
            }
    
    # Cancel
    elif action.action_id in ["cancel", "no_music", "no_tour"]:
        # Gestione context-aware del cancel
        pending = session.pending_question
        
        # Se era in contesto musica
        if action.action_id == "no_music" or pending in ["music_genre", "music_feedback"]:
            session_store.clear_pending_question(action.session_id)
            return {
                "type": "assistant_response",
                "session_id": action.session_id,
                "message": "Ok, niente musica! Se cambi idea, dimmelo. 🎵",
                "ui_options": [],
                "commands": []
            }
        
        # Se era in contesto tour
        if action.action_id == "no_tour" or pending == "tour":
            session_store.clear_pending_question(action.session_id)
            session_store.update_session(action.session_id, mode=SessionMode.NORMAL)
            return {
                "type": "assistant_response",
                "session_id": action.session_id,
                "message": "Va bene, continuiamo verso la destinazione! 🚕",
                "ui_options": [],
                "commands": []
            }
        
        # Default: cancel generico
        result = await policy_service.handle_cancel_selection(
            session_id=action.session_id
        )
        
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            **result
        }
    
    # POI Need buttons (from fallback options like "🍽️ Cibo")
    elif action.action_id.startswith("poi_need:"):
        need = action.action_id.replace("poi_need:", "")
        logger.info(f"POI need button clicked: {need}")
        
        result = await _handle_need_request(
            session_id=action.session_id,
            user_id=user_id,
            need=need
        )
        return result
    
    # Quick actions (from fallback buttons)
    elif action.action_id.startswith("quick:"):
        need_map = {
            "quick:fame": "Fame",
            "quick:sete": "Sete",
            "quick:malessere": "Malessere",
            "quick:divertimento": "Divertimento",
            "quick:shopping": "Shopping",
        }
        need = need_map.get(action.action_id)
        if need:
            result = await _handle_need_request(
                session_id=action.session_id,
                user_id=user_id,
                need=need
            )
            return result
    
    
    # Stop music
    elif action.action_id == "stop_music":
        result = await policy_service.handle_stop_music(
            session_id=action.session_id
        )
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            **result
        }
    
    # Change music genre
    elif action.action_id == "change_music":
        result = await policy_service.handle_change_music(
            session_id=action.session_id
        )
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            **result
        }
    
    # Ask for music (from RIDE_START)
    elif action.action_id == "ask_music":
        result = await policy_service.handle_music_trigger(
            session_id=action.session_id,
            user_id=user_id
        )
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            **result
        }
    
    # Show POIs (from RIDE_START tourist) - Fetch and display actual POIs
    elif action.action_id == "show_pois":
        from app.llm.tools import tool_recommend_pois
        
        # Mostra POI turistici senza un bisogno specifico - usa Svago come default
        result = await tool_recommend_pois(
            user_id=user_id,
            need="Svago",
            limit=5
        )
        
        pois = result.get("pois", [])
        if not pois:
            return {
                "type": "assistant_response",
                "session_id": action.session_id,
                "message": "Mi dispiace, non ho trovato punti di interesse nelle vicinanze.",
                "ui_options": [],
                "commands": []
            }
        
        # Salva POI IDs nella sessione
        poi_ids = [p["poi_id"] for p in pois]
        session_store.update_session(action.session_id, last_poi_suggestions=poi_ids)
        
        # Costruisci risposta con bottoni
        message = "Ecco alcuni punti di interesse nelle vicinanze:\n\n"
        ui_options = []
        for poi in pois:
            message += f"• **{poi['name']}** - {poi['reason']}\n"
            ui_options.append({"id": f"poi:{poi['poi_id']}", "label": poi["name"]})
        
        message += "\nDove vorresti andare?"
        ui_options.append({"id": "cancel", "label": "Continua verso destinazione"})
        
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            "message": message,
            "ui_options": ui_options,
            "commands": []
        }
    
    # Music OK (user happy with music)
    elif action.action_id == "music_ok":
        session_store.clear_pending_question(action.session_id)
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            "message": "Perfetto, buon viaggio! 🎵 Se hai bisogno di altro, sono qui.",
            "ui_options": [],
            "commands": []
        }
    
    # Pause music
    elif action.action_id == "pause_music":
        session_store.pause_music(action.session_id)
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            "message": "Musica in pausa. ⏸️",
            "ui_options": [],
            "commands": [{"type": "PAUSE_MUSIC", "payload": {}}]
        }
    
    # Resume music
    elif action.action_id == "resume_music":
        session_store.resume_music(action.session_id)
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            "message": "Riprendo la musica! 🎵",
            "ui_options": [],
            "commands": [{"type": "RESUME_MUSIC", "payload": {}}]
        }
    
    # Set volume
    elif action.action_id == "volume_set":
        volume = action.payload.get("volume", 5)
        actual_volume = session_store.set_volume(action.session_id, volume)
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            "message": f"Volume impostato a {actual_volume}. 🔊",
            "ui_options": [],
            "commands": [{"type": "SET_VOLUME", "payload": {"volume": actual_volume}}]
        }
    
    # Unknown action
    else:
        return {
            "type": "assistant_response",
            "session_id": action.session_id,
            "message": "Azione ricevuta.",
            "ui_options": [],
            "commands": []
        }


# =============================================================================
# ENTRY POINT
# =============================================================================

if __name__ == "__main__":
    import uvicorn
    uvicorn.run(app, host="0.0.0.0", port=8000)
