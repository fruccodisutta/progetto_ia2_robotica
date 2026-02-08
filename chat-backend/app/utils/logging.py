"""
Configurazione logging strutturato per l'applicazione.
"""

import logging
import sys
from app.config import get_settings


def setup_logging() -> None:
    """Configura il logging dell'applicazione."""
    settings = get_settings()
    
    # Formato log
    log_format = "%(asctime)s | %(levelname)-8s | %(name)s | %(message)s"
    date_format = "%Y-%m-%d %H:%M:%S"
    
    # Configurazione base
    logging.basicConfig(
        level=getattr(logging, settings.log_level.upper(), logging.INFO),
        format=log_format,
        datefmt=date_format,
        stream=sys.stdout,
    )
    
    # Riduci verbosità di alcuni logger esterni
    logging.getLogger("neo4j").setLevel(logging.WARNING)
    logging.getLogger("httpx").setLevel(logging.WARNING)
    logging.getLogger("httpcore").setLevel(logging.WARNING)
    logging.getLogger("uvicorn.access").setLevel(logging.WARNING)


def get_logger(name: str) -> logging.Logger:
    """
    Ottiene un logger con nome specifico.
    
    Args:
        name: Nome del logger (tipicamente __name__)
        
    Returns:
        Logger configurato
    """
    return logging.getLogger(name)
