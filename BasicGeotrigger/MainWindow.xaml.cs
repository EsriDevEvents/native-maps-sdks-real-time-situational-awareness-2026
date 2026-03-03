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

namespace BasicGeotrigger
{
    public sealed partial class MainWindow : Window
    {
        private const string _webmapPath = @"c:\temp\test\geotrigger\geotrigger_webmap.json";
        private const bool _useLegacyGeotriggerInitialization = false;

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
            var geotrigger = _useLegacyGeotriggerInitialization
                ? await CreateAndInitializeGeotriggerFromWebMapAsync(map)
                : CreateAndInitializeGeotriggerProgrammatically(map);
            if (geotrigger is not null)
            {
                // start monitoring
                _monitor = new GeotriggerMonitor(geotrigger);
                _monitor.Notification += Geotrigger_Notification;
                _ = _monitor.StartAsync();
            }

            // show the map
            _mapView.Map = map;

            // can write out a webmap with geotrigger infos
            //File.WriteAllText(@"c:\temp\test\geotrigger\webmap.json", map.ToJson());
        }

        private void Geotrigger_Notification(object? sender, GeotriggerNotificationInfo e)
        {
            DispatcherQueue.TryEnqueue(async () =>
            {
                var info = (FenceGeotriggerNotificationInfo)e;

                // on enter, show the popup for the fence
                if (info.FenceNotificationType is FenceNotificationType.Entered)
                {
                    if (e.Actions.Contains("showPopup"))
                    {
                        var fence = (ArcGISFeature)info.FenceGeoElement;
                        var layer = (FeatureLayer)fence.FeatureTable?.Layer!;
                        _popupViewer.Popup = new Esri.ArcGISRuntime.Mapping.Popups.Popup(fence, layer.PopupDefinition);
                        _popupPanel.Visibility = Visibility.Visible;
                    }

                    if (e.Actions.Contains("selectFence"))
                    {
                        if (info.FenceGeoElement is ArcGISFeature fence
                            && fence.FeatureTable?.Layer is FeatureLayer layer)
                        {
                            layer.SelectFeature(fence);
                        }
                    }
                }
                else // exited the fence, hide the popup
                {
                    if (e.Actions.Contains("showPopup"))
                    {
                        _popupPanel.Visibility = Visibility.Collapsed;
                        _popupViewer.Popup = null;
                    }

                    if (e.Actions.Contains("selectFence"))
                    {
                        if (info.FenceGeoElement is ArcGISFeature fence
                            && fence.FeatureTable?.Layer is FeatureLayer layer)
                        {
                            layer.UnselectFeature(fence);
                        }
                    }

                    if (e.Actions.Contains("showMessage"))
                    {
                        var dialog = new MessageDialog(info.Message)
                        {
                            Title = $"Thanks for stopping by {(string)info.FenceGeoElement.Attributes["Name"]!}"
                        };
                        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                        WinRT.Interop.InitializeWithWindow.Initialize(dialog, hwnd);
                        await dialog.ShowAsync();
                    }
                }
            });
        }
    }
}
