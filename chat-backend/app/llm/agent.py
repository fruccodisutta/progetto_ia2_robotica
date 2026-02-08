"""
Agente LLM per il taxi autonomo.

Implementa client Ollama con tool-calling e fallback rule-based.
"""

import json
from abc import ABC, abstractmethod
from typing import Any

import httpx

from app.config import get_settings
from app.llm.tools import get_tools, execute_tool
from app.schemas import Command, CommandType, UIOption, AssistantResponse
from app.session_store import session_store
from app.utils.logging import get_logger

logger = get_logger(__name__)


# =============================================================================
# INTERFACCIA ASTRATTA
# =============================================================================

class LLMClient(ABC):
    """Interfaccia astratta per client LLM."""
    
    @abstractmethod
    async def chat(
        self,
        session_id: str,
        user_message: str,
        context: dict[str, Any]
    ) -> dict[str, Any]:
        """
        Processa un messaggio utente e genera una risposta.
        
        Args:
            session_id: ID sessione
            user_message: Messaggio dell'utente
            context: Contesto aggiuntivo (user_id, city, taxi_pos, ecc.)
            
        Returns:
            Dict con message, ui_options, commands
        """
        pass


# =============================================================================
# CLIENT OLLAMA
# =============================================================================

class OllamaClient(LLMClient):
    """Client per Ollama con supporto tool-calling."""
    
    def __init__(self):
        """Inizializza il client Ollama."""
        self.settings = get_settings()
        self.base_url = self.settings.ollama_base_url
        self.model = self.settings.ollama_model
        self.timeout = 60.0
        
    async def chat(
        self,
        session_id: str,
        user_message: str,
        context: dict[str, Any]
    ) -> dict[str, Any]:
        """Processa messaggio con Ollama."""
        session = session_store.get_session(session_id)
        
        # Costruisci messaggi
        messages = self._build_messages(session, user_message, context)
        
        logger.info(f"Calling Ollama with message: {user_message[:50]}...")
        
        try:
            # Prima chiamata LLM
            response = await self._call_ollama(messages)
            logger.info(f"Ollama response received: {str(response)[:200]}...")
            
            # Gestisci tool calls se presenti
            tool_calls = response.get("message", {}).get("tool_calls", [])
            tool_results: list[dict[str, Any]] = []
            
            if tool_calls:
                logger.info(f"Processing {len(tool_calls)} tool calls")
                # Esegui tools e raccogli risultati
                tool_results = await self._execute_tool_calls(tool_calls)
                
                # Aggiungi risultati ai messaggi
                messages.append(response["message"])
                for tool_call, result in zip(tool_calls, tool_results):
                    messages.append({
                        "role": "tool",
                        "content": json.dumps(result, ensure_ascii=False)
                    })
                
                # Seconda chiamata per risposta finale
                response = await self._call_ollama(messages)
            
            # Estrai risposta
            content = response.get("message", {}).get("content", "")
            logger.info(f"Final content: {content[:100]}...")
            
            # Parse risposta strutturata
            result = self._parse_response(content, tool_results)
            
            # Aggiorna history
            session_store.add_to_history(session_id, "user", user_message)
            session_store.add_to_history(session_id, "assistant", result["message"])
            
            return result
            
        except Exception as e:
            logger.error(f"Ollama error: {e}", exc_info=True)
            # Fallback rule-based
            return await self._fallback_response(user_message, context)
    
    def _build_messages(
        self,
        session: Any,
        user_message: str,
        context: dict[str, Any]
    ) -> list[dict[str, Any]]:
        """Costruisce la lista di messaggi per l'LLM."""
        messages: list[dict[str, Any]] = []
        
        # Aggiungi contesto
        context_msg = f"""Contesto corrente:
- user_id: {context.get('user_id', 'sconosciuto')}
- session_id: {context.get('session_id', '')}
- city: {context.get('city', 'sconosciuta')}
- taxi_position: ({context.get('taxi_x', 0)}, {context.get('taxi_y', 0)})
- mode: {session.mode.value if hasattr(session.mode, 'value') else session.mode}"""
        
        if session.pending_question:
            context_msg += f"\n- pending_question: {session.pending_question}"
        
        messages.append({"role": "system", "content": context_msg})
        
        # Aggiungi history recente
        for turn in session.history[-6:]:  # Ultimi 3 turni
            messages.append({
                "role": turn["role"],
                "content": turn["content"]
            })
        
        # Messaggio utente corrente
        messages.append({"role": "user", "content": user_message})
        
        return messages
    
    async def _call_ollama(
        self,
        messages: list[dict[str, Any]]
    ) -> dict[str, Any]:
        """Chiama l'API Ollama."""
        url = f"{self.base_url}/api/chat"
        
        payload = {
            "model": self.model,
            "messages": messages,
            "stream": False,
            "tools": get_tools(),
            "options": {
                "temperature": 0.7,
                "num_predict": 500
            }
        }
        
        async with httpx.AsyncClient(timeout=self.timeout) as client:
            response = await client.post(url, json=payload)
            response.raise_for_status()
            return response.json()
    
    async def _execute_tool_calls(
        self,
        tool_calls: list[dict[str, Any]]
    ) -> list[dict[str, Any]]:
        """Esegue le chiamate ai tools."""
        results = []
        
        for call in tool_calls:
            func = call.get("function", {})
            name = func.get("name", "")
            
            # Parse arguments
            args = func.get("arguments", {})
            if isinstance(args, str):
                try:
                    args = json.loads(args)
                except json.JSONDecodeError:
                    args = {}
            
            result = await execute_tool(name, args)
            results.append(result)
        
        return results
    
    def _parse_response(
        self,
        content: str,
        tool_results: list[dict[str, Any]]
    ) -> dict[str, Any]:
        """Parse la risposta LLM ed estrae strutture."""
        message = content
        ui_options: list[dict[str, str]] = []
        commands: list[dict[str, Any]] = []
        
        # Estrai comandi dai tool results
        for result in tool_results:
            if "command" in result:
                commands.append(result["command"])
            
            # Genera ui_options per POI
            if "pois" in result:
                pois = result["pois"]
                for poi in pois:
                    ui_options.append({
                        "id": f"poi:{poi['poi_id']}",
                        "label": poi["name"]
                    })
                # Aggiungi opzione cancel
                ui_options.append({
                    "id": "cancel",
                    "label": "No, continua"
                })
        
        return {
            "message": message,
            "ui_options": ui_options,
            "commands": commands
        }
    
    async def _fallback_response(
        self,
        user_message: str,
        context: dict[str, Any]
    ) -> dict[str, Any]:
        """Risposta fallback quando LLM non disponibile."""
        logger.warning("Using fallback rule-based response")
        
        msg_lower = user_message.lower()
        
        # Rileva bisogni base
        if any(w in msg_lower for w in ["fame", "mangiare", "affamato"]):
            return await self._fallback_need_response("Fame", context)
        
        if any(w in msg_lower for w in ["sete", "bere", "assetato"]):
            return await self._fallback_need_response("Sete", context)
        
        if any(w in msg_lower for w in ["male", "sto male", "farmacia"]):
            return await self._fallback_need_response("Malessere", context)
        
        # Risposta generica
        return {
            "message": "Mi scusi, non sono riuscito a elaborare la richiesta. Può ripetere?",
            "ui_options": [],
            "commands": []
        }
    
    async def _fallback_need_response(
        self,
        need: str,
        context: dict[str, Any]
    ) -> dict[str, Any]:
        """Genera risposta fallback per un bisogno."""
        from app.llm.tools import tool_recommend_pois
        
        result = await tool_recommend_pois(
            user_id=context.get("user_id", "unknown"),
            need=need
        )
        
        pois = result.get("pois", [])
        
        if not pois:
            return {
                "message": f"Mi dispiace, non ho trovato luoghi per {need} nelle vicinanze.",
                "ui_options": [],
                "commands": []
            }
        
        # Salva POI IDs nella sessione per validazione futura
        poi_ids = [poi['poi_id'] for poi in pois]
        session_id = context.get("session_id", "")
        if session_id:
            session_store.update_session(
                session_id,
                last_poi_suggestions=poi_ids
            )
        
        # Messaggio breve + bottoni con info
        message = "🍕 Ecco cosa ho trovato:"
        ui_options = []
        
        for poi in pois:
            rating = poi.get('rating', 0)
            label = f"{poi['name']} ({rating:.1f}⭐)" if rating else poi['name']
            ui_options.append({
                "id": f"poi:{poi['poi_id']}",
                "label": label
            })
        
        ui_options.append({"id": "cancel", "label": "❌ Annulla"})
        
        return {
            "message": message,
            "ui_options": ui_options,
            "commands": []
        }


# =============================================================================
# CLIENT OPENROUTER
# =============================================================================

class OpenRouterClient(LLMClient):
    """Client per OpenRouter API (OpenAI-compatible)."""
    
    def __init__(self):
        """Inizializza il client OpenRouter."""
        self.settings = get_settings()
        self.base_url = self.settings.openrouter_base_url
        self.model = self.settings.openrouter_model
        self.api_key = self.settings.openrouter_api_key
        self.timeout = 90.0
        
    async def chat(
        self,
        session_id: str,
        user_message: str,
        context: dict[str, Any]
    ) -> dict[str, Any]:
        """Processa messaggio con OpenRouter."""
        session = session_store.get_session(session_id)
        
        # Costruisci messaggi (stessa logica di Ollama)
        messages = self._build_messages(session, user_message, context)
        
        logger.info(f"Calling OpenRouter ({self.model}) with message: {user_message[:50]}...")
        
        try:
            # Prima chiamata LLM
            response = await self._call_openrouter(messages)
            logger.info(f"OpenRouter response received")
            
            # Gestisci tool calls se presenti
            message = response.get("choices", [{}])[0].get("message", {})
            tool_calls = message.get("tool_calls", [])
            tool_results: list[dict[str, Any]] = []
            
            if tool_calls:
                logger.info(f"Processing {len(tool_calls)} tool calls")
                tool_results = await self._execute_tool_calls(tool_calls)
                
                # Aggiungi risultati ai messaggi
                messages.append(message)
                for tool_call, result in zip(tool_calls, tool_results):
                    messages.append({
                        "role": "tool",
                        "tool_call_id": tool_call.get("id", ""),
                        "content": json.dumps(result, ensure_ascii=False)
                    })
                
                # Seconda chiamata per risposta finale
                response = await self._call_openrouter(messages)
                message = response.get("choices", [{}])[0].get("message", {})
            
            # Estrai risposta
            content = message.get("content", "")
            logger.info(f"Final content: {content[:100]}...")
            
            # Parse risposta strutturata
            result = self._parse_response(content, tool_results)
            
            # Aggiorna history
            session_store.add_to_history(session_id, "user", user_message)
            session_store.add_to_history(session_id, "assistant", result["message"])
            
            return result
            
        except Exception as e:
            logger.error(f"OpenRouter error: {e}", exc_info=True)
            # Fallback rule-based
            return await self._fallback_response(user_message, context)
    
    def _build_messages(
        self,
        session: Any,
        user_message: str,
        context: dict[str, Any]
    ) -> list[dict[str, Any]]:
        """Costruisce la lista di messaggi per l'LLM."""
        messages: list[dict[str, Any]] = []
        
        # Aggiungi contesto
        context_msg = f"""Contesto corrente:
- user_id: {context.get('user_id', 'sconosciuto')}
- session_id: {context.get('session_id', '')}
- city: {context.get('city', 'sconosciuta')}
- taxi_position: ({context.get('taxi_x', 0)}, {context.get('taxi_y', 0)})
- mode: {session.mode.value if hasattr(session.mode, 'value') else session.mode}"""
        
        if session.pending_question:
            context_msg += f"\n- pending_question: {session.pending_question}"
        
        messages.append({"role": "system", "content": context_msg})
        
        # Aggiungi history recente
        for turn in session.history[-6:]:
            messages.append({
                "role": turn["role"],
                "content": turn["content"]
            })
        
        # Messaggio utente corrente
        messages.append({"role": "user", "content": user_message})
        
        return messages
    
    async def _call_openrouter(
        self,
        messages: list[dict[str, Any]],
        max_retries: int = 3
    ) -> dict[str, Any]:
        """Chiama l'API OpenRouter con retry per rate limiting."""
        import asyncio
        
        url = f"{self.base_url}/chat/completions"
        
        headers = {
            "Authorization": f"Bearer {self.api_key}",
            "Content-Type": "application/json",
            "HTTP-Referer": "http://localhost:8000",
            "X-Title": "Taxi Backend"
        }
        
        # Converti tools al formato OpenAI
        tools = get_tools()
        
        payload = {
            "model": self.model,
            "messages": messages,
            "tools": tools,
            "temperature": 0.7,
            "max_tokens": 1000
        }
        
        for attempt in range(max_retries):
            async with httpx.AsyncClient(timeout=self.timeout) as client:
                response = await client.post(url, json=payload, headers=headers)
                
                if response.status_code == 429:
                    # Rate limited - wait and retry
                    wait_time = (2 ** attempt) + 1  # 2s, 3s, 5s
                    logger.warning(f"Rate limited (429), waiting {wait_time}s before retry {attempt + 1}/{max_retries}")
                    await asyncio.sleep(wait_time)
                    continue
                
                response.raise_for_status()
                return response.json()
        
        # All retries failed
        raise Exception(f"OpenRouter rate limit exceeded after {max_retries} retries")
    
    async def _execute_tool_calls(
        self,
        tool_calls: list[dict[str, Any]]
    ) -> list[dict[str, Any]]:
        """Esegue le chiamate ai tools."""
        results = []
        
        for call in tool_calls:
            func = call.get("function", {})
            name = func.get("name", "")
            
            # Parse arguments
            args = func.get("arguments", {})
            if isinstance(args, str):
                try:
                    args = json.loads(args)
                except json.JSONDecodeError:
                    args = {}
            
            result = await execute_tool(name, args)
            results.append(result)
        
        return results
    
    def _parse_response(
        self,
        content: str,
        tool_results: list[dict[str, Any]]
    ) -> dict[str, Any]:
        """Parse la risposta dell'LLM - usa stessa logica di OllamaClient."""
        ui_options = []
        commands = []
        
        # Estrai comandi dai risultati dei tools
        for result in tool_results:
            if result.get("command"):
                commands.append(result["command"])
            
            # Estrai ui_options se ci sono POI
            if result.get("pois"):
                for poi in result["pois"]:
                    ui_options.append({
                        "id": f"poi:{poi['poi_id']}",
                        "label": poi["name"]
                    })
                ui_options.append({"id": "cancel", "label": "No grazie"})
        
        return {
            "message": content,
            "ui_options": ui_options,
            "commands": commands
        }
    
    async def _fallback_response(
        self,
        user_message: str,
        context: dict[str, Any]
    ) -> dict[str, Any]:
        """Fallback rule-based quando l'LLM fallisce."""
        msg_lower = user_message.lower()
        
        # Mapping bisogni
        need_map = {
            "fame": "Fame",
            "mangiare": "Fame",
            "sete": "Sete",
            "bere": "Sete",
            "male": "Malessere",
            "farmacia": "Malessere",
            "divert": "Divertimento",
            "shopping": "Shopping",
        }
        
        for keyword, need in need_map.items():
            if keyword in msg_lower:
                return await self._fallback_need_response(need, context)
        
        return {
            "message": "Mi scusi, non sono riuscito a elaborare la richiesta. Può ripetere?",
            "ui_options": [],
            "commands": []
        }
    
    async def _fallback_need_response(
        self,
        need: str,
        context: dict[str, Any]
    ) -> dict[str, Any]:
        """Genera risposta fallback per un bisogno."""
        from app.llm.tools import tool_recommend_pois
        
        result = await tool_recommend_pois(
            user_id=context.get("user_id", "unknown"),
            need=need
        )
        
        pois = result.get("pois", [])
        
        if not pois:
            return {
                "message": f"Mi dispiace, non ho trovato luoghi per {need} nelle vicinanze.",
                "ui_options": [],
                "commands": []
            }
        
        # Salva POI IDs nella sessione
        poi_ids = [poi['poi_id'] for poi in pois]
        session_id = context.get("session_id", "")
        if session_id:
            session_store.update_session(session_id, last_poi_suggestions=poi_ids)
        
        # Messaggio breve + bottoni con info
        message = "🍕 Ecco cosa ho trovato:"
        ui_options = []
        
        for poi in pois:
            rating = poi.get('rating', 0)
            label = f"{poi['name']} ({rating:.1f}⭐)" if rating else poi['name']
            ui_options.append({
                "id": f"poi:{poi['poi_id']}",
                "label": label
            })
        
        ui_options.append({"id": "cancel", "label": "❌ Annulla"})
        
        return {
            "message": message,
            "ui_options": ui_options,
            "commands": []
        }


# =============================================================================
# CLIENT STUB (per testing senza LLM)
# =============================================================================

class StubClient(LLMClient):
    """Client stub per testing senza LLM reale."""
    
    async def chat(
        self,
        session_id: str,
        user_message: str,
        context: dict[str, Any]
    ) -> dict[str, Any]:
        """Risponde con messaggi di test."""
        return {
            "message": f"[STUB] Ricevuto: {user_message}",
            "ui_options": [
                {"id": "test_yes", "label": "Sì"},
                {"id": "test_no", "label": "No"}
            ],
            "commands": []
        }


# =============================================================================
# FACTORY
# =============================================================================

_llm_client: LLMClient | None = None


async def get_llm_client() -> LLMClient:
    """Ottiene l'istanza del client LLM in base alla configurazione."""
    global _llm_client
    
    if _llm_client is None:
        settings = get_settings()
        
        if settings.llm_provider == "openrouter" and settings.openrouter_api_key:
            # Usa OpenRouter
            _llm_client = OpenRouterClient()
            logger.info(f"Using OpenRouter client with model: {settings.openrouter_model}")
        else:
            # Usa Ollama (default)
            try:
                async with httpx.AsyncClient(timeout=5.0) as client:
                    response = await client.get(f"{settings.ollama_base_url}/api/tags")
                    if response.status_code == 200:
                        _llm_client = OllamaClient()
                        logger.info(f"Using Ollama client with model: {settings.ollama_model}")
                    else:
                        raise Exception("Ollama not available")
            except Exception as e:
                logger.warning(f"Ollama not available: {e}. Using stub client.")
                _llm_client = StubClient()
    
    return _llm_client


async def reset_llm_client() -> None:
    """Resetta il client LLM (per cambiare provider/modello)."""
    global _llm_client
    _llm_client = None
