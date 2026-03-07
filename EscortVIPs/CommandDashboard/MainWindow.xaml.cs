using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI;
using CommandDashboard.Configuration;
using CommandDashboard.Services;
using CommandDashboard.Services.Messaging;
using CommandDashboard.Models;
using CommandDashboard.ViewModels;
using System.Windows.Controls;

namespace CommandDashboard;

public partial class MainWindow : Window
{
    private const double EscortFollowInitialScale = 900;
    private const double EscortFollowRecenterThresholdMeters = 2.0;
    private readonly MainViewModel viewModel;
    private readonly FieldMessagingHost fieldMessagingHost;
    private readonly GraphicsOverlay routeOverlay = new();
    private readonly GraphicsOverlay safetyOverlay = new();
    private readonly GraphicsOverlay unitLabelOverlay = new();
    private readonly Dictionary<string, Graphic> unitLabelGraphicsById = new(StringComparer.OrdinalIgnoreCase);
    private Graphic? mainRouteGraphic;
    private Graphic? safetyGraphic;
    private Graphic? warningGraphic;
    private bool escortFollowScaleInitialized;
    private bool initialMainRouteViewpointApplied;
    private double? lastEscortCenterLatitude;
    private double? lastEscortCenterLongitude;
    private string? trackedVipDeviceId;

    public MainWindow()
    {
        InitializeComponent();
        viewModel = new MainViewModel(new TourDashboardDataService());
        DataContext = viewModel;
        var endpointPrefix = Environment.GetEnvironmentVariable("DEMO_WS_PREFIX") ?? "http://127.0.0.1:8765/ws/";
        fieldMessagingHost = new FieldMessagingHost(viewModel.SessionId, endpointPrefix);
        fieldMessagingHost.MessageReceived += OnFieldMessageReceived;
        fieldMessagingHost.ConnectionStateChanged += OnConnectionStateChanged;

        Loaded += OnLoaded;
        Closed += OnClosed;
        viewModel.VipUnits.CollectionChanged += OnVipCollectionChanged;
        viewModel.EscortUnit.PropertyChanged += OnUnitPropertyChanged;
        foreach (var vipUnit in viewModel.VipUnits)
        {
            vipUnit.PropertyChanged += OnUnitPropertyChanged;
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        var mapView = MainMapView;
        if (mapView is null)
            return;

        mapView.GraphicsOverlays?.Add(routeOverlay);
        mapView.GraphicsOverlays?.Add(safetyOverlay);
        mapView.GraphicsOverlays?.Add(unitLabelOverlay);
        UpdateMainRouteGraphic();
        UpdateSafetyPolygon();
        if (!TrySetInitialViewpointToMainRouteExtent())
        {
            CenterMapOnEscort();
        }

        try
        {
            await fieldMessagingHost.StartAsync();
            UpdateConnectionStatusFromHost();
        }
        catch
        {
            viewModel.UpdateConnectionStatus(false, 0);
            UpdateConnectionStatusFromHost();
        }
    }

    private async void OnClosed(object? sender, EventArgs e)
    {
        fieldMessagingHost.ConnectionStateChanged -= OnConnectionStateChanged;
        fieldMessagingHost.MessageReceived -= OnFieldMessageReceived;
        await fieldMessagingHost.DisposeAsync();
    }

    private void OnFieldMessageReceived(object? sender, FieldMessageReceivedEventArgs e)
    {
        _ = Dispatcher.InvokeAsync(() =>
        {
            viewModel.ApplyFieldMessage(e.Envelope);
            UpdateMainRouteGraphic();
            UpdateConnectionStatusFromHost();

            if (!initialMainRouteViewpointApplied)
            {
                TrySetInitialViewpointToMainRouteExtent();
            }
        });
    }

    private void OnConnectionStateChanged(object? sender, EventArgs e)
    {
        _ = Dispatcher.InvokeAsync(UpdateConnectionStatusFromHost);
    }

    private void UpdateConnectionStatusFromHost()
    {
        var (isRunning, _, escortCount) = fieldMessagingHost.GetConnectionState();
        viewModel.SetConnectedDevices(fieldMessagingHost.GetConnectedDeviceIds());
        viewModel.UpdateConnectionStatus(isRunning, escortCount);
    }

    private void OnVipCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (FieldUnitStatus unit in e.NewItems)
            {
                unit.PropertyChanged += OnUnitPropertyChanged;
            }
        }

        if (e.OldItems is not null)
        {
            foreach (FieldUnitStatus unit in e.OldItems)
            {
                unit.PropertyChanged -= OnUnitPropertyChanged;

                if (unitLabelGraphicsById.TryGetValue(unit.DisplayName, out var labelGraphic))
                {
                    unitLabelOverlay.Graphics.Remove(labelGraphic);
                    unitLabelGraphicsById.Remove(unit.DisplayName);
                }

                if (!string.IsNullOrWhiteSpace(trackedVipDeviceId)
                    && string.Equals(trackedVipDeviceId, unit.DisplayName, StringComparison.OrdinalIgnoreCase))
                {
                    trackedVipDeviceId = null;
                }
            }
        }

        ApplyVipTrackDisplay();
        UpdateSafetyPolygon();
    }

    private void OnUnitPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not FieldUnitStatus unit)
            return;

        if (e.PropertyName is nameof(FieldUnitStatus.Latitude)
            or nameof(FieldUnitStatus.Longitude)
            or nameof(FieldUnitStatus.Status)
            or nameof(FieldUnitStatus.DistanceMeters)
            or nameof(FieldUnitStatus.DirectionToEscort)
            or nameof(FieldUnitStatus.DisplayName))
        {
            UpdateSafetyPolygon();

            if (ReferenceEquals(unit, viewModel.EscortUnit)
                && e.PropertyName is nameof(FieldUnitStatus.Latitude)
                    or nameof(FieldUnitStatus.Longitude)
                    or nameof(FieldUnitStatus.DisplayName))
            {
                CenterMapOnEscort();
            }
        }
    }

    private void CenterMapOnEscort()
    {
        var mapView = MainMapView;
        if (mapView is null)
            return;

        if (!viewModel.HasEscortPosition)
            return;

        var escortLatitude = viewModel.EscortUnit.Latitude;
        var escortLongitude = viewModel.EscortUnit.Longitude;
        if (lastEscortCenterLatitude.HasValue && lastEscortCenterLongitude.HasValue)
        {
            var movedMeters = CalculateDistanceMeters(
                lastEscortCenterLatitude.Value,
                lastEscortCenterLongitude.Value,
                escortLatitude,
                escortLongitude);

            if (movedMeters < EscortFollowRecenterThresholdMeters)
                return;
        }

        var escortPoint = new MapPoint(escortLongitude, escortLatitude, SpatialReferences.Wgs84);
        var scale = mapView.MapScale;
        if (!escortFollowScaleInitialized || double.IsNaN(scale) || double.IsInfinity(scale) || scale <= 0)
        {
            scale = EscortFollowInitialScale;
            escortFollowScaleInitialized = true;
        }

        lastEscortCenterLatitude = escortLatitude;
        lastEscortCenterLongitude = escortLongitude;
        _ = mapView.SetViewpointCenterAsync(escortPoint, scale);
    }

    private bool TrySetInitialViewpointToMainRouteExtent()
    {
        if (initialMainRouteViewpointApplied)
            return true;

        var mapView = MainMapView;
        if (mapView is null)
            return false;

        var mainRouteGeometry = viewModel.GetMainRouteGeometry();
        if (mainRouteGeometry is not null)
        {
            initialMainRouteViewpointApplied = true;
            escortFollowScaleInitialized = true;
            _ = mapView.SetViewpointGeometryAsync(mainRouteGeometry, 24);
            return true;
        }

        if (!viewModel.HasEscortPosition)
            return false;

        var points = new List<MapPoint>
        {
            new(viewModel.EscortUnit.Longitude, viewModel.EscortUnit.Latitude, SpatialReferences.Wgs84)
        };

        foreach (var vipUnit in viewModel.VipUnits)
        {
            points.Add(new MapPoint(vipUnit.Longitude, vipUnit.Latitude, SpatialReferences.Wgs84));
        }

        if (points.Count < 2)
            return false;

        var envelopeBuilder = new EnvelopeBuilder(SpatialReferences.Wgs84);
        foreach (var point in points)
        {
            envelopeBuilder.UnionOf(point);
        }

        var routeExtent = envelopeBuilder.ToGeometry();
        if (routeExtent is null)
            return false;

        initialMainRouteViewpointApplied = true;
        escortFollowScaleInitialized = true;
        _ = mapView.SetViewpointGeometryAsync(routeExtent, 24);
        return true;
    }

    private void UpdateMainRouteGraphic()
    {
        var routeGeometry = viewModel.GetMainRouteGeometry();
        if (routeGeometry is null)
        {
            if (mainRouteGraphic is not null)
            {
                routeOverlay.Graphics.Remove(mainRouteGraphic);
                mainRouteGraphic = null;
            }

            return;
        }

        var routeSymbol = new SimpleLineSymbol(
            SimpleLineSymbolStyle.Solid,
            System.Drawing.Color.FromArgb(220, 38, 121, 219),
            1.0);

        if (mainRouteGraphic is null)
        {
            mainRouteGraphic = new Graphic(routeGeometry, routeSymbol);
            routeOverlay.Graphics.Add(mainRouteGraphic);
        }
        else
        {
            mainRouteGraphic.Geometry = routeGeometry;
            mainRouteGraphic.Symbol = routeSymbol;
        }
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

    private static double DegreesToRadians(double degrees)
    {
        return degrees * Math.PI / 180.0;
    }

    private void UpdateSafetyPolygon()
    {
        if (!viewModel.HasEscortPosition || string.IsNullOrWhiteSpace(viewModel.EscortUnit.DisplayName))
        {
            if (safetyGraphic is not null)
            {
                safetyOverlay.Graphics.Remove(safetyGraphic);
                safetyGraphic = null;
            }

            if (warningGraphic is not null)
            {
                safetyOverlay.Graphics.Remove(warningGraphic);
                warningGraphic = null;
            }

            UpdateUnitLabels();

            return;
        }

        var escortPoint = new MapPoint(viewModel.EscortUnit.Longitude, viewModel.EscortUnit.Latitude, SpatialReferences.Wgs84);
        var outerBufferedGeometry = GeometryEngine.BufferGeodetic(
            escortPoint,
            DemoConstants.EscortSafetyRadiusMeters,
            LinearUnits.Meters,
            double.NaN,
            GeodeticCurveType.ShapePreserving);

        var innerBufferedGeometry = GeometryEngine.BufferGeodetic(
            escortPoint,
            DemoConstants.EscortDangerRadiusMeters,
            LinearUnits.Meters,
            double.NaN,
            GeodeticCurveType.ShapePreserving);

        if (outerBufferedGeometry is null || innerBufferedGeometry is null)
            return;

        var warningRingGeometry = GeometryEngine.Difference(outerBufferedGeometry, innerBufferedGeometry) ?? outerBufferedGeometry;

        var fillColor = viewModel.IsAnyVipOut
            ? System.Drawing.Color.FromArgb(48, 216, 48, 32)
            : System.Drawing.Color.FromArgb(48, 53, 172, 70);

        var outlineColor = viewModel.IsAnyVipOut
            ? System.Drawing.Color.FromArgb(204, 216, 48, 32)
            : System.Drawing.Color.FromArgb(204, 53, 172, 70);

        var symbol = new SimpleFillSymbol(
            SimpleFillSymbolStyle.Solid,
            fillColor,
            new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, System.Drawing.Color.FromArgb(0, outlineColor.R, outlineColor.G, outlineColor.B), 1.0));

        var warningRingSymbol = new SimpleFillSymbol(
            SimpleFillSymbolStyle.Solid,
            System.Drawing.Color.FromArgb(56, 255, 168, 0),
            new SimpleLineSymbol(SimpleLineSymbolStyle.Solid, System.Drawing.Color.FromArgb(0, 255, 140, 0), 1.0));

        if (safetyGraphic is null)
        {
            safetyGraphic = new Graphic(innerBufferedGeometry, symbol);
            safetyOverlay.Graphics.Add(safetyGraphic);
        }
        else
        {
            safetyGraphic.Geometry = innerBufferedGeometry;
            safetyGraphic.Symbol = symbol;
        }

        if (warningGraphic is null)
        {
            warningGraphic = new Graphic(warningRingGeometry, warningRingSymbol);
            safetyOverlay.Graphics.Add(warningGraphic);
        }
        else
        {
            warningGraphic.Geometry = warningRingGeometry;
            warningGraphic.Symbol = warningRingSymbol;
        }

        UpdateUnitLabels();
    }

    private void UpdateUnitLabels()
    {
        var desiredIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var vipUnit in viewModel.VipUnits)
        {
            if (string.IsNullOrWhiteSpace(vipUnit.DisplayName))
                continue;

            var vipPoint = new MapPoint(vipUnit.Longitude, vipUnit.Latitude, SpatialReferences.Wgs84);
            UpsertUnitLabelGraphic(vipUnit.DisplayName, vipPoint, vipUnit.DisplayName, isEscort: false);
            desiredIds.Add(vipUnit.DisplayName);
        }

        var staleIds = unitLabelGraphicsById.Keys
            .Where(id => !desiredIds.Contains(id))
            .ToList();

        foreach (var staleId in staleIds)
        {
            if (!unitLabelGraphicsById.TryGetValue(staleId, out var staleGraphic))
                continue;

            unitLabelOverlay.Graphics.Remove(staleGraphic);
            unitLabelGraphicsById.Remove(staleId);
        }
    }

    private void UpsertUnitLabelGraphic(string id, MapPoint location, string labelText, bool isEscort)
    {
        var foregroundColor = System.Drawing.Color.Black;
        var textSymbol = new TextSymbol(
            labelText,
            foregroundColor,
            18,
            Esri.ArcGISRuntime.Symbology.HorizontalAlignment.Left,
            Esri.ArcGISRuntime.Symbology.VerticalAlignment.Bottom)
        {
            OffsetY = 12,
            HaloColor = System.Drawing.Color.FromArgb(210, 255, 255, 255),
            HaloWidth = 2
        };

        if (unitLabelGraphicsById.TryGetValue(id, out var existingGraphic))
        {
            existingGraphic.Geometry = location;
            existingGraphic.Symbol = textSymbol;
            return;
        }

        var graphic = new Graphic(location, textSymbol);
        unitLabelOverlay.Graphics.Add(graphic);
        unitLabelGraphicsById[id] = graphic;
    }

    private async void OnVipStopClick(object sender, RoutedEventArgs e)
    {
        var vipDeviceId = ResolveVipDeviceIdFromSender(sender);
        if (string.IsNullOrWhiteSpace(vipDeviceId))
            return;

        var unit = viewModel.VipUnits.FirstOrDefault(v => string.Equals(v.DisplayName, vipDeviceId, StringComparison.OrdinalIgnoreCase));
        if (unit is not null)
            unit.OperatorControl = "STOP";

        await fieldMessagingHost.SendVipStopAsync(vipDeviceId);
    }

    private async void OnVipHurryClick(object sender, RoutedEventArgs e)
    {
        var vipDeviceId = ResolveVipDeviceIdFromSender(sender);
        if (string.IsNullOrWhiteSpace(vipDeviceId))
            return;

        var unit = viewModel.VipUnits.FirstOrDefault(v => string.Equals(v.DisplayName, vipDeviceId, StringComparison.OrdinalIgnoreCase));
        if (unit is not null)
            unit.OperatorControl = "HURRY";

        await fieldMessagingHost.SendVipHurryUpAsync(vipDeviceId);
    }

    private void OnVipTrackToggleClick(object sender, RoutedEventArgs e)
    {
        var vipDeviceId = ResolveVipDeviceIdFromSender(sender);
        if (string.IsNullOrWhiteSpace(vipDeviceId))
            return;

        trackedVipDeviceId = string.Equals(trackedVipDeviceId, vipDeviceId, StringComparison.OrdinalIgnoreCase)
            ? null
            : vipDeviceId;

        ApplyVipTrackDisplay();
    }

    private void ApplyVipTrackDisplay()
    {
        var selectedVip = trackedVipDeviceId;
        foreach (var vipUnit in viewModel.VipUnits)
        {
            vipUnit.IsTrackEnabled = !string.IsNullOrWhiteSpace(selectedVip)
                && string.Equals(vipUnit.DisplayName, selectedVip, StringComparison.OrdinalIgnoreCase);
        }

        var layer = viewModel.DynamicEntityLayer;
        if (layer is null)
            return;

        var trackDisplay = layer.TrackDisplayProperties;

        if (string.IsNullOrWhiteSpace(selectedVip))
        {
            trackDisplay.ShowTrackLine = false;
            trackDisplay.ShowPreviousObservations = false;
            return;
        }

        var selectedTrackSymbol = new SimpleLineSymbol(
            SimpleLineSymbolStyle.Dot,
            System.Drawing.Color.FromArgb(255, 30, 123, 234),
            3.5);

        var hiddenTrackSymbol = new SimpleLineSymbol(
            SimpleLineSymbolStyle.Solid,
            System.Drawing.Color.FromArgb(0, 0, 0, 0),
            0.1);

        var trackRenderer = new UniqueValueRenderer(
            fieldNames: ["trackId"],
            uniqueValues:
            [
                new UniqueValue(
                    selectedVip,
                    selectedVip,
                    selectedTrackSymbol,
                    selectedVip)
            ],
            defaultLabel: "Hidden",
            defaultSymbol: hiddenTrackSymbol);

        trackDisplay.ShowTrackLine = true;
        trackDisplay.ShowPreviousObservations = false;
        trackDisplay.MaximumObservations = 15_000;
        trackDisplay.TrackLineRenderer = trackRenderer;
    }

    private static string? ResolveVipDeviceIdFromSender(object sender)
    {
        if (sender is not Button button)
            return null;

        if (button.Tag is string tagString && !string.IsNullOrWhiteSpace(tagString))
            return tagString.Trim();

        if (button.Tag is not null)
        {
            var tagValue = button.Tag.ToString();
            if (!string.IsNullOrWhiteSpace(tagValue))
                return tagValue.Trim();
        }

        if (button.DataContext is FieldUnitStatus unit && !string.IsNullOrWhiteSpace(unit.DisplayName))
            return unit.DisplayName.Trim();

        return null;
    }

}