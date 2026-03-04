# Code Tour Bookmarks

This document provides quick links to Geotrigger and DynamicEntity-related implementation points, including custom data source patterns.

## BasicGeotrigger
- Geotrigger startup: [MainWindow.InitializeAsync](BasicGeotrigger/MainWindow.xaml.cs#L32)
- Fence geotrigger construction (feed, fence parameters, Arcade message): [CreateAndInitializeGeotrigger](BasicGeotrigger/MainWindow.xaml.cs#L60)
- Geotrigger notification handler: [Geotrigger_Notification](BasicGeotrigger/MainWindow.xaml.cs#L94)
- Click-based location source implementation: [ClickLocationDataSource](BasicGeotrigger/ClickLocationDataSource.cs#L14)
- Convert click geometry to location updates: [OnClick](BasicGeotrigger/ClickLocationDataSource.cs#L95)
- Bind click source to map location display/editor: [ClickLocationDataSource.Create](BasicGeotrigger/ClickLocationDataSource.cs#L106)

## BasicDynamicEntity
- Stream connection and dynamic entity layer creation: [CreateAndConnectAsync](BasicDynamicEntity/MainWindow.xaml.cs#L129)
- Switch to alternate renderers from JSON symbols: [UseAlternateRenderers](BasicDynamicEntity/MainWindow.xaml.cs#L165)
- Enable and configure labels for tracked entities: [ShowLabels](BasicDynamicEntity/MainWindow.xaml.cs#L180)
- Runtime initialization and authentication setup: [OnStartup](BasicDynamicEntity/App.xaml.cs#L9)

## EscortVIPs — Field app snippets
- Simulated location source implementation: [SimulationLocationDataSource](EscortVIPs/FieldMobileApp/Location/SimulationLocationDataSource.cs#L7)
- Start location source for device role/mode: [StartLocationDataSourceAsync](EscortVIPs/FieldMobileApp/MainPage.Location.cs#L13)
- Build escort perimeter geotrigger monitors: [EnsurePerimeterMonitorsAsync](EscortVIPs/FieldMobileApp/MainPage.Geotriggers.cs#L26)
- Create per-ring geotrigger from location feed + fence parameters: [CreateFenceMonitor](EscortVIPs/FieldMobileApp/MainPage.Geotriggers.cs#L153)
- Handle ring notifications and convert to VIP status transitions: [HandleRingNotificationAsync](EscortVIPs/FieldMobileApp/MainPage.Geotriggers.cs#L74)
- Dynamic entity source for escort/VIP telemetry: [EscortVipDynamicEntityDataSource](EscortVIPs/FieldMobileApp/RealTime/EscortVipDynamicEntityDataSource.cs#L8)
- Wire field app to dynamic entity feed: [EnsureEscortVipDynamicEntityDataSourceAsync](EscortVIPs/FieldMobileApp/MainPage.StatusAndDynamics.cs#L99)
- Publish VIP telemetry updates into dynamic entities: [PublishVipTelemetry](EscortVIPs/FieldMobileApp/RealTime/EscortVipDynamicEntityDataSource.cs#L37)

## EscortVIPs — Dashboard snippets
- Custom DynamicEntity data source class: [MockDynamicEntityDataSource](EscortVIPs/CommandDashboard/RealTime/MockDynamicEntityDataSource.cs#L8)
- Attach dynamic entity layer to map: [DynamicEntityLayer initialization](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L66)
- Push current escort/VIP state into layer: [PublishDynamicEntities](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L226)
- Publish per-VIP updates: [PublishVipUnit](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L239)
