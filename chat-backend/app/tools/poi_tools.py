"""
POI Tools per gestione cambio destinazione.

Include:
- NeedTool: richieste indirette per bisogno (ho fame, sete...)
- TagSearchTool: richieste specifiche (voglio hamburger)
- DirectPOITool: richieste dirette (portami al FastFood)
- HomeTool: richieste per tornare a casa
"""

import time
from dataclasses import dataclass, field
from app.schemas import SessionState, ToolContext, ToolResult, UIOption
from app.utils.logging import get_logger

logger = get_logger(__name__)


# Lazy imports
def get_session_store():
    from app.session_store import session_store
    return session_store

def get_neo4j_repo():
    from app.neo4j.repo import neo4j_repo
    return neo4j_repo



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
# TAG DISPONIBILI PER MATCHING
# =============================================================================

TAG_KEYWORDS = {
    # Cibo specifico
    "pizza": "pizza",
    "hamburger": "hamburger",
    "burger": "hamburger",
    "patatine": "patatine",
    "pollo": "pollo",
    "fritto": "fritto",
    "pane": "pane",
    "cornetto": "cornetti",
    "cornetti": "cornetti",
    "dolci": "dolci",
    "pesce": "pesce",
    "gelato": "gelato",
    "cucina": "ristorante",
    
    # Bevande specifiche
    "cocktail": "cocktail",
    "aperitivo": "aperitivo",
    "birra": "birra",
    "caffè": "caffè",
    "caffe": "caffè",
    "cappuccino": "cappuccino",
    "drink": "drink",
    "bere": "bere",
    "bevande": "bevande",
    
    # Intrattenimento/Cultura
    "cinema": "cinema",
    "film": "film",
    "museo": "museo",
    "mostra": "museo",
    "discoteca": "discoteca",
    "ballo": "ballo",
    
    # Sport/Fitness
    "palestra": "palestra",
    "allenarmi": "allenarmi",
    "allenarsi": "allenarsi",
    "fitness": "fitness",
    "gym": "gym",
    "allenamento": "allenamento",
    "calcio": "calcio",
    "partita": "partita",
    "stadio": "calcio",
    
    # Shopping/Acquisti
    "regalo": "regalo",
    "regali": "regalo",
    "libri": "libri",
    "libro": "libri",
    "scarpe": "scarpe",
    "mocassini": "scarpe",
    "sandali": "sandali",
    "sneakers": "sneakers",
    "vestiti": "vestiti",
    "abbigliamento": "abbigliamento",
    
    # Servizi
    "banca": "banca",
    "bancomat": "bancomat",
    "atm": "atm",
    "prelevare": "prelevare",
    "soldi": "soldi",
    "contanti": "contanti",
    
    # Salute
    "farmacia": "farmacia",
    "farmaci": "farmaci",
    "medicina": "medicina",
    "ospedale": "ospedale",
    
    # Alloggio
    "hotel": "hotel",
    "dormire": "dormire",
    "alloggio": "alloggio",
    
    # Spesa
    "spesa": "spesa",
    "supermarket": "spesa",
    "supermercato": "spesa",
    
    # Natura
    "parco": "parco",
    "giardino": "giardino",
    "passeggiata": "passeggiata",
}


# =============================================================================
# BISOGNI → PATTERNS
# =============================================================================

NEED_PATTERNS = {
    "Fame": [
        "ho fame", "fame", "mangiare", "pranzo", "cena", "colazione",
        "affamato", "ristorante", "pizzeria", "trattoria", "mangio",
        "voglio mangiare", "qualcosa da mangiare"
    ],
    "Sete": [
        "ho sete", "sete", "bere", "bar", "assetato", "qualcosa da bere",
        "voglio bere", "drink", "bevanda", "aperitivo", "cocktail"
    ],
    "Malessere": [
        "sto male", "farmacia", "malessere", "mal di", "stomaco",
        "testa", "medicina", "medicinale", "dottore", "non mi sento bene",
        "nausea", "febbre", "male", "dolore", "ospedale"
    ],
    "Divertimento": [
        "divertirmi", "svago", "annoiato", "mi annoio", "noia",
        "divertimento", "divertire", "passatempo"
    ],
    "Shopping": [
        "shopping", "comprare", "negozio", "acquisti", "spesa",
        "vestiti", "abbigliamento"
    ],
    "Cinema": [
        "cinema", "film", "vedere un film", "guardare un film", "multisala",
        "pellicola", "proiezione"
    ],
    "Fitness": [
        "palestra", "allenarmi", "allenarsi", "allenamento", "fitness",
        "gym", "esercizio", "pesi", "cardio", "sport"
    ],
    "Alloggio": [
        "dormire", "hotel", "alloggio", "pernottare", "letto",
        "stanza", "camera", "notte", "posto per dormire", "dove dormire"
    ],
    "Denaro": [
        "soldi", "prelevare", "bancomat", "atm", "banca", "contanti",
        "contante", "denaro", "prelievo", "ritirare"
    ],
    "Cultura": [
        "museo", "mostra", "arte", "cultura", "storia", "libreria"
    ],
    "Relax": [
        "relax", "rilassarmi", "passeggiata", "parco", "giardino",
        "verde", "natura", "aria aperta", "mare", "spiaggia"
    ],
}



def extract_tag(message: str) -> str | None:
    """Estrae un tag dalla frase utente."""
    text = message.lower()
    for keyword, tag in TAG_KEYWORDS.items():
        if keyword in text:
            return tag
    return None


def extract_need(message: str) -> str | None:
    """Estrae un bisogno dalla frase utente."""
    text = message.lower()
    for need, patterns in NEED_PATTERNS.items():
        for pattern in patterns:
            if pattern in text:
                return need
    return None


def extract_poi_name(message: str) -> str | None:
    """
    Estrae un possibile nome POI dalle richieste dirette.
    Pattern: "portami al/a/da [nome]", "vai al/a/da [nome]"
    Supporta anche preposizioni articolate con apostrofo (all', dall').
    """
    import re
    text = message.lower()
    
    # Pattern comuni
    patterns = [
        # Prepositions ending with vowel (usually followed by space)
        r"portami (?:al|alla|a|da|dal|dalla)\s+(.+)",
        r"vai (?:al|alla|a|da|dal|dalla)\s+(.+)",
        r"andiamo (?:al|alla|a|da|dal|dalla)\s+(.+)",
        r"fermati (?:al|alla|a|da|dal|dalla)\s+(.+)",
        # Prepositions ending with apostrophe (space optional)
        r"portami (?:all'|dall'|sull')\s*(.+)",
        r"vai (?:all'|dall'|sull')\s*(.+)",
        r"andiamo (?:all'|dall'|sull')\s*(.+)",
        r"fermati (?:all'|dall'|sull')\s*(.+)",
    ]
    
    for pattern in patterns:
        match = re.search(pattern, text)
        if match:
            return match.group(1).strip()
    
    return None


def _infer_need_from_context(message: str) -> str | None:
    """
    Inferisce il bisogno da parole chiave contestuali.
    Usato come fallback quando tag specifici non trovano risultati.
    
    Esempi:
    - "farina", "latte", "uova", "ingredienti" → Shopping (Supermercato)
    - "regalo", "compleanno" → Shopping
    - "cavo", "strumenti" → Shopping (Music Store)
    """
    text = message.lower()
    
    # Ingredienti/alimenti → Supermercato (Shopping)
    food_keywords = [
        "farina", "latte", "uova", "zucchero", "burro", "olio",
        "pasta", "riso", "pane", "frutta", "verdura", "carne",
        "dispensa", "ingredienti", "spesa", "supermercato"
    ]
    if any(kw in text for kw in food_keywords):
        return "Shopping"
    
    # Regali/occasioni → Shopping
    gift_keywords = [
        "regalo", "compleanno", "anniversario", "festa",
        "sorpresa", "dono", "presente"
    ]
    if any(kw in text for kw in gift_keywords):
        return "Shopping"
    
    # Attrezzatura/strumenti → Shopping
    equipment_keywords = [
        "cavo", "strumento", "strumenti", "attrezzatura",
        "console", "accessori", "equipaggiamento"
    ]
    if any(kw in text for kw in equipment_keywords):
        return "Shopping"
    
    return None


def is_home_request(message: str) -> bool:
    """
    Verifica se il messaggio è una richiesta di andare a casa.
    
    Pattern supportati:
    - "portami a casa"
    - "voglio andare a casa"
    - "torniamo a casa"
    - "a casa"
    - "casa mia"
    """
    text = message.lower().strip()
    
    home_patterns = [
        "a casa",
        "casa mia",
        "torno a casa",
        "torniamo a casa",
        "portami a casa",
        "vai a casa",
        "andiamo a casa",
        "voglio andare a casa",
        "verso casa",
    ]
    
    return any(pattern in text for pattern in home_patterns)


# =============================================================================
# POI TOOLS
# =============================================================================


class NeedTool(Tool):
    """Gestisce richieste basate su bisogni (ho fame, sete, ecc.)."""
    def __init__(self):
        super().__init__(
            id="poi_need",
            name="Bisogno",
            # IMPORTANTE: Questa descrizione viene usata nel prompt LLM!
            description="Gestisce bisogni: Fame, Sete, Salute, Svago, Shopping, Cinema, Fitness, Alloggio, Denaro, Cultura, Relax, Lavoro, Meccanico, Spesa.",
            patterns=[
                # Fame
                "ho fame", "fame", "mangiare", "pranzo", "cena", "colazione",
                "affamato", "ristorante", "pizzeria",
                # Sete
                "ho sete", "sete", "bere", "bar", "assetato",
                # Salute
                "sto male", "farmacia", "malessere", "mal di",
                # Svago/Divertimento
                "divertirmi", "svago", "annoiato", "mi annoio", "noia",
                # Shopping
                "shopping", "comprare", "negozio", "acquisti",
                # Spesa
                "spesa", "supermercato",
                # Cinema
                "cinema", "film",
                # Fitness
                "palestra", "allenarmi", "fitness",
                # Alloggio
                "hotel", "dormire", "alloggio",
                # Denaro
                "banca", "prelevare", "soldi", "bancomat",
                # Cultura
                "museo", "libreria",
                # Relax
                "parco", "passeggiata",
                # Lavoro
                "lavoro", "ufficio", "fabbrica", "lavorare",
                # Meccanico
                "meccanico", "officina", "auto guasta", "riparare auto",
            ],
            examples=["ho fame", "ho sete", "vedere un film", "allenarmi", "prelevare soldi", "dormire", "andare a lavoro", "cercare meccanico"],
            category="poi"
        )
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        start_time = time.perf_counter()
        session_store = get_session_store()
        neo4j_repo = get_neo4j_repo()
        
        logger.info(f"\n🔧 [NeedTool] Executing")
        logger.info(f"  Message: '{ctx.message}'")
        logger.info(f"  User: {ctx.user_id}")
        logger.info(f"  Params: {ctx.params}")
        
        # 1. SMART CHECK: Does the message contain a specific TAG?
        # Example: "voglio mangiare (Need) un hamburger (Tag)"
        # We should prioritize the Tag (Hamburger) over the Need (Generic Food)
        specific_tag = extract_tag(ctx.message)
        logger.info(f"  Extracted tag: {specific_tag}")
        
        if specific_tag:
            pois = await neo4j_repo.get_pois_by_tag(
                user_id=ctx.user_id,
                tag=specific_tag,
                limit=4
            )
            if pois:
                elapsed_ms = (time.perf_counter() - start_time) * 1000
                logger.info(f"  ✅ Found {len(pois)} POIs by tag in {elapsed_ms:.2f}ms")
                return _build_poi_response(pois, ctx.session_id, session_store, f"Tag: {specific_tag}")

        # 2. Extract need - PREFER LOCAL extraction over LLM
        # Local extraction is more reliable for specific needs like Fitness, Cinema, etc.
        local_need = extract_need(ctx.message) or _infer_need_from_context(ctx.message)
        llm_need = ctx.params.get("need") if ctx.params else None
        
        # Smart need selection:
        # - If local extraction found a specific need, use it
        # - If LLM returned a more specific need than local, use LLM
        # - Otherwise fallback chain
        specific_needs = {"Cinema", "Fitness", "Alloggio", "Denaro", "Cultura", "Relax", "Sete"}
        generic_needs = {"Svago", "Divertimento", "Shopping", "Fame"}
        
        if local_need and local_need in specific_needs:
            # Local found a specific need, prefer it
            need = local_need
            logger.info(f"  Using LOCAL need (specific): {need}")
        elif llm_need and llm_need in specific_needs:
            # LLM found a specific need, use it
            need = llm_need
            logger.info(f"  Using LLM need (specific): {need}")
        elif local_need:
            # Local found something, use it
            need = local_need
            logger.info(f"  Using LOCAL need: {need}")
        elif llm_need:
            # Fallback to LLM
            need = llm_need
            logger.info(f"  Using LLM need (fallback): {need}")
        else:
            need = None
        
        logger.info(f"  Final need: {need} (local={local_need}, llm={llm_need})")
        
        if not need:
            # Fallback if specific tag logic above didn't return anything generic
            elapsed_ms = (time.perf_counter() - start_time) * 1000
            logger.warning(f"  ⚠️ No need detected in {elapsed_ms:.2f}ms")
            
            return ToolResult(
                message="Non ho capito di cosa hai bisogno. Dimmi se hai fame, sete, o altro!",
                ui_options=[],
                commands=[]
            )
        
        # 3. Query KB for Need
        pois = await neo4j_repo.get_pois_by_need(
            user_id=ctx.user_id,
            need=need,
            limit=4
        )
        
        if not pois:
             # Last resort: if we had a tag but found nothing, try filtering by category maybe?
             # But for now, just return not found.
            return ToolResult(
                message="😕 Non ho trovato nulla nelle vicinanze per questo bisogno.",
                ui_options=[],
                commands=[]
            )
        
        return _build_poi_response(pois, ctx.session_id, session_store, need)


class TagSearchTool(Tool):
    """Gestisce richieste specifiche tramite tag (hamburger, pizza, ecc.)."""
    def __init__(self):
        super().__init__(
            id="poi_tag",
            name="Ricerca Tag",
            description="Cerca POI per tag specifico (hamburger, pizza, cocktail...).",
            # DYNAMIC PATTERNS: use all defined keywords as strict patterns
            patterns=list(TAG_KEYWORDS.keys()),
            examples=["voglio un hamburger", "mi va una pizza", "cerco un libro"],
            category="poi"
        )
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        session_store = get_session_store()
        neo4j_repo = get_neo4j_repo()
        
        # Priorità 1: usa il bisogno passato dall'LLM se presente
        need_from_llm = ctx.params.get("need")
        
        # Estrai tag
        tag = ctx.params.get("tag") or extract_tag(ctx.message)
        
        pois = []
        
        # Se abbiamo un tag, proviamo prima la ricerca per tag
        if tag:
            pois = await neo4j_repo.get_pois_by_tag(
                user_id=ctx.user_id,
                tag=tag,
                limit=4
            )
        
        # Fallback 1: usa il bisogno passato dall'LLM
        if not pois and need_from_llm:
            pois = await neo4j_repo.get_pois_by_need(
                user_id=ctx.user_id,
                need=need_from_llm,
                limit=4
            )
            if pois:
                return _build_poi_response(pois, ctx.session_id, session_store, need_from_llm)
        
        # Fallback 2: inferisci bisogno dal messaggio locale
        if not pois:
            need = extract_need(ctx.message) or _infer_need_from_context(ctx.message)
            if need:
                pois = await neo4j_repo.get_pois_by_need(
                    user_id=ctx.user_id,
                    need=need,
                    limit=4
                )
                if pois:
                    return _build_poi_response(pois, ctx.session_id, session_store, need)
        
        # Se abbiamo trovato POI con il tag
        if pois:
            return _build_poi_response(pois, ctx.session_id, session_store, tag or "Ricerca")
        
        # Nessun risultato - offri opzioni
        return ToolResult(
            message="😕 Non ho trovato esattamente quello che cerchi. Cosa ti serve?",
            ui_options=[
                UIOption(id="poi_need:Fame", label="🍽️ Cibo"),
                UIOption(id="poi_need:Sete", label="🍹 Bevande"),
                UIOption(id="poi_need:Malessere", label="💊 Sto male"),
                UIOption(id="poi_need:Divertimento", label="🎉 Divertimento"),
                UIOption(id="poi_need:Shopping", label="🛍️ Shopping"),
                UIOption(id="cancel", label="❌ Annulla"),
            ],
            commands=[]
        )


class DirectPOITool(Tool):
    """Gestisce richieste dirette per nome POI e richieste 'a casa'."""
    def __init__(self):
        super().__init__(
            id="poi_direct",
            name="Vai a POI",
            description="Porta direttamente a un POI specifico per nome, oppure a casa dell'utente.",
            patterns=[
                "portami", "vai", "andiamo", "fermati",
                "al fastfood", "alla pizzeria", "al bar", "al ristorante",
                "a casa", "casa mia", "torno a casa"  # Added home patterns
            ],
            examples=["portami al FastFood Express", "vai alla Pizzeria Da Mario", "portami a casa"],
            category="poi"
        )
    
    async def execute(self, ctx: ToolContext) -> ToolResult:
        start_time = time.perf_counter()
        neo4j_repo = get_neo4j_repo()
        session_store = get_session_store()
        
        logger.info(f"\n🔧 [DirectPOITool] Executing")
        logger.info(f"  Message: '{ctx.message}'")
        logger.info(f"  User: {ctx.user_id}")
        logger.info(f"  Params: {ctx.params}")
        
        # === CHECK HOME REQUEST FIRST ===
        if is_home_request(ctx.message):
            logger.info(f"  🏠 Home request detected!")
            
            # Cerca la casa dell'utente
            home = await neo4j_repo.get_user_home(ctx.user_id)
            
            if home:
                logger.info(f"  🏠 Found user home: {home.get('name')} (id_unity: {home.get('id_unity')})")
                session_store.update_session(ctx.session_id, last_poi_suggestions=[home["id"]])
                
                elapsed_ms = (time.perf_counter() - start_time) * 1000
                logger.info(f"  ✅ DirectPOITool completed in {elapsed_ms:.2f}ms")
                
                return ToolResult(
                    message="🏠 Ti porto a casa!",
                    ui_options=[
                        UIOption(id=f"poi:{home['id']}", label=f"🏠 {home['name']}"),
                        UIOption(id="cancel", label="❌ Annulla"),
                    ],
                    commands=[]
                )
            else:
                logger.warning(f"  ⚠️ No home found for user {ctx.user_id}")
                return ToolResult(
                    message="😕 Non ho trovato la tua casa nel sistema. Vuoi andare altrove?",
                    ui_options=[
                        UIOption(id="poi_need:Fame", label="🍽️ Cibo"),
                        UIOption(id="poi_need:Sete", label="🍹 Bevande"),
                        UIOption(id="cancel", label="❌ No grazie"),
                    ],
                    commands=[]
                )
        
        # Estrai nome POI
        poi_name = ctx.params.get("poi_name") or extract_poi_name(ctx.message)
        logger.info(f"  Extracted POI name: '{poi_name}'")
        
        if not poi_name:
            elapsed_ms = (time.perf_counter() - start_time) * 1000
            logger.info(f"  ⚠️ No POI name found, completed in {elapsed_ms:.2f}ms")
            return ToolResult(
                message="Dove vuoi andare? Dimmi il nome del posto!",
                ui_options=[],
                commands=[]
            )

        
        # Cerca POI per nome esatto
        poi = await neo4j_repo.find_poi_by_name(
            name=poi_name
        )
        
        if not poi:
            # Fallback 1: prova fuzzy search con singolo token
            tokens = poi_name.split()
            if len(tokens) > 1:
                longest = max(tokens, key=len)
                if len(longest) > 3:
                    poi = await neo4j_repo.find_poi_by_name(name=longest)
        
        if poi:
            # Trovato! Mostra conferma invece di navigare direttamente
            session_store.update_session(ctx.session_id, last_poi_suggestions=[poi["id"]])
            
            rating = poi.get("rating", 0)
            label = f"{poi['name']} ⭐{rating:.1f}" if rating else poi['name']
            
            return ToolResult(
                message=f"📍 Ho trovato questo posto:",
                ui_options=[
                    UIOption(id=f"poi:{poi['id']}", label=label),
                    UIOption(id="cancel", label="❌ Annulla"),
                ],
                commands=[]
            )
        
        # Fallback 2: prova ricerca per TAG con la parola chiave
        # Es: "partita" -> trova stadio con tag "partita"
        tag_to_search = poi_name.lower().strip()
        pois_by_tag = await neo4j_repo.get_pois_by_tag(
            user_id=ctx.user_id or "unknown",
            tag=tag_to_search,
            limit=4
        )
        
        if pois_by_tag:
            # Trovati POI con questo tag!
            session_store = get_session_store()
            return _build_poi_response(
                pois=pois_by_tag,
                session_id=ctx.session_id,
                session_store=session_store,
                context="Svago"  # Tag search is usually for activities
            )
        
        # Fallback 3: niente trovato, offri alternative
        return ToolResult(
            message=f"😕 Non ho trovato '{poi_name}'. Vuoi che ti mostri cosa c'è nelle vicinanze?",
            ui_options=[
                UIOption(id="poi_need:Fame", label="🍽️ Cibo"),
                UIOption(id="poi_need:Sete", label="🍹 Bevande"),
                UIOption(id="poi_need:Malessere", label="💊 Sto male"),
                UIOption(id="poi_need:Divertimento", label="🎉 Divertimento"),
                UIOption(id="poi_need:Shopping", label="🛍️ Shopping"),
                UIOption(id="cancel", label="❌ No grazie"),
            ],
            commands=[]
        )




# =============================================================================
# HELPER: Costruisce risposta con POI
# =============================================================================

def _build_poi_response(
    pois: list[dict],
    session_id: str,
    session_store,
    context: str
) -> ToolResult:
    """
    Costruisce messaggio compatto con POI suggeriti.
    Solo messaggio intro + bottoni con info (no lista testuale duplicata).
    """
    # Salva POI per selezione successiva
    poi_ids = [p["id"] for p in pois]
    session_store.update_session(session_id, last_poi_suggestions=poi_ids)
    
    # Emoji per contesto
    emoji_map = {
        "Fame": "🍕",
        "Sete": "🍺",
        "Malessere": "💊",
        "Svago": "🎉",
        "Divertimento": "🎉",
        "Shopping": "🛍️",
    }
    emoji = emoji_map.get(context, "📍")
    
    # Messaggio breve di intro (senza lista)
    message = f"{emoji} Ecco cosa ho trovato:"
    
    # Bottoni con info integrate (nome + rating + indicatori)
    ui_options = []
    
    for poi in pois:
        name = poi.get("name", "???")
        rating = poi.get("rating", 0)
        liked = poi.get("liked", False)
        visited = poi.get("visited", False)
        
        # Costruisci label con tutte le info
        label = name
        if rating:
            label += f" ⭐{rating:.1f}"
        if liked:
            label += " ❤️"
        elif visited:
            label += " 🔄"
        
        ui_options.append(UIOption(id=f"poi:{poi['id']}", label=label))
    
    ui_options.append(UIOption(id="cancel", label="❌ Annulla"))
    
    return ToolResult(
        message=message,
        ui_options=ui_options,
        commands=[]
    )


# =============================================================================
# ESPORTA TOOLS
# =============================================================================

POI_TOOLS = [
    DirectPOITool(),  # Priority 1: Direct requests
    NeedTool(),       # Priority 2: Needs
    TagSearchTool(),  # Priority 3: Tag Search
]
