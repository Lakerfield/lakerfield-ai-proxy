# Lakerfield AI Proxy — Implementation Plan

This document tracks the full implementation roadmap for the **Lakerfield AI Proxy** project — an intelligent AI Load Balancer in C#/.NET.

---

## Phase Overview

| Phase | Status | Description |
|-------|--------|-------------|
| 1 | ✅ Done | Basis Proxy (MVP) |
| 2 | ✅ Done | Load Balancing |
| 3 | 🔲 Stub created | Request Logging |
| 4 | 🔲 Stub created | Realtime Dashboard |
| 5 | 🔲 Planned | Polish & Docker |

---

## ✅ Phase 1 — Basis Proxy (MVP)

- [x] Solution & project created under `/src` (`Lakerfield.AiProxy.slnx`)
- [x] ASP.NET Core 8 web application
- [x] OpenAI-compatible endpoints:
  - `GET /v1/models` — geaggregeerde lijst van alle modellen
  - `POST /v1/chat/completions` — chat completions (gebruikt door Claude Code)
  - `POST /v1/completions` — text completions
  - `POST /api/chat` — Ollama native chat endpoint
  - `POST /api/generate` — Ollama native generate endpoint
- [x] Streaming responses correct doorgestuurd (chunked transfer / SSE)
- [x] `appsettings.json` met Ollama instances configuratie
- [x] YARP geïntegreerd als dependency (voor eventuele toekomstige YARP-configuratie)

---

## ✅ Phase 2 — Load Balancing

- [x] `OllamaRegistryService` — thread-safe instantie-beheer & model-registry
- [x] `LoadBalancerService` — least-connections selectie met round-robin fallback
- [x] Health checks — `OllamaHealthCheckService` background service die elke 30s `/api/tags` aanroept
- [x] Active connection tracking — Interlocked counters per instantie
- [x] Model-aware routing — requests voor specifiek model gaan naar instanties met dat model
- [x] Retry logic — bij fout op instantie A, probeer instantie B (max 2 retries)
- [x] Model update via health check response — models dynamisch bijgewerkt vanuit `/api/tags`

---

## 🔲 Phase 3 — Request Logging (stub aangemaakt)

**Stub locaties:**
- `src/Lakerfield.AiProxy/Services/RequestLogService.cs`
- `src/Lakerfield.AiProxy/Middleware/RequestLoggingMiddleware.cs`

**Al geïmplementeerd (stub):**
- [x] Per-dag folder structuur aangemaakt (`logs/yyyy-MM-dd/`)
- [x] `RequestLogEntry` model met alle velden
- [x] `RequestLoggingMiddleware` meet request duration en vangt errors
- [x] `RequestLogService.LogRequestAsync()` schrijft naar `requests.jsonl` of `errors.jsonl`
- [x] Log directory wordt aangemaakt bij startup

**Nog te implementeren (Phase 3 volledige PR):**
- [ ] Volledige request body logging (met configurable max-size)
- [ ] Response body sampling voor token counting
- [ ] Token counting integratie (parse usage uit Ollama response)
- [ ] Log rotation & cleanup (configurable retention)
- [ ] Structured query API voor log entries
- [ ] Metrics aggregation (requests/min, avg latency, etc.)

**Log directory structuur:**
```
logs/
├── 2026-03-02/
│   ├── requests.jsonl
│   └── errors.jsonl
```

**Per log entry format:**
```json
{
  "timestamp": "2026-03-02T14:32:01Z",
  "requestId": "uuid",
  "endpoint": "/v1/chat/completions",
  "model": "llama3",
  "routedTo": "ollama-1",
  "durationMs": 1234,
  "inputTokens": 150,
  "outputTokens": 320,
  "statusCode": 200,
  "streaming": true
}
```

---

## 🔲 Phase 4 — Realtime Dashboard (stub aangemaakt)

**Stub locaties:**
- `src/Lakerfield.AiProxy/Hubs/RequestMonitorHub.cs`
- `src/Lakerfield.AiProxy/wwwroot/index.html`

**Al geïmplementeerd (stub):**
- [x] `RequestMonitorHub` SignalR hub met events:
  - `RequestReceived` — nieuw request binnengekomen
  - `RequestForwarded` — doorgestuurd naar welke instantie
  - `RequestCompleted` — afgerond met duurtijd & tokens
  - `RequestFailed` — fout opgetreden
- [x] `RequestMonitorService` voor broadcasting vanuit controllers
- [x] `wwwroot/index.html` basis dashboard met SignalR JS client
- [x] Live event stream in dashboard
- [x] Statistieken teller (total, completed, failed, active)

**Nog te implementeren (Phase 4 volledige PR):**
- [ ] Instance status overzicht (gezonde/ongezonde instances, active connections)
- [ ] Throughput grafieken (requests/s over tijd)
- [ ] Model usage statistieken
- [ ] Filtering en zoeken in event stream
- [ ] Dashboard authenticatie (optioneel)
- [ ] Dark/light mode toggle
- [ ] Export van statistieken

---

## 🔲 Phase 5 — Polish & Docker

**Nog te implementeren:**
- [ ] `Dockerfile` voor containerisatie
- [ ] `docker-compose.yml` met proxy + meerdere Ollama instances
- [ ] Configuration validation bij startup (valideer BaseUrl format, etc.)
- [ ] Unit tests voor `LoadBalancerService` en `OllamaRegistryService`
- [ ] Integration tests
- [ ] README uitbreiden met Docker instructies
- [ ] Health endpoint `/health` voor monitoring tools
- [ ] Metrics endpoint `/metrics` (Prometheus formaat)
- [ ] CORS configuratie voor dashboard
- [ ] Rate limiting
- [ ] Authentication voor proxy endpoints (API key validation)

---

## Architectuur

```
Claude Code / OpenAI Client
         │
         ▼
┌─────────────────────────────────┐
│     Lakerfield.AiProxy          │  ← ASP.NET Core 8
│  - ProxyController              │    OpenAI-compatible endpoints
│  - LoadBalancerService          │    Least-connections + retry
│  - OllamaRegistryService        │    Instance & model registry
│  - OllamaHealthCheckService     │    Background health checks
│  - RequestLoggingMiddleware     │    Disk logging (Phase 3)
│  - RequestMonitorHub (SignalR)  │    Realtime dashboard (Phase 4)
└────────────┬────────────────────┘
             │
    ┌────────┼────────┐
    ▼        ▼        ▼
Ollama 1  Ollama 2  Ollama 3
(llama3)  (mistral) (phi3)
```

---

## Configuratie

```json
{
  "AiProxy": {
    "LogDirectory": "./logs",
    "LoadBalancingStrategy": "LeastConnections",
    "OllamaInstances": [
      {
        "Name": "ollama-1",
        "BaseUrl": "http://localhost:11434",
        "Models": ["llama3", "mistral"]
      }
    ]
  }
}
```
