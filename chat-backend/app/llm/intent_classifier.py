"""
Intent Classifier per il Taxi Backend.

Usa LLM per classificare l'intent dell'utente con opzioni vincolate.
Due modalità:
1. classify_need() - Mappa messaggio libero → bisogno (Fame, Sete, ecc.)
2. match_option() - Mappa risposta utente → opzione dalla lista
"""

import json
import re
import time
from typing import Any
from dataclasses import dataclass

import httpx

from app.config import get_settings
from app.utils.logging import get_logger
from app.utils.timing import log_llm_request, log_llm_response

logger = get_logger(__name__)


# =============================================================================
# RESULT TYPES
# =============================================================================

@dataclass
class ClassifyResult:
    """Risultato della classificazione bisogno."""
    need: str | None  # Fame, Sete, Malessere, Divertimento, Shopping, or None
    confidence: float  # 0.0 to 1.0
    subcategory: str | None = None  # colazione, pranzo, cena, etc.
    
    
@dataclass  
class MatchResult:
    """Risultato del matching opzioni."""
    selected_id: str | None  # ID dell'opzione selezionata o None
    action: str  # "select", "cancel", "unclear"


@dataclass
class ToolClassifyResult:
    """Risultato della classificazione tool context-aware."""
    tool_id: str | None  # ID del tool matchato
    params: dict[str, Any] | None = None  # Parametri estratti (genre, volume, etc.)
    confidence: float = 0.0
    
    @staticmethod
    def none() -> "ToolClassifyResult":
        return ToolClassifyResult(tool_id=None, confidence=0.0)


# =============================================================================
# PROMPTS
# =============================================================================

NEED_CLASSIFICATION_PROMPT = """Sei un classificatore di intenti. L'utente è un passeggero in un taxi autonomo.
Analizza il messaggio e determina se esprime un bisogno.

BISOGNI DISPONIBILI:
- Fame: voglia di mangiare (pranzo, cena, colazione, spuntino, ristorante, pizza, panino, torta, dolce)
- Sete: voglia di bere (bar, caffè, bevande, rinfrescarsi, caldo, aperitivo)
- Malessere: problemi di salute (farmacia, medicina, mal di testa, nausea, stare male)
- Divertimento: svago, intrattenimento (cinema, teatro, disco, stadio, giocare, noia)
- Shopping: acquisti, negozi (comprare, regalo, vestiti, scarpe, spesa, supermercato, ingredienti)

REGOLE:
- Se il messaggio esprime chiaramente un bisogno, restituisci il bisogno con alta confidence
- Se il messaggio è ambiguo ma potrebbe indicare un bisogno, restituisci con bassa confidence
- Se è un saluto, domanda generica o off-topic, restituisci need=null
- Per "Fame", identifica anche la subcategory: "colazione" (mattina), "pranzo" (mezzogiorno), "cena" (sera), "spuntino" (leggero)

ESEMPI COMPLESSI:
- "oggi è perfetto per una torta" -> need="Fame", subcategory="spuntino" (o Shopping se cerca pasticceria)
- "domani la mia ragazza fa il compleanno" -> need="Shopping" (regalo)
- "mi annoio a morte" -> need="Divertimento"
- "ho il frigo vuoto" -> need="Shopping" (spesa)

Rispondi SOLO con JSON valido, senza altro testo:
{{"need": "Fame|Sete|Malessere|Divertimento|Shopping|null", "confidence": 0.0-1.0, "subcategory": "colazione|pranzo|cena|spuntino|null"}}

Messaggio utente: "{message}"
"""

OPTION_MATCHING_PROMPT = """Sei un assistente che aiuta a capire quale opzione l'utente ha scelto.
L'utente deve scegliere tra queste opzioni:

{options_list}

Analizza il messaggio dell'utente e determina:
1. Se ha scelto un'opzione specifica (restituisci l'ID esatto)
2. Se vuole annullare/rifiutare (parole come "no", "niente", "annulla", "non voglio", "lascia stare")  
3. Se non hai capito cosa vuole

REGOLE:
- Cerca corrispondenze anche parziali nei nomi (es. "la terrazza" → "Ristorante La Terrazza")
- Se l'utente dice "il primo", "il secondo" ecc., mappa alla posizione nella lista
- Se l'utente esprime un nuovo bisogno diverso, considera come "unclear"

Rispondi SOLO con JSON valido, senza altro testo:
{{"selected_id": "ID_OPZIONE|cancel|null", "action": "select|cancel|unclear"}}

Messaggio utente: "{message}"
"""

CONVERSATIONAL_PROMPT = """Sei l'assistente vocale di un taxi autonomo. L'utente ti ha scritto qualcosa che NON rientra nei tuoi compiti.

⛔ NON SEI IN GRADO DI RISPONDERE A:
- Domande di cultura generale (capitali, storia, scienza, ecc.)
- Richieste di intrattenimento (barzellette, storie, giochi)
- Consigli personali o sentimentali
- Qualsiasi cosa non riguardi il viaggio in taxi

✅ PUOI AIUTARE CON:
- Musica durante il viaggio
- Trovare posti dove mangiare o bere
- Destinazioni e punti di interesse

ISTRUZIONI:
1. Riconosci gentilmente che l'utente ha chiesto qualcosa fuori dalle tue competenze
2. NON rispondere alla domanda
3. Ricorda cosa PUOI fare per lui durante il viaggio
4. Sii simpatico ma fermo

Messaggio utente: "{message}"

Rispondi in italiano, max 2 frasi. Usa emoji per essere amichevole.
"""


TOOL_CLASSIFICATION_PROMPT = """Sei l'assistente di un taxi autonomo. Analizza il messaggio e scegli il tool corretto.

CONTESTO CORRENTE: {context}

TOOL DISPONIBILI:
{tools}

MESSAGGIO UTENTE: "{message}"

=== REGOLE CRITICHE ===

1. NEGAZIONI: Se il messaggio contiene "non", "no", "niente", rispondi con tool_id "none"
   - "non ho fame" → {{"tool_id": "none"}}

2. MUSICA = ASCOLTARE durante il viaggio
   - "metti musica", "voglio ascoltare" → music_play
   - "ferma/spegni la musica" → music_stop

3. BISOGNI (poi_need): Mappa alle seguenti categorie:
   - CIBO: "fame", "mangiare", "pizzeria", "ristorante" → need="Fame"
   - BERE: "sete", "bere", "bar", "caffè", "drink" → need="Sete"
   - SPESA: "spesa", "supermercato", "latte", "pane" → need="Spesa"
   - SHOPPING: "regalo", "vestiti", "negozio", "comprare" → need="Shopping"
   - SALUTE: "sto male", "farmacia", "medicina", "ospedale" → need="Salute" (o "Malessere" se farmacia)
   - CINEMA: "vedere un film", "cinema", "andare al cinema" → need="Cinema"
   - FITNESS: "palestra", "allenarmi", "fitness", "sport" → need="Fitness"
   - SOLDI: "prelevare", "banca", "bancomat", "soldi", "contanti" → need="Denaro"
   - DORMIRE: "hotel", "dormire", "alloggio", "stanza" → need="Alloggio"
   - RELAX: "parco", "giardino", "passeggiata", "mare" → need="Relax"
   - CULTURA: "museo", "mostra", "arte", "libreria" → need="Cultura"
   - DIVERTIMENTO: "annoio", "divertirmi", "svago" → need="Divertimento"
   - LAVORO: "lavoro", "ufficio", "fabbrica", "andare a lavorare" → need="Lavoro"
   - MECCANICO: "meccanico", "auto guasta", "officina", "riparazione" → need="Meccanico"

4. RISPOSTE SPECIFICHE (poi_tag): Usa quando l'utente chiede un oggetto/cibo specifico
   - "voglio una pizza" → poi_tag con tag="pizza"
   - "voglio un hamburger" → poi_tag con tag="hamburger"

5. NAVIGAZIONE DIRETTA (poi_direct):
   - "portami al/alla [nome]" → poi_direct con poi_name

=== FORMATO RISPOSTA ===
Rispondi SOLO con JSON valido:
{{"tool_id": "...", "params": {{"...": "..."}}, "confidence": 0.0-1.0}}

ESEMPI:
- "ho fame" → {{"tool_id": "poi_need", "params": {{"need": "Fame"}}, "confidence": 0.95}}
- "devo prelevare soldi" → {{"tool_id": "poi_need", "params": {{"need": "Denaro"}}, "confidence": 0.95}}
- "voglio allenarmi" → {{"tool_id": "poi_need", "params": {{"need": "Fitness"}}, "confidence": 0.95}}
- "andiamo al cinema" → {{"tool_id": "poi_need", "params": {{"need": "Cinema"}}, "confidence": 0.95}}
- "cerco un hotel" → {{"tool_id": "poi_need", "params": {{"need": "Alloggio"}}, "confidence": 0.95}}
- "voglio bere qualcosa" → {{"tool_id": "poi_need", "params": {{"need": "Sete"}}, "confidence": 0.95}}
- "portami a casa" → {{"tool_id": "poi_direct", "params": {{"poi_name": "casa"}}, "confidence": 0.95}}
"""


# =============================================================================
# INTENT CLASSIFIER
# =============================================================================

class IntentClassifier:
    """
    Classifica l'intent dell'utente usando LLM con opzioni vincolate.
    
    Usa chiamate LLM leggere con prompt specifici per:
    - Classificare bisogni dal linguaggio naturale
    - Mappare risposte utente su opzioni disponibili
    """
    
    def __init__(self):
        self.settings = get_settings()
        self.timeout = 30.0
    
    async def classify_with_tools(
        self, 
        message: str,
        tools_prompt: str,
        context_info: str = ""
    ) -> ToolClassifyResult:
        """
        Classifica il messaggio usando LLM con lista tool disponibili.
        
        Args:
            message: Messaggio dell'utente
            tools_prompt: Stringa con tool disponibili (da tool_registry)
            context_info: Info sul contesto attuale (musica on, etc.)
            
        Returns:
            ToolClassifyResult con tool_id e params
        """
        prompt = TOOL_CLASSIFICATION_PROMPT.format(
            message=message,
            tools=tools_prompt,
            context=context_info or "Nessun contesto speciale"
        )
        
        logger.info(f"\n{'='*60}")
        logger.info(f"🔍 [TOOL_CLASSIFY] Starting classification")
        logger.info(f"{'='*60}")
        logger.info(f"  Message: '{message}'")
        logger.info(f"  Context: {context_info}")
        logger.info(f"  Tools prompt length: {len(tools_prompt)} chars")
        logger.debug(f"  Full tools prompt:\n{tools_prompt}")
        logger.info(f"  Total prompt length: {len(prompt)} chars (~{len(prompt)//4} tokens)")
        
        try:
            start_time = time.perf_counter()
            response = await self._call_llm(prompt)
            elapsed_ms = (time.perf_counter() - start_time) * 1000
            
            if elapsed_ms > 2000:
                logger.warning(f"  ⚠️ LLM call took {elapsed_ms:.2f}ms (SLOW!)")
            else:
                logger.info(f"  LLM call took {elapsed_ms:.2f}ms")
            
            logger.info(f"  LLM Response: {response[:200]}..." if len(response) > 200 else f"  LLM Response: {response}")
            
            # Parse JSON response
            import re
            # Cerca JSON completo nella risposta (gestisce anche params: {})
            # Trova la prima { e l'ultima } per estrarre JSON completo
            start_idx = response.find('{')
            end_idx = response.rfind('}')
            
            if start_idx != -1 and end_idx != -1 and end_idx > start_idx:
                json_str = response[start_idx:end_idx + 1]
                logger.info(f"[TOOL_CLASSIFY] Extracted JSON: {json_str}")
                data = json.loads(json_str)
                tool_id = data.get("tool_id")
                params = data.get("params", {})
                confidence = data.get("confidence", 0.8)
                
                logger.info(f"[TOOL_CLASSIFY] Parsed: tool_id={tool_id}, params={params}, confidence={confidence}")
                
                if tool_id and tool_id != "none":
                    logger.info(f"[TOOL_CLASSIFY] SUCCESS: matched tool {tool_id}")
                    return ToolClassifyResult(
                        tool_id=tool_id,
                        params=params,
                        confidence=confidence
                    )
                else:
                    logger.info(f"[TOOL_CLASSIFY] LLM returned 'none' or empty tool_id")
            else:
                logger.warning(f"[TOOL_CLASSIFY] No JSON found in response: {response}")
            
            return ToolClassifyResult.none()
            
        except Exception as e:
            logger.error(f"[TOOL_CLASSIFY] Error: {e}")
            return ToolClassifyResult.none()
        
    async def classify_need(
        self, 
        message: str,
        context: dict[str, Any] | None = None
    ) -> ClassifyResult:
        """
        Classifica se il messaggio esprime un bisogno.
        
        Args:
            message: Messaggio dell'utente
            context: Contesto opzionale (città, ora, ecc.)
            
        Returns:
            ClassifyResult con need, confidence, subcategory
        """
        prompt = NEED_CLASSIFICATION_PROMPT.format(message=message)
        
        try:
            response = await self._call_llm(prompt)
            result = self._parse_json_response(response)
            
            need = result.get("need")
            if need == "null" or need is None:
                need = None
            
            # Pulisci il need se il modello ha restituito valori spuri
            # Es: "Shopping|null" -> "Shopping"
            if need:
                need = self._clean_need_value(need)
                
            confidence = float(result.get("confidence", 0.0))
            
            subcategory = result.get("subcategory")
            if subcategory == "null" or subcategory is None:
                subcategory = None
            # Pulisci anche subcategory
            if subcategory:
                subcategory = self._clean_subcategory_value(subcategory)
            
            logger.info(f"Classified '{message[:30]}...' -> need={need}, conf={confidence:.2f}, sub={subcategory}")
            
            return ClassifyResult(
                need=need,
                confidence=confidence,
                subcategory=subcategory
            )
            
        except Exception as e:
            logger.error(f"Need classification failed: {e}")
            # Fallback: ritorna nessun bisogno
            return ClassifyResult(need=None, confidence=0.0, subcategory=None)
    
    async def match_option(
        self,
        message: str,
        options: list[dict[str, str]]
    ) -> MatchResult:
        """
        Mappa la risposta utente su una delle opzioni disponibili.
        
        Args:
            message: Messaggio dell'utente
            options: Lista di opzioni [{id: "poi:POI_001", label: "Nome POI"}, ...]
            
        Returns:
            MatchResult con selected_id e action
        """
        if not options:
            return MatchResult(selected_id=None, action="unclear")
        
        # Pre-check: ordinals/numbers/cancel for deterministic matching
        try:
            text_lower = message.lower()
            if re.search(r"\b(no|niente|annulla|non voglio|lascia stare)\b", text_lower):
                return MatchResult(selected_id=None, action="cancel")

            index = self._extract_option_index(text_lower, len(options))
            if index is not None:
                selected_id = options[index - 1].get("id")
                if selected_id:
                    return MatchResult(selected_id=selected_id, action="select")
        except Exception as e:
            logger.warning(f"Option pre-check failed: {e}")

        # Costruisci lista opzioni per il prompt
        options_list = "\n".join([
            f"- ID: {opt['id']} | Nome: {opt['label']}"
            for opt in options
        ])
        
        prompt = OPTION_MATCHING_PROMPT.format(
            options_list=options_list,
            message=message
        )
        
        try:
            response = await self._call_llm(prompt)
            result = self._parse_json_response(response)
            
            selected_id = result.get("selected_id")
            action = result.get("action", "unclear")
            
            # Normalizza
            if selected_id in ["null", None, ""]:
                selected_id = None
            if selected_id == "cancel":
                action = "cancel"
                selected_id = None
            
            # Estrai ID pulito se il modello ha restituito testo extra
            # Es: "ID: poi:POI_008|Nome: Stadio" -> "poi:POI_008"
            if selected_id and action == "select":
                import re
                # Cerca pattern poi:POI_XXX
                poi_match = re.search(r'poi:POI_\d+', selected_id)
                if poi_match:
                    selected_id = poi_match.group()
                else:
                    # Cerca solo POI_XXX e aggiungi prefix
                    poi_match = re.search(r'POI_\d+', selected_id)
                    if poi_match:
                        selected_id = f"poi:{poi_match.group()}"
                    else:
                        # Verifica se c'è un match con le opzioni disponibili
                        selected_id = self._fuzzy_match_option(selected_id, options)
                
            logger.info(f"Matched '{message[:30]}...' -> id={selected_id}, action={action}")
            
            return MatchResult(
                selected_id=selected_id,
                action=action
            )
            
        except Exception as e:
            logger.error(f"Option matching failed: {e}")
            return MatchResult(selected_id=None, action="unclear")

    def _extract_option_index(self, text: str, options_count: int) -> int | None:
        """Estrae l'indice (1-based) di un'opzione da numeri o ordinali nel testo."""
        if options_count <= 0:
            return None

        ordinal_map = {
            "primo": 1, "prima": 1,
            "secondo": 2, "seconda": 2,
            "terzo": 3, "terza": 3,
            "quarto": 4, "quarta": 4,
            "quinto": 5, "quinta": 5,
            "sesto": 6, "sesta": 6,
            "settimo": 7, "settima": 7,
            "ottavo": 8, "ottava": 8,
            "nono": 9, "nona": 9,
            "decimo": 10, "decima": 10,
        }

        for word, idx in ordinal_map.items():
            if re.search(rf"\b{word}\b", text):
                if 1 <= idx <= options_count:
                    return idx

        # Pattern "numero 2", "n. 2"
        match = re.search(r"\b(?:numero|n\.?|n°|nº)\s*(\d+)\b", text)
        if match:
            idx = int(match.group(1))
            if 1 <= idx <= options_count:
                return idx

        # Bare number
        match = re.search(r"\b(\d+)\b", text)
        if match:
            idx = int(match.group(1))
            if 1 <= idx <= options_count:
                return idx

        return None
    
    def _fuzzy_match_option(self, text: str, options: list[dict[str, str]]) -> str | None:
        """Cerca un match fuzzy tra il testo e le opzioni disponibili."""
        text_lower = text.lower()
        for opt in options:
            if opt["label"].lower() in text_lower or text_lower in opt["label"].lower():
                return opt["id"]
        return None
    
    def _clean_need_value(self, need: str) -> str | None:
        """
        Pulisce il valore del need se il modello ha restituito valori spuri.
        Es: "Shopping|null" -> "Shopping", "Fame|Sete" -> "Fame"
        """
        # Bisogni validi
        valid_needs = {"Fame", "Sete", "Malessere", "Divertimento", "Shopping"}
        
        # Se è già valido, ritorna
        if need in valid_needs:
            return need
        
        # Prova a estrarre un bisogno valido
        for valid in valid_needs:
            if valid.lower() in need.lower():
                return valid
        
        # Se contiene |, prendi il primo valore
        if "|" in need:
            first = need.split("|")[0].strip()
            if first in valid_needs:
                return first
            # Cerca match parziale
            for valid in valid_needs:
                if valid.lower() in first.lower():
                    return valid
        
        return None
    
    def _clean_subcategory_value(self, subcategory: str) -> str | None:
        """
        Pulisce il valore della subcategory.
        Es: "colazione|pranzo|cena" -> "colazione"
        """
        valid_subcategories = {"colazione", "pranzo", "cena", "spuntino"}
        
        # Se è già valido, ritorna
        if subcategory.lower() in valid_subcategories:
            return subcategory.lower()
        
        # Se contiene |, prendi il primo valore valido
        if "|" in subcategory:
            for part in subcategory.split("|"):
                part = part.strip().lower()
                if part in valid_subcategories:
                    return part
        
        # Cerca match parziale
        for valid in valid_subcategories:
            if valid in subcategory.lower():
                return valid
        
        return None
    
    async def get_conversational_response(self, message: str) -> str:
        """
        Genera una risposta conversazionale per messaggi off-topic.
        
        Args:
            message: Messaggio dell'utente
            
        Returns:
            Risposta testuale
        """
        prompt = CONVERSATIONAL_PROMPT.format(message=message)
        
        try:
            response = await self._call_llm(prompt)
            return response.strip()
        except Exception as e:
            logger.error(f"Conversational response failed: {e}")
            return "Come posso aiutarti? Dimmi se hai fame, sete, o se vuoi visitare qualche posto!"
    
    async def _call_llm(self, prompt: str) -> str:
        """Chiama l'LLM con il prompt dato."""
        settings = self.settings
        
        if settings.llm_provider == "openrouter" and settings.openrouter_api_key:
            return await self._call_openrouter(prompt)
        else:
            return await self._call_ollama(prompt)
    
    async def _call_ollama(self, prompt: str) -> str:
        """Chiama Ollama."""
        url = f"{self.settings.ollama_base_url}/api/generate"
        model = self.settings.ollama_model
        
        logger.debug(f"🤖 [OLLAMA] Calling model: {model}")
        logger.debug(f"🤖 [OLLAMA] Prompt length: {len(prompt)} chars")
        
        payload = {
            "model": model,
            "prompt": prompt,
            "stream": False,
            "options": {
                "temperature": 0.3,  # Bassa per risposte più deterministiche
                "num_predict": 200
            }
        }
        
        start_time = time.perf_counter()
        async with httpx.AsyncClient(timeout=self.timeout) as client:
            response = await client.post(url, json=payload)
            response.raise_for_status()
            result = response.json().get("response", "")
        
        elapsed_ms = (time.perf_counter() - start_time) * 1000
        logger.debug(f"🤖 [OLLAMA] Response in {elapsed_ms:.2f}ms, length: {len(result)} chars")
        
        return result
    
    async def _call_openrouter(self, prompt: str) -> str:
        """Chiama OpenRouter con retry per rate limiting."""
        import asyncio
        
        url = f"{self.settings.openrouter_base_url}/chat/completions"
        model = self.settings.openrouter_model
        
        logger.debug(f"🤖 [OPENROUTER] Calling model: {model}")
        logger.debug(f"🤖 [OPENROUTER] Prompt length: {len(prompt)} chars")
        
        headers = {
            "Authorization": f"Bearer {self.settings.openrouter_api_key}",
            "Content-Type": "application/json",
            "HTTP-Referer": "http://localhost:8000",
            "X-Title": "Taxi Backend"
        }
        
        payload = {
            "model": model,
            "messages": [{"role": "user", "content": prompt}],
            "temperature": 0.3,
            "max_tokens": 200
        }
        
        max_retries = 5
        total_start = time.perf_counter()
        
        for attempt in range(max_retries):
            start_time = time.perf_counter()
            async with httpx.AsyncClient(timeout=self.timeout) as client:
                response = await client.post(url, json=payload, headers=headers)
                elapsed_ms = (time.perf_counter() - start_time) * 1000
                
                if response.status_code == 429:
                    # Exponential backoff with longer waits for free tier
                    wait_time = (3 ** attempt) + 2  # 3s, 5s, 11s, 29s, 83s
                    logger.warning(f"🤖 [OPENROUTER] Rate limited after {elapsed_ms:.2f}ms, waiting {wait_time}s (attempt {attempt + 1}/{max_retries})")
                    await asyncio.sleep(wait_time)
                    continue
                
                response.raise_for_status()
                choices = response.json().get("choices", [])
                if choices:
                    result = choices[0].get("message", {}).get("content", "")
                    total_elapsed = (time.perf_counter() - total_start) * 1000
                    logger.debug(f"🤖 [OPENROUTER] Response in {total_elapsed:.2f}ms, length: {len(result)} chars")
                    return result
                return ""
        
        raise Exception("OpenRouter rate limit exceeded")
    
    def _parse_json_response(self, response: str) -> dict[str, Any]:
        """Estrae JSON dalla risposta LLM."""
        # Rimuovi eventuali markdown code blocks
        response = response.strip()
        if response.startswith("```"):
            lines = response.split("\n")
            response = "\n".join(lines[1:-1])
        
        # Cerca JSON nella risposta
        try:
            return json.loads(response)
        except json.JSONDecodeError:
            # Prova a trovare JSON embedded
            import re
            json_match = re.search(r'\{[^}]+\}', response)
            if json_match:
                try:
                    return json.loads(json_match.group())
                except json.JSONDecodeError:
                    pass
            
            logger.warning(f"Could not parse JSON from: {response[:100]}")
            return {}


# Singleton instance
intent_classifier = IntentClassifier()
