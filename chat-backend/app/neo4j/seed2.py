"""
Script per seeding aggiuntivo: Zone e Policy per il pathfinding pesato.

Aggiunge:
- Zone della città con tipo di superficie
- Policy di guida (Comfort, Sport, Eco)
- Relazioni STRUGGLES_ON (penalty per A*)

Eseguire con: python -m app.neo4j.seed2
NOTA: Non fa reset! Aggiunge ai dati esistenti.
"""

import asyncio
from app.neo4j.driver import neo4j_driver
from app.utils.logging import get_logger, setup_logging

logger = get_logger(__name__)


# =============================================================================
# ZONE DELLA CITTÀ
# Devono matchare con le CityZone in Unity (kbZoneId)
# =============================================================================

ZONES = [
    {
        "id": "centro_storico",
        "name": "Centro Storico",
        "surface": "Cobblestone",  # Sanpietrino/Pavé
        "type": "Tourist"
    },
    {
        "id": "residenziale",
        "name": "Zona Residenziale",
        "surface": "SpeedBumps",  # Dossi rallentatori
        "type": "Residential"
    },
    {
        "id": "suburbana",
        "name": "Zona Suburbana",
        "surface": "Smooth",  # Asfalto liscio
        "type": "Suburban"
    },
    {
        "id": "industriale",
        "name": "Zona Industriale",
        "surface": "Uneven",  # Strada dissestata
        "type": "Work"
    },
]


# =============================================================================
# POLICY DI GUIDA
# =============================================================================

POLICIES = [
    {
        "id": "comfort", 
        "name": "Comfort",
        "max_speed": 9.0,
        "acceleration": 0.9,
        "brake_power": 35.0,
        "steering_speed": 6.0,
        "consumption_multiplier": 1.0
    },
    {
        "id": "sport", 
        "name": "Sport",
        "max_speed": 9.0,
        "acceleration": 1.2,
        "brake_power": 35.0,
        "steering_speed": 6.0,
        "consumption_multiplier": 1.2
    },
    {
        "id": "eco", 
        "name": "Eco",
        "max_speed": 9.0,
        "acceleration": 0.7,
        "brake_power": 35.0,
        "steering_speed": 6.0,
        "consumption_multiplier": 0.7
    },
]


# =============================================================================
# RELAZIONI POLICY -> ZONE (STRUGGLES_ON)
# penalty = moltiplicatore per il costo degli archi nella zona
# 1.0 = nessuna penalità, 2.0 = doppio costo, 3.0 = triplo, etc.
# =============================================================================

STRUGGLES_ON = [
    # COMFORT: Odia vibrazioni (Sanpietrino) e scossoni (Dissestato, Dossi)
    {"policy": "Comfort", "surfaces": ["Cobblestone", "Uneven", "SpeedBumps"], "penalty": 2.5},
    
    # SPORT: Odia rallentamenti fisici (Dossi e Buche)
    {"policy": "Sport", "surfaces": ["SpeedBumps", "Uneven"], "penalty": 2.0},
    
    # ECO: Per ora nessuna penalità basata su superficie
    # (potrebbe preferire percorsi più corti o con meno stop-and-go)
]


# =============================================================================
# REGOLE CONDIZIONI -> POLICY (hardcoded per semplicità)
# Se un utente ha una di queste condizioni, la policy è forzata
# =============================================================================

CONDITION_POLICY_RULES = {
    "pregnancy": {
        "forces_policy": "Comfort",
        "reason": "Per la tua sicurezza, utilizziamo la modalità Comfort 🛋️"
    },
    # Aggiungi altre condizioni se necessario
    # "disability": {"forces_policy": "Comfort", "reason": "..."},
}




# =============================================================================
# FUNZIONI SEED
# =============================================================================

async def seed_zones() -> None:
    """Inserisce le zone della città."""
    for zone in ZONES:
        query = """
        MERGE (z:Zone {id: $id})
        SET z.name = $name,
            z.surface = $surface,
            z.type = $type
        """
        await neo4j_driver.execute_write(query, zone)
        logger.info(f"Seeded zone: {zone['name']} ({zone['surface']})")
    
    logger.info(f"Seeded {len(ZONES)} zones")


async def seed_policies() -> None:
    """Inserisce le policy di guida."""
    for policy in POLICIES:
        query = """
        MERGE (p:Policy {id: $id})
        SET p.name = $name,
            p.max_speed = $max_speed,
            p.acceleration = $acceleration,
            p.brake_power = $brake_power,
            p.steering_speed = $steering_speed,
            p.consumption_multiplier = $consumption_multiplier
        """
        await neo4j_driver.execute_write(query, policy)
        logger.info(f"Seeded policy: {policy['name']}")
    
    logger.info(f"Seeded {len(POLICIES)} policies")


async def seed_struggles_on() -> None:
    """Crea le relazioni STRUGGLES_ON tra Policy e Zone."""
    for rel in STRUGGLES_ON:
        query = """
        MATCH (p:Policy {name: $policy})
        MATCH (z:Zone)
        WHERE z.surface IN $surfaces
        MERGE (p)-[r:STRUGGLES_ON]->(z)
        SET r.penalty = $penalty
        RETURN p.name AS policy, z.name AS zone, r.penalty AS penalty
        """
        result = await neo4j_driver.execute_write(query, {
            "policy": rel["policy"],
            "surfaces": rel["surfaces"],
            "penalty": rel["penalty"]
        })
        
        # Log delle relazioni create
        for record in result:
            logger.info(f"  {record['policy']} -[STRUGGLES_ON {record['penalty']}x]-> {record['zone']}")
    
    logger.info(f"Seeded {len(STRUGGLES_ON)} STRUGGLES_ON relationships")


async def seed_weather_rules() -> None:
    """Crea nodi Context e regole MAKES_HAZARDOUS per il meteo (Rain)."""
    
    # 1. Crea nodo Context Rain
    await neo4j_driver.execute_write("""
        MERGE (c:Context {name: "Rain"})
        SET c.type = "Weather"
    """)
    
    # 2. Regole specifiche per superficie
    # Cobblestone -> 3.0
    await neo4j_driver.execute_write("""
        MATCH (c:Context {name: "Rain"}), (z:Zone {surface: "Cobblestone"})
        MERGE (c)-[r:MAKES_HAZARDOUS]->(z)
        SET r.risk_factor = 3.0
    """)
    
    # Uneven -> 2.0
    await neo4j_driver.execute_write("""
        MATCH (c:Context {name: "Rain"}), (z:Zone {surface: "Uneven"})
        MERGE (c)-[r:MAKES_HAZARDOUS]->(z)
        SET r.risk_factor = 2.0
    """)
    
    # Smooth/Regular -> 1.2
    await neo4j_driver.execute_write("""
        MATCH (c:Context {name: "Rain"}), (z:Zone)
        WHERE z.surface IN ["Smooth", "Regular"]
        MERGE (c)-[r:MAKES_HAZARDOUS]->(z)
        SET r.risk_factor = 1.2
    """)
    
    # SpeedBumps -> 1.5 
    await neo4j_driver.execute_write("""
        MATCH (c:Context {name: "Rain"}), (z:Zone {surface: "SpeedBumps"})
        MERGE (c)-[r:MAKES_HAZARDOUS]->(z)
        SET r.risk_factor = 1.5
    """)


async def seed_time_rules() -> None:
    """Crea contesti temporali (Scuola, Stadio, Fabbrica) e regole ZTL."""
    
    # 1. Associazioni POI -> Zona (per nodi critici)
    # Necessario per permettere alle regole di congestione di inferire la zona correttamente 
    # se seed2.py viene lanciato senza attendere il sync di Unity.
    
    poi_zone_fixes = [
        {"id_unity": 25, "zone_id": "centro_storico", "desc": "Scuola"},
        {"id_unity": 3, "zone_id": "centro_storico", "desc": "Stadio"},
        {"id_unity": 6, "zone_id": "industriale", "desc": "Fabbrica"}
    ]
    
    for fix in poi_zone_fixes:
        await neo4j_driver.execute_write(f"""
            MATCH (p:PuntoInteresse {{id_unity: {fix['id_unity']}}})
            MATCH (z:Zone {{id: "{fix['zone_id']}"}})
            MERGE (p)-[:LOCATED_IN]->(z)
        """)
        logger.info(f"Assigned {fix['desc']} (id_unity: {fix['id_unity']}) to zone: {fix['zone_id']}")

    # 2. Definisci Contesti Temporali
    # Nota: 7.5 = 7:30. Neo4j gestisce correttamente i float.
    time_contexts = [
        {"name": "SchoolEntry", "type": "Time", "start": 7.5, "end": 8.5, "desc": "Entrata Scuola"},
        {"name": "SchoolExit", "type": "Time", "start": 13.0, "end": 14.0, "desc": "Uscita Scuola"},
        {"name": "MatchNight", "type": "Event", "start": 20.0, "end": 23.0, "desc": "Partita Serale"},
        {"name": "FactoryShift_Morning", "type": "Time", "start": 7.0, "end": 8.0, "desc": "Turno Fabbrica Mattina"},
        {"name": "FactoryShift_Evening", "type": "Time", "start": 17.0, "end": 18.0, "desc": "Turno Fabbrica Sera"},
        # ZTL Attiva solo di giorno (8:00 - 20:00)
        {"name": "ZTL_Active", "type": "ZTL_Rule", "start": 8.0, "end": 20.0, "desc": "ZTL Attiva"}
    ]
    
    # Crea nodi Context
    query_contexts = """
    UNWIND $contexts AS ctx
    MERGE (c:Context {name: ctx.name})
    SET c.type = ctx.type,
        c.start = ctx.start, 
        c.end = ctx.end,
        c.description = ctx.desc
    """
    await neo4j_driver.execute_write(query_contexts, {"contexts": time_contexts})
    
    # 3. Regole di Congestione [:CONGESTS] - Inferiamo le zone dai POI
    # Ora che abbiamo garantito LOCATED_IN per i nodi critici sopra, queste query funzioneranno sempre.
    await neo4j_driver.execute_write("""
        MATCH (school:PuntoInteresse {id_unity: 25})-[:LOCATED_IN]->(z:Zone)
        MATCH (c1:Context {name: "SchoolEntry"})
        MATCH (c2:Context {name: "SchoolExit"})
        MERGE (c1)-[r1:CONGESTS]->(z)
        MERGE (c2)-[r2:CONGESTS]->(z)
        SET r1.multiplier = 10.0, r2.multiplier = 10.0
    """)
    
    await neo4j_driver.execute_write("""
        MATCH (stadium:PuntoInteresse {id_unity: 3})-[:LOCATED_IN]->(z:Zone)
        MATCH (c:Context {name: "MatchNight"})
        MERGE (c)-[r:CONGESTS]->(z)
        SET r.multiplier = 20.0
    """)
    
    await neo4j_driver.execute_write("""
        MATCH (factory:PuntoInteresse {id_unity: 6})-[:LOCATED_IN]->(z:Zone)
        MATCH (c1:Context {name: "FactoryShift_Morning"})
        MATCH (c2:Context {name: "FactoryShift_Evening"})
        MERGE (c1)-[r1:CONGESTS]->(z)
        MERGE (c2)-[r2:CONGESTS]->(z)
        SET r1.multiplier = 5.0, r2.multiplier = 5.0
    """)
    
    # 3. ZTL Perk (Time Dependent)
    # Colleghiamo il Context ZTL alla zona
    query_ztl = """
    MATCH (c:Context {name: "ZTL_Active"})
    MATCH (z:Zone {id: "centro_storico"})
    SET z.type = "ZTL"
    MERGE (c)-[p:GRANTS_PERK]->(z)
    SET p.traffic_reduction = 0.6, 
        p.description = "ZTL Active: Low traffic for Taxi"
    
    // Rimuovi vecchie relazioni statiche se esistono
    WITH z
    MATCH (z)-[old:HAS_PERK]->(z)
    DELETE old
    """
    await neo4j_driver.execute_write(query_ztl)
    
    # 4. Note: POI Locations for critical nodes are now handled at the beginning of this function
    # to ensure dynamic rule inference works even without Unity sync.
    # Unity will still perform its sync but it will find the relationships already existing or overwrite them.
    
    logger.info("Seeded Time Rules (School, Stadium, Factory) & ZTL (Time-Dependent)")


async def seed_zones_and_policies() -> None:
    """Esegue il seeding di zone e policy (senza reset)."""
    logger.info("Starting Zone & Policy seed...")
    
    try:
        await neo4j_driver.connect()
        
        # Crea constraints
        constraints = [
            ("Zone", "id"),
            ("Policy", "id"),
        ]
        for label, prop in constraints:
            try:
                await neo4j_driver.execute_write(f"""
                    CREATE CONSTRAINT IF NOT EXISTS
                    FOR (n:{label})
                    REQUIRE n.{prop} IS UNIQUE
                """)
            except Exception:
                pass  # Constraint may already exist
        
        await seed_zones()
        await seed_policies()
        await seed_struggles_on()
        await seed_weather_rules()
        await seed_time_rules()
        
        logger.info("✅ Zone & Policy seed completed!")
        
    except Exception as e:
        logger.error(f"Seed failed: {e}")
        raise
    finally:
        await neo4j_driver.disconnect()


# =============================================================================
# QUERY HELPER (per uso dal backend)
# =============================================================================

async def get_user_conditions(user_id: str) -> list[str]:
    """
    Ottiene le condizioni speciali di un utente dalla KB.
    """
    query = """
    MATCH (u:Utente {id: $user_id})
    RETURN u.conditions AS conditions
    """
    results = await neo4j_driver.execute_query(query, {"user_id": user_id})
    
    if results and results[0].get("conditions"):
        return results[0]["conditions"]
    return []


def get_effective_policy(requested_policy: str, user_conditions: list[str]) -> tuple[str, str | None]:
    """
    Determina la policy effettiva considerando le condizioni dell'utente.
    """
    # Controlla se una condizione forza una policy diversa
    for condition in user_conditions:
        if condition in CONDITION_POLICY_RULES:
            rule = CONDITION_POLICY_RULES[condition]
            return (rule["forces_policy"], rule["reason"])
    
    # Nessun override, usa la policy richiesta
    return (requested_policy, None)


async def get_zone_multipliers_with_context(
    policy_name: str, 
    weather: str | None,
    hour: float = 12.0
) -> dict[str, float]:
    """
    Calcola i moltiplicatori combinando:
    1. Policy (Comfort/Sport/Eco)
    2. Weather (Rain -> Hazard)
    3. Time/Event (School, Stadium, Factory -> Congestion)
    4. Perks (ZTL -> Reduction ONLY if active)
    """
    
    weather_condition = weather.capitalize() if weather else "clear"
    
    query = """
    MATCH (z:Zone)
    
    // 1. Policy Base
    OPTIONAL MATCH (p:Policy {name: $policy})-[r1:STRUGGLES_ON]->(z)
    WITH z, COALESCE(r1.penalty, 1.0) AS PolicyPenalty
    
    // 2. Weather Malus
    OPTIONAL MATCH (c:Context {name: $weather})-[r2:MAKES_HAZARDOUS]->(z)
    WITH z, PolicyPenalty, COALESCE(r2.risk_factor, 1.0) AS WeatherPenalty
    
    // 3. Time/Event Congestion
    // Cerca contesti attivi ORA (start <= hour <= end)
    OPTIONAL MATCH (t:Context)-[r3:CONGESTS]->(z)
    WHERE t.start <= $hour AND $hour <= t.end
    // Raccogliamo i nomi degli eventi attivi
    WITH z, PolicyPenalty, WeatherPenalty, 
         collect(t.name) as TimeEvents, 
         MAX(COALESCE(r3.multiplier, 1.0)) AS TimePenalty
    
    // 4. ZTL Perk (Time Dependent)
    // Controlla se c'è un benefit ZTL attivo ORA
    OPTIONAL MATCH (ztl:Context)-[r4:GRANTS_PERK]->(z)
    WHERE ztl.start <= $hour AND $hour <= ztl.end
    WITH z, PolicyPenalty, WeatherPenalty, TimePenalty, TimeEvents, COALESCE(r4.traffic_reduction, 1.0) AS PerkFactor
    
    // Calcolo Finale
    // Base * Weather * Time * Perk
    RETURN z.id AS zone_id, 
           z.name AS zone_name,
           PolicyPenalty,
           WeatherPenalty,
           TimePenalty,
           TimeEvents,
           PerkFactor,
           (PolicyPenalty * WeatherPenalty * TimePenalty * PerkFactor) AS final_multiplier
    """
    
    try:
        results = await neo4j_driver.execute_query(query, {
            "policy": policy_name, 
            "weather": weather_condition,
            "hour": float(hour)
        })
        
        # Log dettagliato per debug
        logger.info(f"\n--- TRAFFIC ANALYSIS (Policy: {policy_name}, Weather: {weather_condition}, Hour: {hour:.2f}) ---")
        for r in results:
            mult = r["final_multiplier"]
            events = r["TimeEvents"]
            
            # Logga solo se c'è qualcosa di interessante (mult != 1.0 o eventi attivi)
            if mult != 1.0 or events:
                event_str = f"Events: {events}" if events else "No Events"
                logger.info(
                    f"Zone: {r['zone_name']:<15} | Mult: {mult:5.2f}x | "
                    f"Base: {r['PolicyPenalty']:.1f} * Wth: {r['WeatherPenalty']:.1f} * "
                    f"Time: {r['TimePenalty']:.1f} ({event_str}) * Perk: {r['PerkFactor']:.1f}"
                )
        logger.info("----------------------------------------------------------------------------------\n")
        
        return {r["zone_id"]: r["final_multiplier"] for r in results}
        
    except Exception as e:
        logger.error(f"Error calculating context multipliers: {e}")
        return {}


if __name__ == "__main__":
    setup_logging()
    asyncio.run(seed_zones_and_policies())
