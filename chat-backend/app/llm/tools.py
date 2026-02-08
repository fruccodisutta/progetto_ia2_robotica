"""
Definizione tools per LLM e dispatcher.

Implementa le funzioni che l'LLM può chiamare e il mapping tool→funzione.
"""

from typing import Any
from app.neo4j.repo import neo4j_repo
from app.session_store import session_store
from app.schemas import Command, CommandType, POI
from app.config import get_settings
from app.utils.logging import get_logger

logger = get_logger(__name__)

# =============================================================================
# DEFINIZIONE TOOLS (formato OpenAI-compatible)
# =============================================================================

TOOLS = [
    {
        "type": "function",
        "function": {
            "name": "get_user_context",
            "description": "Ottiene il contesto dell'utente: se è turista e se è la prima volta nella città corrente.",
            "parameters": {
                "type": "object",
                "properties": {
                    "user_id": {
                        "type": "string",
                        "description": "ID dell'utente"
                    },
                    "city": {
                        "type": "string",
                        "description": "Nome della città corrente"
                    }
                },
                "required": ["user_id", "city"]
            }
        }
    },
    {
        "type": "function",
        "function": {
            "name": "get_music_preference",
            "description": "Ottiene la preferenza musicale dell'utente. Ritorna il genere preferito o null se non impostato.",
            "parameters": {
                "type": "object",
                "properties": {
                    "user_id": {
                        "type": "string",
                        "description": "ID dell'utente"
                    }
                },
                "required": ["user_id"]
            }
        }
    },
    {
        "type": "function",
        "function": {
            "name": "set_music_preference",
            "description": "Salva la preferenza musicale dell'utente.",
            "parameters": {
                "type": "object",
                "properties": {
                    "user_id": {
                        "type": "string",
                        "description": "ID dell'utente"
                    },
                    "genre": {
                        "type": "string",
                        "description": "Genere musicale preferito"
                    }
                },
                "required": ["user_id", "genre"]
            }
        }
    },
    {
        "type": "function",
        "function": {
            "name": "recommend_pois",
            "description": "Raccomanda punti di interesse basati su un bisogno dell'utente (Fame, Sete, Shopping, Svago, Salute, Cultura, Relax, Trasporto, Denaro, Fitness, Alloggio).",
            "parameters": {
                "type": "object",
                "properties": {
                    "user_id": {
                        "type": "string",
                        "description": "ID dell'utente"
                    },
                    "need": {
                        "type": "string",
                        "description": "Bisogno dell'utente: Fame, Sete, Shopping, Svago, Salute, Cultura, Relax, Trasporto, Denaro, Fitness, Alloggio",
                        "enum": ["Fame", "Sete", "Shopping", "Svago", "Salute", "Cultura", "Relax", "Trasporto", "Denaro", "Fitness", "Alloggio"]
                    },
                    "limit": {
                        "type": "integer",
                        "description": "Numero massimo di POI da restituire (default: 5)"
                    }
                },
                "required": ["user_id", "need"]
            }
        }
    },
    {
        "type": "function",
        "function": {
            "name": "reroute_to_poi",
            "description": "Genera il comando per deviare il taxi verso un punto di interesse specifico.",
            "parameters": {
                "type": "object",
                "properties": {
                    "session_id": {
                        "type": "string",
                        "description": "ID della sessione"
                    },
                    "poi_id": {
                        "type": "string",
                        "description": "ID del punto di interesse"
                    }
                },
                "required": ["session_id", "poi_id"]
            }
        }
    },
    {
        "type": "function",
        "function": {
            "name": "get_last_visited_place",
            "description": "Ottiene l'ultimo posto visitato dall'utente in una certa categoria.",
            "parameters": {
                "type": "object",
                "properties": {
                    "user_id": {
                        "type": "string",
                        "description": "ID dell'utente"
                    },
                    "category_name": {
                        "type": "string",
                        "description": "Nome della categoria (es. Ristorante, Pizzeria, Bar)"
                    }
                },
                "required": ["user_id", "category_name"]
            }
        }
    },
    {
        "type": "function",
        "function": {
            "name": "play_music",
            "description": "Genera il comando per riprodurre musica di un certo genere.",
            "parameters": {
                "type": "object",
                "properties": {
                    "genre": {
                        "type": "string",
                        "description": "Genere musicale da riprodurre"
                    }
                },
                "required": ["genre"]
            }
        }
    },
]


# =============================================================================
# IMPLEMENTAZIONE FUNZIONI TOOL
# =============================================================================

async def tool_get_user_context(user_id: str, city: str) -> dict[str, Any]:
    """Ottiene contesto utente."""
    context = await neo4j_repo.get_user_context(user_id, city)
    return {
        "is_tourist": context.get("is_tourist", False),
        "first_time_in_city": context.get("first_time_in_city", True)
    }


async def tool_get_music_preference(user_id: str) -> dict[str, Any]:
    """Ottiene preferenza musicale."""
    genre = await neo4j_repo.get_music_preference(user_id)
    return {"genre": genre}


async def tool_set_music_preference(user_id: str, genre: str) -> dict[str, Any]:
    """Salva preferenza musicale."""
    success = await neo4j_repo.set_music_preference(user_id, genre)
    return {"success": success, "genre": genre}


async def tool_recommend_pois(
    user_id: str,
    need: str,
    limit: int = 5
) -> dict[str, Any]:
    """Raccomanda POI per un bisogno."""
    settings = get_settings()
    limit = limit or settings.default_poi_limit
    
    pois = await neo4j_repo.get_pois_by_need(
        user_id=user_id,
        need=need,
        limit=limit
    )
    
    # Genera reason per ogni POI e traccia preferenze
    result_pois = []
    has_preferences = False
    
    for poi in pois:
        from_pref = poi.get("from_preference", False)
        if from_pref:
            has_preferences = True
            
        reason = _generate_poi_reason(poi, need, from_pref)
        result_pois.append({
            "poi_id": poi["id"],
            "name": poi["name"],
            "id_unity": poi.get("id_unity"),
            "category": poi.get("category", ""),
            "rating": poi.get("rating"),
            "from_preference": from_pref,
            "reason": reason
        })
    
    return {
        "pois": result_pois, 
        "need": need,
        "has_preferences": has_preferences  # True se almeno un POI viene da preferenze
    }


def _generate_poi_reason(poi: dict[str, Any], need: str, from_preference: bool = False) -> str:
    """Genera una spiegazione per la raccomandazione del POI."""
    name = poi.get("name", "")
    category = poi.get("category", "")
    rating = poi.get("rating")
    
    parts = []
    
    # Indica se è una preferenza utente
    if from_preference:
        parts.append("⭐ Tra i tuoi preferiti")
    
    # Categoria
    if category:
        parts.append(f"{category}")
    
    # Rating
    if rating and rating >= 4.5:
        parts.append("ottima valutazione")
    elif rating and rating >= 4.0:
        parts.append("ben valutato")
    
    return " - ".join(parts) if parts else "Consigliato"


async def tool_reroute_to_poi(session_id: str, poi_id: str) -> dict[str, Any]:
    """Genera comando REROUTE_TO. Cerca prima per ID, poi per nome."""
    # Prima prova a cercare per ID esatto
    poi = await neo4j_repo.get_poi_by_id(poi_id)
    
    # Se non trovato, prova a estrarre nome e cercare per nome
    if not poi:
        # L'LLM potrebbe passare "poi:NomePOI" o solo il nome
        search_name = poi_id
        if poi_id.startswith("poi:"):
            search_name = poi_id.replace("poi:", "").replace("_", " ")
        
        # Cerca per nome
        poi = await neo4j_repo.get_poi_by_name(search_name)
    
    if poi:
        command = {
            "type": "REROUTE_TO",
            "payload": {
                "poi_id": poi.get("id"),
                "name": poi.get("name"),
                "id_unity": poi.get("id_unity")
            }
        }
        return {"success": True, "command": command, "poi_name": poi.get("name")}
    
    return {"success": False, "error": "POI non trovato"}




async def tool_get_last_visited_place(
    user_id: str,
    category_name: str
) -> dict[str, Any]:
    """Ottiene ultimo posto visitato per categoria."""
    poi = await neo4j_repo.get_last_visited_place(user_id, category_name)
    
    if poi:
        return {
            "found": True,
            "poi_id": poi["id"],
            "name": poi["name"],
            "category": poi.get("category")
        }
    
    return {"found": False}


async def tool_play_music(genre: str) -> dict[str, Any]:
    """Genera comando PLAY_MUSIC."""
    command = {
        "type": "PLAY_MUSIC",
        "payload": {"genre": genre}
    }
    return {"success": True, "command": command, "genre": genre}


# =============================================================================
# DISPATCHER
# =============================================================================

TOOL_FUNCTIONS = {
    "get_user_context": tool_get_user_context,
    "get_music_preference": tool_get_music_preference,
    "set_music_preference": tool_set_music_preference,
    "recommend_pois": tool_recommend_pois,
    "reroute_to_poi": tool_reroute_to_poi,
    "get_last_visited_place": tool_get_last_visited_place,
    "play_music": tool_play_music,
}


async def execute_tool(name: str, arguments: dict[str, Any]) -> dict[str, Any]:
    """
    Esegue un tool chiamato dall'LLM.
    
    Args:
        name: Nome del tool
        arguments: Argomenti del tool
        
    Returns:
        Risultato dell'esecuzione
        
    Raises:
        ValueError: Se il tool non esiste
    """
    if name not in TOOL_FUNCTIONS:
        raise ValueError(f"Tool sconosciuto: {name}")
    
    logger.info(f"Executing tool: {name} with args: {arguments}")
    
    try:
        result = await TOOL_FUNCTIONS[name](**arguments)
        logger.info(f"Tool {name} result: {result}")
        return result
    except Exception as e:
        logger.error(f"Tool {name} failed: {e}")
        return {"error": str(e)}


def get_tools() -> list[dict[str, Any]]:
    """Restituisce la lista dei tools per l'LLM."""
    return TOOLS
