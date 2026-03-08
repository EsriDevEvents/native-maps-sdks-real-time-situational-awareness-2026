using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Threading.Tasks;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Location;
using Esri.ArcGISRuntime.Symbology;
using Esri.ArcGISRuntime.UI.Controls;
using Esri.ArcGISRuntime.UI.Editing;

namespace CustomSource;

public class CustomLocationDataSource : LocationDataSource
{
    private readonly MapView _mapView;
    private readonly SimpleMarkerSymbol _vertexSymbol = new(SimpleMarkerSymbolStyle.Circle, Color.Red, 2d);

    public GeometryEditor GeometryEditor { get; } = new();

    public double HorizontalAccuracy
    {
        get => _horizontalAccuracy;
        set
        {
            _horizontalAccuracy = value;
            UpdateHorizontalAccuracy();
        }
    }
    private double _horizontalAccuracy = 5d;

    public CustomLocationDataSource(MapView mapView)
    {
        _mapView = mapView;
        ((INotifyPropertyChanged)_mapView).PropertyChanged += MapView_PropertyChanged;

        // geometry editor
        var tool = new VertexTool();
        tool.Style.VertexSymbol = _vertexSymbol;
        tool.Style.FeedbackVertexSymbol = _vertexSymbol;
        tool.Style.SelectedVertexSymbol = null;
        tool.Configuration.SetAllowCreation(true);
        tool.Configuration.SetAllowDeletion(false);
        tool.Configuration.SetAllowSelection(false);
        tool.Configuration.SetAllowTransformation(false);
        GeometryEditor.Tool = tool;
        GeometryEditor.PropertyChanged += GeometryEditor_PropertyChanged;
        GeometryEditor.Start(GeometryType.Point);
    }

    protected override Task OnStartAsync() => Task.CompletedTask;

    protected override Task OnStopAsync() => Task.CompletedTask;

    private void MapView_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MapView.UnitsPerPixel) && HorizontalAccuracy > 0d)
        {
            UpdateHorizontalAccuracy();
        }
    }

    private void UpdateHorizontalAccuracy()
    {
        if (_mapView.VisibleArea?.Extent is not Envelope extent)
            return;

        // update the size of the symbol based on horizontal accuracy
        var buffer = GeometryEngine.BufferGeodetic(extent.GetCenter(), HorizontalAccuracy, LinearUnits.Meters);
        if (buffer.Extent is Envelope bufferExtent)
        {
            var size = Math.Max(2d, bufferExtent.Width / _mapView.UnitsPerPixel);
            var outlineSymbol = new SimpleMarkerSymbol(SimpleMarkerSymbolStyle.Circle, Color.FromArgb(128, Color.Red), size);
            GeometryEditor.Tool.Style.FeedbackVertexSymbol = new CompositeSymbol([outlineSymbol, _vertexSymbol]);
        }
    }

    private void GeometryEditor_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        try
        {
            if (e.PropertyName != nameof(GeometryEditor.Geometry)
                || GeometryEditor.Geometry is not MapPoint location
                || double.IsNaN(location.X) || double.IsNaN(location.Y))
                return;

            OnClick(location);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex.ToString());
        }
    }

    public void OnClick(MapPoint point)
    {
        if (Status is not LocationDataSourceStatus.Started)
            return;

        var location = new Location(
            (MapPoint)point.Project(SpatialReferences.Wgs84),
            HorizontalAccuracy, 0d, 0d, false);
        UpdateLocation(location);
    }

    public static CustomLocationDataSource Create(MapView mapView)
    {
        var customSource = new CustomLocationDataSource(mapView);

        mapView.LocationDisplay.DataSource = customSource;
        mapView.LocationDisplay.DefaultSymbol = new SimpleMarkerSymbol(
            SimpleMarkerSymbolStyle.Circle, Color.CornflowerBlue, 5d);
        mapView.LocationDisplay.ShowPingAnimationSymbol = false;
        mapView.LocationDisplay.IsEnabled = true;
        mapView.GeometryEditor = customSource.GeometryEditor;

        return customSource;
    }
}
