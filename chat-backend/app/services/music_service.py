"""
Music Service per gestione audio.

Gestisce riproduzione, stato e controlli musicali.
"""

import os
from pathlib import Path
from typing import Any

from app.config import get_settings
from app.utils.logging import get_logger

logger = get_logger(__name__)

# Directory dei file musicali
MUSIC_DIR = Path(__file__).parent.parent.parent / "assets" / "music"


class MusicService:
    """Servizio per gestione audio musicale."""
    
    def __init__(self):
        self.settings = get_settings()
        self._available_genres: list[str] | None = None
    
    def get_available_genres(self) -> list[str]:
        """
        Ottiene i generi musicali disponibili (dai file MP3).
        
        Returns:
            Lista dei generi disponibili
        """
        if self._available_genres is None:
            self._available_genres = []
            if MUSIC_DIR.exists():
                for file in MUSIC_DIR.glob("*.mp3"):
                    # Nome file senza estensione = genere
                    genre = file.stem
                    self._available_genres.append(genre)
            logger.info(f"Available music genres: {self._available_genres}")
        return self._available_genres
    
    def get_music_file_path(self, genre: str) -> Path | None:
        """
        Ottiene il percorso del file musicale per un genere.
        
        Args:
            genre: Nome del genere
            
        Returns:
            Path del file o None se non esiste
        """
        # Cerca match case-insensitive
        for available in self.get_available_genres():
            if available.lower() == genre.lower():
                file_path = MUSIC_DIR / f"{available}.mp3"
                if file_path.exists():
                    return file_path
        return None
    
    def is_valid_genre(self, genre: str) -> bool:
        """Verifica se un genere è valido."""
        available = [g.lower() for g in self.get_available_genres()]
        return genre.lower() in available
    
    def normalize_genre(self, genre: str) -> str | None:
        """
        Normalizza il nome del genere al formato corretto.
        
        Args:
            genre: Nome genere (case insensitive)
            
        Returns:
            Nome genere normalizzato o None
        """
        for available in self.get_available_genres():
            if available.lower() == genre.lower():
                return available
        return None
    
    def get_music_url(self, genre: str) -> str | None:
        """
        Ottiene l'URL per lo streaming di un genere.
        
        Args:
            genre: Nome del genere
            
        Returns:
            URL dello stream o None
        """
        normalized = self.normalize_genre(genre)
        if normalized:
            return f"/music/{normalized}"
        return None


# Istanza singleton
music_service = MusicService()
