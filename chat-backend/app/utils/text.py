"""
Utilità per processamento testo.

Include:
- Negation detection
- Text cleaning
- Simple confirmation detection
"""

import re
from typing import NamedTuple


class TextAnalysis(NamedTuple):
    """Risultato dell'analisi del testo."""
    has_negation: bool
    is_simple_confirmation: bool
    is_simple_rejection: bool
    cleaned_text: str


# Pattern per negazioni italiane
NEGATION_PATTERNS = [
    r"\bnon\s+",         # "non voglio", "non ho"
    r"\bniente\s+",      # "niente musica"
    r"\bno\b(?!\s+grazie)",  # "no" ma non "no grazie" (che è rifiuto gentile)
    r"\bmai\b",          # "mai"
    r"\bsenza\s+",       # "senza musica"
    r"\bnon\b",          # "non" anche senza spazio dopo
]

# Conferme semplici - risposte brevi e positive
SIMPLE_CONFIRMATIONS = [
    "ok", "okay", "sì", "si", "va bene", "perfetto", "grazie", 
    "d'accordo", "capito", "certo", "esatto", "giusto",
    "benissimo", "ottimo", "fantastico", "eccellente"
]

# Rifiuti semplici - risposte brevi e negative
SIMPLE_REJECTIONS = [
    "no", "no grazie", "niente", "lascia stare", "annulla",
    "non importa", "non serve", "lascia perdere", "basta così"
]


def analyze_text(text: str) -> TextAnalysis:
    """
    Analizza il testo per negazioni, conferme e rifiuti.
    
    Args:
        text: Testo da analizzare
        
    Returns:
        TextAnalysis con tutti i flag
    """
    text_lower = text.lower().strip()
    words = text_lower.split()
    
    # Rileva negazione
    has_negation = _detect_negation(text_lower)
    
    # Rileva conferma semplice (max 3 parole, SOLO parole di conferma con word boundary)
    # Usa word boundary per evitare false match come "mu-SI-ca"
    is_simple_confirmation = False
    if len(words) <= 3 and not has_negation:
        for conf in SIMPLE_CONFIRMATIONS:
            # Usa word boundary per match esatto
            if re.search(rf'\b{re.escape(conf)}\b', text_lower):
                # Verifica che la frase sia PRINCIPALMENTE una conferma
                # Evita frasi come "ferma la musica" che contengono "la" ma non sono conferme
                is_simple_confirmation = True
                break
    
    # Verifica aggiuntiva: se ci sono verbi di azione, NON è una conferma
    action_indicators = ["metti", "ferma", "cambia", "vai", "portami", "voglio", "cerco", "trova"]
    if is_simple_confirmation:
        for action in action_indicators:
            if action in text_lower:
                is_simple_confirmation = False
                break
    
    # Rileva rifiuto semplice
    is_simple_rejection = False
    if len(words) <= 5:
        for rej in SIMPLE_REJECTIONS:
            if re.search(rf'\b{re.escape(rej)}\b', text_lower):
                is_simple_rejection = True
                break
    
    # Testo pulito (rimuove punteggiatura eccessiva)
    cleaned_text = re.sub(r'[!?.,;:]+', ' ', text).strip()
    cleaned_text = re.sub(r'\s+', ' ', cleaned_text)
    
    return TextAnalysis(
        has_negation=has_negation,
        is_simple_confirmation=is_simple_confirmation,
        is_simple_rejection=is_simple_rejection,
        cleaned_text=cleaned_text
    )


def _detect_negation(text: str) -> bool:
    """Rileva se il testo contiene una negazione."""
    for pattern in NEGATION_PATTERNS:
        if re.search(pattern, text, re.IGNORECASE):
            return True
    return False


def is_greeting(text: str) -> bool:
    """Rileva se il testo è un saluto."""
    greetings = [
        "ciao", "salve", "buongiorno", "buonasera", "buonanotte",
        "hey", "ehi", "hello", "hi"
    ]
    text_lower = text.lower().strip()
    words = text_lower.split()
    
    # Saluto puro (1-3 parole, contiene saluto)
    return len(words) <= 3 and any(g in text_lower for g in greetings)


def is_help_request(text: str) -> bool:
    """Rileva se l'utente chiede aiuto/capabilities."""
    help_patterns = [
        "aiuto", "help", "cosa puoi fare", "cosa sai fare",
        "come funziona", "che puoi fare", "cosa fai",
        "quali sono le opzioni", "menu", "comandi"
    ]
    text_lower = text.lower()
    return any(p in text_lower for p in help_patterns)


def extract_number(text: str) -> int | None:
    """Estrae un numero dal testo (per volume, ecc.)."""
    # Pattern: "volume a 8", "metti a 5", "al 7"
    match = re.search(r'\b(\d+)\b', text)
    if match:
        num = int(match.group(1))
        # Se è percentuale (>10), converti
        if num > 10:
            return max(1, min(10, round(num / 10)))
        return max(1, min(10, num))
    return None
