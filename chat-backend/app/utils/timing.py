"""
Modulo per timing e logging avanzato delle operazioni.

Fornisce:
- Decorator @timed_async per misurare tempo di esecuzione funzioni async
- Classe RequestTimer per tracciare timing end-to-end
- Utilities per logging strutturato
"""

import time
import functools
from typing import Any, Callable
from dataclasses import dataclass, field
from contextlib import asynccontextmanager

from app.utils.logging import get_logger

logger = get_logger(__name__)


# =============================================================================
# TIMING DECORATOR
# =============================================================================

def timed_async(operation_name: str = None):
    """
    Decorator per misurare e loggare il tempo di esecuzione di funzioni async.
    
    Args:
        operation_name: Nome dell'operazione (default: nome funzione)
        
    Usage:
        @timed_async("query_neo4j")
        async def my_function():
            ...
    """
    def decorator(func: Callable):
        @functools.wraps(func)
        async def wrapper(*args, **kwargs):
            op_name = operation_name or func.__name__
            start_time = time.perf_counter()
            
            try:
                result = await func(*args, **kwargs)
                elapsed_ms = (time.perf_counter() - start_time) * 1000
                
                # Log con colori per evidenziare tempi lenti
                if elapsed_ms > 2000:
                    logger.warning(f"⏱️ [SLOW] {op_name}: {elapsed_ms:.2f}ms")
                elif elapsed_ms > 500:
                    logger.info(f"⏱️ [TIMING] {op_name}: {elapsed_ms:.2f}ms")
                else:
                    logger.debug(f"⏱️ [TIMING] {op_name}: {elapsed_ms:.2f}ms")
                
                return result
            except Exception as e:
                elapsed_ms = (time.perf_counter() - start_time) * 1000
                logger.error(f"⏱️ [ERROR] {op_name} failed after {elapsed_ms:.2f}ms: {e}")
                raise
        
        return wrapper
    return decorator


# =============================================================================
# REQUEST TIMER - Tracker per timing end-to-end
# =============================================================================

@dataclass
class RequestTimer:
    """
    Tracker per misurare timing end-to-end di una richiesta completa.
    
    Usage:
        timer = RequestTimer("handle_user_message")
        timer.start("parse_message")
        ... do work ...
        timer.stop("parse_message")
        timer.start("llm_call")
        ... do work ...
        timer.stop("llm_call")
        timer.log_summary()
    """
    request_name: str
    start_time: float = field(default_factory=time.perf_counter)
    steps: dict = field(default_factory=dict)
    current_step: str | None = None
    current_step_start: float | None = None
    
    def start(self, step_name: str):
        """Inizia a misurare uno step."""
        self.current_step = step_name
        self.current_step_start = time.perf_counter()
    
    def stop(self, step_name: str = None):
        """Ferma la misurazione dello step corrente."""
        step = step_name or self.current_step
        if step and self.current_step_start:
            elapsed_ms = (time.perf_counter() - self.current_step_start) * 1000
            self.steps[step] = elapsed_ms
            self.current_step = None
            self.current_step_start = None
    
    def add_timing(self, step_name: str, elapsed_ms: float):
        """Aggiunge timing manualmente."""
        self.steps[step_name] = elapsed_ms
    
    def get_total_ms(self) -> float:
        """Restituisce tempo totale in ms."""
        return (time.perf_counter() - self.start_time) * 1000
    
    def log_summary(self):
        """Logga un riepilogo completo dei timing."""
        total_ms = self.get_total_ms()
        
        logger.info(f"\n{'='*60}")
        logger.info(f"📊 REQUEST TIMING SUMMARY: {self.request_name}")
        logger.info(f"{'='*60}")
        
        # Ordina steps per tempo decrescente
        sorted_steps = sorted(self.steps.items(), key=lambda x: x[1], reverse=True)
        
        for step, ms in sorted_steps:
            bar_length = min(int(ms / 50), 40)  # 1 char = 50ms, max 40 chars
            bar = "█" * bar_length
            
            if ms > 2000:
                logger.warning(f"  🐢 {step:30s} {ms:8.2f}ms {bar}")
            elif ms > 500:
                logger.info(f"  ⏳ {step:30s} {ms:8.2f}ms {bar}")
            else:
                logger.info(f"  ⚡ {step:30s} {ms:8.2f}ms {bar}")
        
        logger.info(f"{'-'*60}")
        
        if total_ms > 3000:
            logger.warning(f"  🐢 TOTAL: {total_ms:.2f}ms (SLOW!)")
        else:
            logger.info(f"  ✅ TOTAL: {total_ms:.2f}ms")
        
        logger.info(f"{'='*60}\n")
        
        return total_ms


# =============================================================================
# CONTEXT MANAGER PER STEP TIMING
# =============================================================================

@asynccontextmanager
async def timed_step(timer: RequestTimer, step_name: str):
    """
    Context manager async per misurare uno step.
    
    Usage:
        async with timed_step(timer, "llm_call"):
            result = await call_llm()
    """
    timer.start(step_name)
    try:
        yield
    finally:
        timer.stop(step_name)


# =============================================================================
# UTILITIES PER LOGGING DETTAGLIATO
# =============================================================================

def log_llm_request(prompt: str, model: str, provider: str):
    """Logga dettagli di una richiesta LLM."""
    prompt_length = len(prompt)
    prompt_lines = prompt.count('\n') + 1
    
    # Stima approssimativa token (4 char ~ 1 token)
    estimated_tokens = prompt_length // 4
    
    logger.info(f"\n{'─'*60}")
    logger.info(f"🤖 LLM REQUEST")
    logger.info(f"{'─'*60}")
    logger.info(f"  Provider: {provider}")
    logger.info(f"  Model: {model}")
    logger.info(f"  Prompt length: {prompt_length} chars ({prompt_lines} lines)")
    logger.info(f"  Estimated tokens: ~{estimated_tokens}")
    
    # Log primo e ultimo blocco del prompt per debug
    if prompt_length > 500:
        logger.debug(f"  Prompt (first 300 chars):\n{prompt[:300]}...")
        logger.debug(f"  Prompt (last 200 chars): ...{prompt[-200:]}")
    else:
        logger.debug(f"  Prompt:\n{prompt}")
    logger.info(f"{'─'*60}")


def log_llm_response(response: str, elapsed_ms: float):
    """Logga risposta LLM."""
    response_length = len(response)
    
    logger.info(f"\n{'─'*60}")
    logger.info(f"🤖 LLM RESPONSE ({elapsed_ms:.2f}ms)")
    logger.info(f"{'─'*60}")
    logger.info(f"  Response length: {response_length} chars")
    
    if response_length > 300:
        logger.debug(f"  Response: {response[:300]}...")
    else:
        logger.info(f"  Response: {response}")
    logger.info(f"{'─'*60}")


def log_neo4j_query(query: str, params: dict, operation: str):
    """Logga query Neo4j."""
    # Pulisci query per log compatto
    clean_query = ' '.join(query.split())
    
    logger.debug(f"\n{'─'*60}")
    logger.debug(f"🗃️ NEO4J QUERY: {operation}")
    logger.debug(f"{'─'*60}")
    logger.debug(f"  Query: {clean_query[:200]}...")
    logger.debug(f"  Params: {params}")
    logger.debug(f"{'─'*60}")


def log_neo4j_result(operation: str, result_count: int, elapsed_ms: float, sample_result: Any = None):
    """Logga risultato query Neo4j."""
    if elapsed_ms > 100:
        logger.warning(f"🗃️ [SLOW] {operation}: {result_count} results in {elapsed_ms:.2f}ms")
    else:
        logger.debug(f"🗃️ {operation}: {result_count} results in {elapsed_ms:.2f}ms")
    
    if sample_result:
        logger.debug(f"  Sample result: {sample_result}")


def log_tool_execution(tool_id: str, params: dict, message: str):
    """Logga esecuzione tool."""
    logger.info(f"\n🔧 TOOL EXECUTION: {tool_id}")
    logger.info(f"  Message: {message[:100]}...")
    logger.info(f"  Params: {params}")


def log_tool_result(tool_id: str, result: Any, elapsed_ms: float):
    """Logga risultato tool."""
    logger.info(f"🔧 TOOL RESULT: {tool_id} ({elapsed_ms:.2f}ms)")
    
    if isinstance(result, dict):
        if 'ui_options' in result:
            logger.info(f"  UI Options: {len(result.get('ui_options', []))} items")
        if 'message' in result:
            logger.info(f"  Message: {result['message'][:100]}...")
    else:
        logger.info(f"  Result: {str(result)[:200]}")
