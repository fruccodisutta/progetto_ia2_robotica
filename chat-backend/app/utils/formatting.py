SIMULATION_TIME_SCALE = 12.0

def format_duration_minutes(minutes: float) -> str:
    """Formatta una durata in minuti (REAL TIME) in tempo SIMULATO leggibile."""
    try:
        real_value = float(minutes)
    except (TypeError, ValueError):
        return "n.d."

    if real_value < 0:
        real_value = 0
        
    # Converti in tempo simulato (x12)
    sim_minutes = real_value * SIMULATION_TIME_SCALE
    
    # Formattazione
    if sim_minutes < 1:
        # Meno di 1 minuto simulato (5 sec reali)
        seconds = max(1, int(round(sim_minutes * 60)))
        return f"{seconds} sec (sim)"
    
    if sim_minutes >= 60:
        hours = int(sim_minutes // 60)
        mins = int(sim_minutes % 60)
        if mins == 0:
             return f"{hours} ore" if hours > 1 else "1 ora"
        return f"{hours}h {mins}m"
        
    # Minuti
    return f"{int(round(sim_minutes))} min"
