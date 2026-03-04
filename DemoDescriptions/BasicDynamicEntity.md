# BasicDynamicEntity Demo Description

## Overview

BasicDynamicEntity demonstrates how to use the DynamicEntity API to visualize and interact with realtime moving observations from a stream service.

## How it Works

The implementation follows a simple real-time entity pipeline:

1. Connect to an ArcGIS stream service.
2. Load and start the stream data source.
3. Create a `DynamicEntityLayer` from that source.
4. Add the layer to the map and render incoming observations.

## Realtime Connection

The app creates an `ArcGISStreamService`, explicitly calls `LoadAsync` and `ConnectAsync`, and then binds it to a `DynamicEntityLayer`. This keeps service connection and map rendering setup clear and explicit.

## Runtime Behavior

After the layer is added, incoming observations are rendered as dynamic entities. The UI allows interactive exploration of common dynamic entity capabilities:

- Toggle track history and track lines.
- Toggle alternate symbology for current and previous observations.
- Toggle labels using the stream service track ID field.

## Key Implementation References

- [Stream connection and layer creation](../BasicDynamicEntity/MainWindow.xaml.cs#L136)
- [Track display configuration](../BasicDynamicEntity/MainWindow.xaml.cs#L159)
- [Alternate renderer setup](../BasicDynamicEntity/MainWindow.xaml.cs#L172)
- [Label definition and enablement](../BasicDynamicEntity/MainWindow.xaml.cs#L196)
