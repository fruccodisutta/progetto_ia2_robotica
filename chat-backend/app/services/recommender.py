"""
Servizio Recommender per ranking POI.

Logica di ranking e spiegabilità per le raccomandazioni.
"""

from typing import Any
from app.neo4j.repo import neo4j_repo
from app.utils.logging import get_logger

logger = get_logger(__name__)


class RecommenderService:
    """Servizio per raccomandazioni POI."""
    
    async def recommend_for_need(
        self,
        user_id: str,
        need: str,
        limit: int = 5
    ) -> list[dict[str, Any]]:
        """
        Raccomanda POI per un bisogno specifico.
        
        Il ranking considera:
        1. Priorità della regola Bisogno→Categoria
        2. Preferenze PIACE su POI
        3. Storico visite (familiarità)
        
        Args:
            user_id: ID utente
            need: Nome del bisogno
            limit: Numero max POI
            
        Returns:
            Lista POI ordinati per rilevanza con reason
        """
        pois = await neo4j_repo.get_pois_by_need(
            user_id=user_id,
            need=need,
            limit=limit
        )
        
        result = []
        for poi in pois:
            reason = self._generate_reason(poi, need)
            result.append({
                "poi_id": poi["id"],
                "name": poi["name"],
                "id_unity": poi.get("id_unity"),
                "category": poi.get("category", ""),
                "rating": poi.get("rating"),
                "score": poi.get("score"),
                "reason": reason
            })
        
        return result
    
    def _generate_reason(self, poi: dict[str, Any], need: str) -> str:
        """Genera spiegazione per il POI raccomandato."""
        parts = []
        
        category = poi.get("category", "")
        rating = poi.get("rating")
        
        # Categoria
        if category:
            parts.append(category)
        
        # Rating
        if rating:
            if rating >= 4.5:
                parts.append("★★★★★")
            elif rating >= 4.0:
                parts.append("★★★★")
            elif rating >= 3.5:
                parts.append("★★★")
        
        return " · ".join(parts) if parts else "Consigliato"
    
    async def get_last_visited(
        self,
        user_id: str,
        category: str
    ) -> dict[str, Any] | None:
        """
        Ottiene l'ultimo POI visitato per categoria.
        
        Args:
            user_id: ID utente
            category: Nome categoria
            
        Returns:
            POI o None
        """
        return await neo4j_repo.get_last_visited_place(user_id, category)


# Istanza singleton
recommender_service = RecommenderService()
