"""
Music Tools con logica execute() integrata.

Ogni tool gestisce una specifica azione musicale.
"""

from dataclasses import dataclass, field
from app.schemas import SessionState, ToolContext, ToolResult, UIOption


# Import servizi necessari (lazy per evitare circular imports)
def get_session_store():
    from app.session_store import session_store
    return session_store

def get_neo4j_repo():
    from app.neo4j.repo import neo4j_repo
    return neo4j_repo

def get_music_service():
    from app.services.music_service import music_service
    return music_service


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
# MUSIC TOOLS
# =============================================================================

class MusicPlayTool(Tool):
    """Avvia la musica."""
    def __init__(self):
        super().__init__(
            id="music_play",
            name="Metti Musica",
            description="Avvia la riproduzione musicale. Usa quando l'utente vuole ascoltare musica.",
            # PATTERN SPECIFICI - evitano falsi positivi
            patterns=[
                "metti musica", "metti la musica", "avvia musica",
                "voglio ascoltare musica", "musica per favore",
                "metti jazz", "metti rock", "metti pop", "metti classica",
                "metti hip hop", "metti elettronica",
                "ascoltare jazz", "ascoltare rock", "ascoltare pop",
            ],
            examples=[
                "metti un po' di musica",
                "voglio ascoltare della musica",
                "metti jazz"
            ],
            category="music"
        )
    
    def is_available(self, state: SessionState) -> bool:
        return not state.music_playing
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        session_store = get_session_store()
        neo4j_repo = get_neo4j_repo()
        music_service = get_music_service()
        
        genre = ctx.params.get("genre")
        
        # Se è già in riproduzione (cambio genere)
        if ctx.music_playing:
            if genre and genre.lower() != (ctx.music_genre or "").lower():
                normalized = music_service.normalize_genre(genre)
                if normalized:
                    session_store.start_music(ctx.session_id, normalized)
                    return ToolResult(
                        message=f"🎵 Cambio a {normalized}!",
                        ui_options=[],
                        commands=[{"type": "PLAY_MUSIC", "payload": {"genre": normalized, "url": f"/music/{normalized}"}}]
                    )
            
            return ToolResult(
                message=f"🎵 La musica è già accesa ({ctx.music_genre}). Vuoi cambiare genere?",
                ui_options=[
                    UIOption(id="change_music", label="Cambia genere"),
                    UIOption(id="music_ok", label="Va bene così")
                ],
                commands=[]
            )
        
        # Se non ha specificato genere, prova dalla KB
        if not genre:
            pref_genre = await neo4j_repo.get_music_preference(ctx.user_id)
            if pref_genre:
                genre = pref_genre
        
        # Se ancora non c'è genere, chiedi
        if not genre:
            session_store.update_session(ctx.session_id, pending_question="music_genre")
            return ToolResult(
                message="🎵 Che genere preferisci?",
                ui_options=[
                    UIOption(id="genre:Pop", label="Pop"),
                    UIOption(id="genre:Rock", label="Rock"),
                    UIOption(id="genre:Jazz", label="Jazz"),
                    UIOption(id="genre:Classica", label="Classica"),
                    UIOption(id="genre:HipHop", label="Hip Hop"),
                    UIOption(id="genre:Elettronica", label="Elettronica"),
                    UIOption(id="no_music", label="No grazie"),
                ],
                commands=[]
            )
        
        # Normalizza e avvia
        normalized = music_service.normalize_genre(genre)
        if not normalized:
            return ToolResult(
                message=f"Non conosco il genere '{genre}'. Prova con Pop, Rock, Jazz, Classica...",
                ui_options=[],
                commands=[]
            )
        
        session_store.start_music(ctx.session_id, normalized)
        return ToolResult(
            message=f"🎵 Avvio {normalized}! Buon ascolto!",
            ui_options=[],
            commands=[{"type": "PLAY_MUSIC", "payload": {"genre": normalized, "url": f"/music/{normalized}"}}]
        )


class MusicStopTool(Tool):
    """Ferma la musica."""
    def __init__(self):
        super().__init__(
            id="music_stop",
            name="Ferma Musica",
            description="Ferma la riproduzione musicale.",
            # PATTERN SPECIFICI - richiedono contesto musica
            patterns=[
                "ferma la musica", "spegni la musica", "stop musica",
                "basta musica", "togli la musica", "smetti con la musica",
            ],
            examples=["ferma la musica", "spegni la musica"],
            category="music"
        )
    
    def is_available(self, state: SessionState) -> bool:
        return state.music_playing
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        session_store = get_session_store()
        
        if not ctx.music_playing:
            return ToolResult(
                message="🔇 La musica è già spenta. Vuoi che la accenda?",
                ui_options=[
                    UIOption(id="ask_music", label="Sì, metti musica 🎵"),
                    UIOption(id="cancel", label="No grazie")
                ],
                commands=[]
            )
        
        session_store.stop_music(ctx.session_id)
        return ToolResult(
            message="🔇 Musica fermata. Se vuoi riascoltarla, dimmelo!",
            ui_options=[],
            commands=[{"type": "STOP_MUSIC", "payload": {}}]
        )


class MusicPauseTool(Tool):
    """Mette in pausa la musica."""
    def __init__(self):
        super().__init__(
            id="music_pause",
            name="Pausa Musica",
            description="Mette in pausa la musica senza fermarla completamente.",
            # PATTERN SPECIFICI
            patterns=["metti in pausa la musica", "pausa la musica"],
            examples=["metti in pausa la musica"],
            category="music"
        )
    
    def is_available(self, state: SessionState) -> bool:
        return state.music_playing and not state.music_paused
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        session_store = get_session_store()
        
        if not ctx.music_playing:
            return ToolResult(
                message="La musica non è in riproduzione.",
                ui_options=[],
                commands=[]
            )
        
        session_store.pause_music(ctx.session_id)
        return ToolResult(
            message="⏸️ Musica in pausa.",
            ui_options=[UIOption(id="resume_music", label="▶️ Riprendi")],
            commands=[{"type": "PAUSE_MUSIC", "payload": {}}]
        )


class MusicResumeTool(Tool):
    """Riprende la musica dalla pausa."""
    def __init__(self):
        super().__init__(
            id="music_resume",
            name="Riprendi Musica",
            description="Riprende la musica dalla pausa.",
            # PATTERN SPECIFICI
            patterns=["riprendi la musica", "continua la musica", "riavvia la musica"],
            examples=["riprendi la musica"],
            category="music"
        )
    
    def is_available(self, state: SessionState) -> bool:
        return state.music_playing and state.music_paused
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        session_store = get_session_store()
        
        if not ctx.music_playing:
            return ToolResult(
                message="Non c'è musica da riprendere. Vuoi che la metta?",
                ui_options=[
                    UIOption(id="ask_music", label="Sì, metti musica 🎵"),
                    UIOption(id="cancel", label="No grazie")
                ],
                commands=[]
            )
        
        session_store.resume_music(ctx.session_id)
        return ToolResult(
            message=f"▶️ Riprendo {ctx.music_genre}!",
            ui_options=[],
            commands=[{"type": "RESUME_MUSIC", "payload": {}}]
        )


class VolumeUpTool(Tool):
    """Alza il volume."""
    def __init__(self):
        super().__init__(
            id="volume_up",
            name="Alza Volume",
            description="Aumenta il volume della musica.",
            patterns=["alza il volume", "volume più alto", "alza volume", "più volume", "più forte", "non sento"],
            examples=["non sento bene", "puoi alzare?", "metti più forte"],
            category="music"
        )
    
    def is_available(self, state: SessionState) -> bool:
        return state.music_playing
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        session_store = get_session_store()
        new_vol = session_store.adjust_volume(ctx.session_id, 2)
        return ToolResult(
            message=f"🔊 Volume alzato a {new_vol}/10",
            ui_options=[],
            commands=[{"type": "SET_VOLUME", "payload": {"volume": new_vol}}]
        )


class VolumeDownTool(Tool):
    """Abbassa il volume."""
    def __init__(self):
        super().__init__(
            id="volume_down",
            name="Abbassa Volume",
            description="Diminuisce il volume della musica.",
            patterns=["abbassa il volume", "volume più basso", "abbassa volume", "meno volume", "più piano", "troppo forte", "troppo alto"],
            examples=["il volume è troppo forte", "puoi abbassare un po'?"],
            category="music"
        )
    
    def is_available(self, state: SessionState) -> bool:
        return state.music_playing
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        session_store = get_session_store()
        new_vol = session_store.adjust_volume(ctx.session_id, -2)
        return ToolResult(
            message=f"🔉 Volume abbassato a {new_vol}/10",
            ui_options=[],
            commands=[{"type": "SET_VOLUME", "payload": {"volume": new_vol}}]
        )


class VolumeSetTool(Tool):
    """Imposta volume specifico."""
    def __init__(self):
        super().__init__(
            id="volume_set",
            name="Imposta Volume",
            description="Imposta il volume a un livello specifico (1-10).",
            patterns=["volume a", "volume al", "metti il volume"],
            examples=["metti il volume a 5", "volume al 50%"],
            category="music"
        )
    
    def is_available(self, state: SessionState) -> bool:
        return state.music_playing
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        session_store = get_session_store()
        volume = ctx.params.get("volume", 5)
        new_vol = session_store.set_volume(ctx.session_id, volume)
        return ToolResult(
            message=f"🔊 Volume impostato a {new_vol}/10",
            ui_options=[],
            commands=[{"type": "SET_VOLUME", "payload": {"volume": new_vol}}]
        )


class ChangeGenreTool(Tool):
    """Cambia genere musicale."""
    def __init__(self):
        super().__init__(
            id="change_genre",
            name="Cambia Genere",
            description="Cambia il genere musicale.",
            patterns=[
                "cambia genere", "cambiare genere", "altro genere", "cambio genere",
                "altra musica", "canzone diversa",
                "metti jazz", "metti rock", "metti pop", "metti classica",
                "cambia in jazz", "cambia in rock", "passa a jazz", "passa a rock"
            ],
            examples=["non mi piace questa musica", "metti qualcos'altro", "metti jazz"],
            category="music"
        )
    
    def is_available(self, state: SessionState) -> bool:
        return state.music_playing
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        session_store = get_session_store()
        music_service = get_music_service()
        
        genre = ctx.params.get("genre")
        
        if genre:
            # Genere specificato: cambia direttamente
            normalized = music_service.normalize_genre(genre)
            if normalized:
                session_store.start_music(ctx.session_id, normalized)
                return ToolResult(
                    message=f"🎵 Cambio a {normalized}!",
                    ui_options=[],
                    commands=[{"type": "PLAY_MUSIC", "payload": {"genre": normalized, "url": f"/music/{normalized}"}}]
                )
        
        # Nessun genere: chiedi
        session_store.update_session(ctx.session_id, pending_question="music_genre")
        return ToolResult(
            message="🎵 Che genere preferisci?",
            ui_options=[
                UIOption(id="genre:Pop", label="Pop"),
                UIOption(id="genre:Rock", label="Rock"),
                UIOption(id="genre:Jazz", label="Jazz"),
                UIOption(id="genre:Classica", label="Classica"),
                UIOption(id="genre:HipHop", label="Hip Hop"),
                UIOption(id="genre:Elettronica", label="Elettronica"),
            ],
            commands=[]
        )


# =============================================================================
# ESPORTA TUTTI I MUSIC TOOLS
# =============================================================================

MUSIC_TOOLS = [
    MusicPlayTool(),
    MusicStopTool(),
    MusicPauseTool(),
    MusicResumeTool(),
    VolumeUpTool(),
    VolumeDownTool(),
    VolumeSetTool(),
    ChangeGenreTool(),
]
