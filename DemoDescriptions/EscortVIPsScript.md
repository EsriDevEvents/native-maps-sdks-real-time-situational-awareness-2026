# EscortVIPs Official Demo Script

## Opening Line

This last demo brings everything together in a full multi-app realtime workflow. We have a group of VIP scientists on a walking tour during a conference break on the Esri campus, and we need to keep them safe and coordinated. We’ll use Escort and VIP mobile apps plus a command-and-control dashboard, powered by Geotriggers and Dynamic Entities in the ArcGIS Maps SDK for Native Apps.

## Scenario Setup

We have two main cooperating components:

- Field apps for one Escort and multiple VIPs
- A Command Dashboard for operational visibility and control

The field apps are the primary focus: each role publishes and consumes shared operational messages over the field network channel so every client stays synchronized in real time. The simulation engine is only a backup path because of demo / venue constraints.

## What to Say About Field Network

Think of this as a live field network workflow:

- **Live field layer**: Escort and VIP clients exchange role/status/location messages so everyone shares the same operational state.
- **Command layer**: The dashboard ingests those live updates and renders a synchronized operational view.

If needed for this indoor conference venue, we can switch to simulation briefly, but the architecture and behaviors remain the same.

## What to Say on the Field Apps Screen

What you are seeing here are role-based field clients.

- The Escort app represents the lead security unit.
- The VIP apps represent each scientist in the group.
- Each app continuously shares location and status telemetry.

On the VIP clients, status is generated from two geotrigger rings centered on the escort:

- Green (`IN`): VIP is inside the inner warning ring.
- Amber (`WARNING`/`DANGER`): VIP is inside outer perimeter but outside inner ring.
- Red (`OUT`): VIP is outside the escort perimeter.

Those statuses are computed in the field app geotrigger pipeline and published as status updates over websocket.

## Transition to Dashboard

Now I’ll switch to the command dashboard, where those same updates are aggregated into a single operational view.

## What to Say on the Dashboard Screen

The dashboard ingests field updates, publishes dynamic entities, and renders the current state of escort and VIP units on the map.

This gives us two complementary capabilities in one workflow:

- **Geotriggers** tell us when meaningful spatial events happen.
- **Dynamic Entities** show continuous motion and current tracked state.

Together, that is realtime situational awareness: event detection plus live operational context.

## Event Flow Narrative (short version)

1. Field apps register with role and device identity.
2. Simulation engine sends assigned locations and route snapshots.
3. VIP geotriggers evaluate proximity to the escort perimeter.
4. VIP status transitions are published (`IN`, `DANGER`, `OUT`).
5. Dashboard republishes and visualizes the current dynamic entity state.

## Optional Deep-Dive References (if asked)

- [Simulation publish loop](../EscortVIPs/SimulationEngine/Program.cs#L284)
- [Field geotrigger setup](../EscortVIPs/FieldMobileApp/MainPage.Geotriggers.cs#L26)
- [Field status publish](../EscortVIPs/FieldMobileApp/MainPage.Geotriggers.cs#L205)
- [Dashboard dynamic publish](../EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L226)
- [Shared message contracts](../EscortVIPs/CommandMessaging/Payloads.cs#L3)

## Closing Line

EscortVIPs shows how to scale from a single-device sample to a coordinated multi-app system where geotriggers drive alerts and dynamic entities drive live command awareness.

## TODO

- Dashboard:
  - Add method to show full track of a VIP on demand
- Escort:
- VIP:
