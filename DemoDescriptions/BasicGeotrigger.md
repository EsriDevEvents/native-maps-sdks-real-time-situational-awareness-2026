# BasicGeotrigger Demo Description

## Overview

BasicGeotrigger demonstrates how to use the Geotrigger API to turn live location updates into realtime enter/exit notifications for geofence areas.

## How it Works

The implementation follows a simple event-driven pipeline:

1. A location feed provides position updates.
2. A fence geometry source defines monitored boundaries.
3. A `FenceGeotrigger` evaluates enter/exit conditions.
4. A `GeotriggerMonitor` raises notifications.

## Simulated Location Input

The sample uses a click-based custom location data source to simulate movement on the map. Each click publishes a new location update, allowing geotrigger behavior to be tested without GPS or physical movement. The same geotrigger logic can be used with real device location feeds.

## Runtime Behavior

When the simulated location enters a fence, the app selects the fence feature and shows its popup. When the location exits, the selection and popup are cleared and a farewell dialog is shown.

## Key Implementation Reference

- [Geotrigger construction](../BasicGeotrigger/MainWindow.xaml.cs#L100)
