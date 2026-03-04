using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using Esri.ArcGISRuntime.ArcGISServices;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Mapping;
using Esri.ArcGISRuntime.Mapping.Labeling;
using Esri.ArcGISRuntime.RealTime;
using Esri.ArcGISRuntime.Symbology;

namespace Simple
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private static readonly Envelope _extent
            = new(-112.07191257948598, 40.494668733340376, -111.70060766277847, 40.644571817187085, SpatialReferences.Wgs84);

        public MainWindow()
        {
            InitializeComponent();
        }

        #region Properties

        public Map Map { get; } = new(BasemapStyle.ArcGISStreets) { InitialViewpoint = new Viewpoint(_extent) };

        public DynamicEntityLayer? DynamicEntityLayer
        {
            get => _dynamicEntityLayer;
            set
            {
                _dynamicEntityLayer = value;
                OnPropertyChanged();
            }
        }
        private DynamicEntityLayer? _dynamicEntityLayer;

        #endregion

        #region INotifyPropertyChanged

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged([CallerMemberName] string? property = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));

        #endregion

        #region UI

        private async void AddLayer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                await CreateAndConnectAsync();
                OnPropertyChanged(nameof(DynamicEntityLayer));
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.ToString(), "Error");
            }
        }

        private void ToggleTrackDisplay_Click(object sender, RoutedEventArgs e)
        {
            if (DynamicEntityLayer is null)
                return;

            if (!DynamicEntityLayer.TrackDisplayProperties.ShowPreviousObservations)
            {
                ShowTrack(DynamicEntityLayer, 5);
            }
            else
            {
                DynamicEntityLayer.TrackDisplayProperties.ShowPreviousObservations = false;
                DynamicEntityLayer.TrackDisplayProperties.ShowTrackLine = false;
            }
        }

        private Renderer? _defaultRenderer;
        private Renderer? _defaultPreviousRenderer;
        private Renderer? _defaultTrackRenderer;

        private void ToggleAlternateRenderer_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (DynamicEntityLayer is null)
                    return;

                if (_useAltRenderer.IsChecked == true)
                {
                    if (_defaultRenderer is null)
                    {
                        _defaultRenderer = DynamicEntityLayer.Renderer;
                        _defaultPreviousRenderer = DynamicEntityLayer.TrackDisplayProperties.PreviousObservationRenderer;
                        _defaultTrackRenderer = DynamicEntityLayer.TrackDisplayProperties.TrackLineRenderer;
                    }

                    UseAlternateRenderers(DynamicEntityLayer);
                }
                else
                {
                    DynamicEntityLayer.Renderer = _defaultRenderer;
                    DynamicEntityLayer.TrackDisplayProperties.PreviousObservationRenderer = _defaultPreviousRenderer;
                    DynamicEntityLayer.TrackDisplayProperties.TrackLineRenderer = _defaultTrackRenderer;
                }
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Alternate renderer error");
            }
        }

        private void ToggleLabels_Click(object sender, RoutedEventArgs e)
        {
            if (DynamicEntityLayer is null)
                return;

            if (DynamicEntityLayer.LabelsEnabled)
            {
                DynamicEntityLayer.LabelsEnabled = false;
            }
            else
            {
                ShowLabels(DynamicEntityLayer);
            }
        }

        #endregion

        private async Task CreateAndConnectAsync()
        {
            // create the stream service from the StreamServer URL
            var streamServiceUrl = "https://realtimegis2016.esri.com:6443/arcgis/rest/services/SandyVehicles/StreamServer";
            var streamService = new ArcGISStreamService(new Uri(streamServiceUrl));

            // use ConnectionStatus and ConnectionStatusChanged to update the status in our UI
            streamService.ConnectionStatusChanged += (s, status) =>
                Dispatcher.Invoke(() => _statusText.Text = $"{status}");

            // explicit load (will populate streamService.ServiceInfo)
            await streamService.LoadAsync();

            // explicit connection (will start receiving observations)
            await streamService.ConnectAsync();

            // create the layer
            _dynamicEntityLayer = new DynamicEntityLayer(streamService);

            // add the layer to the map
            Map.OperationalLayers.Add(_dynamicEntityLayer);
        }

        private static void ShowTrack(DynamicEntityLayer dynamicEntityLayer, int count)
        {
            // show previous observation points
            dynamicEntityLayer.TrackDisplayProperties.ShowPreviousObservations = true;

            // show a track line between the observations
            dynamicEntityLayer.TrackDisplayProperties.ShowTrackLine = true;

            // adjust the maximum number of observations to show (includes the latest observation)
            dynamicEntityLayer.TrackDisplayProperties.MaximumObservations = count;
        }

        // update the three renderers on the dynamic entity layer
        private static void UseAlternateRenderers(DynamicEntityLayer dynamicEntityLayer)
        {
            // update renderer for the latest observation
            dynamicEntityLayer.Renderer =
                Renderer.FromJson(File.ReadAllText(GetContentFilePath("sandy_uvr.json")));

            // update renderer for previous observations
            dynamicEntityLayer.TrackDisplayProperties.PreviousObservationRenderer =
                Renderer.FromJson(File.ReadAllText(GetContentFilePath("sandy_uvr_prev.json")));

            // update renderer for the track line
            dynamicEntityLayer.TrackDisplayProperties.TrackLineRenderer =
                new SimpleRenderer(new SimpleLineSymbol(SimpleLineSymbolStyle.Dash, Color.Blue, 1d));
        }

        private static string GetContentFilePath(string fileName)
        {
            var contentPath = Path.Combine(AppContext.BaseDirectory, "Content", fileName);
            if (!File.Exists(contentPath))
                throw new FileNotFoundException($"Could not find renderer file: {contentPath}");

            return contentPath;
        }

        private static void ShowLabels(DynamicEntityLayer dynamicEntityLayer)
        {
            // create a label definition - base the label expression on the ServiceInfo.TrackIdField
            // - SimpleLabelExpression and ArcadeLabelExpression are supported
            var trackIdField = ((ArcGISStreamService)dynamicEntityLayer.DataSource).ServiceInfo!.TrackIdField;
            var expr = new SimpleLabelExpression($"[{trackIdField}]");
            var labelSymbol = new TextSymbol() { Color = Color.Blue, Size = 12d };
            var labelDef = new LabelDefinition(expr, labelSymbol) { Placement = LabelingPlacement.PointAboveCenter };

            // add the label definition to the layer (same as other layers)
            dynamicEntityLayer.LabelDefinitions.Add(labelDef);

            // turn on labels (will only display labels on the latest observation in a track)
            dynamicEntityLayer.LabelsEnabled = true;
        }
    }
}
