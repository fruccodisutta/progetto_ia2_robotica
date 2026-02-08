"""
Package tools per il Taxi Backend.

Esporta tutti i tool disponibili.
"""

from app.tools.music_tools import MUSIC_TOOLS
from app.tools.poi_tools import POI_TOOLS
from app.tools.taxi_tools import TAXI_TOOLS

# Tutti i tool disponibili
ALL_TOOLS = MUSIC_TOOLS + POI_TOOLS + TAXI_TOOLS
