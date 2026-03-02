# Lakerfield AI Proxy

An intelligent AI Load Balancer in C#/.NET 8 that forwards requests to multiple Ollama instances, fully compatible with the OpenAI API (as used by Claude Code).

## Features

- **OpenAI-compatible API** — drop-in replacement for OpenAI clients pointing to Ollama
- **Intelligent Load Balancing** — least-connections routing with round-robin fallback
- **Model-aware routing** — requests are routed only to instances that have the requested model
- **Health monitoring** — background health checks every 30s via `/api/tags`
- **Retry logic** — automatic retry on a different instance if one fails
- **Request logging** — per-day JSONL logs on disk
- **Realtime dashboard** — SignalR-powered live monitoring
- **Health endpoint** — `/health` for liveness/readiness probes
- **Prometheus metrics** — `/metrics` in Prometheus text format
- **CORS support** — configurable allowed origins
- **Rate limiting** — per-IP request rate limiting (configurable)
- **API key authentication** — optional key validation on proxy endpoints
- **Docker support** — Dockerfile and docker-compose for easy deployment

## Architecture

```
Claude Code / OpenAI Client
         │
         ▼
┌─────────────────────────────────┐
│     Lakerfield.AiProxy          │  ← ASP.NET Core 8
│  - OpenAI-compatible endpoints  │
│  - Load Balancing Logic         │
│  - Request Logging Middleware   │
│  - SignalR Hub                  │
└────────────┬────────────────────┘
             │
    ┌────────┼────────┐
    ▼        ▼        ▼
Ollama 1  Ollama 2  Ollama 3
(llama3)  (mistral) (phi3)
```

## Supported Endpoints

| Endpoint | Method | Description |
|----------|--------|-------------|
| `/v1/chat/completions` | POST | Chat completions (used by Claude Code) |
| `/v1/models` | GET | Aggregated list of available models |
| `/v1/completions` | POST | Text completions |
| `/api/chat` | POST | Ollama native chat endpoint |
| `/api/generate` | POST | Ollama native generate endpoint |
| `/health` | GET | Liveness/readiness health check |
| `/metrics` | GET | Prometheus metrics |
| `/api/metrics` | GET | JSON metrics summary |
| `/api/instances` | GET | Instance status overview |
| `/api/logs` | GET | Request log query |
| `/` | GET | Realtime monitoring dashboard |

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0) (for local run)
- [Docker](https://docs.docker.com/get-docker/) (for containerized run)
- One or more running [Ollama](https://ollama.ai) instances

### Configuration

Edit `src/Lakerfield.AiProxy/appsettings.json`:

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
      },
      {
        "Name": "ollama-2",
        "BaseUrl": "http://192.168.1.100:11434",
        "Models": ["phi3", "llama3"]
      }
    ],
    "ApiKey": "",
    "CorsAllowedOrigins": [],
    "RateLimitRequestsPerMinute": 0
  }
}
```

| Option | Default | Description |
|--------|---------|-------------|
| `ApiKey` | `""` (disabled) | If set, all proxy endpoints require `X-Api-Key` header, `Authorization: Bearer <key>`, or `?api_key=` query param |
| `CorsAllowedOrigins` | `[]` (all origins) | List of allowed CORS origins, e.g. `["https://myapp.example.com"]`. Empty = allow all. |
| `RateLimitRequestsPerMinute` | `0` (disabled) | Max requests per IP per minute. `0` = no limit. |

### Run locally

```bash
cd src/Lakerfield.AiProxy
dotnet run
```

The proxy listens on `http://0.0.0.0:8080` by default.

### Run with Docker

```bash
# Build and start proxy + two Ollama instances
docker compose up -d

# View logs
docker compose logs -f proxy

# Stop everything
docker compose down
```

The proxy will be available at `http://localhost:8080`.

> **Note:** The Ollama instances start empty. Pull models after startup:
> ```bash
> docker exec ollama-1 ollama pull llama3
> docker exec ollama-2 ollama pull phi3
> ```

### Run with Docker (single container, existing Ollama)

```bash
docker build -t lakerfield-ai-proxy .
docker run -d \
  -p 8080:8080 \
  -e AiProxy__OllamaInstances__0__Name=ollama-1 \
  -e AiProxy__OllamaInstances__0__BaseUrl=http://host.docker.internal:11434 \
  -e AiProxy__OllamaInstances__0__Models__0=llama3 \
  --name lakerfield-ai-proxy \
  lakerfield-ai-proxy
```

### Use with Claude Code

Configure Claude Code to point to the proxy:

```bash
export ANTHROPIC_BASE_URL=http://localhost:8080
```

Or in your Claude Code configuration, set the OpenAI base URL to `http://localhost:8080`.

If you have `ApiKey` configured:

```bash
export ANTHROPIC_API_KEY=your-secret-key-here
```

### Dashboard

Open `http://localhost:8080` in your browser to view the realtime monitoring dashboard.

### Health Check

```bash
curl http://localhost:8080/health
# {"status":"healthy"}
```

### Prometheus Metrics

```bash
curl http://localhost:8080/metrics
# HELP aiproxy_requests_total Total number of requests in the last 60 seconds
# TYPE aiproxy_requests_total gauge
# aiproxy_requests_total 42
# ...
```

## Project Structure

```
src/
├── Lakerfield.AiProxy/
│   ├── Controllers/
│   │   ├── ProxyController.cs           # OpenAI-compatible endpoints + proxy logic
│   │   └── LogsController.cs            # Metrics, logs, instances, Prometheus /metrics
│   ├── Hubs/
│   │   └── RequestMonitorHub.cs         # SignalR hub for realtime monitoring
│   ├── Middleware/
│   │   ├── ApiKeyMiddleware.cs          # API key authentication
│   │   └── RequestLoggingMiddleware.cs  # Request logging to disk
│   ├── Models/
│   │   ├── AiProxyOptions.cs            # Configuration model
│   │   ├── OllamaInstance.cs            # Instance state model
│   │   ├── RequestLogEntry.cs           # Log entry model
│   │   └── ProxyRequest.cs              # In-flight request model
│   ├── Services/
│   │   ├── ConfigurationValidation.cs   # Startup config validation
│   │   ├── LoadBalancerService.cs       # Least-connections + round-robin
│   │   ├── OllamaRegistryService.cs     # Instance & model registry
│   │   ├── HealthCheckService.cs        # Background health checks
│   │   ├── MetricsService.cs            # In-memory metrics aggregation
│   │   ├── RequestLogService.cs         # Disk logging per day
│   │   └── LogRetentionService.cs       # Log file retention/cleanup
│   ├── wwwroot/
│   │   └── index.html                  # Realtime dashboard
│   ├── appsettings.json
│   └── Program.cs
└── Lakerfield.AiProxy.Tests/
    ├── LoadBalancerServiceTests.cs      # Unit tests for load balancer
    └── OllamaRegistryServiceTests.cs    # Unit tests for registry
Dockerfile
docker-compose.yml
docker-compose.override.yml
```

## Implementation Roadmap

See [PLAN.md](PLAN.md) for the full implementation roadmap across all 5 phases.

- ✅ **Phase 1** — Basis Proxy (MVP): OpenAI-compatible endpoints, streaming
- ✅ **Phase 2** — Load Balancing: health checks, least-connections, retry
- ✅ **Phase 3** — Request Logging: per-day JSONL, token counting, retention, metrics
- ✅ **Phase 4** — Realtime Dashboard: SignalR, live event stream, charts
- ✅ **Phase 5** — Polish & Docker: containerization, tests, auth, rate limiting, health & Prometheus endpoints

## Tech Stack

| Component | Technology |
|-----------|------------|
| Framework | ASP.NET Core 8 / .NET 8 |
| Reverse Proxy | YARP (`Yarp.ReverseProxy`) |
| Realtime Dashboard | SignalR |
| Logging | Serilog + custom disk logger |
| Frontend | Vanilla HTML/JS + SignalR JS client |
| Configuration | `appsettings.json` / environment variables |
| Container | Docker / Docker Compose |