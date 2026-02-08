"""
Script per seeding del database Neo4j.

Schema KB:
- Utente -[PIACE]-> POI (solo POI, non categorie!)
- Utente -[ABITA]-> POI (Casa dell'utente)
- Utente -[HA_VISITATO]-> POI
- Bisogno -[SUGGERISCE]-> Categoria
- POI -[HA_CATEGORIA]-> Categoria
- POI -[HA_TAG]-> Tag

Eseguire con: python -m app.neo4j.seed
"""

import asyncio
from app.neo4j.driver import neo4j_driver
from app.utils.logging import get_logger, setup_logging

logger = get_logger(__name__)


# =============================================================================
# CATEGORIE (mappate ai POI Unity)
# =============================================================================

CATEGORIE = [
    "Residenziale",     # case, hotel (parte alloggio)
    "Sport",            # stadio, palestra
    "Abbigliamento",    # negozi vestiti
    "Cultura",          # musei, librerie, cinema
    "Industria",        # fabbrica, officina
    "Salute",           # farmacia, ospedale
    "Trasporti",        # fermata autobus
    "Intrattenimento",  # discoteca
    "Natura",           # parchi, giardini
    "Servizi",          # banca
    "Alimentari",       # supermercato, panificio
    "Ristorazione",     # ristorante, pizzeria
    "Bar",              # bar, caffè
    "Alloggio",         # hotel
    "Cinema",           # cinema
    "Libreria",         # libreria
    "Museo",            # museo
    "Palestra",         # palestra
    "Stadio",           # stadio
    "Auto",             # officina
    "Istruzione",       # scuola, università
]


# =============================================================================
# BISOGNI → CATEGORIE
# =============================================================================

BISOGNI = {
    "Fame": [
        ("Ristorazione", 1.0),
        ("Alimentari", 0.8),
        ("Bar", 0.6),
    ],
    "Sete": [
        ("Bar", 1.0),
        ("Intrattenimento", 0.8),  # Discopub
    ],
    "Malessere": [
        ("Salute", 1.0),
    ],
    "Salute": [  # Alias per Malessere
        ("Salute", 1.0),
    ],
    "Divertimento": [
        ("Intrattenimento", 1.0),
        ("Cinema", 0.8),
        ("Stadio", 0.8),
        ("Bar", 0.7),
    ],
    "Svago": [ # Alias per Divertimento
        ("Intrattenimento", 1.0),
        ("Cinema", 0.8),
        ("Stadio", 0.8),
        ("Bar", 0.7),
    ],
    "Shopping": [
        ("Abbigliamento", 1.0),
        ("Libreria", 1.0),
        # Nota: rimossa "Alimentari" per distinguere da Spesa
    ],
    "Spesa": [
        ("Alimentari", 1.0),
    ],
    "Cultura": [
        ("Museo", 1.0),
        ("Libreria", 0.9),
        ("Cinema", 0.8),
    ],
    "Cinema": [
        ("Cinema", 1.0),
    ],
    "Relax": [
        ("Natura", 1.0),
        ("Alloggio", 0.8), # Hotel
        ("Bar", 0.6),
    ],
    "Trasporto": [
        ("Trasporti", 1.0),
    ],
    "Denaro": [
        ("Servizi", 1.0), # Banca
    ],
    "Fitness": [
        ("Palestra", 1.0),
    ],
    "Alloggio": [
        ("Alloggio", 1.0), # Hotel separati
        ("Residenziale", 0.8),
    ],
    "Lavoro": [
        ("Industria", 1.0),
        ("Servizi", 0.5), # Uffici generici
    ],
    "Meccanico": [
        ("Auto", 1.0),
    ]
}


# =============================================================================
# POI con TAG (dati Unity)
# =============================================================================

POI_DATI = [
    # === RESIDENZIALE ===
    {
        "id": "POI_001",
        "id_unity": 0,
        "nome": "Casa U1",
        "categoria": "Residenziale",
        "valutazione": 5.0,
        "tags": ["casa", "abitazione", "privato", "residenza"]
    },
    {
        "id": "POI_002",
        "id_unity": 1,
        "nome": "Casa U2",
        "categoria": "Residenziale",
        "valutazione": 5.0,
        "tags": ["casa", "abitazione", "privato", "residenza"]
    },
    {
        "id": "POI_003",
        "id_unity": 2,
        "nome": "Casa U3",
        "categoria": "Residenziale",
        "valutazione": 5.0,
        "tags": ["casa", "abitazione", "privato", "residenza"]
    },

    # === SPORT ===
    {
        "id": "POI_004",
        "id_unity": 3,
        "nome": "Stadio Renzo Barbera",
        "categoria": "Stadio",
        "valutazione": 4.7,
        "tags": ["calcio", "partita", "sport", "eventi", "tifosi", "palermo"]
    },
    {
        "id": "POI_005",
        "id_unity": 16,
        "nome": "Palestra McFIT",
        "categoria": "Palestra",
        "valutazione": 4.3,
        "tags": ["palestra", "fitness", "allenamento", "pesi", "cardio", "sport", "allenarmi", "allenarsi", "gym", "esercizio"]
    },

    # === ABBIGLIAMENTO ===
    {
        "id": "POI_006",
        "id_unity": 4,
        "nome": "Abbigliamento Zara",
        "categoria": "Abbigliamento",
        "valutazione": 4.2,
        "tags": ["vestiti", "moda", "shopping", "abbigliamento", "fashion"]
    },

    # === CULTURA ===
    {
        "id": "POI_007",
        "id_unity": 5,
        "nome": "Museo Archeologico",
        "categoria": "Museo",
        "valutazione": 4.6,
        "tags": ["museo", "arte", "storia", "archeologia", "cultura", "turismo"]
    },
    {
        "id": "POI_008",
        "id_unity": 19,
        "nome": "Libreria Feltrinelli",
        "categoria": "Libreria",
        "valutazione": 4.5,
        "tags": ["libri", "lettura", "cultura", "fumetti", "regalo"]
    },
    {
        "id": "POI_009",
        "id_unity": 9,
        "nome": "Cinema UCI",
        "categoria": "Cinema",
        "valutazione": 4.4,
        "tags": ["cinema", "film", "intrattenimento", "popcorn", "serata", "vedere_film", "guardare", "pellicola", "multisala"]
    },

    # === INDUSTRIA ===
    {
        "id": "POI_010",
        "id_unity": 6,
        "nome": "Fabbrica Meccanica.",
        "categoria": "Industria",
        "valutazione": 3.5,
        "tags": ["fabbrica", "industria", "lavoro", "meccanica"]
    },
    {
        "id": "POI_011",
        "id_unity": 18,
        "nome": "Officina Russo",
        "categoria": "Auto",
        "valutazione": 4.1,
        "tags": ["officina", "auto", "meccanico", "riparazioni", "manutenzione"]
    },

    # === SALUTE ===
    {
        "id": "POI_012",
        "id_unity": 7,
        "nome": "Farmacia Centrale",
        "categoria": "Salute",
        "valutazione": 4.5,
        "tags": ["farmacia", "medicina", "farmaci", "salute", "emergenza"]
    },
    {
        "id": "POI_013",
        "id_unity": 14,
        "nome": "Ospedale Civico",
        "categoria": "Salute",
        "valutazione": 4.0,
        "tags": ["ospedale", "pronto_soccorso", "medici", "cure", "emergenza", "salute"]
    },

    # === TRASPORTI ===
    {
        "id": "POI_014",
        "id_unity": 8,
        "nome": "Fermata Autobus",
        "categoria": "Trasporti",
        "valutazione": 3.8,
        "tags": ["autobus", "trasporto", "pubblico", "fermata", "attesa"]
    },

    # === INTRATTENIMENTO ===
    {
        "id": "POI_015",
        "id_unity": 15,
        "nome": "Discopub Dragon",
        "categoria": "Intrattenimento",
        "valutazione": 4.3,
        "tags": ["discoteca", "musica", "ballo", "drink", "serata", "divertimento", "bere", "cocktail", "aperitivo", "birra"]
    },

    # === NATURA ===
    {
        "id": "POI_016",
        "id_unity": 10,
        "nome": "Parco della Favorita",
        "categoria": "Natura",
        "valutazione": 4.8,
        "tags": ["parco", "natura", "passeggiata", "verde", "relax", "aria_aperta"]
    },
    {
        "id": "POI_017",
        "id_unity": 11,
        "nome": "Giardino Inglese",
        "categoria": "Natura",
        "valutazione": 4.6,
        "tags": ["giardino", "natura", "fiori", "passeggiata", "relax", "verde"]
    },

    # === SERVIZI ===
    {
        "id": "POI_018",
        "id_unity": 12,
        "nome": "Banca Intesa Sanpaolo",
        "categoria": "Servizi",
        "valutazione": 3.9,
        "tags": ["banca", "soldi", "bancomat", "finanziamenti", "servizi", "prelevare", "denaro", "atm", "contanti"]
    },

    # === ALIMENTARI ===
    {
        "id": "POI_019",
        "id_unity": 13,
        "nome": "Supermercato Conad",
        "categoria": "Alimentari",
        "valutazione": 4.2,
        "tags": ["supermercato", "spesa", "alimentari", "prodotti", "conveniente"]
    },
    {
        "id": "POI_020",
        "id_unity": 20,
        "nome": "Panificio San Francesco",
        "categoria": "Alimentari",
        "valutazione": 4.7,
        "tags": ["pane", "panificio", "cornetti", "dolci", "colazione", "forno"]
    },

    # === RISTORAZIONE ===
    {
        "id": "POI_021",
        "id_unity": 21,
        "nome": "Pizzeria Da Peppe",
        "categoria": "Ristorazione",
        "valutazione": 4.5,
        "tags": ["pizza", "pizzeria", "forno_a_legna", "cena", "tradizionale"]
    },
    {
        "id": "POI_022",
        "id_unity": 22,
        "nome": "Ristorante Trattoria Siciliana",
        "categoria": "Ristorazione",
        "valutazione": 4.6,
        "tags": ["ristorante", "cucina_siciliana", "pesce", "pranzo", "cena", "tradizionale"]
    },
    {
        "id": "POI_023",
        "id_unity": 23,
        "nome": "Coffee Shop Starbucks",
        "categoria": "Bar",
        "valutazione": 4.1,
        "tags": ["caffè", "cappuccino", "colazione", "dolci", "americano", "bere", "bevande", "bar", "drink"]
    },
    {
        "id": "POI_024",
        "id_unity": 24,
        "nome": "KFC",
        "categoria": "Ristorazione",
        "valutazione": 3.9,
        "tags": ["pollo", "fritto", "fast_food", "economico", "veloce"]
    },

    # === ALLOGGIO ===
    {
        "id": "POI_025",
        "id_unity": 17,
        "nome": "Hotel Plaza",
        "categoria": "Alloggio",
        "valutazione": 4.4,
        "tags": ["hotel", "albergo", "turismo", "alloggio", "dormire", "letto", "pernottamento", "notte"]
    },

    # === ISTRUZIONE ===
    {
        "id": "POI_026",
        "id_unity": 25,
        "nome": "Scuola Elementare Giuseppe Garibaldi",
        "categoria": "Istruzione",
        "valutazione": 4.3,
        "tags": ["scuola", "istruzione", "studio", "studenti", "lezioni", "insegnanti", "educazione", "bambini", "elementare", "primaria", "imparare", "classe", "aula", "compiti"]
    },
]


# =============================================================================
# GENERI MUSICALI
# =============================================================================

GENERI_MUSICALI = ["Pop", "Rock", "Jazz", "Classica", "HipHop", "Elettronica"]


# =============================================================================
# UTENTI (uno per ogni casa)
# =============================================================================

UTENTI_TEST = [
    {
        "id": "U1",
        "nome": "Marco Rossi",
        "eta": 35,
        "genere_musicale": "Rock",
        "casa_poi_id": "POI_001",  # Casa U1
        "poi_preferiti": ["POI_021", "POI_004"],  # Pizzeria Da Peppe, Stadio
        "poi_visitati": ["POI_019", "POI_012", "POI_007"],  # Supermercato, Farmacia, Museo
    },
    {
        "id": "U2",
        "nome": "Laura Bianchi",
        "eta": 25,
        "genere_musicale": "Pop",
        "casa_poi_id": "POI_002",  # Casa U2
        "poi_preferiti": ["POI_015", "POI_006", "POI_023"],  # Discoteca, Zara, Starbucks
        "poi_visitati": ["POI_005", "POI_017", "POI_009"],  # Palestra, Giardino, Cinema
        "conditions": ["pregnancy"],  # Condizioni speciali -> forza Comfort
    },
    {
        "id": "U3",
        "nome": "Giuseppe Verdi",
        "eta": 55,
        "genere_musicale": "Classica",
        "casa_poi_id": "POI_003",  # Casa U3
        "poi_preferiti": ["POI_022", "POI_008", "POI_016"],  # Trattoria, Libreria, Parco
        "poi_visitati": ["POI_007", "POI_018", "POI_020"],  # Museo, Banca, Panificio
    },
]


# =============================================================================
# FUNZIONI SEED
# =============================================================================

async def reset_database() -> None:
    """Elimina tutti i nodi e le relazioni dal database."""
    logger.info("🗑️  Resetting database...")
    
    # Elimina tutte le relazioni e nodi
    query = "MATCH (n) DETACH DELETE n"
    
    try:
        await neo4j_driver.execute_write(query)
        logger.info("Database reset completed")
    except Exception as e:
        logger.error(f"Error resetting database: {e}")
        raise


async def create_constraints() -> None:
    """Crea vincoli di unicità."""
    constraints = [
        ("Utente", "id"),
        ("PuntoInteresse", "id"),
        ("Categoria", "nome"),
        ("Bisogno", "nome"),
        ("Tag", "nome"),
        ("GenereMusicale", "nome"),
    ]
    
    for label, prop in constraints:
        query = f"""
        CREATE CONSTRAINT IF NOT EXISTS
        FOR (n:{label})
        REQUIRE n.{prop} IS UNIQUE
        """
        try:
            await neo4j_driver.execute_write(query)
            logger.info(f"Constraint: {label}.{prop}")
        except Exception as e:
            logger.warning(f"Constraint may exist: {label}.{prop}")


async def seed_categories() -> None:
    """Inserisce le categorie."""
    query = """
    UNWIND $categorie AS cat
    MERGE (c:Categoria {nome: cat})
    """
    await neo4j_driver.execute_write(query, {"categorie": CATEGORIE})
    logger.info(f"Seeded {len(CATEGORIE)} categories")


async def seed_needs() -> None:
    """Inserisce i bisogni con relazioni SUGGERISCE."""
    for bisogno, categorie in BISOGNI.items():
        await neo4j_driver.execute_write(
            "MERGE (b:Bisogno {nome: $nome})",
            {"nome": bisogno}
        )
        
        for cat_nome, priorita in categorie:
            query = """
            MATCH (b:Bisogno {nome: $bisogno})
            MATCH (c:Categoria {nome: $categoria})
            MERGE (b)-[s:SUGGERISCE]->(c)
            SET s.priorita = $priorita
            """
            await neo4j_driver.execute_write(
                query,
                {"bisogno": bisogno, "categoria": cat_nome, "priorita": priorita}
            )
    
    logger.info(f"Seeded {len(BISOGNI)} needs")


async def seed_pois() -> None:
    """Inserisce i POI con categoria e tag."""
    for poi in POI_DATI:
        # Crea POI con categoria
        query_poi = """
        MERGE (p:PuntoInteresse {id: $id})
        SET p.nome = $nome,
            p.id_unity = $id_unity,
            p.valutazione_media = $valutazione
        WITH p
        MATCH (c:Categoria {nome: $categoria})
        MERGE (p)-[:HA_CATEGORIA]->(c)
        """
        await neo4j_driver.execute_write(query_poi, {
            "id": poi["id"],
            "nome": poi["nome"],
            "id_unity": poi["id_unity"],
            "valutazione": poi["valutazione"],
            "categoria": poi["categoria"],
        })
        
        # Aggiungi tag
        for tag in poi.get("tags", []):
            query_tag = """
            MATCH (p:PuntoInteresse {id: $poi_id})
            MERGE (t:Tag {nome: $tag})
            MERGE (p)-[:HA_TAG]->(t)
            """
            await neo4j_driver.execute_write(query_tag, {
                "poi_id": poi["id"],
                "tag": tag
            })
    
    logger.info(f"Seeded {len(POI_DATI)} POIs with tags")


async def seed_music_genres() -> None:
    """Inserisce i generi musicali."""
    query = """
    UNWIND $generi AS g
    MERGE (gm:GenereMusicale {nome: g})
    """
    await neo4j_driver.execute_write(query, {"generi": GENERI_MUSICALI})
    logger.info(f"Seeded {len(GENERI_MUSICALI)} music genres")


async def seed_users() -> None:
    """Inserisce gli utenti con preferenze, casa e visite."""
    for user in UTENTI_TEST:
        # Crea utente
        conditions = user.get("conditions", [])
        await neo4j_driver.execute_write("""
            MERGE (u:Utente {id: $id})
            SET u.nome = $nome,
                u.eta = $eta,
                u.conditions = $conditions
        """, {
            "id": user["id"],
            "nome": user["nome"],
            "eta": user["eta"],
            "conditions": conditions,
        })
        
        # Relazione ABITA -> Casa
        await neo4j_driver.execute_write("""
            MATCH (u:Utente {id: $user_id})
            MATCH (p:PuntoInteresse {id: $casa_id})
            MERGE (u)-[:ABITA]->(p)
        """, {"user_id": user["id"], "casa_id": user["casa_poi_id"]})
        
        # Preferenza musicale
        if user.get("genere_musicale"):
            await neo4j_driver.execute_write("""
                MATCH (u:Utente {id: $user_id})
                MATCH (g:GenereMusicale {nome: $genere})
                MERGE (u)-[:PIACE]->(g)
            """, {"user_id": user["id"], "genere": user["genere_musicale"]})
        
        # Preferenze POI (PIACE solo verso POI!)
        for poi_id in user.get("poi_preferiti", []):
            await neo4j_driver.execute_write("""
                MATCH (u:Utente {id: $user_id})
                MATCH (p:PuntoInteresse {id: $poi_id})
                MERGE (u)-[:PIACE]->(p)
            """, {"user_id": user["id"], "poi_id": poi_id})
        
        # POI visitati (HA_VISITATO)
        for poi_id in user.get("poi_visitati", []):
            await neo4j_driver.execute_write("""
                MATCH (u:Utente {id: $user_id})
                MATCH (p:PuntoInteresse {id: $poi_id})
                MERGE (u)-[:HA_VISITATO]->(p)
            """, {"user_id": user["id"], "poi_id": poi_id})
        
        logger.info(f"Seeded user: {user['id']}")
    
    logger.info(f"Seeded {len(UTENTI_TEST)} users")


async def seed_all() -> None:
    """Esegue tutto il seeding (con reset completo della KB)."""
    logger.info("Starting Neo4j seed...")
    
    try:
        await neo4j_driver.connect()
        
        # RESET COMPLETO della KB
        await reset_database()
        
        await create_constraints()
        await seed_categories()
        await seed_needs()
        await seed_pois()
        await seed_music_genres()
        await seed_users()
        
        logger.info("✅ Seed completed!")
        
    except Exception as e:
        logger.error(f"Seed failed: {e}")
        raise
    finally:
        await neo4j_driver.disconnect()


if __name__ == "__main__":
    setup_logging()
    asyncio.run(seed_all())
