# EscortVIPs Demo Description

## Overview

EscortVIPs demonstrates a multi-app realtime situational awareness workflow that combines Geotriggers and Dynamic Entities across three running components:

- Simulation engine (assigned locations, route snapshots, control messages)
- Field mobile apps (escort and VIP clients)
- Command dashboard (operational view and command controls)

## Multi-App Architecture

The apps communicate over websocket message envelopes and shared payload types. This keeps transport and app-specific behavior decoupled while allowing role-specific clients to react to the same realtime stream.

## How it Works

The end-to-end flow follows this pipeline:

1. Field apps register and connect to websocket endpoints.
2. The simulation engine publishes escort/VIP assigned locations and route snapshots.
3. Field apps ingest location updates and evaluate escort perimeter geotriggers.
4. Field apps publish VIP telemetry and status transitions.
5. The dashboard ingests current unit state and republishes dynamic entities for map visualization.

## Runtime Behavior

- VIP and escort units are simulated in coordinated motion.
- Geotrigger ring notifications determine VIP status transitions relative to escort perimeter rules.
- Dynamic entity layers on both field and dashboard views render moving entities and current state.
- Dashboard commands can influence VIP behavior through control messages.

## Key Implementation References

- [Simulation engine lifecycle and loops](../EscortVIPs/SimulationEngine/Program.cs#L67)
- [Per-tick assigned location publishing](../EscortVIPs/SimulationEngine/Program.cs#L284)
- [Field app location source startup](../EscortVIPs/FieldMobileApp/MainPage.Location.cs#L13)
- [Field app perimeter geotrigger setup](../EscortVIPs/FieldMobileApp/MainPage.Geotriggers.cs#L26)
- [Field app ring notification handling](../EscortVIPs/FieldMobileApp/MainPage.Geotriggers.cs#L74)
- [Field app VIP status publication](../EscortVIPs/FieldMobileApp/MainPage.Geotriggers.cs#L205)
- [Field app dynamic entity source wiring](../EscortVIPs/FieldMobileApp/MainPage.StatusAndDynamics.cs#L99)
- [Dashboard view model realtime publish flow](../EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L226)
- [Dashboard per-VIP dynamic publish](../EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L239)
- [Shared message envelope and payload contracts](../EscortVIPs/CommandMessaging/FieldMessageEnvelope.cs#L5)

## Why this Pattern Matters

EscortVIPs demonstrates how Geotriggers and Dynamic Entities complement each other in a production-like, multi-client system: geotriggers provide actionable spatial event logic, while dynamic entities provide continuous movement context and map-centric operational visibility.

## Reset

- Run task: `Stop EscortVIPs Demo Stack`
