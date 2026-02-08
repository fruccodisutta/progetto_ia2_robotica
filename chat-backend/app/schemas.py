"""
Schemi Pydantic per messaggi WebSocket e modelli interni.

Definisce i contratti di comunicazione Unity ↔ Backend.
"""

from enum import Enum
from typing import Any, Literal
from pydantic import BaseModel, Field


# ============================================================================
# INPUT: Messaggi da Unity → Backend
# ============================================================================

class TaxiPosition(BaseModel):
    """Posizione corrente del taxi."""
    x: float
    y: float


class UserMessage(BaseModel):
    """Messaggio testuale dall'utente."""
    type: Literal["user_message"] = "user_message"
    session_id: str
    user_id: str
    ride_id: str
    city: str
    taxi: TaxiPosition
    text: str


class Trigger(BaseModel):
    """Evento trigger da Unity (es. ASK_MUSIC, ARRIVED_PICKUP)."""
    type: Literal["trigger"] = "trigger"
    session_id: str
    ride_id: str
    name: str
    payload: dict[str, Any] = Field(default_factory=dict)


class UIAction(BaseModel):
    """Azione UI (click bottone)."""
    type: Literal["ui_action"] = "ui_action"
    session_id: str
    action_id: str
    payload: dict[str, Any] = Field(default_factory=dict)


# ============================================================================
# OUTPUT: Risposte Backend → Unity
# ============================================================================

class UIOption(BaseModel):
    """Opzione bottone UI."""
    id: str
    label: str


class CommandType(str, Enum):
    """Tipi di comandi supportati."""
    PLAY_MUSIC = "PLAY_MUSIC"
    STOP_MUSIC = "STOP_MUSIC"
    PAUSE_MUSIC = "PAUSE_MUSIC"
    RESUME_MUSIC = "RESUME_MUSIC"
    SET_VOLUME = "SET_VOLUME"
    REROUTE_TO = "REROUTE_TO"
    END_RIDE = "END_RIDE"


class Command(BaseModel):
    """Comando strutturato per Unity."""
    type: CommandType | str  # str per retrocompatibilità
    payload: dict[str, Any] = Field(default_factory=dict)


# ============================================================================
# TOOL EXECUTION: Contesto e risultato per i tool
# ============================================================================

class ToolContext(BaseModel):
    """Contesto passato a ogni tool per l'esecuzione."""
    session_id: str
    user_id: str
    message: str  # Messaggio originale dell'utente
    city: str | None = None
    taxi_x: float = 0.0
    taxi_y: float = 0.0
    params: dict[str, Any] = Field(default_factory=dict)  # Parametri estratti (es. genere, volume)
    
    # Stato sessione (popolato dal dispatcher)
    music_playing: bool = False
    music_genre: str | None = None
    music_paused: bool = False
    music_volume: int = 5


class ToolResult(BaseModel):
    """Risultato standardizzato dell'esecuzione di un tool."""
    message: str
    ui_options: list["UIOption"] = Field(default_factory=list)
    commands: list[dict[str, Any]] = Field(default_factory=list)  # Serializzati per flessibilità
    
    def to_response(self, session_id: str) -> dict[str, Any]:
        """Converte in formato risposta WebSocket."""
        return {
            "type": "assistant_response",
            "session_id": session_id,
            "message": self.message,
            "ui_options": [opt.model_dump() for opt in self.ui_options],
            "commands": self.commands
        }


class AssistantResponse(BaseModel):
    """Risposta completa del backend a Unity."""
    type: Literal["assistant_response"] = "assistant_response"
    session_id: str
    message: str
    ui_options: list[UIOption] = Field(default_factory=list)
    commands: list[Command] = Field(default_factory=list)


# ============================================================================
# INTERNAL: Modelli interni
# ============================================================================

class POI(BaseModel):
    """Punto di interesse."""
    id: str
    name: str
    x: float
    y: float
    category: str
    rating: float | None = None
    reason: str | None = None


class UserContext(BaseModel):
    """Contesto utente per la sessione."""
    user_id: str
    name: str | None = None
    age: int | None = None
    is_tourist: bool = False
    first_time_in_city: bool = False
    language: str = "it"


class SessionMode(str, Enum):
    """Modalità sessione."""
    PRE_RIDE = "pre_ride"  # Booking screen, only POI tools available
    NORMAL = "normal"  # In-ride, all tools available


class SessionState(BaseModel):
    """Stato completo della sessione."""
    session_id: str
    user_id: str | None = None
    ride_id: str | None = None
    city: str | None = None
    taxi_x: float = 0.0
    taxi_y: float = 0.0
    history: list[dict[str, Any]] = Field(default_factory=list)
    pending_question: str | None = None
    last_poi_suggestions: list[str] = Field(default_factory=list)
    last_ui_options: list[dict[str, Any]] = Field(default_factory=list)
    mode: SessionMode = SessionMode.NORMAL
    # Music state
    music_playing: bool = False
    music_genre: str | None = None
    music_paused: bool = False
    music_volume: int = 5  # 1-10, default 5
    # Ride lifecycle state
    is_ride_active: bool = False
    current_destination_name: str | None = None
    current_destination_id: str | None = None
    current_passenger_id: str | None = None
    ride_started_at: str | None = None  # ISO format timestamp
    driving_policy: str = "Sport"  # Sport, Comfort, Eco - default Sport


# ============================================================================
# WRAPPER: Per parsing messaggi in ingresso
# ============================================================================

class IncomingMessage(BaseModel):
    """Wrapper per identificare tipo messaggio in ingresso."""
    type: str
    session_id: str
