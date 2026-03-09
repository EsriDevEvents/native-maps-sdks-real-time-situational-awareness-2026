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
- VIP: Build escort perimeter geotrigger monitors: [EnsurePerimeterMonitorsAsync](EscortVIPs/FieldMobileApp/MainPage.Geotriggers.cs#L30)
- VIP: Update escort location fence: [UpdateEscortFenceAsync](EscortVIPs/FieldMobileApp/MainPage.Geotriggers.cs#L102)
- Escort: Dynamic entity source for VIP roster: [EscortVipDynamicEntityDataSource](EscortVIPs/FieldMobileApp/RealTime/EscortVipDynamicEntityDataSource.cs#L8)

## EscortVIPs — Dashboard snippets
- Attach dynamic entity layer to map: [DynamicEntityLayer initialization](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L53)
- Query: [ApplyVipFilterAsync](EscortVIPs/CommandDashboard/ViewModels/MainViewModel.cs#L144)
