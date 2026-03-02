# Lakerfield AI Proxy

An intelligent AI Load Balancer in C#/.NET 8 that forwards requests to multiple Ollama instances, fully compatible with the OpenAI API (as used by Claude Code).

## Features

- **OpenAI-compatible API** — drop-in replacement for OpenAI clients pointing to Ollama
- **Intelligent Load Balancing** — least-connections routing with round-robin fallback
- **Model-aware routing** — requests are routed only to instances that have the requested model
- **Health monitoring** — background health checks every 30s via `/api/tags`
- **Retry logic** — automatic retry on a different instance if one fails
- **Request logging** — per-day JSONL logs on disk (Phase 3 stub)
- **Realtime dashboard** — SignalR-powered live monitoring (Phase 4 stub)

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
| `/` | GET | Realtime monitoring dashboard |

## Getting Started

### Prerequisites

- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)
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
    ]
  }
}
```

### Run

```bash
cd src/Lakerfield.AiProxy
dotnet run
```

The proxy listens on `http://0.0.0.0:8080` by default.

### Use with Claude Code

Configure Claude Code to point to the proxy:

```bash
export ANTHROPIC_BASE_URL=http://localhost:8080
```

Or in your Claude Code configuration, set the OpenAI base URL to `http://localhost:8080`.

### Dashboard

Open `http://localhost:8080` in your browser to view the realtime monitoring dashboard.

## Project Structure

```
src/
├── Lakerfield.AiProxy/
│   ├── Controllers/
│   │   └── ProxyController.cs           # OpenAI-compatible endpoints + proxy logic
│   ├── Hubs/
│   │   └── RequestMonitorHub.cs         # SignalR hub for realtime monitoring
│   ├── Middleware/
│   │   └── RequestLoggingMiddleware.cs  # Request logging to disk
│   ├── Models/
│   │   ├── AiProxyOptions.cs            # Configuration model
│   │   ├── OllamaInstance.cs            # Instance state model
│   │   ├── RequestLogEntry.cs           # Log entry model
│   │   └── ProxyRequest.cs             # In-flight request model
│   ├── Services/
│   │   ├── LoadBalancerService.cs       # Least-connections + round-robin
│   │   ├── OllamaRegistryService.cs     # Instance & model registry
│   │   ├── HealthCheckService.cs        # Background health checks
│   │   └── RequestLogService.cs         # Disk logging per day
│   ├── wwwroot/
│   │   └── index.html                  # Realtime dashboard
│   ├── appsettings.json
│   └── Program.cs
```

## Implementation Roadmap

See [PLAN.md](PLAN.md) for the full implementation roadmap across all 5 phases.

- ✅ **Phase 1** — Basis Proxy (MVP): OpenAI-compatible endpoints, streaming
- ✅ **Phase 2** — Load Balancing: health checks, least-connections, retry
- 🔲 **Phase 3** — Request Logging (stub created)
- 🔲 **Phase 4** — Realtime Dashboard (stub created)
- 🔲 **Phase 5** — Docker, unit tests, production polish

## Tech Stack

| Component | Technology |
|-----------|------------|
| Framework | ASP.NET Core 8 / .NET 8 |
| Reverse Proxy | YARP (`Yarp.ReverseProxy`) |
| Realtime Dashboard | SignalR |
| Logging | Serilog + custom disk logger |
| Frontend | Vanilla HTML/JS + SignalR JS client |
| Configuration | `appsettings.json` |