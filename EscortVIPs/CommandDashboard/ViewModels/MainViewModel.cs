using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using CommandMessaging;
using CommandDashboard.Models;
using CommandDashboard.RealTime;
using CommandDashboard.Services;
using Esri.Calcite.WPF;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Symbology;
using System.Windows;
using System.IO;
using System.Globalization;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Esri.ArcGISRuntime.RealTime;
using System.Collections.Specialized;

namespace CommandDashboard.ViewModels;

public partial class MainViewModel : ObservableObject
{
    private readonly MockDynamicEntityDataSource dynamicEntityDataSource;
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

        dynamicEntityDataSource = new MockDynamicEntityDataSource();
        var vipVipSymbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, System.Drawing.Color.FromArgb(236, 239, 241), 14)
        {
            Outline = new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, System.Drawing.Color.FromArgb(38, 50, 56), 1.5)
        };
        var escortSymbol = CreateEscortShieldSymbol();

        var unitRenderer = new UniqueValueRenderer(
            fieldNames: new[] { "role" },
            uniqueValues: new[]
            {
                new UniqueValue("Escort", "Escort", escortSymbol, "Escort")
            },
            defaultLabel: "VIP",
            defaultSymbol: vipVipSymbol);

        DynamicEntityLayer = new DynamicEntityLayer(dynamicEntityDataSource)
        {
            Renderer = unitRenderer
        };
        MainMap.OperationalLayers.Add(DynamicEntityLayer);

        _ = dynamicEntityDataSource.LoadAsync();
        _ = dynamicEntityDataSource.ConnectAsync();
        dynamicEntityDataSource.DynamicEntityReceived += (_, eventArgs) => HandleDynamicEntityReceived(eventArgs);
        PublishDynamicEntities();

        sessionId = $"DEVSUMMIT-2026-{DateTime.Now:HHmm}";
        RecalculateAggregateState();
        EscortConnectionState = "Disconnected";
        VipWhereClause = "role = 'VIP'";
        VipFilterStatus = "Filter: showing all VIPs";
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
    private DynamicEntityLayer dynamicEntityLayer;

    [ObservableProperty]
    private string escortConnectionState = "Disconnected";

    [ObservableProperty]
    private int totalVIPs;

    [ObservableProperty]
    private string vipOverallStatus = "All VIPs In Range";

    [ObservableProperty]
    private string vipWhereClause = "role = 'VIP'";

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
            var queryParameters = new DynamicEntityQueryParameters
            {
                WhereClause = whereClause
            };

            var queryResult = await dynamicEntityDataSource.QueryDynamicEntitiesAsync(queryParameters, CancellationToken.None);
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
        VipWhereClause = "role = 'VIP'";
        RebuildFilteredVipUnits();
        VipFilterStatus = "Filter: showing all VIPs";
    }

    private void UnitOnPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
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
        IsAnyVipOut = VipUnits.Any(unit => unit.Status == UnitStatus.Out);
        IsAnyVipDanger = VipUnits.Any(unit => unit.Status == UnitStatus.Danger);
        TotalVIPs = VipUnits.Count;
        VipOverallStatus = IsAnyVipOut
            ? "VIPs Out of Range"
            : IsAnyVipDanger
                ? "VIPs in Warning Perimeter"
                : "All VIPs in Range";
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
        var fallback = new SimpleMarkerSymbol(
            SimpleMarkerSymbolStyle.Diamond,
            System.Drawing.Color.FromArgb(30, 136, 229),
            14)
        {
            Outline = new SimpleLineSymbol(
                SimpleLineSymbolStyle.Solid,
                System.Drawing.Color.FromArgb(21, 101, 192),
                1.5)
        };

        try
        {
            var shieldPath = BuildCalciteShieldMarkerImage();
            if (string.IsNullOrWhiteSpace(shieldPath) || !File.Exists(shieldPath))
            {
                return fallback;
            }

            var symbol = new PictureMarkerSymbol(new Uri(shieldPath, UriKind.Absolute))
            {
                Width = 32,
                Height = 32
            };

            return symbol;
        }
        catch
        {
            return fallback;
        }
    }

    private static string? BuildCalciteShieldMarkerImage()
    {
        if (Application.Current?.Resources is null)
            return null;

        var shieldGlyph = new CalciteIconGlyphExtension
        {
            Icon = CalciteIcon.ShieldCoin
        }.ProvideValue(null!)?.ToString();

        if (string.IsNullOrWhiteSpace(shieldGlyph))
            return null;

        if (Application.Current.Resources["CalciteUIIconsMediumFontFamily"] is not FontFamily calciteIconFont)
            return null;

        var markerSize = 44;
        var glyphSize = 36d;
        var typeface = new Typeface(calciteIconFont, FontStyles.Normal, FontWeights.Bold, FontStretches.Normal);
        var glyphBrush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0, 86, 153));
        glyphBrush.Freeze();

        var formattedText = new FormattedText(
            shieldGlyph,
            CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight,
            typeface,
            glyphSize,
            glyphBrush,
            1.0);

        var x = (markerSize - formattedText.WidthIncludingTrailingWhitespace) / 2;
        var y = (markerSize - formattedText.Height) / 2;

        var visual = new DrawingVisual();
        using (var context = visual.RenderOpen())
        {
            context.DrawText(formattedText, new Point(x, y));
        }

        var bitmap = new RenderTargetBitmap(markerSize, markerSize, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);

        var outputDirectory = Path.Combine(Path.GetTempPath(), "CommandDashboard");
        Directory.CreateDirectory(outputDirectory);
        var outputPath = Path.Combine(outputDirectory, "escort-shield-calcite.png");

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (var stream = File.Create(outputPath))
        {
            encoder.Save(stream);
        }

        return outputPath;
    }

    private void UpdateVipRelativeMetrics(FieldUnitStatus unit)
    {
        if (unit.Role != UnitRole.Vip)
            return;

        if (string.IsNullOrWhiteSpace(EscortUnit.DisplayName))
            return;

        var distanceMeters = CalculateDistanceMeters(unit.Latitude, unit.Longitude, EscortUnit.Latitude, EscortUnit.Longitude);
        if (double.IsNaN(distanceMeters) || double.IsInfinity(distanceMeters))
            return;

        unit.DistanceMeters = distanceMeters;
        unit.DirectionToEscort = ComputeDirectionCardinal(unit.Latitude, unit.Longitude, EscortUnit.Latitude, EscortUnit.Longitude);
    }

    private static double CalculateDistanceMeters(double latitude1, double longitude1, double latitude2, double longitude2)
    {
        const double earthRadiusMeters = 6_371_000;
        var latitudeDelta = DegreesToRadians(latitude2 - latitude1);
        var longitudeDelta = DegreesToRadians(longitude2 - longitude1);
        var latitude1Radians = DegreesToRadians(latitude1);
        var latitude2Radians = DegreesToRadians(latitude2);

        var haversine = Math.Sin(latitudeDelta / 2) * Math.Sin(latitudeDelta / 2)
            + Math.Cos(latitude1Radians) * Math.Cos(latitude2Radians)
            * Math.Sin(longitudeDelta / 2) * Math.Sin(longitudeDelta / 2);

        var centralAngle = 2 * Math.Atan2(Math.Sqrt(haversine), Math.Sqrt(1 - haversine));
        return earthRadiusMeters * centralAngle;
    }

    private static string ComputeDirectionCardinal(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude)
    {
        var phi1 = DegreesToRadians(fromLatitude);
        var phi2 = DegreesToRadians(toLatitude);
        var deltaLambda = DegreesToRadians(toLongitude - fromLongitude);

        var y = Math.Sin(deltaLambda) * Math.Cos(phi2);
        var x = Math.Cos(phi1) * Math.Sin(phi2) - Math.Sin(phi1) * Math.Cos(phi2) * Math.Cos(deltaLambda);
        var bearing = (RadiansToDegrees(Math.Atan2(y, x)) + 360) % 360;

        var cardinals = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
        var index = (int)Math.Round(bearing / 45.0, MidpointRounding.AwayFromZero) % 8;
        return cardinals[index];
    }

    private static double DegreesToRadians(double degrees) => degrees * Math.PI / 180.0;

    private static double RadiansToDegrees(double radians) => radians * 180.0 / Math.PI;

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
                if (Enum.TryParse<UnitStatus>(payload.Status, ignoreCase: true, out var status))
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

                if (Enum.TryParse<UnitStatus>(payload.Status, ignoreCase: true, out var telemetryStatus))
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

                if (Enum.TryParse(statusText, true, out UnitStatus parsedStatus))
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