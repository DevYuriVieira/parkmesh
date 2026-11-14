# ParkMesh — Blueprint Arquitetural

## 1. Visão Arquitetural: Modular Monolith + Workers Dedicados

Para manter latência sub-10ms no caminho crítico (`Cancela → A* → Redis Lease → SignalR`) sem adicionar *hops* de rede desnecessários entre microsserviços, o `ParkMesh` é estruturado como um **Modular Monolith** com separação de processos onde há separação real de carga:

```text
[Simulador / Sensores IoT] ──MQTT──► [ParkMesh.TelemetryWorker]
                                              │ (Channel<T> + Debounce)
[Cancela / GateFlow (HTTP/MQTT)] ────────────►▼
                                     [ParkMesh.Api / Engine] ◄──► [Redis (Leases + Cache)]
                                       │               │
                                    SignalR          EF Core
                                   (Deltas)            │
                                       ▼               ▼
                             [Dashboard Visual]   [PostgreSQL]
```

### Estrutura da Solution (`ParkMesh.sln`)

```text
ParkMesh/
├── src/
│   ├── ParkMesh.Domain/            # Grafo, Nós/Arestas, Vagas, Perfis de Veículo, Regras de Domínio
│   ├── ParkMesh.Application/       # Motor A*, Orquestrador de Alocação, Re-roteamento, Contratos
│   ├── ParkMesh.Infrastructure/    # Redis Lua Leases, MQTTnet Client, PostgreSQL/EF Core, OTel
│   ├── ParkMesh.Api/               # REST Endpoints, SignalR Hub (/hubs/parking), Health/Metrics
│   ├── ParkMesh.TelemetryWorker/   # Worker de ingestão MQTT + Bounded Channel + Anti-Flapping
│   └── ParkMesh.Simulator/         # Simulador de chegadas, movimento nas arestas e sensores/ruído
├── tests/
│   ├── ParkMesh.UnitTests/         # A*, pesos dinâmicos, heurística, debounce, regras de vaga
│   └── ParkMesh.IntegrationTests/  # Testcontainers (Redis, PostgreSQL, Mosquitto/EMQX) + corridas
├── web/                            # Dashboard & Simulador Visual 2D Multi-Andar (SignalR Client)
├── infra/
│   └── docker-compose.yml          # Mosquitto/EMQX, Redis, PostgreSQL, Prometheus, Grafana, Jaeger
└── docs/
    └── ARCHITECTURE.md
```

---

## 2. Modelagem do Grafo Multi-Andar & Algoritmo A\*

O estacionamento é modelado como um **grafo direcionado ponderado** $G = (V, E)$ carregado em memória para leitura rápida e persistido/versionado em banco para topologia.

### Tipos de Nós (`GraphNode`)
- `Gate`: Ponto de entrada/saída (cancela).
- `Intersection`: Cruzamento de corredores; pode conter um **Painel de LED** associado que aponta a próxima direção dominante.
- `Ramp`: Transição unidirecional ou bidirecional entre andares (`Floor z -> z ± 1`).
- `Bay`: Nó folha representando uma baia física com atributos:
  - `BayId` (ex.: `F2-B14`)
  - `Floor`
  - `AllowedTypes` (`Standard`, `EV`, `SUV`, `PCD`, `Moto`)
  - `Status` (`Free`, `Leased`, `Occupied`, `OutOfService`)
  - `Position (X, Y, Floor)`

### Função de Custo das Arestas (`EdgeWeight`)
O custo de atravessar uma aresta $e = (u, v)$ é dinâmico:

$$\text{cost}(u, v) = \text{baseDistance}(u, v) \times \text{rampPenalty}(e) + \text{congestionFactor}(e)$$

- **Mão única:** Arestas respeitam sentido de tráfego dos corredores.
- **Penalidade de rampa:** Mudar de andar custa mais do que andar no mesmo pavimento.
- **Congestionamento dinâmico:** Veículos atualmente roteados sobre a aresta $e$ somam um fator temporário ao peso, evitando que 20 carros recebam exatamente o mesmo corredor gargalo.
- **Heurística $h(n)$:** Distância Manhattan/Euclidiana até a vaga candidata + custo mínimo por diferença de andar (admissível para o A\*).

### Seleção da Vaga Ideal
Em vez de rodar A\* para toda vaga individualmente, usa-se **busca multi-alvo (ou pré-filtro espacial + A\* reverso/direto)**:
1. Filtra vagas `Free` compatíveis com o `VehicleType`.
2. Seleciona os $K$ melhores candidatos por distância heurística (ou roda Dijkstra/A\* multi-destino a partir da cancela parando nos primeiros $N$ alvos válidos).
3. Tenta adquirir o **Lease no Redis** em ordem de menor custo real até conseguir o lock atômico.

---

## 3. Concorrência Crítica: Lease + Fencing Token no Redis

Em vez de depender de *RedLock* puro para corretude física, o `ParkMesh` combina:
1. **Lease atômico no Redis** com expiração automática de **90 segundos** (`EX 90`).
2. **Fencing Token monotônico** (`INCR parkmesh:fence:{bayId}`) devolvido na aquisição, garantindo que apenas a alocação mais recente possa confirmar ou liberar aquela reserva.
3. **Reconciliação por evento físico do sensor:** Se outro carro entrar fisicamente na vaga reservada, o sensor soberano ganha e dispara o re-roteamento do veículo lesado.

### Contrato do Script Lua (`acquire_bay_lease.lua`)
```lua
-- KEYS[1] = "parkmesh:lease:bay:{bayId}"
-- KEYS[2] = "parkmesh:fence:bay:{bayId}"
-- ARGV[1] = vehicleSessionId
-- ARGV[2] = ttlSeconds (90)

if redis.call("EXISTS", KEYS[1]) == 1 then
    return {0, 0}
end

local token = redis.call("INCR", KEYS[2])
local payload = ARGV[1] .. ":" .. tostring(token)
redis.call("SET", KEYS[1], payload, "EX", tonumber(ARGV[2]))
return {1, token}
```

### Cenário de Corrida Física & Re-roteamento Ao Vivo
1. Sessão `V1` adquire `Lease(F1-B08, token=42, ttl=90s)` e segue pela rota `[G1 -> N2 -> N5 -> F1-B08]`.
2. Um motorista intruso (sem reserva) estaciona em `F1-B08`.
3. O sensor publica `Occupied` via MQTT.
4. O motor detecta que `F1-B08` estava `Leased` para `V1`:
   - Invalida o lease no Redis (checando token / estado físico).
   - Marca `F1-B08` como `Occupied`.
   - Toma o **último nó conhecido** de `V1` no trajeto, roda novo A\* para a melhor vaga livre (ex.: `F1-B11`) e adquire novo lease.
   - Envia `RouteReassigned` via **SignalR** para o painel/simulador, mudando as setas de LED ao vivo.

---

## 4. Ingestão IoT via MQTT & Resiliência

### Topologia de Tópicos
- `parkmesh/sensors/{floor}/{bayId}/state` (QoS 1)
  - Payload: `{ "bayId": "F1-B08", "occupied": true, "distanceCm": 42, "seq": 1094, "ts": "..." }`
- `parkmesh/gates/{gateId}/admitted` (QoS 1) — **Ponto de integração com o `GateFlow` ou Simulador**
  - Payload: `{ "eventId": "...", "gateId": "GATE-N1", "vehicleType": "EV", "plateHash": "...", "ts": "..." }`
- `parkmesh/panels/{panelId}/direction` (QoS 0/1)
  - Payload: `{ "panelId": "LED-N2", "arrow": "RIGHT", "floor": 1, "ts": "..." }`

### Pipeline no `ParkMesh.TelemetryWorker`
1. **Recepção `MQTTnet`:** Não abre transação ou lock no callback da rede; empurra o evento para um `BoundedChannel<SensorReading>` (`DropOldest` ou *backpressure* controlado por baia).
2. **Anti-Flapping / Debounce Temporal:** Sensores ultrassônicos oscilam com pedestres ou reflexos. Uma mudança de estado só é promovida para evento de domínio quando confirmada por janela estável (ex.: 2 leituras consecutivas ou > 1.5s estável).
3. **Idempotência por sequência (`seq`):** Mensagens fora de ordem ou duplicadas são descartadas.

---

## 5. Simulação Visual em Tempo Real (SignalR)

Para cumprir o requisito de **demonstração visual viva** (não apenas endpoints):

### Protocolo SignalR (`/hubs/parking`)
- **Snapshot inicial** ao conectar (`GetParkingSnapshot`): planta dos andares (nós, arestas, baias, painéis LED) + sessões ativas.
- **Deltas contínuos** (`VehicleMoved`, `BayStateChanged`, `RouteAssigned`, `RouteReassigned`, `LedPanelUpdated`, `MetricsTick`) evitando serializar o grafo inteiro a cada frame.

### Capacidades do Simulador Visual (`web/` + `ParkMesh.Simulator`)
- Visualização **2D multi-andar** com troca de pavimento (Térreo, G1, G2).
- Carros animados percorrendo as arestas da rota calculada pelo A\*.
- Vagas mudando de cor em tempo real (`Verde = Livre`, `Âmbar = Reservada com countdown 90s`, `Vermelho = Ocupada`, `Azul = EV`).
- Setas luminosas dos **Painéis de LED** nos cruzamentos piscando conforme o direcionamento do próximo veículo.
- Botões de **Caos / Teste ao vivo**:
  - *"Injetar Intruso"*: ocupa fisicamente uma vaga que está reservada para ver o **re-roteamento acontecer na tela**.
  - *"Rajada de Entrada"*: envia 50 veículos simultâneos pelas cancelas para testar contenção no Redis e pesos dinâmicos das vias.
  - Painel de métricas ao vivo: P50/P95/P99 do A\*, mensagens MQTT/s, taxa de ocupação e re-roteamentos.

---

## 6. Roadmap de Implementação (MVP Enxuto)

1. **Fase 1 — Grafo, A\* e Concorrência Redis (Core):**
   - Criar `ParkMesh.sln` com `Domain`, `Application`, `Infrastructure`, `Api` e `UnitTests`.
   - Modelar planta padrão de 3 andares (ex.: 120 vagas, 2 cancelas, rampas, vagas EV/PCD/SUV).
   - Implementar A\* + script Lua no Redis + testes de concorrência com Testcontainers.
2. **Fase 2 — MQTT, Simulador de Frota e Re-roteamento:**
   - Subir Mosquitto/EMQX + Redis + PostgreSQL no `docker-compose.yml`.
   - Implementar `TelemetryWorker` com `Channel<T>` + debounce e `ParkMesh.Simulator` simulando carros e invasões de vaga.
3. **Fase 3 — SignalR + Dashboard Visual + Observabilidade:**
   - Expor Hub SignalR com deltas e construir o canvas 2D interativo em `web/`.
   - Instrumentar OpenTelemetry (latência do A\*, tempo de lease no Redis, vazão MQTT) e rodar benchmark de carga.
