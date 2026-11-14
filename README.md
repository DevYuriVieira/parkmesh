# ParkMesh

Motor de roteamento dinâmico de vagas e telemetria IoT em tempo real para estacionamentos verticais de grande porte, construído em **.NET 10 (C# 14)**.

## Visão Geral

- **Ingestão IoT de alta frequência:** Sensores de baia publicando estado via **MQTT** com controle de *backpressure* (`System.Threading.Channels`) e filtro anti-oscilação (*anti-flapping*).
- **Roteamento em grafo multi-andar (A\*):** Cálculo da melhor vaga compatível com o perfil do veículo (`Standard`, `EV`, `SUV`, `Pcd`, `Moto`) e rota curva a curva considerando congestionamento dinâmico das vias.
- **Concorrência física com Lease + Fencing Token (Redis):** Reserva temporária de 90s via script Lua atômico e **re-roteamento automático ao vivo** caso outra viatura ocupe a vaga reservada fisicamente.
- **Painéis de LED & Simulação Visual via SignalR:** Atualização em tempo real por deltas de setas luminosas nos corredores e movimentação dos veículos em canvas interativo.

## Como Rodar (Fases 1 e 2)

```bash
# Subir Redis, PostgreSQL e Mosquitto
docker compose -f infra/docker-compose.yml up -d

# Terminal 1 — Motor Central + API (porta 5000)
dotnet run --project src/ParkMesh.Api

# Terminal 2 — Worker de Ingestão MQTT (debounce + channel backpressure)
dotnet run --project src/ParkMesh.TelemetryWorker

# Terminal 3 — Simulador de Frota, Ruído Ultrassônico & Invasão de Vagas
dotnet run --project src/ParkMesh.Simulator

# Rodar testes unitários
dotnet test tests/ParkMesh.UnitTests

# Rodar testes de integração (requer Docker ativo para Testcontainers.Redis)
dotnet test tests/ParkMesh.IntegrationTests
```

## Documentação

- [Arquitetura Detalhada e Blueprint Técnico](docs/ARCHITECTURE.md)
