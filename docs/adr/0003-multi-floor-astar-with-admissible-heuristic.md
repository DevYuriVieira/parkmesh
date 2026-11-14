# ADR 0003 — Multi-Floor A\* Pathfinding with Admissible Euclidean Heuristic

- **Status:** Accepted
- **Date:** 2026-11-13
- **Context Drivers:** Shortest-cost vehicle routing across stacked garage floors, dynamic avoidance of congested aisles, real-time mid-route rerouting.

## Context

A physical parking garage is not flat Euclidean space: vehicles must obey one-way aisle geometry, navigate helical inter-floor ramps (`RampUp`, `RampDown`), match vehicle attributes (`EV`, `PRM`, `Compact`, `VIP`, `Standard`), and avoid gridlocked lanes during rush-hour arrivals. Plain BFS/Dijkstra over all bays ignores geometry toward destinations, whereas naive straight-line heuristics ignore vertical floor transitions and congestion weights.

## Decision

Model the garage as a directed weighted graph (`ParkingGraph`) and route vehicles with `AStarPathfinder` (`IPathfinder`):

1. Represent spatial coordinates $(x, y, \text{floor})$ per `GraphNode` and model ramps with explicit `GraphEdge` weights and `EdgeType.Ramp`.
2. Combine static edge distances with dynamic traffic load multipliers exposed by `IEdgeCongestionTracker` so heavily utilized aisles incur higher traversal costs.
3. Compute the heuristic $h(n, \text{goal})$ using 2D Euclidean distance $\sqrt{(x_n - x_g)^2 + (y_n - y_g)^2}$ plus `Math.Abs(floor_n - floor_g) * floorHeightCost`, remaining admissible when ramp traversal exceeds inter-floor vertical weight.
4. Support re-anchoring searches from any interior graph node (`LastKnownNodeId`) so live rerouting after a stolen bay does not send the vehicle back to the entry gate.

## Consequences

### Positive
- Guarantees optimal lowest-cost path discovery consistent with Dijkstra parity while visiting significantly fewer vertices on large graphs.
- Naturally distributes inbound traffic across parallel aisles and alternative floors when congestion weights climb.
- Mid-route rerouting seamlessly computes detours from where the driver currently is.

### Negative
- Graph weight calibration (`floorHeightCost` vs. ramp edge weights) must preserve admissibility whenever custom garage topologies are authored.
