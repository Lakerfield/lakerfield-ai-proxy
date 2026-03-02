# Build stage
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and project files first for layer caching
COPY src/Lakerfield.AiProxy.slnx ./
COPY src/Lakerfield.AiProxy/Lakerfield.AiProxy.csproj Lakerfield.AiProxy/

RUN dotnet restore Lakerfield.AiProxy/Lakerfield.AiProxy.csproj

# Copy remaining source and publish
COPY src/Lakerfield.AiProxy/ Lakerfield.AiProxy/
RUN dotnet publish Lakerfield.AiProxy/Lakerfield.AiProxy.csproj \
    -c Release \
    -o /app/publish \
    --no-restore

# Runtime stage
FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

# Install curl for health check
RUN apt-get update && apt-get install -y --no-install-recommends curl && rm -rf /var/lib/apt/lists/*

# Create logs directory
RUN mkdir -p /app/logs

# Copy published output
COPY --from=build /app/publish .

# Expose proxy port
EXPOSE 8080

# Health check
HEALTHCHECK --interval=30s --timeout=5s --start-period=10s --retries=3 \
    CMD curl -f http://localhost:8080/health || exit 1

ENV ASPNETCORE_URLS=http://+:8080
ENV DOTNET_RUNNING_IN_CONTAINER=true

ENTRYPOINT ["dotnet", "Lakerfield.AiProxy.dll"]
