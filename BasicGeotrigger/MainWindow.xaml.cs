using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Geotriggers;
using Esri.ArcGISRuntime.Mapping;
using Microsoft.UI.Xaml;
using Windows.UI.Popups;
using ClickSource;
using System.Data.SqlTypes;

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

    public MainWindow()
    {
        InitializeComponent();
        _ = InitializeAsync();
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

        // start monitoring geotriggers using one of the two implementation paths
        var geotrigger = CreateAndInitializeGeotrigger(map);
        if (geotrigger is not null)
        {
            // start monitoring
            _monitor = new GeotriggerMonitor(geotrigger);
            _monitor.Notification += Geotrigger_Notification;
            _ = _monitor.StartAsync();
        }

        // show the map
        _mapView.Map = map;
    }

    private Geotrigger? CreateAndInitializeGeotrigger(Map map)
    {
        var featureLayer = map.OperationalLayers.OfType<FeatureLayer>()
            .FirstOrDefault(layer => layer.Id == _fenceLayerId)
            ?? map.OperationalLayers.OfType<FeatureLayer>().FirstOrDefault();
        if (featureLayer?.FeatureTable is not FeatureTable featureTable)
            return null;

        var clickSource = ClickLocationDataSource.Create(_mapView);
        var feed = new LocationGeotriggerFeed(clickSource);
        var fenceParameters = new FeatureFenceParameters(featureTable, bufferDistance: 30d);

        var messageExpression = new Esri.ArcGISRuntime.ArcadeExpression(
            "{\n" +
            "  'message': `${$fencefeature['TEXT_FOR_DESCRIPTION']}`,\n" +
            "}");

        var geotrigger = new FenceGeotrigger(
            feed,
            FenceRuleType.EnterOrExit,
            fenceParameters,
            messageExpression,
            "Palm Springs locales")
        {
            FeedAccuracyMode = FenceGeotriggerFeedAccuracyMode.UseGeometry,
            EnterExitSpatialRelationship = FenceEnterExitSpatialRelationship.EnterContainsAndExitDoesNotIntersect,
        };

        return geotrigger;
    }

    private void Geotrigger_Notification(object? sender, GeotriggerNotificationInfo e)
    {
        DispatcherQueue.TryEnqueue(async () =>
        {
            var info = (FenceGeotriggerNotificationInfo)e;

            // get the fence feature and layer associated with the notification
            var fence = info.FenceGeoElement as ArcGISFeature;
            var layer = fence?.FeatureTable?.Layer as FeatureLayer;
            if (layer is null)
                return;
                
            // on enter: show the popup and select the fence feature
            if (info.FenceNotificationType is FenceNotificationType.Entered)
            {
                // show the popup
                _popupViewer.Popup = new Esri.ArcGISRuntime.Mapping.Popups.Popup(fence, layer.PopupDefinition);
                _popupPanel.Visibility = Visibility.Visible;

                // select the fence
                layer.SelectFeature(fence);
            }
            else // on exit: hide the popup, unselect the fence, and show a farewell dialog
            {
                // hide the popup
                _popupPanel.Visibility = Visibility.Collapsed;
                _popupViewer.Popup = null;

                // unselect the fence
                layer.UnselectFeature(fence);

                // show a farewell dialog
                var dialog = new MessageDialog(info.Message)
                {
                    Title = $"Thanks for stopping by {(string)info.FenceGeoElement.Attributes["Name"]!}"
                };
                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                WinRT.Interop.InitializeWithWindow.Initialize(dialog, hwnd);
                await dialog.ShowAsync();
            }
        });
    }
}
