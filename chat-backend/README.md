# TaxiChat Backend — Guida Rapida

Backend FastAPI + WebSocket per il progetto “Taxi autonomo” (Unity ↔ backend ↔ UI web).

## Avvio veloce

```bash
cd chat-backend
python -m venv venv
source venv/bin/activate  # macOS/Linux
pip install -r requirements.txt
cp .env.example .env
python -m app.neo4j.seed
uvicorn app.main:app --reload --port 8000
```

Apri l’app:  
`http://127.0.0.1:8000/app/mobile.html`

## Avvio per usare il telefono (stessa rete)

Avvia il server su tutte le interfacce:

```bash
uvicorn app.main:app --reload --host 0.0.0.0 --port 8000
```

Dal telefono (stessa rete Wi‑Fi):

```
http://<IP_PC>:8000/app/mobile.html
```

Esempio:
```
http://192.168.178.24:8000/app/mobile.html
```

Se sul telefono risulta “Disconnesso”:
- verifica che PC e telefono siano sulla stessa rete (no rete “ospiti”)
- controlla firewall/porta 8000
- il WebSocket usa l’host della pagina (non localhost), quindi basta aprire l’URL corretto

## Configurazione (.env)

Copia `.env.example` in `.env` e imposta:

- `NEO4J_URI` (es. `bolt://localhost:7687`)
- `NEO4J_USER` (default: `neo4j`)
- `NEO4J_PASSWORD`
- `OLLAMA_BASE_URL` (es. `http://localhost:11434`)
- `OLLAMA_MODEL` (es. `qwen2.5:3b-instruct`)

## Neo4j (seed)

```bash
python -m app.neo4j.seed
```

## Ollama (opzionale)

```bash
ollama pull qwen2.5:3b-instruct
ollama serve
```

## Endpoint principali

- Web UI: `http://<host>:8000/app/mobile.html`
- WebSocket: `ws://<host>:8000/ws`
- POI search: `http://<host>:8000/api/pois/search?q=...`

## Test WebSocket (quick)

```bash
# brew install websocat
websocat ws://localhost:8000/ws

# messaggio utente
echo '{"type":"user_message","session_id":"S1","user_id":"U1","ride_id":"R1","city":"Palermo","taxi":{"x":0,"y":0},"text":"Ciao"}' | websocat ws://localhost:8000/ws
```

## Git — guida rapida

### Pull (quando il collega ha pushato)
```bash
git status -sb
git pull --rebase
```

### Lavorare e pushare
```bash
git status -sb
git add -A
git commit -m "Descrizione chiara"
git push
```

### Solo alcuni file
```bash
git add path/to/file1 path/to/file2
git commit -m "Descrizione"
git push
```

### Conflitti durante il pull
```bash
git status -sb
# risolvi i conflitti
git add <file_risolto>
git rebase --continue
git push
```

### Annullare un rebase
```bash
git rebase --abort
```

## Struttura progetto (sintesi)

```
chat-backend/
├── app/
│   ├── main.py            # FastAPI + WebSocket
│   ├── session_store.py   # Stato sessioni
│   ├── llm/               # Intenti e tool LLM
│   ├── neo4j/             # Driver + repo + seed
│   ├── services/          # Policy + business logic
│   └── utils/             # Logging, timing, helpers
├── test-client/
│   └── mobile.html        # UI web
├── .env.example
├── requirements.txt
└── README.md
```
