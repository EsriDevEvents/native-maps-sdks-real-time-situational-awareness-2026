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
- Custom simulated location source class: [SimulationLocationDataSource](EscortVIPs/FieldMobileApp/Location/SimulationLocationDataSource.cs#L7)
- Location data source startup (system/simulated switching): [StartLocationDataSourceAsync](EscortVIPs/FieldMobileApp/MainPage.xaml.cs#L632)
- Geotrigger monitor setup for VIP perimeter checks: [EnsureVipGeotriggerMonitorAsync](EscortVIPs/FieldMobileApp/MainPage.xaml.cs#L739)
- Outer/inner fence geotriggers from location feed: [FenceGeotrigger creation](EscortVIPs/FieldMobileApp/MainPage.xaml.cs#L757)
- Geotrigger notifications to status transitions: [HandleVipFenceNotificationAsync](EscortVIPs/FieldMobileApp/MainPage.xaml.cs#L802)
- Assign target location in simulation mode: [SetAssignedLocation](EscortVIPs/FieldMobileApp/Location/SimulationLocationDataSource.cs#L81)
- Custom DynamicEntity source for escort/VIP telemetry: [EscortVipDynamicEntityDataSource](EscortVIPs/FieldMobileApp/RealTime/EscortVipDynamicEntityDataSource.cs#L8)
- Wire custom DynamicEntity source into field app: [EnsureEscortVipDynamicEntityDataSourceAsync](EscortVIPs/FieldMobileApp/MainPage.xaml.cs#L1347)
- Publish incoming VIP telemetry to dynamic entities: [PublishVipTelemetry](EscortVIPs/FieldMobileApp/RealTime/EscortVipDynamicEntityDataSource.cs#L37)

## EscortVIPs — Dashboard snippets
- Custom DynamicEntity data source class: [MockDynamicEntityDataSource](EscortVIPs/CommandDashboard/RealTime/MockDynamicEntityDataSource.cs#L8)
- Attach dynamic entity layer to map: [DynamicEntityLayer initialization](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L66)
- Push current escort/VIP state into layer: [PublishDynamicEntities](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L226)
- Publish per-VIP updates: [PublishVipUnit](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L239)
