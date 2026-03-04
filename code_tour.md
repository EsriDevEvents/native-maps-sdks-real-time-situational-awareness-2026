# Code Tour Bookmarks

This document provides quick links to key implementation points across the sample apps.

## BasicGeotrigger
- Geotrigger startup: [MainWindow.InitializeAsync](BasicGeotrigger/MainWindow.xaml.cs#L60)
- Fence geotrigger construction (feed, fence parameters, Arcade message): [CreateAndInitializeGeotrigger](BasicGeotrigger/MainWindow.xaml.cs#L60)
- Geotrigger enter/exit handling in UI: [Geotrigger_Notification](BasicGeotrigger/MainWindow.xaml.cs#L91)
- Click-based location source implementation: [ClickLocationDataSource](BasicGeotrigger/ClickLocationDataSource.cs#L14)
- Convert click geometry to location updates: [OnClick](BasicGeotrigger/ClickLocationDataSource.cs#L95)
- Bind click source to map location display/editor: [ClickLocationDataSource.Create](BasicGeotrigger/ClickLocationDataSource.cs#L106)
- Runtime and API key bootstrap: [App constructor](BasicGeotrigger/App.xaml.cs#L36)

## BasicDynamicEntity
- Stream connection and dynamic entity layer creation: [CreateAndConnectAsync](BasicDynamicEntity/MainWindow.xaml.cs#L129)
- Switch to alternate renderers from JSON symbols: [UseAlternateRenderers](BasicDynamicEntity/MainWindow.xaml.cs#L165)
- Enable and configure labels for tracked entities: [ShowLabels](BasicDynamicEntity/MainWindow.xaml.cs#L180)
- Runtime initialization and authentication setup: [OnStartup](BasicDynamicEntity/App.xaml.cs#L9)

## EscortVIPs
- Simulation engine host entry point: [SimulationEngineHost](EscortVIPs/SimulationEngine/Program.cs#L20)
- Simulation WebSocket client handling: [HandleClientAsync](EscortVIPs/SimulationEngine/Program.cs#L148)
- Dashboard messaging host startup: [FieldMessagingHost.StartAsync](EscortVIPs/CommandDashboard/Services/Messaging/FieldMessagingHost.cs#L53)
- Dashboard message ingress and registration flow: [FieldMessagingHost.HandleClientAsync](EscortVIPs/CommandDashboard/Services/Messaging/FieldMessagingHost.cs#L170)
- Role-based relay logic (Escort/VIP): [FieldMessagingHost.RelayIfNeededAsync](EscortVIPs/CommandDashboard/Services/Messaging/FieldMessagingHost.cs#L295)
- Dashboard state orchestration: [MainViewModel](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L22)
- Dynamic VIP filter example: [ApplyVipFilterAsync](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L134)
- Publish unit state to map entities: [PublishDynamicEntities](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L226)
- Shared message envelope serialization: [MessageSerializer](EscortVIPs/CommandMessaging/MessageSerializer.cs#L12)

## Suggested reading order
1. BasicGeotrigger
2. BasicDynamicEntity
3. EscortVIPs
