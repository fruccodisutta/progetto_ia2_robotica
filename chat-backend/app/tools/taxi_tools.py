"""
Taxi Tools per gestione della corsa.

Include strumenti per controllare la modalità di guida del taxi.
"""

from dataclasses import dataclass, field
from app.schemas import SessionState, ToolContext, ToolResult, UIOption
import logging

logger = logging.getLogger(__name__)


# Import servizi necessari (lazy per evitare circular imports)
def get_session_store():
    from app.session_store import session_store
    return session_store

def get_connection_manager():
    from app.main import connection_manager
    return connection_manager

def get_condition_helpers():
    from app.neo4j.seed2 import get_user_conditions, get_effective_policy
    return get_user_conditions, get_effective_policy


# =============================================================================
# BASE TOOL
# =============================================================================

@dataclass
class Tool:
    """Classe base per tutti i tool."""
    id: str
    name: str
    description: str
    patterns: list[str] = field(default_factory=list)
    examples: list[str] = field(default_factory=list)
    category: str = "general"
    
    def is_available(self, state: SessionState) -> bool:
        return True
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        return ToolResult(
            message=f"Tool {self.id} non implementato.",
            ui_options=[],
            commands=[]
        )


# =============================================================================
# TAXI TOOLS
# =============================================================================

# Mappa dei termini utente -> policy
POLICY_KEYWORDS = {
    "sport": ["fretta", "urgente", "urgenza", "veloce", "sbrigati", "corri", "rapido", 
              "di fretta", "ho fretta", "è urgente", "sbrigarmi", "presto", "velocemente",
              "sport", "sportiva", "modalità sport"],
    "comfort": ["relax", "tranquillo", "calma", "comodo", "comfort", "confortevole",
                "senza fretta", "con calma", "piano", "lentamente", "rilassato",
                "modalità comfort", "comfortevole"],
    "eco": ["ecologico", "eco", "risparmio", "ecosostenibile", "verde", "ambiente",
            "risparmiare", "economico", "modalità eco", "green"]
}

def detect_policy_from_text(text: str) -> str | None:
    """Rileva la policy desiderata dal testo dell'utente."""
    text_lower = text.lower()
    
    for policy, keywords in POLICY_KEYWORDS.items():
        for keyword in keywords:
            if keyword in text_lower:
                return policy.capitalize()  # Sport, Comfort, Eco
    return None


class ChangeDrivingPolicyTool(Tool):
    """Cambia la modalità di guida durante la corsa."""
    
    def __init__(self):
        super().__init__(
            id="change_driving_policy",
            name="Cambia Modalità di Guida",
            description="Cambia la modalità di guida del taxi (Comfort, Sport, Eco). Usa quando l'utente ha fretta o vuole un viaggio più tranquillo.",
            patterns=[
                # Urgenza -> Sport
                "ho fretta", "è urgente", "urgente", "sbrigati", "fai presto", 
                "veloce", "più veloce", "vado di fretta", "sono in ritardo",
                "corri", "accelera", "più rapido",
                # Relax -> Comfort
                "vai piano", "con calma", "senza fretta", "tranquillo",
                "rilassato", "più lento", "non c'è fretta",
                # Eco
                "modalità eco", "guida ecologica", "risparmia",
                # Espliciti
                "modalità sport", "modalità comfort", "cambia modalità",
                "guida sportiva", "guida confortevole"
            ],
            examples=[
                "ho fretta, puoi andare più veloce?",
                "non c'è urgenza, vai tranquillo",
                "metti la modalità sport"
            ],
            category="taxi"
        )
    
    def is_available(self, state: SessionState) -> bool:
        """Disponibile solo durante la corsa (con passeggero a bordo)."""
        from app.schemas import SessionMode
        # In-ride mode significa che siamo in viaggio
        return state.mode == SessionMode.NORMAL
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        """Esegue il cambio policy."""
        session_store = get_session_store()
        connection_manager = get_connection_manager()
        
        # === CONTROLLO CONDIZIONI UTENTE ===
        # Se l'utente ha condizioni che forzano una policy, non può cambiarla
        try:
            get_user_conditions, get_effective_policy = get_condition_helpers()
            user_conditions = await get_user_conditions(ctx.user_id)
            if user_conditions:
                _, override_reason = get_effective_policy("Sport", user_conditions)  # Test con qualsiasi policy
                if override_reason:
                    # Utente ha condizioni che forzano una policy
                    return ToolResult(
                        message=f"🛋️ {override_reason}\n\nLa modalità Comfort non può essere cambiata per garantire la tua sicurezza.",
                        ui_options=[],
                        commands=[]
                    )
        except Exception as e:
            logger.warning(f"[TAXI_TOOL] Errore controllo condizioni: {e}")
        
        # Rileva la policy dal messaggio dell'utente
        new_policy = ctx.params.get("policy")
        if not new_policy:
            new_policy = detect_policy_from_text(ctx.message)
        
        if not new_policy:
            # Chiedi all'utente quale modalità vuole
            return ToolResult(
                message="🎯 Quale modalità di guida preferisci?",
                ui_options=[
                    UIOption(id="policy:Sport", label="🏎️ Sport - Più veloce"),
                    UIOption(id="policy:Comfort", label="🛋️ Comfort - Più fluido"),
                    UIOption(id="policy:Eco", label="🌿 Eco - Più ecologico"),
                ],
                commands=[]
            )
        
        # Normalizza la policy
        new_policy = new_policy.capitalize()
        if new_policy not in ["Sport", "Comfort", "Eco"]:
            return ToolResult(
                message=f"⚠️ Modalità '{new_policy}' non riconosciuta. Scegli tra Sport, Comfort o Eco.",
                ui_options=[
                    UIOption(id="policy:Sport", label="🏎️ Sport"),
                    UIOption(id="policy:Comfort", label="🛋️ Comfort"),
                    UIOption(id="policy:Eco", label="🌿 Eco"),
                ],
                commands=[]
            )
        
        # Controlla se la policy richiesta è già attiva
        session = session_store.get_session(ctx.session_id)
        if session.driving_policy.lower() == new_policy.lower():
            return ToolResult(
                message=f"ℹ️ Modalità {session.driving_policy} già attiva.",
                ui_options=[],
                commands=[]
        )

        # NOTA: Il check policy_locked è stato rimosso. Unity fa sempre il check batteria
        # accurato con il percorso A* corretto e rifiuta se non c'è abbastanza energia.
        
        # Invia il comando a Unity
        unity_msg = {
            "type": "cambio_policy",
            "session_id": ctx.session_id,
            "payload": {
                "nuova_policy": new_policy
            }
        }
        
        if connection_manager.is_unity_connected():
            await connection_manager.send_to_unity(unity_msg)
            logger.info(f"[TAXI_TOOL] Sent policy change to Unity: {new_policy}")
            
            # Non aggiorniamo la policy qui: attendiamo conferma da Unity per coerenza stato
            return ToolResult(
                message=f"Richiesta inviata: sto verificando se posso passare a {new_policy}.",
                ui_options=[],
                commands=[]
            )
        else:
            logger.warning("[TAXI_TOOL] Unity not connected, cannot change policy")
            return ToolResult(
                message="⚠️ Il taxi non è connesso. Riprova tra poco.",
                ui_options=[],
                commands=[]
            )


# =============================================================================
# ESPORTA TUTTI I TAXI TOOLS
# =============================================================================

TAXI_TOOLS = [
    ChangeDrivingPolicyTool(),
]
