using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Esri.ArcGISRuntime;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Geotriggers;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Mapping.Popups;
using Esri.ArcGISRuntime.Toolkit.UI.Controls;
using Esri.ArcGISRuntime.UI.Controls;
using Microsoft.UI.Xaml;
using Windows.UI.Popups;
using ClickSource;

namespace BasicGeotrigger;

public sealed partial class MainWindow : Window
{
    private const string _webmapPath = @"c:\temp\test\geotrigger\webmap.json";
    private const string _fenceLayerId = "3f8be9a6ecda4add81b1b20c2edbe712";

    private readonly Envelope _extent = new(
        xMin: -12973930.396137554, yMin: 4004799.0937918006,
        xMax: -12971963.953236824, yMax: 4005786.6339308205,
        0d, 0d, spatialReference: SpatialReferences.WebMercator);
    private GeotriggerMonitor? _monitor;
    private MapView MapView { get; set; } = default!;
    private PopupViewer PopupViewer { get; set; } = default!;
    private FrameworkElement PopupPanel { get; set; } = default!;

    public MainWindow()
    {
        InitializeComponent();
        InitializeViewReferences();
        _ = InitializeAsync();
    }

    private void InitializeViewReferences()
    {
        if (Content is not FrameworkElement root)
            throw new InvalidOperationException("Window content is not initialized.");

        MapView = root.FindName("_mapView") as Esri.ArcGISRuntime.UI.Controls.MapView
            ?? throw new InvalidOperationException("Could not find '_mapView'.");
        PopupViewer = root.FindName("_popupViewer") as Esri.ArcGISRuntime.Toolkit.UI.Controls.PopupViewer
            ?? throw new InvalidOperationException("Could not find '_popupViewer'.");
        PopupPanel = root.FindName("_popupPanel") as FrameworkElement
            ?? throw new InvalidOperationException("Could not find '_popupPanel'.");
    }

    private async Task InitializeAsync()
    {
        // create the map from the web map
        var map = Map.FromJson(File.ReadAllText(_webmapPath)) ?? throw new InvalidDataException();
        map.InitialViewpoint = new Viewpoint(_extent);

        // load the map to retrieve the web map metadata
        await map.LoadAsync();

        // set the window title to the map's title
        if (map.Item is not null)
            Title = map.Item.Title;

        // retrieve the feature table from the fence layer in the web map
        var fenceLayer = map.OperationalLayers.OfType<FeatureLayer>()
            .FirstOrDefault(layer => layer.Id == _fenceLayerId)
            ?? map.OperationalLayers.OfType<FeatureLayer>().FirstOrDefault();
        if (fenceLayer?.FeatureTable is not FeatureTable fenceTable)
            return;

        // create the geotrigger and start monitoring
        CreateAndInitializeGeotrigger(fenceTable);

        // show the map
        MapView.Map = map;
    }

    private void CreateAndInitializeGeotrigger(FeatureTable fenceTable)
    {
        // Feed / Fence / Rule - Geotrigger setup

        // create a geotrigger feed based on our custom click location data source
        var clickSource = ClickLocationDataSource.Create(MapView);
        var feed = new LocationGeotriggerFeed(clickSource);

        // create fence parameters with a buffer distance of 30 meters around the fence point feature
        var fenceParameters = new FeatureFenceParameters(fenceTable, bufferDistance: 30d);

        // Arcade expression - evaluated when geotrigger notification is generated
        var messageExpression = new ArcadeExpression("$FenceFeature['TEXT_FOR_DESCRIPTION']");

        // create the geotrigger
        var geotrigger = new FenceGeotrigger(
            feed,
            FenceRuleType.EnterOrExit,
            fenceParameters,
            messageExpression,
            geotriggerName: "Palm Springs locales")
        {
            FeedAccuracyMode = FenceGeotriggerFeedAccuracyMode.UseGeometry,
            EnterExitSpatialRelationship = FenceEnterExitSpatialRelationship.EnterContainsAndExitDoesNotIntersect,
        };

        // start monitoring
        _monitor = new GeotriggerMonitor(geotrigger);
        _monitor.Notification += Geotrigger_Notification;
        _ = _monitor.StartAsync();
    }

    private void Geotrigger_Notification(object? sender, GeotriggerNotificationInfo e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var info = (FenceGeotriggerNotificationInfo)e;

            // get the fence feature and layer associated with the notification
            var fence = info.FenceGeoElement as ArcGISFeature;
            var layer = fence?.FeatureTable?.Layer as FeatureLayer;
            if (fence is null || layer is null)
                return;

            // on enter: show the popup and select the fence feature
            if (info.FenceNotificationType is FenceNotificationType.Entered)
            {
                // show the popup
                PopupViewer.Popup = new Popup(fence, layer.PopupDefinition);
                PopupPanel.Visibility = Visibility.Visible;

                // select the fence
                layer.SelectFeature(fence);
            }
            else // on exit: hide the popup, unselect the fence, and show a farewell dialog
            {
                // hide the popup
                PopupPanel.Visibility = Visibility.Collapsed;
                PopupViewer.Popup = null;

                // unselect the fence
                layer.UnselectFeature(fence);

                // show a farewell dialog
                var fenceName = fence.Attributes.TryGetValue("Name", out var nameValue)
                    ? nameValue?.ToString() ?? "this location"
                    : "this location";
                var dialog = new MessageDialog(info.Message)
                {
                    Title = $"Thanks for stopping by {fenceName}"
                };
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(dialog, hwnd);
                await dialog.ShowAsync();
            }
        });
    }
}
