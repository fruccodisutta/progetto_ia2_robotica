"""
Repository per query Neo4j.

Contiene tutte le query Cypher per accedere ai dati del grafo.
"""

import time
from typing import Any
from app.neo4j.driver import neo4j_driver
from app.utils.logging import get_logger

logger = get_logger(__name__)


# Mapping per normalizzare i nomi dei bisogni tra frontend/LLM e database
NEED_MAPPING = {
    # Mappings per nomi alternativi
    "Hunger": "Fame",  # English fallback
    "Thirst": "Sete",  # English fallback
    "Entertainment": "Divertimento",  # English fallback
    "Health": "Salute",  # English fallback
    "Transport": "Trasporto",
    "Money": "Denaro",
    "Culture": "Cultura",
    "Accommodation": "Alloggio",
    "Work": "Lavoro",
    
    # Identità (nomi corretti nel DB)
    "Fame": "Fame",
    "Sete": "Sete",
    "Svago": "Svago",
    "Divertimento": "Divertimento",
    "Salute": "Salute",
    "Shopping": "Shopping",
    "Cultura": "Cultura",
    "Trasporto": "Trasporto",
    "Denaro": "Denaro",
    "Fitness": "Fitness",
    "Alloggio": "Alloggio",
    "Relax": "Relax",
    "Cinema": "Cinema",
    "Spesa": "Spesa",
    "Lavoro": "Lavoro",
    "Meccanico": "Meccanico",
}



def normalize_need(need: str) -> str:
    """Normalizza il nome del bisogno per il database."""
    return NEED_MAPPING.get(need, need)


class Neo4jRepository:
    """Repository per query al database Neo4j."""
    
    # =========================================================================
    # UTENTE
    # =========================================================================
    
    async def get_user_by_id(self, user_id: str) -> dict[str, Any] | None:
        """
        Ottiene il profilo utente.
        
        Args:
            user_id: ID dell'utente
            
        Returns:
            Dati utente o None se non trovato
        """
        query = """
        MATCH (u:Utente {id: $user_id})
        OPTIONAL MATCH (u)-[:ABITA]->(casa:PuntoInteresse)
        RETURN u.id AS id,
               u.nome AS nome,
               u.eta AS eta,
               casa.nome AS casa,
               casa.id_unity AS casa_id_unity
        """
        try:
            results = await neo4j_driver.execute_query(query, {"user_id": user_id})
            return results[0] if results else None
        except Exception as e:
            logger.error(f"Error getting user {user_id}: {e}")
            return None
    
    async def get_user_home(self, user_id: str) -> dict[str, Any] | None:
        """
        Ottiene la casa dell'utente.
        
        Args:
            user_id: ID dell'utente
            
        Returns:
            Dati POI casa o None
        """
        query = """
        MATCH (u:Utente {id: $user_id})-[:ABITA]->(casa:PuntoInteresse)
        OPTIONAL MATCH (casa)-[:HA_CATEGORIA]->(c:Categoria)
        RETURN casa.id AS id,
               casa.nome AS name,
               casa.id_unity AS id_unity,
               c.nome AS category,
               casa.valutazione_media AS rating
        """
        try:
            results = await neo4j_driver.execute_query(query, {"user_id": user_id})
            return results[0] if results else None
        except Exception as e:
            logger.error(f"Error getting user home: {e}")
            return None
    
    # =========================================================================
    # MUSICA
    # =========================================================================
    
    async def get_music_preference(self, user_id: str) -> str | None:
        """
        Ottiene la preferenza musicale dell'utente.
        Se l'utente ha più generi preferiti, ne sceglie uno casualmente.
        
        Args:
            user_id: ID utente
            
        Returns:
            Genere musicale preferito (random se multipli) o None
        """
        import random
        
        query = """
        MATCH (u:Utente {id: $user_id})-[:PIACE]->(g:GenereMusicale)
        RETURN g.nome AS genre
        """
        try:
            results = await neo4j_driver.execute_query(query, {"user_id": user_id})
            if results:
                genres = [r["genre"] for r in results]
                selected = random.choice(genres)
                logger.info(f"User {user_id} has {len(genres)} music preferences: {genres}, selected: {selected}")
                return selected
            return None
        except Exception as e:
            logger.error(f"Error getting music preference: {e}")
            return None
    
    async def set_music_preference(self, user_id: str, genre: str) -> bool:
        """
        Salva la preferenza musicale dell'utente.
        
        Args:
            user_id: ID utente
            genre: Genere musicale
            
        Returns:
            True se salvato con successo
        """
        query = """
        MATCH (u:Utente {id: $user_id})
        MERGE (g:GenereMusicale {nome: $genre})
        MERGE (u)-[:PIACE]->(g)
        RETURN u.id AS id
        """
        try:
            await neo4j_driver.execute_write(
                query, {"user_id": user_id, "genre": genre}
            )
            return True
        except Exception as e:
            logger.error(f"Error setting music preference: {e}")
            return False
    
    # =========================================================================
    # POI
    # =========================================================================
    
    async def get_pois_by_need(
        self,
        user_id: str,
        need: str,
        limit: int = 4
    ) -> list[dict[str, Any]]:
        """
        Ottiene POI raccomandati per un bisogno specifico.
        
        Ranking basato su:
        - Priorità regola Bisogno→Categoria
        - Preferenze PIACE su POI (boost 2x)
        - HA_VISITATO su POI (boost 1.5x)
        - Valutazione media POI
        
        Args:
            user_id: ID utente
            need: Nome del bisogno (es. "Fame")
            limit: Numero massimo POI (default 4)
            
        Returns:
            Lista POI ordinati per score
        """
        query = """
        // Trova categorie suggerite per il bisogno
        MATCH (b:Bisogno {nome: $need})-[s:SUGGERISCE]->(cat:Categoria)
        
        // Trova POI di quelle categorie
        MATCH (poi:PuntoInteresse)-[:HA_CATEGORIA]->(cat)
        
        // Casa dell'utente (serve per filtrare Residenziale in Alloggio)
        OPTIONAL MATCH (home_user:Utente {id: $user_id})-[:ABITA]->(home:PuntoInteresse)
        
        // Controlla preferenze PIACE (solo su POI!)
        OPTIONAL MATCH (u:Utente {id: $user_id})-[:PIACE]->(poi)
        
        // Controlla visite precedenti
        OPTIONAL MATCH (u2:Utente {id: $user_id})-[v:HA_VISITATO]->(poi)
        
        WITH poi, cat, s.priorita AS priorita, home,
             CASE WHEN u IS NOT NULL THEN true ELSE false END AS liked,
             CASE WHEN v IS NOT NULL THEN true ELSE false END AS visited,
             CASE WHEN u IS NOT NULL THEN 2.0 ELSE 1.0 END AS like_boost,
             CASE WHEN v IS NOT NULL THEN 1.5 ELSE 1.0 END AS visit_boost
        
        // Per Alloggio: mostra solo la casa dell'utente + hotel (escludi altre case)
        WHERE $need <> "Alloggio"
           OR cat.nome <> "Residenziale"
           OR poi = home
        
        // Calcola score: priorità × boost_piace × boost_visitato × rating
        WITH poi, cat.nome AS categoria, priorita, liked, visited,
             priorita * like_boost * visit_boost * COALESCE(poi.valutazione_media, 3.0) AS score
        
        RETURN poi.id AS id,
               poi.nome AS name,
               poi.id_unity AS id_unity,
               categoria AS category,
               poi.valutazione_media AS rating,
               liked,
               visited,
               score
        ORDER BY score DESC
        LIMIT $limit
        """
        try:
            # Normalizza il nome del bisogno (es. "Divertimento" -> "Svago")
            normalized_need = normalize_need(need)
            
            logger.info(f"\n{'─'*50}")
            logger.info(f"🗃️ [NEO4J] get_pois_by_need")
            logger.info(f"{'─'*50}")
            logger.info(f"  Need: '{need}' -> normalized: '{normalized_need}'")
            logger.info(f"  User: {user_id}, Limit: {limit}")
            
            start_time = time.perf_counter()
            results = await neo4j_driver.execute_query(
                query,
                {
                    "user_id": user_id,
                    "need": normalized_need,
                    "limit": limit
                }
            )
            elapsed_ms = (time.perf_counter() - start_time) * 1000
            
            logger.info(f"  Results: {len(results)} POIs in {elapsed_ms:.2f}ms")
            for poi in results:
                logger.debug(f"    - {poi.get('name')} (score: {poi.get('score', 'N/A')}, liked: {poi.get('liked')}, visited: {poi.get('visited')})")
            logger.info(f"{'─'*50}")
            
            return results
        except Exception as e:
            logger.error(f"Error getting POIs by need: {e}")
            return []
    
    async def get_pois_by_tag(
        self,
        user_id: str,
        tag: str,
        limit: int = 4
    ) -> list[dict[str, Any]]:
        """
        Trova POI che hanno un determinato tag.
        
        Usato per richieste specifiche come "voglio un hamburger".
        
        Args:
            user_id: ID utente
            tag: Nome del tag (es. "hamburger")
            limit: Numero massimo POI (default 4)
            
        Returns:
            Lista POI con quel tag, ordinati per score
        """
        query = """
        // Trova POI con il tag
        MATCH (poi:PuntoInteresse)-[:HA_TAG]->(t:Tag)
        WHERE toLower(t.nome) = toLower($tag)
        
        // Controlla preferenze
        OPTIONAL MATCH (u:Utente {id: $user_id})-[:PIACE]->(poi)
        OPTIONAL MATCH (u2:Utente {id: $user_id})-[v:HA_VISITATO]->(poi)
        
        WITH poi, t,
             CASE WHEN u IS NOT NULL THEN true ELSE false END AS liked,
             CASE WHEN v IS NOT NULL THEN true ELSE false END AS visited,
             CASE WHEN u IS NOT NULL THEN 2.0 ELSE 1.0 END AS like_boost,
             CASE WHEN v IS NOT NULL THEN 1.5 ELSE 1.0 END AS visit_boost
        
        // Ottieni categoria
        OPTIONAL MATCH (poi)-[:HA_CATEGORIA]->(cat:Categoria)
        
        WITH poi, cat.nome AS category, liked, visited,
             like_boost * visit_boost * COALESCE(poi.valutazione_media, 3.0) AS score
        
        RETURN poi.id AS id,
               poi.nome AS name,
               poi.id_unity AS id_unity,
               category,
               poi.valutazione_media AS rating,
               liked,
               visited,
               score
        ORDER BY score DESC
        LIMIT $limit
        """
        try:
            logger.info(f"\n{'─'*50}")
            logger.info(f"🗃️ [NEO4J] get_pois_by_tag")
            logger.info(f"{'─'*50}")
            logger.info(f"  Tag: '{tag}'")
            logger.info(f"  User: {user_id}, Limit: {limit}")
            
            start_time = time.perf_counter()
            results = await neo4j_driver.execute_query(
                query,
                {
                    "user_id": user_id,
                    "tag": tag,
                    "limit": limit
                }
            )
            elapsed_ms = (time.perf_counter() - start_time) * 1000
            
            logger.info(f"  Results: {len(results)} POIs in {elapsed_ms:.2f}ms")
            for poi in results:
                logger.debug(f"    - {poi.get('name')} (category: {poi.get('category')})")
            logger.info(f"{'─'*50}")
            
            return results
        except Exception as e:
            logger.error(f"Error getting POIs by tag: {e}")
            return []
    
    async def find_poi_by_name(
        self,
        name: str
    ) -> dict[str, Any] | None:
        """
        Cerca un POI per nome esatto o parziale (token-based).
        
        Supporta query come "antico forno" per trovare "Forno Antico".
        
        Args:
            name: Nome (o parte) del POI
            
        Returns:
            POI trovato o None
        """
        # 1. Pulisci la query
        cleansed = name.lower().replace("al", "").replace("alla", "").replace("da", "").strip()
        tokens = [t for t in cleansed.split() if len(t) > 2]
        
        if not tokens:
            tokens = [cleansed]
            
        # 2. Costruisci query Cypher dinamica
        # WHERE n.nome CONTAINS t1 AND n.nome CONTAINS t2 ...
        where_clauses = ["toLower(poi.nome) CONTAINS $token_" + str(i) for i in range(len(tokens))]
        where_stmt = " AND ".join(where_clauses)
        
        query = f"""
        MATCH (poi:PuntoInteresse)
        WHERE {where_stmt}
        
        OPTIONAL MATCH (poi)-[:HA_CATEGORIA]->(cat:Categoria)
        
        RETURN poi.id AS id,
               poi.nome AS name,
               poi.id_unity AS id_unity,
               cat.nome AS category,
               poi.valutazione_media AS rating
        ORDER BY size(poi.nome)
        LIMIT 1
        """
        
        params = {f"token_{i}": t for i, t in enumerate(tokens)}
        
        try:
            results = await neo4j_driver.execute_query(query, params)
            if results:
                logger.info(f"Found POI '{results[0]['name']}' for query '{name}'")
                return results[0]
            return None
        except Exception as e:
            logger.error(f"Error finding POI by name: {e}")
            return None
    
    async def search_pois_autocomplete(
        self,
        query: str,
        limit: int = 5
    ) -> list[dict[str, Any]]:
        """
        Search POIs by partial name for autocomplete dropdown.
        
        Returns POIs matching the query, ordered by:
        1. Exact prefix match (starts with query)
        2. Contains match
        3. Name length (shorter = more relevant)
        
        Args:
            query: Search query string (partial POI name)
            limit: Maximum results to return
            
        Returns:
            List of POIs with id, name, category, rating
        """
        if not query or len(query) < 2:
            return []
        
        search_term = query.lower().strip()
        
        cypher_query = """
        MATCH (poi:PuntoInteresse)
        WHERE toLower(poi.nome) CONTAINS $search_term
        
        OPTIONAL MATCH (poi)-[:HA_CATEGORIA]->(cat:Categoria)
        
        WITH poi, cat,
             // Priority: exact prefix > contains > longer names
             CASE 
                 WHEN toLower(poi.nome) STARTS WITH $search_term THEN 0
                 ELSE 1
             END AS match_priority
        
        RETURN poi.id AS id,
               poi.nome AS name,
               poi.id_unity AS id_unity,
               cat.nome AS category,
               poi.valutazione_media AS rating,
               match_priority
        ORDER BY match_priority ASC, size(poi.nome) ASC
        LIMIT $limit
        """
        
        try:
            results = await neo4j_driver.execute_query(
                cypher_query,
                {
                    "search_term": search_term,
                    "limit": limit
                }
            )
            # Remove match_priority from results
            return [
                {k: v for k, v in r.items() if k != "match_priority"}
                for r in results
            ]
        except Exception as e:
            logger.error(f"Error in autocomplete search: {e}")
            return []
    
    
    async def get_last_visited_place(
        self,
        user_id: str,
        category_name: str
    ) -> dict[str, Any] | None:
        """
        Ottiene l'ultimo POI visitato di una certa categoria.
        
        Args:
            user_id: ID utente
            category_name: Nome categoria
            
        Returns:
            POI o None
        """
        query = """
        MATCH (u:Utente {id: $user_id})-[v:HA_VISITATO]->(poi:PuntoInteresse)
              -[:HA_CATEGORIA]->(c:Categoria {nome: $category})
        RETURN poi.id AS id,
               poi.nome AS name,
               poi.id_unity AS id_unity,
               c.nome AS category,
               v.data AS last_visit
        ORDER BY v.data DESC
        LIMIT 1
        """
        try:
            results = await neo4j_driver.execute_query(
                query,
                {"user_id": user_id, "category": category_name}
            )
            return results[0] if results else None
        except Exception as e:
            logger.error(f"Error getting last visited place: {e}")
            return None
    
    async def get_poi_by_id(self, poi_id: str) -> dict[str, Any] | None:
        """
        Ottiene un POI per ID.
        
        Args:
            poi_id: ID del POI
            
        Returns:
            Dati POI o None
        """
        query = """
        MATCH (poi:PuntoInteresse {id: $poi_id})
        OPTIONAL MATCH (poi)-[:HA_CATEGORIA]->(c:Categoria)
        RETURN poi.id AS id,
               poi.nome AS name,
               poi.id_unity AS id_unity,
               c.nome AS category,
               poi.valutazione_media AS rating
        """
        try:
            results = await neo4j_driver.execute_query(query, {"poi_id": poi_id})
            return results[0] if results else None
        except Exception as e:
            logger.error(f"Error getting POI {poi_id}: {e}")
            return None
    
    async def get_poi_by_name(self, name: str) -> dict[str, Any] | None:
        """
        Cerca un POI per nome (case-insensitive, partial match).
        
        Args:
            name: Nome del POI da cercare
            
        Returns:
            Dati POI o None
        """
        # Cerca match esatto o parziale
        query = """
        MATCH (poi:PuntoInteresse)
        WHERE toLower(poi.nome) CONTAINS toLower($name)
        OPTIONAL MATCH (poi)-[:HA_CATEGORIA]->(c:Categoria)
        RETURN poi.id AS id,
               poi.nome AS name,
               poi.id_unity AS id_unity,
               c.nome AS category,
               poi.valutazione_media AS rating
        ORDER BY 
            CASE WHEN toLower(poi.nome) = toLower($name) THEN 0 ELSE 1 END,
            size(poi.nome)
        LIMIT 1
        """
        try:
            results = await neo4j_driver.execute_query(query, {"name": name})
            return results[0] if results else None
        except Exception as e:
            logger.error(f"Error getting POI by name {name}: {e}")
            return None
    
    async def get_all_pois(self) -> list[dict[str, Any]]:
        """
        Ottiene tutti i POI.
        
        Returns:
            Lista di tutti i POI
        """
        query = """
        MATCH (poi:PuntoInteresse)
        OPTIONAL MATCH (poi)-[:HA_CATEGORIA]->(c:Categoria)
        OPTIONAL MATCH (poi)-[:HA_TAG]->(t:Tag)
        RETURN poi.id AS id,
               poi.nome AS name,
               poi.id_unity AS id_unity,
               c.nome AS category,
               poi.valutazione_media AS rating,
               collect(DISTINCT t.nome) AS tags
        ORDER BY poi.id_unity
        """
        try:
            results = await neo4j_driver.execute_query(query, {})
            return results
        except Exception as e:
            logger.error(f"Error getting all POIs: {e}")
            return []
    
    async def record_visit(
        self,
        user_id: str,
        poi_id: str
    ) -> dict[str, Any]:
        """
        Registra una visita a un POI con conteggio incrementale.
        
        Dopo 3+ visite a POI della stessa categoria, crea automaticamente
        una relazione PIACE verso quella categoria (inferenza preferenza).
        
        Args:
            user_id: ID utente
            poi_id: ID POI
            
        Returns:
            Dict con visit_count e new_preference (se creata)
        """
        # Query 1: Registra visita con conteggio
        visit_query = """
        MATCH (u:Utente {id: $user_id})
        MATCH (poi:PuntoInteresse {id: $poi_id})
        MERGE (u)-[v:HA_VISITATO]->(poi)
        ON CREATE SET v.conteggio = 1, v.prima_visita = datetime()
        ON MATCH SET v.conteggio = COALESCE(v.conteggio, 0) + 1
        SET v.ultima_visita = datetime()
        
        // Ottieni categoria per check preferenza
        WITH u, poi, v
        OPTIONAL MATCH (poi)-[:HA_CATEGORIA]->(cat:Categoria)
        
        RETURN v.conteggio AS visit_count, 
               cat.nome AS category,
               poi.nome AS poi_name
        """
        try:
            results = await neo4j_driver.execute_write(
                visit_query, {"user_id": user_id, "poi_id": poi_id}
            )
            
            if not results:
                return {"success": False, "visit_count": 0}
            
            result = results[0] if isinstance(results, list) else results
            visit_count = result.get("visit_count", 1)
            category = result.get("category")
            poi_name = result.get("poi_name")
            
            logger.info(f"Recorded visit #{visit_count} for user {user_id} to {poi_name}")
            
            response = {
                "success": True,
                "visit_count": visit_count,
                "poi_name": poi_name,
                "new_preference": None
            }
            
            # Query 2: Check se creare auto-PIACE (3+ visite a cat)
            if category:
                auto_pref_query = """
                MATCH (u:Utente {id: $user_id})-[v:HA_VISITATO]->(poi:PuntoInteresse)
                      -[:HA_CATEGORIA]->(cat:Categoria {nome: $category})
                WITH u, cat, COUNT(DISTINCT poi) AS visited_count
                WHERE visited_count >= 3
                
                // Controlla se già esiste preferenza
                OPTIONAL MATCH (u)-[existing:PIACE]->(cat)
                WITH u, cat, visited_count, existing
                WHERE existing IS NULL
                
                // Crea nuova preferenza
                MERGE (u)-[:PIACE]->(cat)
                RETURN cat.nome AS category, visited_count
                """
                try:
                    pref_results = await neo4j_driver.execute_write(
                        auto_pref_query, {"user_id": user_id, "category": category}
                    )
                    if pref_results:
                        pref = pref_results[0] if isinstance(pref_results, list) else pref_results
                        new_cat = pref.get("category")
                        if new_cat:
                            response["new_preference"] = new_cat
                            logger.info(f"Auto-created PIACE preference for user {user_id} -> {new_cat}")
                except Exception as e:
                    logger.warning(f"Could not check auto-preference: {e}")
            
            return response
            
        except Exception as e:
            logger.error(f"Error recording visit: {e}")
            return {"success": False, "visit_count": 0}
    
    # =========================================================================
    # PREFERENZE
    # =========================================================================
    
    async def get_user_preferences(
        self,
        user_id: str
    ) -> dict[str, list[str]]:
        """
        Ottiene tutte le preferenze dell'utente.
        
        Args:
            user_id: ID utente
            
        Returns:
            Dict con liste di preferenze per tipo
        """
        query = """
        MATCH (u:Utente {id: $user_id})
        
        OPTIONAL MATCH (u)-[:PIACE]->(c:Categoria)
        OPTIONAL MATCH (u)-[:PIACE]->(g:GenereMusicale)
        OPTIONAL MATCH (u)-[:PIACE]->(poi:PuntoInteresse)
        
        RETURN collect(DISTINCT c.nome) AS categorie,
               collect(DISTINCT g.nome) AS generi_musicali,
               collect(DISTINCT poi.id) AS poi_preferiti
        """
        try:
            results = await neo4j_driver.execute_query(query, {"user_id": user_id})
            if results:
                r = results[0]
                return {
                    "categorie": [x for x in r.get("categorie", []) if x],
                    "generi_musicali": [x for x in r.get("generi_musicali", []) if x],
                    "poi_preferiti": [x for x in r.get("poi_preferiti", []) if x],
                }
            return {
                "categorie": [],
                "generi_musicali": [],
                "poi_preferiti": []
            }
        except Exception as e:
            logger.error(f"Error getting user preferences: {e}")
            return {
                "categorie": [],
                "generi_musicali": [],
                "poi_preferiti": []
            }
    
    # =========================================================================
    # BISOGNI
    # =========================================================================
    
    async def get_need_categories(self, need: str) -> list[dict[str, Any]]:
        """
        Ottiene le categorie suggerite per un bisogno.
        
        Args:
            need: Nome del bisogno
            
        Returns:
            Lista categorie con priorità
        """
        query = """
        MATCH (b:Bisogno {nome: $need})-[s:SUGGERISCE]->(c:Categoria)
        RETURN c.nome AS category, s.priorita AS priority
        ORDER BY s.priorita DESC
        """
        try:
            return await neo4j_driver.execute_query(query, {"need": need})
        except Exception as e:
            logger.error(f"Error getting need categories: {e}")
            return []


# Istanza singleton
neo4j_repo = Neo4jRepository()
