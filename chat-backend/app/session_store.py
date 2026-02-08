"""
Session Store per gestione stato sessioni in memoria.

Mantiene lo stato conversazionale per ogni session_id.
"""

from typing import Any
from app.schemas import SessionState, SessionMode
from app.config import get_settings


class SessionStore:
    """Store in-memory per le sessioni utente."""
    
    def __init__(self):
        """Inizializza lo store."""
        self._sessions: dict[str, SessionState] = {}
        self._settings = get_settings()
    
    def get_session(self, session_id: str) -> SessionState:
        """
        Ottiene lo stato della sessione, creandola se non esiste.
        
        Args:
            session_id: ID univoco della sessione
            
        Returns:
            SessionState corrente
        """
        if session_id not in self._sessions:
            self._sessions[session_id] = SessionState(session_id=session_id)
        return self._sessions[session_id]
    
    def update_session(
        self,
        session_id: str,
        user_id: str | None = None,
        ride_id: str | None = None,
        city: str | None = None,
        taxi_x: float | None = None,
        taxi_y: float | None = None,
        pending_question: str | None = None,
        last_poi_suggestions: list[str] | None = None,
        last_ui_options: list[dict[str, Any]] | None = None,
        mode: SessionMode | None = None,
        music_playing: bool | None = None,
        music_genre: str | None = None,
        music_paused: bool | None = None,
    ) -> SessionState:
        """
        Aggiorna lo stato della sessione.
        
        Args:
            session_id: ID sessione
            **kwargs: Campi da aggiornare
            
        Returns:
            SessionState aggiornato
        """
        session = self.get_session(session_id)
        
        if user_id is not None:
            session.user_id = user_id
        if ride_id is not None:
            session.ride_id = ride_id
        if city is not None:
            session.city = city
        if taxi_x is not None:
            session.taxi_x = taxi_x
        if taxi_y is not None:
            session.taxi_y = taxi_y
        if pending_question is not None:
            session.pending_question = pending_question
        if last_poi_suggestions is not None:
            session.last_poi_suggestions = last_poi_suggestions
        if last_ui_options is not None:
            session.last_ui_options = last_ui_options
        if mode is not None:
            session.mode = mode
        if music_playing is not None:
            session.music_playing = music_playing
        if music_genre is not None:
            session.music_genre = music_genre
        if music_paused is not None:
            session.music_paused = music_paused
            
        return session
    
    def add_to_history(
        self,
        session_id: str,
        role: str,
        content: str,
        **extra: Any
    ) -> None:
        """
        Aggiunge un turno alla history della sessione.
        
        Args:
            session_id: ID sessione
            role: "user" o "assistant"
            content: Contenuto del messaggio
            **extra: Dati aggiuntivi (tool_calls, ecc.)
        """
        session = self.get_session(session_id)
        turn = {"role": role, "content": content, **extra}
        session.history.append(turn)
        
        # Limita la history agli ultimi N turni
        max_turns = self._settings.max_history_turns
        if len(session.history) > max_turns:
            session.history = session.history[-max_turns:]
    
    def clear_pending_question(self, session_id: str) -> None:
        """Rimuove la pending question dalla sessione."""
        session = self.get_session(session_id)
        session.pending_question = None
    
    def clear_poi_suggestions(self, session_id: str) -> None:
        """Rimuove i suggerimenti POI dalla sessione."""
        session = self.get_session(session_id)
        session.last_poi_suggestions = []
    
    def is_poi_valid(self, session_id: str, poi_id: str) -> bool:
        """
        Verifica se un POI è tra quelli suggeriti nella sessione.
        
        Args:
            session_id: ID sessione
            poi_id: ID del POI da verificare
            
        Returns:
            True se il POI è valido
        """
        session = self.get_session(session_id)
        return poi_id in session.last_poi_suggestions
    
    def delete_session(self, session_id: str) -> None:
        """Elimina una sessione."""
        self._sessions.pop(session_id, None)
    
    def clear_session(self, session_id: str) -> None:
        """Resetta lo stato della sessione (fine corsa)."""
        session = self.get_session(session_id)
        session.pending_question = None
        session.last_poi_suggestions = []
        session.last_ui_options = []
        session.mode = SessionMode.NORMAL
        session.history = []
        # Reset music state
        session.music_playing = False
        session.music_genre = None
        session.music_paused = False
        # Reset ride state
        session.is_ride_active = False
        session.current_destination_name = None
        session.current_destination_id = None
        session.current_passenger_id = None
        session.ride_started_at = None
    
    # =========================================================================
    # RIDE LIFECYCLE
    # =========================================================================
    
    def start_ride(
        self,
        session_id: str,
        destination_name: str,
        destination_id: str,
        passenger_id: str | None = None
    ) -> None:
        """Inizia una corsa per la sessione."""
        from datetime import datetime
        session = self.get_session(session_id)
        session.is_ride_active = True
        session.current_destination_name = destination_name
        session.current_destination_id = destination_id
        session.current_passenger_id = passenger_id
        session.ride_started_at = datetime.now().isoformat()
        session.mode = SessionMode.NORMAL
        
        from app.utils.logging import get_logger
        logger = get_logger(__name__)
        logger.info(f"[SESSION:{session_id}] RIDE_START | dest={destination_name}, id={destination_id}")
    
    def end_ride(self, session_id: str) -> None:
        """Termina la corsa e resetta lo stato."""
        session = self.get_session(session_id)
        
        from app.utils.logging import get_logger
        logger = get_logger(__name__)
        logger.info(f"[SESSION:{session_id}] RIDE_END | was_active={session.is_ride_active}, dest={session.current_destination_name}")
        
        session.is_ride_active = False
        session.current_destination_name = None
        session.current_destination_id = None
        session.current_passenger_id = None
        session.ride_started_at = None
        # Reset music
        session.music_playing = False
        session.music_genre = None
        session.music_paused = False
        session.mode = SessionMode.NORMAL
    
    def is_ride_active(self, session_id: str) -> bool:
        """Verifica se c'è una corsa attiva."""
        return self.get_session(session_id).is_ride_active
    
    def update_destination(self, session_id: str, name: str, dest_id: str) -> None:
        """Aggiorna la destinazione corrente durante una corsa."""
        session = self.get_session(session_id)
        if session.is_ride_active:
            old_dest = session.current_destination_name
            session.current_destination_name = name
            session.current_destination_id = dest_id
            
            from app.utils.logging import get_logger
            logger = get_logger(__name__)
            logger.info(f"[SESSION:{session_id}] DESTINATION_CHANGE | {old_dest} -> {name}")
    
    # =========================================================================
    # MUSIC STATE
    # =========================================================================
    
    def is_music_playing(self, session_id: str) -> bool:
        """Verifica se la musica è in riproduzione."""
        session = self.get_session(session_id)
        return session.music_playing and not session.music_paused
    
    def get_music_state(self, session_id: str) -> dict:
        """Ottiene lo stato completo della musica."""
        session = self.get_session(session_id)
        return {
            "playing": session.music_playing,
            "genre": session.music_genre,
            "paused": session.music_paused,
            "volume": session.music_volume
        }
    
    def start_music(self, session_id: str, genre: str) -> None:
        """Avvia la musica."""
        session = self.get_session(session_id)
        session.music_playing = True
        session.music_genre = genre
        session.music_paused = False
    
    def stop_music(self, session_id: str) -> None:
        """Ferma la musica."""
        session = self.get_session(session_id)
        session.music_playing = False
        session.music_genre = None
        session.music_paused = False
    
    def pause_music(self, session_id: str) -> None:
        """Mette in pausa la musica."""
        session = self.get_session(session_id)
        session.music_paused = True
    
    def resume_music(self, session_id: str) -> None:
        """Riprende la musica dalla pausa."""
        session = self.get_session(session_id)
        session.music_paused = False
    
    def set_volume(self, session_id: str, volume: int) -> int:
        """Imposta il volume (1-10). Ritorna il volume effettivo."""
        session = self.get_session(session_id)
        session.music_volume = max(1, min(10, volume))
        return session.music_volume
    
    def adjust_volume(self, session_id: str, delta: int) -> int:
        """Regola il volume di delta. Ritorna il volume effettivo."""
        session = self.get_session(session_id)
        session.music_volume = max(1, min(10, session.music_volume + delta))
        return session.music_volume


# Istanza singleton
session_store = SessionStore()
