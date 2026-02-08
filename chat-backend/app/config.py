"""
Configurazione applicazione Taxi Backend.

Carica settings da variabili d'ambiente usando Pydantic Settings.
"""

from functools import lru_cache
from pydantic_settings import BaseSettings, SettingsConfigDict


class Settings(BaseSettings):
    """Settings applicazione caricate da .env o variabili ambiente."""
    
    model_config = SettingsConfigDict(
        env_file=".env",
        env_file_encoding="utf-8",
        extra="ignore"
    )
    
    # Neo4j
    neo4j_uri: str = "bolt://localhost:7687"
    neo4j_user: str = "neo4j"
    neo4j_password: str = "12345678"
    
    # LLM Provider: "ollama" or "openrouter"
    llm_provider: str = "ollama"
    
    # Ollama LLM
    ollama_base_url: str = "http://localhost:11434"
    ollama_model: str = "llama3.1:8b"
    
    # OpenRouter LLM
    openrouter_api_key: str = ""
    openrouter_model: str = "google/gemini-2.0-flash-exp:free"
    openrouter_base_url: str = "https://openrouter.ai/api/v1"
    
    # Logging
    log_level: str = "INFO"
    
    # Session
    max_history_turns: int = 10
    
    # Recommender
    default_poi_limit: int = 5


# Available free models on OpenRouter
OPENROUTER_FREE_MODELS = [
    "google/gemini-2.0-flash-exp:free",
    "xiaomi/mimo-v2-flash:free",
    "qwen/qwen3-235b-a22b:free",
    "meta-llama/llama-3.3-70b-instruct:free",
    "z-ai/glm-4.5-air:free",
]


@lru_cache
def get_settings() -> Settings:
    """Ottiene istanza singleton delle settings."""
    return Settings()

