"""
Servizio Policy per logica di business.

Gestisce trigger, validazioni e flussi specifici.
"""

from typing import Any
from app.neo4j.repo import neo4j_repo
from app.session_store import session_store
from app.schemas import Command, CommandType, UIOption, SessionMode
from app.utils.logging import get_logger

logger = get_logger(__name__)


class PolicyService:
    """Servizio per logica di business e policy."""
    
    async def handle_music_trigger(
        self,
        session_id: str,
        user_id: str
    ) -> dict[str, Any]:
        """
        Gestisce il trigger ASK_MUSIC.
        
        - Se utente ha preferenza → riproduce automaticamente
        - Altrimenti → propone tutti i generi
        
        Args:
            session_id: ID sessione
            user_id: ID utente
            
        Returns:
            Dict con message, ui_options, commands
        """
        # Verifica preferenza esistente
        genre = await neo4j_repo.get_music_preference(user_id)
        
        if genre:
            # Ha preferenza: avvia musica automaticamente senza conferma
            command = Command(
                type=CommandType.PLAY_MUSIC,
                payload={"genre": genre, "url": f"/music/{genre}"}
            )
            # Aggiorna stato sessione musica
            session_store.start_music(session_id, genre)
            session_store.update_session(session_id, pending_question="music_feedback")
            return {
                "message": f"Ho visto che ti piace il {genre}, ho selezionato questa canzone per te! 🎵",
                "ui_options": [
                    UIOption(id="music_ok", label="Perfetto!").model_dump(),
                    UIOption(id="change_music", label="Cambia genere").model_dump(),
                    UIOption(id="stop_music", label="Ferma la musica").model_dump(),
                ],
                "commands": [command.model_dump()]
            }
        else:
            # Non ha preferenza: propone tutti i generi
            session_store.update_session(
                session_id,
                pending_question="music_genre"
            )
            return {
                "message": "Che ne dici di un po' di musica? Che genere preferisci?",
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
    
    async def validate_poi_selection(
        self,
        session_id: str,
        poi_id: str
    ) -> bool:
        """
        Valida che il POI selezionato sia tra quelli suggeriti.
        
        Args:
            session_id: ID sessione
            poi_id: ID del POI selezionato
            
        Returns:
            True se valido
        """
        return session_store.is_poi_valid(session_id, poi_id)
    
    async def handle_genre_selection(
        self,
        session_id: str,
        user_id: str,
        genre: str
    ) -> dict[str, Any]:
        """
        Gestisce la selezione del genere musicale.
        
        Args:
            session_id: ID sessione
            user_id: ID utente
            genre: Genere selezionato
            
        Returns:
            Risposta con comando PLAY_MUSIC
        """
        # Salva preferenza
        await neo4j_repo.set_music_preference(user_id, genre)
        
        # Pulisci pending question
        session_store.clear_pending_question(session_id)
        
        # Aggiorna stato sessione musica
        session_store.start_music(session_id, genre)
        
        # Avvia musica
        command = Command(
            type=CommandType.PLAY_MUSIC,
            payload={"genre": genre, "url": f"/music/{genre}"}
        )
        
        return {
            "message": f"Perfetto! Ho salvato la tua preferenza e avviato {genre}. 🎵",
            "ui_options": [],
            "commands": [command.model_dump()]
        }
    
    async def handle_poi_selection(
        self,
        session_id: str,
        user_id: str,
        poi_id: str
    ) -> dict[str, Any]:
        """
        Gestisce la selezione di un POI.
        
        Args:
            session_id: ID sessione
            user_id: ID utente
            poi_id: ID del POI selezionato
            
        Returns:
            Risposta con comando REROUTE_TO
        """
        # Ottieni POI direttamente dal database (non richiede validazione stretta)
        poi = await neo4j_repo.get_poi_by_id(poi_id)
        
        if not poi:
            return {
                "message": "Mi dispiace, non ho trovato il luogo selezionato.",
                "ui_options": [],
                "commands": []
            }
        
        # Registra visita
        await neo4j_repo.record_visit(user_id, poi_id)
        
        # Pulisci suggerimenti
        session_store.clear_poi_suggestions(session_id)
        
        # Genera comando
        command = Command(
            type=CommandType.REROUTE_TO,
            payload={
                "poi_id": poi_id,
                "name": poi["name"],
                "id_unity": poi.get("id_unity")
            }
        )
        
        is_ride_active = session_store.is_ride_active(session_id)
        message = "" if is_ride_active else f"Perfetto! Ti sto portando a {poi['name']}. 🚕"

        return {
            "message": message,
            "ui_options": [],
            "commands": [command.model_dump()]
        }
    
    async def handle_cancel_selection(
        self,
        session_id: str
    ) -> dict[str, Any]:
        """
        Gestisce l'annullamento di una selezione.
        
        Args:
            session_id: ID sessione
            
        Returns:
            Risposta di conferma
        """
        # Pulisci stato
        session_store.clear_poi_suggestions(session_id)
        session_store.clear_pending_question(session_id)
        session_store.update_session(session_id, mode=SessionMode.NORMAL)
        
        return {
            "message": "Va bene, continuiamo verso la destinazione originale. 🚕",
            "ui_options": [],
            "commands": []
        }
    
    async def handle_ride_start(
        self,
        session_id: str,
        user_id: str,
        city: str,
        destination: str,
        eta_minutes: int
    ) -> dict[str, Any]:
        """
        Gestisce l'inizio della corsa.
        
        Saluta l'utente e propone musica.
        
        Args:
            session_id: ID sessione
            user_id: ID utente
            city: Città corrente
            destination: Destinazione
            eta_minutes: Tempo stimato in minuti
            
        Returns:
            Risposta con saluto e proposta
        """
        # Ottieni info utente
        user = await neo4j_repo.get_user_by_id(user_id)
        user_name = user.get("nome", "ospite") if user else "ospite"
        
        from app.utils.formatting import format_duration_minutes
        
        # Saluto iniziale
        greeting = f"Ciao {user_name}, benvenuto in questo taxi! "
        greeting += f"Siamo diretti a {destination} con un tempo stimato di {format_duration_minutes(eta_minutes)}."
        
        # Propone musica
        return {
            "message": greeting + "\n\n" +
                      "Vuoi ascoltare un po' di musica?",
            "ui_options": [
                UIOption(id="ask_music", label="Sì, metti della musica 🎵").model_dump(),
                UIOption(id="no_music", label="No grazie").model_dump(),
            ],
            "commands": []
        }
    
    async def handle_stop_music(
        self,
        session_id: str
    ) -> dict[str, Any]:
        """Ferma la musica."""
        # Aggiorna stato sessione
        session_store.stop_music(session_id)
        session_store.clear_pending_question(session_id)
        
        command = Command(
            type=CommandType.STOP_MUSIC,
            payload={}
        )
        return {
            "message": "🔇 Musica fermata. Se hai bisogno di altro, sono qui!",
            "ui_options": [],
            "commands": [command.model_dump()]
        }
    
    async def handle_change_music(
        self,
        session_id: str
    ) -> dict[str, Any]:
        """Propone cambio genere musicale."""
        # Prima ferma la musica corrente
        stop_cmd = Command(type=CommandType.STOP_MUSIC, payload={})
        session_store.update_session(session_id, pending_question="music_genre")
        return {
            "message": "Che genere preferisci?",
            "ui_options": [
                UIOption(id="genre:Pop", label="Pop").model_dump(),
                UIOption(id="genre:Rock", label="Rock").model_dump(),
                UIOption(id="genre:Jazz", label="Jazz").model_dump(),
                UIOption(id="genre:Classica", label="Classica").model_dump(),
                UIOption(id="genre:HipHop", label="Hip Hop").model_dump(),
                UIOption(id="genre:Elettronica", label="Elettronica").model_dump(),
            ],
            "commands": [stop_cmd.model_dump()]
        }


# Istanza singleton
policy_service = PolicyService()
