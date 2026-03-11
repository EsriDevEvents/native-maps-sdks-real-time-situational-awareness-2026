using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommandMessaging;
using CommandDashboard.Models;
using CommandDashboard.RealTime;
using CommandDashboard.Services;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Symbology;
using System.Windows;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Esri.ArcGISRuntime.RealTime;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using DrawingColor = System.Drawing.Color;
using WpfPoint = System.Windows.Point;
using WpfLineSegment = System.Windows.Media.LineSegment;

namespace CommandDashboard.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private TourDynamicEntityDataSource dynamicEntityDataSource = null!;
    private readonly HashSet<string> connectedDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> registeredFieldAppDeviceIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<DynamicEntity> subscribedDynamicEntities = new(ReferenceEqualityComparer.Instance);
    private readonly object dynamicEntitySubscriptionGate = new();
    private HashSet<string>? activeVipFilterTrackIds;
    private string? escortDeviceId;
    private List<MapPoint> mainRoutePoints = [];

    [ObservableProperty]
    private bool hasEscortPosition;

    public MainViewModel(IDashboardDataService dashboardDataService)
    {
        VipUnits = new ObservableCollection<FieldUnitStatus>();
        VipPanelUnits = new ObservableCollection<FieldUnitStatus>();
        FilteredVipUnits = new ObservableCollection<FieldUnitStatus>();
        VipUnits.CollectionChanged += (_, _) => RecalculateAggregateState();
        VipUnits.CollectionChanged += OnVipUnitsCollectionChanged;
        EscortUnit = dashboardDataService.CreateEscortUnit();
        EscortUnit.DisplayName = string.Empty;
        EscortUnit.Status = UnitStatus.Unknown;
        EscortUnit.DistanceMeters = 0;
        EscortUnit.DirectionToEscort = "-";
        MainMap = new Map(BasemapStyle.ArcGISStreets);

        InitializeDynamicEntityLayer();

        sessionId = $"DEVSUMMIT-2026-{DateTime.Now:HHmm}";
        RecalculateAggregateState();
        EscortConnectionState = "Disconnected";
        VipWhereClause = "name = \"Tesla\"";
        VipFilterStatus = "Filter: showing all VIPs";
    }

    private void InitializeDynamicEntityLayer()
    {
        // Custom DynamicEntityDataSource built from VIP and Escort location and status updates
        dynamicEntityDataSource = new TourDynamicEntityDataSource();

        // create the main renderer for the DynamicEntityLayer
        var vipSymbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, DrawingColor.FromArgb(236, 239, 241), 14)
        {
            Outline = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, DrawingColor.FromArgb(38, 50, 56), 1.5)
        };
        var escortSymbol = CreateEscortShieldSymbol();

        var unitRenderer = new UniqueValueRenderer(
            fieldNames: ["role"],
            uniqueValues: [new UniqueValue("Escort", "Escort", escortSymbol, "Escort")],
            defaultLabel: "VIP",
            defaultSymbol: vipSymbol);

        // Create the DynamicEntityLayer with the custom data source and renderer, and add it to the map
        DynamicEntityLayer = new DynamicEntityLayer(dynamicEntityDataSource)
        {
            Renderer = unitRenderer
        };
        MainMap.OperationalLayers.Add(DynamicEntityLayer);

        // Connect to the DynamicEntityDataSource and subscribe to incoming DynamicEntity observations
        _ = dynamicEntityDataSource.ConnectAsync();
        dynamicEntityDataSource.DynamicEntityReceived += (_, eventArgs) => HandleDynamicEntityReceived(eventArgs);
        PublishDynamicEntities();
    }

    public ObservableCollection<FieldUnitStatus> VipUnits { get; }

    public ObservableCollection<FieldUnitStatus> VipPanelUnits { get; }

    public ObservableCollection<FieldUnitStatus> FilteredVipUnits { get; }

    public FieldUnitStatus EscortUnit { get; }

    [ObservableProperty]
    private string sessionId = string.Empty;

    [ObservableProperty]
    private bool isAnyVipOut;

    [ObservableProperty]
    private bool isAnyVipDanger;

    [ObservableProperty]
    private Map mainMap;

    [ObservableProperty]
    private DynamicEntityLayer dynamicEntityLayer = null!;

    [ObservableProperty]
    private string escortConnectionState = "Disconnected";

    [ObservableProperty]
    private int totalVIPs;

    [ObservableProperty]
    private string vipOverallStatus = "All VIPs are inside the security perimeter.";

    [ObservableProperty]
    private string vipPerimeterSummary = "0 of 0 VIPs inside the security perimeter";

    [ObservableProperty]
    private string vipWhereClause = "name = \"Tesla\"";

    [ObservableProperty]
    private string vipFilterStatus = "Filter: showing all VIPs";

    [ObservableProperty]
    private bool isVipFilterActive;

    [RelayCommand]
    private void Refresh()
    {
        RecalculateAggregateState();
        PublishDynamicEntities();
        RebuildFilteredVipUnits();
    }

    [RelayCommand]
    private async Task ApplyVipFilterAsync()
    {
        var whereClause = string.IsNullOrWhiteSpace(VipWhereClause)
            ? "role = 'VIP'"
            : VipWhereClause.Trim();

        try
        {
            // Query the DynamicEntityDataSource for VIP entities matching the where clause filter
            // - DynamicEntityQueryParameters also supports spatial filters and track Id queries
            var queryParameters = new DynamicEntityQueryParameters
            {
                WhereClause = whereClause
            };

            // QueryDynamicEntitiesAsync will return dynamic entities whose latest observation satisfies the query parameters
            // - the result is a snapshot in time of the DynamicEntityDataSource at the moment of the query
            var queryResult = await dynamicEntityDataSource.QueryDynamicEntitiesAsync(queryParameters, CancellationToken.None);

            // filter the list
            var matchedTrackIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var dynamicEntity in queryResult)
            {
                var trackId = ReadAttributeAsString(dynamicEntity.Attributes, "trackId");
                if (!string.IsNullOrWhiteSpace(trackId))
                {
                    matchedTrackIds.Add(trackId);
                }
            }

            activeVipFilterTrackIds = matchedTrackIds;
            IsVipFilterActive = true;
            RebuildFilteredVipUnits();
            VipFilterStatus = $"Filter: {matchedTrackIds.Count} VIP(s) match '{whereClause}'";
        }
        catch (Exception ex)
        {
            VipFilterStatus = $"Filter error: {ex.Message}";
        }
    }

    [RelayCommand]
    private void ClearVipFilter()
    {
        activeVipFilterTrackIds = null;
        IsVipFilterActive = false;
        VipWhereClause = "name = \"Tesla\"";
        RebuildFilteredVipUnits();
        VipFilterStatus = "Filter: showing all VIPs";
    }

    private void UnitOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FieldUnitStatus.Status) || e.PropertyName == nameof(FieldUnitStatus.DistanceMeters))
        {
            RecalculateAggregateState();
        }

        if (sender is FieldUnitStatus unit && unit.Role == UnitRole.Vip)
        {
            RebuildFilteredVipUnits();
        }
    }

    private void OnVipUnitsCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        RebuildFilteredVipUnits();
    }

    private void RebuildFilteredVipUnits()
    {
        FilteredVipUnits.Clear();

        foreach (var vipUnit in VipUnits)
        {
            if (vipUnit.Role != UnitRole.Vip)
                continue;

            if (activeVipFilterTrackIds is not null && !activeVipFilterTrackIds.Contains(vipUnit.DisplayName))
                continue;

            FilteredVipUnits.Add(vipUnit);
        }
    }

    private void RecalculateAggregateState()
    {
        var outOfPerimeterCount = VipUnits.Count(unit => unit.Status == UnitStatus.Out);

        IsAnyVipOut = outOfPerimeterCount > 0;
        IsAnyVipDanger = VipUnits.Any(unit => unit.Status == UnitStatus.Warning);
        TotalVIPs = VipUnits.Count;
        VipOverallStatus = IsAnyVipOut
            ? "One or more VIPs are outside the security perimeter."
            : IsAnyVipDanger
                ? "One or more VIPs are near the perimeter boundary."
                : "All VIPs are inside the security perimeter.";

        VipPerimeterSummary = outOfPerimeterCount > 0
            ? $"{outOfPerimeterCount} of {TotalVIPs} VIPs outside the security perimeter"
            : $"{TotalVIPs} of {TotalVIPs} VIPs inside the security perimeter";
    }

    private void PublishDynamicEntities()
    {
        if (ShouldPublishEscort())
        {
            dynamicEntityDataSource.PublishUnit(EscortUnit);
        }

        foreach (var unit in VipUnits)
        {
            PublishVipUnit(unit);
        }
    }

    private void PublishVipUnit(FieldUnitStatus unit)
    {
        dynamicEntityDataSource.PublishUnit(unit);
    }

    private static Symbol CreateEscortShieldSymbol()
    {
        var fallback = new TextSymbol
        {
            Text = "🛡",
            Color = DrawingColor.FromArgb(0, 86, 153),
            Size = 30,
            HaloColor = DrawingColor.FromArgb(187, 222, 251),
            HaloWidth = 2
        };

        try
        {
            var shieldPath = BuildEscortShieldMarkerImage();
            if (string.IsNullOrWhiteSpace(shieldPath) || !File.Exists(shieldPath))
            {
                return fallback;
            }

            var shieldFileUri = new Uri(Path.GetFullPath(shieldPath));
            return new PictureMarkerSymbol(shieldFileUri)
            {
                Width = 44,
                Height = 44
            };
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to create escort shield symbol. Falling back to text marker. {ex}");
            return fallback;
        }
    }

    private static string? BuildEscortShieldMarkerImage()
    {
        const int markerSize = 64;
        var outlineColor = Color.FromRgb(0, 86, 153);
        var fillColor = Color.FromRgb(241, 248, 255);
        const double viewBoxSize = 100d;
        var baseScale = markerSize / viewBoxSize;

        var outputDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CommandDashboard");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "escort-shield-custom.png");

        if (File.Exists(outputPath))
            return outputPath;

        var outlineBrush = new SolidColorBrush(outlineColor);
        outlineBrush.Freeze();

        var fillBrush = new SolidColorBrush(fillColor);
        fillBrush.Freeze();

        var outerShieldGeometry = CreateShieldGeometry();
        outerShieldGeometry.Transform = new MatrixTransform(baseScale, 0, 0, baseScale, 0, 0);
        outerShieldGeometry.Freeze();

        var innerShieldGeometry = CreateShieldGeometry();
        const double innerInsetScale = 0.86d;
        const double viewBoxCenter = 50d;
        var innerScale = baseScale * innerInsetScale;
        var innerOffset = (viewBoxCenter * (1 - innerInsetScale)) * baseScale;
        innerShieldGeometry.Transform = new MatrixTransform(innerScale, 0, 0, innerScale, innerOffset, innerOffset);
        innerShieldGeometry.Freeze();

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawGeometry(outlineBrush, null, outerShieldGeometry);
            context.DrawGeometry(fillBrush, null, innerShieldGeometry);
        }

        var bitmap = new RenderTargetBitmap(markerSize, markerSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(outputPath))
        {
            encoder.Save(stream);
        }

        return outputPath;
    }

    private static PathGeometry CreateShieldGeometry()
    {
        var start = new WpfPoint(50, 6);
        var figure = new PathFigure
        {
            StartPoint = start,
            IsClosed = true,
            IsFilled = true
        };

        figure.Segments.Add(new BezierSegment(new WpfPoint(62, 12), new WpfPoint(76, 16), new WpfPoint(88, 18), true));
        figure.Segments.Add(new WpfLineSegment(new WpfPoint(88, 44), true));
        figure.Segments.Add(new BezierSegment(new WpfPoint(88, 69), new WpfPoint(73, 88), new WpfPoint(50, 97), true));
        figure.Segments.Add(new BezierSegment(new WpfPoint(27, 88), new WpfPoint(12, 69), new WpfPoint(12, 44), true));
        figure.Segments.Add(new WpfLineSegment(new WpfPoint(12, 18), true));
        figure.Segments.Add(new BezierSegment(new WpfPoint(24, 16), new WpfPoint(38, 12), new WpfPoint(50, 6), true));

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        return geometry;
    }

    public void ApplyFieldMessage(FieldMessageEnvelope envelope)
    {
        switch (envelope.Type)
        {
            case MessageTypes.RouteSnapshot:
                {
                    var payload = MessageSerializer.DeserializePayload<RouteSnapshotPayload>(envelope);
                    if (payload is null)
                        break;

                    var points = payload.Points
                        .Where(point => !double.IsNaN(point.Latitude) && !double.IsNaN(point.Longitude))
                        .Select(point => new MapPoint(point.Longitude, point.Latitude, SpatialReferences.Wgs84))
                        .ToList();

                    mainRoutePoints = points;
                    break;
                }
            case MessageTypes.Register:
                {
                    var payload = MessageSerializer.DeserializePayload<RegisterPayload>(envelope);
                    if (payload is null)
                        break;

                    if (string.IsNullOrWhiteSpace(envelope.DeviceId))
                        break;

                    if (!IsFieldAppClient(payload))
                        break;

                    registeredFieldAppDeviceIds.Add(envelope.DeviceId);

                    if (!TryParseRole(payload.Role, out var role))
                    {
                        role = UnitRole.Vip;
                    }

                    if (role == UnitRole.Escort)
                    {
                        escortDeviceId = envelope.DeviceId;
                        EscortUnit.DisplayName = envelope.DeviceId;
                        EscortUnit.Status = UnitStatus.In;
                    }
                    else if (VipUnits.All(x => !string.Equals(x.DisplayName, envelope.DeviceId, StringComparison.OrdinalIgnoreCase)))
                    {
                        var unit = new FieldUnitStatus(envelope.DeviceId, UnitRole.Vip)
                        {
                            Status = UnitStatus.In,
                            DirectionToEscort = "-",
                            Latitude = EscortUnit.Latitude,
                            Longitude = EscortUnit.Longitude
                        };
                        unit.PropertyChanged += UnitOnPropertyChanged;
                        VipUnits.Add(unit);
                    }
                    else
                    {
                        var existing = ResolveUnit(envelope.DeviceId);
                        existing.Status = UnitStatus.In;
                    }

                    break;
                }
            case MessageTypes.LocationUpdate:
                {
                    if (string.IsNullOrWhiteSpace(envelope.DeviceId) || IsSimulatorDeviceId(envelope.DeviceId))
                        break;

                    if (IsEscortDevice(envelope.DeviceId))
                        break;

                    EnsureFieldDeviceRegistered(envelope.DeviceId, UnitRole.Vip);
                    if (!registeredFieldAppDeviceIds.Contains(envelope.DeviceId))
                        break;

                    var payload = MessageSerializer.DeserializePayload<LocationUpdatePayload>(envelope);
                    if (payload is null)
                        break;

                    var unit = ResolveUnit(envelope.DeviceId);
                    unit.Latitude = payload.Latitude;
                    unit.Longitude = payload.Longitude;

                    if (payload.DistanceMeters.HasValue)
                    {
                        unit.DistanceMeters = payload.DistanceMeters.Value;
                    }

                    if (!string.IsNullOrWhiteSpace(payload.DirectionToEscort))
                    {
                        unit.DirectionToEscort = payload.DirectionToEscort;
                    }

                    break;
                }
            case MessageTypes.EscortPosition:
                {
                    if (string.IsNullOrWhiteSpace(envelope.DeviceId) || IsSimulatorDeviceId(envelope.DeviceId))
                        break;

                    EnsureFieldDeviceRegistered(envelope.DeviceId, UnitRole.Escort);
                    if (!registeredFieldAppDeviceIds.Contains(envelope.DeviceId))
                        break;

                    if (string.IsNullOrWhiteSpace(escortDeviceId)
                        || !string.Equals(envelope.DeviceId, escortDeviceId, StringComparison.OrdinalIgnoreCase))
                    {
                        escortDeviceId = envelope.DeviceId;
                        EscortUnit.DisplayName = envelope.DeviceId;
                    }

                    var payload = MessageSerializer.DeserializePayload<EscortPositionPayload>(envelope);
                    if (payload is null)
                        break;

                    EscortUnit.Latitude = payload.Latitude;
                    EscortUnit.Longitude = payload.Longitude;
                    HasEscortPosition = true;
                    break;
                }
            case MessageTypes.StatusUpdate:
                {
                    if (string.IsNullOrWhiteSpace(envelope.DeviceId) || IsSimulatorDeviceId(envelope.DeviceId))
                        break;

                    if (IsEscortDevice(envelope.DeviceId))
                        break;

                    EnsureFieldDeviceRegistered(envelope.DeviceId, UnitRole.Vip);
                    if (!registeredFieldAppDeviceIds.Contains(envelope.DeviceId))
                        break;

                    var payload = MessageSerializer.DeserializePayload<StatusUpdatePayload>(envelope);
                    if (payload is null)
                        break;

                    var unit = ResolveUnit(envelope.DeviceId);
                    if (TryParseVipStatus(payload.Status, out var status))
                    {
                        unit.Status = status;
                    }

                    break;
                }
            case MessageTypes.VipTelemetry:
                {
                    if (string.IsNullOrWhiteSpace(envelope.DeviceId) || IsSimulatorDeviceId(envelope.DeviceId))
                        break;

                    if (IsEscortDevice(envelope.DeviceId))
                        break;

                    EnsureFieldDeviceRegistered(envelope.DeviceId, UnitRole.Vip);
                    if (!registeredFieldAppDeviceIds.Contains(envelope.DeviceId))
                        break;

                    var payload = MessageSerializer.DeserializePayload<VipTelemetryPayload>(envelope);
                    if (payload is null)
                        break;

                    var unit = ResolveUnit(envelope.DeviceId);
                    unit.Latitude = payload.Latitude;
                    unit.Longitude = payload.Longitude;

                    if (TryParseVipStatus(payload.Status, out var telemetryStatus))
                    {
                        unit.Status = telemetryStatus;
                    }

                    if (payload.DistanceMeters.HasValue)
                    {
                        unit.DistanceMeters = payload.DistanceMeters.Value;
                    }

                    if (!string.IsNullOrWhiteSpace(payload.DirectionToEscort))
                    {
                        unit.DirectionToEscort = payload.DirectionToEscort;
                    }

                    break;
                }
            case MessageTypes.VipControl:
                {
                    var payload = MessageSerializer.DeserializePayload<VipControlPayload>(envelope);
                    if (payload is null || string.IsNullOrWhiteSpace(payload.DeviceId))
                        break;

                    EnsureFieldDeviceRegistered(payload.DeviceId, UnitRole.Vip);
                    if (!registeredFieldAppDeviceIds.Contains(payload.DeviceId))
                        break;

                    var unit = ResolveUnit(payload.DeviceId);
                    if (!payload.IsActive
                        || string.Equals(payload.Signal, "RESUME", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(payload.Signal, "NORMAL", StringComparison.OrdinalIgnoreCase))
                    {
                        unit.OperatorControl = null;
                    }
                    else if (string.Equals(payload.Signal, "STOP", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(payload.Signal, "HURRY", StringComparison.OrdinalIgnoreCase))
                    {
                        unit.OperatorControl = payload.Signal.Trim().ToUpperInvariant();
                    }

                    break;
                }
        }

        RecalculateAggregateState();
        PublishDynamicEntities();
    }

    public Polyline? GetMainRouteGeometry()
    {
        if (mainRoutePoints.Count < 2)
            return null;

        return new Polyline(mainRoutePoints);
    }

    public void SetConnectedDevices(IEnumerable<string> deviceIds)
    {
        connectedDeviceIds.Clear();
        foreach (var deviceId in deviceIds)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
            {
                connectedDeviceIds.Add(deviceId);
            }
        }

        registeredFieldAppDeviceIds.RemoveWhere(deviceId => !connectedDeviceIds.Contains(deviceId));
    }

    public void UpdateConnectionStatus(bool hostRunning, int escortCount)
    {
        EscortConnectionState = hostRunning && escortCount > 0 ? "Connected" : "Disconnected";
    }

    private FieldUnitStatus ResolveUnit(string deviceId)
    {
        if ((!string.IsNullOrWhiteSpace(escortDeviceId) && string.Equals(deviceId, escortDeviceId, StringComparison.OrdinalIgnoreCase))
            || (!string.IsNullOrWhiteSpace(EscortUnit.DisplayName) && string.Equals(deviceId, EscortUnit.DisplayName, StringComparison.OrdinalIgnoreCase)))
        {
            return EscortUnit;
        }

        var vipUnit = VipUnits.FirstOrDefault(x => string.Equals(x.DisplayName, deviceId, StringComparison.OrdinalIgnoreCase));
        if (vipUnit is not null)
            return vipUnit;

        var created = new FieldUnitStatus(deviceId, UnitRole.Vip)
        {
            Status = UnitStatus.Unknown,
            DirectionToEscort = "-"
        };
        created.PropertyChanged += UnitOnPropertyChanged;
        VipUnits.Add(created);

        return created;
    }

    private static bool TryParseRole(string value, out UnitRole role)
    {
        if (Enum.TryParse<UnitRole>(value, ignoreCase: true, out role))
            return true;

        role = UnitRole.Vip;
        return false;
    }

    private static bool IsFieldAppClient(RegisterPayload payload)
    {
        return !string.Equals(payload.ClientType, "simulator", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSimulatorDeviceId(string deviceId)
    {
        return deviceId.StartsWith("Sim-", StringComparison.OrdinalIgnoreCase);
    }

    private void HandleDynamicEntityReceived(DynamicEntityEventArgs eventArgs)
    {
        try
        {
            var dynamicEntity = eventArgs.DynamicEntity;
            TrySubscribeDynamicEntityChanged(dynamicEntity);
            UpdateVipPanelFromDynamicEntity(dynamicEntity);
        }
        catch
        {
        }
    }

    private void OnDynamicEntityChanged(DynamicEntity dynamicEntity, DynamicEntityChangedEventArgs eventArgs)
    {
        try
        {
            UpdateVipPanelFromDynamicEntity(dynamicEntity);
        }
        catch
        {
        }
    }

    private void TrySubscribeDynamicEntityChanged(DynamicEntity dynamicEntity)
    {
        try
        {
            lock (dynamicEntitySubscriptionGate)
            {
                if (!subscribedDynamicEntities.Add(dynamicEntity))
                    return;

                dynamicEntity.DynamicEntityChanged += (_, eventArgs) => OnDynamicEntityChanged(dynamicEntity, eventArgs);
            }
        }
        catch
        {
        }
    }

    private void UpdateVipPanelFromDynamicEntity(DynamicEntity dynamicEntity)
    {
        try
        {
            var attributes = dynamicEntity.Attributes;
            var role = ReadAttributeAsString(attributes, "role");
            if (!string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase))
                return;

            var deviceId = ReadAttributeAsString(attributes, "trackId");
            if (string.IsNullOrWhiteSpace(deviceId))
                return;

            var statusText = ReadAttributeAsString(attributes, "status");
            var directionText = ReadAttributeAsString(attributes, "direction");
            var distance = ReadAttributeAsDouble(attributes, "distanceMeters");
            var vipUnit = VipUnits.FirstOrDefault(x => string.Equals(x.DisplayName, deviceId, StringComparison.OrdinalIgnoreCase));

            if (!distance.HasValue && vipUnit is not null)
            {
                distance = vipUnit.DistanceMeters;
            }

            if (string.IsNullOrWhiteSpace(directionText) && vipUnit is not null)
            {
                directionText = vipUnit.DirectionToEscort;
            }

            Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
            {
                var panelUnit = VipPanelUnits.FirstOrDefault(x => string.Equals(x.DisplayName, deviceId, StringComparison.OrdinalIgnoreCase));
                if (panelUnit is null)
                {
                    panelUnit = new FieldUnitStatus(deviceId, UnitRole.Vip)
                    {
                        DirectionToEscort = "-"
                    };
                    VipPanelUnits.Add(panelUnit);
                }

                if (TryParseVipStatus(statusText, out var parsedStatus))
                {
                    panelUnit.Status = parsedStatus;
                }
                else if (vipUnit is not null)
                {
                    panelUnit.Status = vipUnit.Status;
                }

                if (distance.HasValue)
                {
                    panelUnit.DistanceMeters = distance.Value;
                }

                if (!string.IsNullOrWhiteSpace(directionText))
                {
                    panelUnit.DirectionToEscort = directionText;
                }
            }));
        }
        catch
        {
        }
    }

    private static string? ReadAttributeAsString(IDictionary<string, object?> attributes, string key)
    {
        try
        {
            var value = ReadAttributeValue(attributes, key);
            return value?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private static double? ReadAttributeAsDouble(IDictionary<string, object?> attributes, string key)
    {
        try
        {
            var value = ReadAttributeValue(attributes, key);
            if (value is null)
                return null;

            if (value is double number)
                return number;

            if (double.TryParse(value.ToString(), out double parsed))
                return parsed;
        }
        catch
        {
        }

        return null;
    }

    private static object? ReadAttributeValue(IDictionary<string, object?> attributes, string key)
    {
        if (attributes.TryGetValue(key, out var value))
            return value;
        return null;
    }

    private static bool TryParseVipStatus(string? rawStatus, out UnitStatus status)
    {
        return Enum.TryParse(rawStatus, ignoreCase: true, out status);
    }

    private void EnsureFieldDeviceRegistered(string deviceId, UnitRole fallbackRole)
    {
        if (string.IsNullOrWhiteSpace(deviceId))
            return;

        if (registeredFieldAppDeviceIds.Contains(deviceId))
            return;

        registeredFieldAppDeviceIds.Add(deviceId);

        if (fallbackRole == UnitRole.Escort)
        {
            escortDeviceId = deviceId;
            EscortUnit.DisplayName = deviceId;
            if (EscortUnit.Status == UnitStatus.Unknown)
            {
                EscortUnit.Status = UnitStatus.In;
            }

            return;
        }

        if (VipUnits.All(x => !string.Equals(x.DisplayName, deviceId, StringComparison.OrdinalIgnoreCase)))
        {
            var unit = new FieldUnitStatus(deviceId, UnitRole.Vip)
            {
                Status = UnitStatus.In,
                DirectionToEscort = "-",
                Latitude = EscortUnit.Latitude,
                Longitude = EscortUnit.Longitude
            };
            unit.PropertyChanged += UnitOnPropertyChanged;
            VipUnits.Add(unit);
        }
    }

    private bool ShouldPublishEscort()
    {
        if (string.IsNullOrWhiteSpace(escortDeviceId))
            return false;

        if (string.IsNullOrWhiteSpace(EscortUnit.DisplayName))
            return false;

        if (!HasEscortPosition)
            return false;

        return registeredFieldAppDeviceIds.Contains(escortDeviceId);
    }

    private bool IsEscortDevice(string deviceId)
    {
        if (!string.IsNullOrWhiteSpace(escortDeviceId)
            && string.Equals(deviceId, escortDeviceId, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!string.IsNullOrWhiteSpace(EscortUnit.DisplayName)
            && string.Equals(deviceId, EscortUnit.DisplayName, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

}