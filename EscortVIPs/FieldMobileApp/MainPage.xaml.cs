using System.Net.WebSockets;
using System.Text;
using System.Collections.ObjectModel;
using CommandMessaging;
using Esri.ArcGISRuntime.Data;
using Esri.ArcGISRuntime.Geometry;
using Esri.ArcGISRuntime.Geotriggers;
using Esri.ArcGISRuntime.Location;
using Esri.ArcGISRuntime.RealTime;
using FieldMobileApp.Location;
using FieldMobileApp.RealTime;

namespace FieldMobileApp;

public partial class MainPage : ContentPage
{
	private ClientWebSocket? socket;
	private ClientWebSocket? simulationSocket;
	private CancellationTokenSource? receiveCts;
	private Task? receiveTask;
	private Task? simulationReceiveTask;
	private Task? simulationConnectTask;
	private Task? sendTask;
	private EventHandler<Esri.ArcGISRuntime.Location.Location>? locationChangedHandler;
	private LocationDataSource? locationDataSource;
	private SimulationLocationDataSource? simulatedLocationDataSource;
	private readonly Lock latestLocationGate = new();
	private Esri.ArcGISRuntime.Location.Location? latestLocation;
	private readonly bool simulatedMode;
	private readonly SemaphoreSlim escortFenceUpdateGate = new(1, 1);
	private readonly SemaphoreSlim socketSendGate = new(1, 1);
	private readonly SemaphoreSlim simulationConnectGate = new(1, 1);
	private readonly string configuredRole;
	private readonly string configuredDeviceId;
	private readonly string configuredSessionId;
	private readonly string configuredHostUrl;
	private readonly string configuredSimulationUrl;
	private bool isShuttingDown;
	private bool reconnectLoopActive;
	private bool simulationReconnectLoopActive;
	private bool handlingConnectionDrop;
	private string? connectedDeviceId;
	private string? connectedSessionId;
	private string? vipStatus;
	private double? latestEscortLatitude;
	private double? latestEscortLongitude;
	private long geotriggerEventCount;
	private FeatureCollectionTable? vipOuterFenceTable;
	private FeatureCollectionTable? vipInnerFenceTable;
	private Feature? vipOuterFenceFeature;
	private Feature? vipInnerFenceFeature;
	private GeotriggerMonitor? vipOuterFenceMonitor;
	private GeotriggerMonitor? vipInnerFenceMonitor;
	private bool? vipInsideOuterFence;
	private bool? vipInsideInnerFence;
	private readonly object diagnosticsGate = new();
	private readonly string diagnosticsLogPath;
	private CancellationTokenSource? watchdogCts;
	private Task? watchdogTask;
	private DateTimeOffset lastInboundUtc;
	private DateTimeOffset lastOutboundUtc;
	private bool hasSentOutbound;
	private string displayedDistance = "  --.- m";
	private string displayedDirection = "-";
	private string? lastRenderedDetailText;
	private readonly ObservableCollection<VipStatusListItem> escortVipStatuses = new();
	private readonly Dictionary<string, VipStatusListItem> escortVipListItemByDevice = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, VipStatusEntry> escortVipStatusByDevice = new(StringComparer.OrdinalIgnoreCase);
	private readonly Dictionary<string, VipPositionEntry> escortVipPositionByDevice = new(StringComparer.OrdinalIgnoreCase);
	private readonly EscortVipDynamicEntityDataSource escortVipDynamicEntityDataSource = new();
	private readonly HashSet<DynamicEntity> subscribedEscortVipDynamicEntities = new(ReferenceEqualityComparer.Instance);
	private readonly Lock escortVipDynamicEntityGate = new();
	private bool escortVipDynamicEntityInitialized;
	private const string BundledRouteGeodatabaseFileName = "campus-routes.geodatabase";
	private const string DefaultRouteLayerName = "CampusRoute";
	private const string DeveloperFallbackRouteGeodatabasePath = @"C:\Users\greg5999\Documents\ArcGIS\Projects\MyProject5\CampusRoute.geodatabase";

	private const double VipFenceRadiusMeters = 25;
	private const double VipDangerFenceRadiusMeters = VipFenceRadiusMeters * 0.75;
	private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1.5);
	private static readonly TimeSpan EscortVipActiveWindow = TimeSpan.FromSeconds(6);
	private static readonly TimeSpan VipStatusTransitionMinimumInterval = TimeSpan.FromSeconds(1.5);
	private DateTimeOffset lastVipStatusChangeUtc = DateTimeOffset.MinValue;

	public MainPage()
	{
		InitializeComponent();

		var launchOptions = ParseLaunchOptions(Environment.GetCommandLineArgs());
		simulatedMode = string.Equals(launchOptions.Mode, "simulated", StringComparison.OrdinalIgnoreCase);
		configuredRole = string.Equals(launchOptions.Role, "Escort", StringComparison.OrdinalIgnoreCase)
			? "Escort"
			: "VIP";
		configuredDeviceId = !string.IsNullOrWhiteSpace(launchOptions.DeviceId)
			? launchOptions.DeviceId
			: configuredRole == "Escort" ? "Escort" : "VIP-01";
		configuredSessionId = !string.IsNullOrWhiteSpace(launchOptions.SessionId)
			? launchOptions.SessionId
			: "DEVSUMMIT-2026";
		configuredHostUrl = !string.IsNullOrWhiteSpace(launchOptions.HostUrl)
			? launchOptions.HostUrl
			: "ws://127.0.0.1:8765/ws/";
		configuredSimulationUrl = !string.IsNullOrWhiteSpace(launchOptions.SimulationUrl)
			? launchOptions.SimulationUrl
			: "ws://127.0.0.1:8775/ws/";

		Title = configuredDeviceId;

		var invalidNameCharacters = Path.GetInvalidFileNameChars();
		var safeDeviceId = new string(configuredDeviceId.Select(character => invalidNameCharacters.Contains(character) ? '_' : character).ToArray());
		diagnosticsLogPath = Path.Combine(FileSystem.Current.AppDataDirectory, $"field-app-{safeDeviceId}.log");

		ApplyStatusVisual(null, simulatedMode ? "Simulated mode" : "Live mode");
		UpdateVipCommandBanner(null, null, false);
		UpdateEscortMetricsDetail();
		VipBottomPanel.IsVisible = string.Equals(configuredRole, "VIP", StringComparison.OrdinalIgnoreCase);
		EscortBottomPanel.IsVisible = string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase);
		EscortOverallStatusBanner.IsVisible = string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase);
		VipStatusListView.ItemsSource = escortVipStatuses;
		UpdateEscortOverallStatusBanner();
		LogDiagnostic($"Startup role={configuredRole} device={configuredDeviceId} mode={(simulatedMode ? "simulated" : "live")} host={configuredHostUrl} simHost={configuredSimulationUrl} routeLayer={DefaultRouteLayerName}");

		Loaded += OnPageLoaded;
	}

	private async Task<bool> ConnectAsync()
	{
		if (isShuttingDown)
			return false;

		if (socket is { State: WebSocketState.Open })
			return true;

		var role = configuredRole;
		var deviceId = configuredDeviceId;
		var sessionId = configuredSessionId;
		var url = configuredHostUrl;

		if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(url))
		{
			return false;
		}

		try
		{
			socket = new ClientWebSocket();
			receiveCts = new CancellationTokenSource();

			await EnsureEscortVipDynamicEntityDataSourceAsync(role);
			await StartLocationDataSourceAsync();

			await socket.ConnectAsync(new Uri(url), receiveCts.Token);

			var registerEnvelope = MessageSerializer.CreateEnvelope(
				MessageTypes.Register,
				sessionId,
				deviceId,
				new RegisterPayload(deviceId, role, "field-app"));

			var registerSent = await SendAsync(registerEnvelope, receiveCts.Token);
			if (!registerSent)
				throw new InvalidOperationException("Failed to send register envelope because websocket is not open.");
			connectedDeviceId = deviceId;
			connectedSessionId = sessionId;
			vipStatus = null;
			UpdateVipCommandBanner(null, null, false);
			displayedDistance = "  --.- m";
			displayedDirection = "-";
			lastRenderedDetailText = null;
			UpdateEscortMetricsDetail();
			lastInboundUtc = DateTimeOffset.UtcNow;
			lastOutboundUtc = DateTimeOffset.UtcNow;
			hasSentOutbound = false;
			LogDiagnostic($"Connected socket role={role} device={deviceId}");

			await EnsureVipGeotriggerMonitorAsync(role);
			ApplyStatusVisual(null, role == "Escort" ? "Monitoring VIP statuses" : "Waiting for perimeter state");

			receiveTask = Task.Run(() => ReceiveLoopAsync(role, deviceId, receiveCts.Token), receiveCts.Token);
			simulationReceiveTask = null;
			simulationConnectTask = null;
			sendTask = Task.Run(() => SendLoopAsync(role, deviceId, sessionId, receiveCts.Token), receiveCts.Token);

			if (simulatedMode)
			{
				StartSimulationReconnectLoop(role, deviceId, sessionId, receiveCts.Token);
			}

			watchdogCts?.Cancel();
			watchdogCts?.Dispose();
			watchdogCts = new CancellationTokenSource();
			watchdogTask = Task.Run(() => WatchdogLoopAsync(role, watchdogCts.Token), watchdogCts.Token);
			return true;
		}
		catch (Exception ex)
		{
			LogDiagnostic($"ConnectAsync failed: {ex}");
			ApplyStatusVisual(null, "Connection failed");
			await DisconnectAsync();
			return false;
		}
	}

	private async void OnPageLoaded(object? sender, EventArgs e)
	{
		await EnsureConnectedLoopAsync();
	}

	private async Task ReceiveLoopAsync(string role, string deviceId, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested && socket is { State: WebSocketState.Open })
			{
				string? message;
				try
				{
					message = await ReadTextMessageAsync(socket, cancellationToken);
				}
				catch (OperationCanceledException)
				{
					throw;
				}
				catch (Exception ex)
				{
					LogDiagnostic($"ReceiveLoop read error: {ex.Message}");
					if (socket is not { State: WebSocketState.Open })
						break;

					await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
					continue;
				}

				if (message is null)
				{
					LogDiagnostic("ReceiveLoop close frame received.");
					break;
				}

				if (string.IsNullOrWhiteSpace(message))
					continue;

				FieldMessageEnvelope? envelope;
				try
				{
					envelope = MessageSerializer.DeserializeEnvelope(message);
				}
				catch (Exception ex)
				{
					LogDiagnostic($"ReceiveLoop deserialize error: {ex.Message}");
					continue;
				}

				if (envelope is null)
					continue;

				lastInboundUtc = DateTimeOffset.UtcNow;

				await MainThread.InvokeOnMainThreadAsync(() =>
				{
					if (envelope.Type == MessageTypes.AssignedLocation)
					{
						var assigned = MessageSerializer.DeserializePayload<AssignedLocationPayload>(envelope);
						if (assigned is not null
							&& string.Equals(assigned.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
						{
							simulatedLocationDataSource?.SetAssignedLocation(assigned.Latitude, assigned.Longitude);
						}
					}

					if (role == "VIP" && envelope.Type == MessageTypes.AssignedLocation)
					{
						var payload = MessageSerializer.DeserializePayload<AssignedLocationPayload>(envelope);
						if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
							return;
						ApplyAssignedLocationPayload(payload, role, deviceId);
						return;
					}

					if (role == "VIP" && envelope.Type == MessageTypes.EscortPosition)
					{
						var payload = MessageSerializer.DeserializePayload<EscortPositionPayload>(envelope);
						if (payload is null)
							return;

						latestEscortLatitude = payload.Latitude;
						latestEscortLongitude = payload.Longitude;
						simulatedLocationDataSource?.SetEscortReference(payload.Latitude, payload.Longitude);
						_ = UpdateEscortFenceAsync(payload.Latitude, payload.Longitude);
						return;
					}

					if (role == "VIP" && envelope.Type == MessageTypes.VipTelemetry)
					{
						var payload = MessageSerializer.DeserializePayload<VipTelemetryPayload>(envelope);
						if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
							return;
						return;
					}

					if (role == "VIP" && envelope.Type == MessageTypes.VipControl)
					{
						var payload = MessageSerializer.DeserializePayload<VipControlPayload>(envelope);
						if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
							return;

						UpdateVipCommandBanner(payload.Signal, payload.Message, payload.IsActive);
						_ = RelayVipControlToSimulationEngineAsync(payload);
						return;
					}

					if (role == "Escort" && envelope.Type == MessageTypes.VipTelemetry)
					{
						var payload = MessageSerializer.DeserializePayload<VipTelemetryPayload>(envelope);
						if (payload is null)
							return;

						escortVipDynamicEntityDataSource.PublishVipTelemetry(payload);
					}
				});
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			LogDiagnostic($"ReceiveLoop fatal error: {ex}");
		}

		await HandleConnectionDropAsync("ReceiveLoop");
	}

	private async Task ConnectSimulationAsync(string role, string deviceId, string sessionId, CancellationToken cancellationToken)
	{
		await DisconnectSimulationSocketAsync();
		simulationSocket = new ClientWebSocket();
		await simulationSocket.ConnectAsync(new Uri(configuredSimulationUrl), cancellationToken);

		var registerEnvelope = MessageSerializer.CreateEnvelope(
			MessageTypes.Register,
			sessionId,
			deviceId,
			new RegisterPayload(deviceId, role, "field-app"));

		var registerSent = await SendSimulationAsync(registerEnvelope, cancellationToken);
		if (!registerSent)
			throw new InvalidOperationException("Failed to register with simulation engine because websocket is not open.");
	}

	private void StartSimulationReconnectLoop(string role, string deviceId, string sessionId, CancellationToken cancellationToken)
	{
		if (!simulatedMode || isShuttingDown)
			return;

		if (simulationConnectTask is { IsCompleted: false })
			return;

		simulationConnectTask = Task.Run(
			() => EnsureSimulationConnectedAsync(role, deviceId, sessionId, cancellationToken),
			cancellationToken);
	}

	private async Task EnsureSimulationConnectedAsync(string role, string deviceId, string sessionId, CancellationToken cancellationToken)
	{
		if (!simulatedMode || isShuttingDown)
			return;

		if (simulationSocket is { State: WebSocketState.Open } && simulationReceiveTask is { IsCompleted: false })
			return;

		if (simulationReconnectLoopActive)
			return;

		simulationReconnectLoopActive = true;
		try
		{
			while (!isShuttingDown && !cancellationToken.IsCancellationRequested)
			{
				if (simulationSocket is { State: WebSocketState.Open } && simulationReceiveTask is { IsCompleted: false })
					return;

				await simulationConnectGate.WaitAsync(cancellationToken);
				try
				{
					if (simulationSocket is { State: WebSocketState.Open } && simulationReceiveTask is { IsCompleted: false })
						return;

					try
					{
						await ConnectSimulationAsync(role, deviceId, sessionId, cancellationToken);
						simulationReceiveTask = Task.Run(() => SimulationReceiveLoopAsync(role, deviceId, cancellationToken), cancellationToken);
						LogDiagnostic("Simulation socket connected.");
						return;
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception ex)
					{
						LogDiagnostic($"Simulation connect attempt failed: {ex.Message}");
					}
				}
				finally
				{
					simulationConnectGate.Release();
				}

				await Task.Delay(ReconnectDelay, cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
		}
		finally
		{
			simulationReconnectLoopActive = false;
		}
	}

	private async Task SimulationReceiveLoopAsync(string role, string deviceId, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested && simulationSocket is { State: WebSocketState.Open })
			{
				var message = await ReadTextMessageAsync(simulationSocket, cancellationToken);
				if (message is null)
					break;

				if (string.IsNullOrWhiteSpace(message))
					continue;

				var envelope = MessageSerializer.DeserializeEnvelope(message);
				if (envelope is null)
					continue;

				await MainThread.InvokeOnMainThreadAsync(() =>
				{
					if (envelope.Type == MessageTypes.RouteSnapshot)
					{
						_ = RelayRouteSnapshotToDashboardAsync(envelope);
						return;
					}

					if (envelope.Type == MessageTypes.AssignedLocation)
					{
						var payload = MessageSerializer.DeserializePayload<AssignedLocationPayload>(envelope);
						if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
							return;

						ApplyAssignedLocationPayload(payload, role, deviceId);
						return;
					}

					if (role == "VIP" && envelope.Type == MessageTypes.VipControl)
					{
						var payload = MessageSerializer.DeserializePayload<VipControlPayload>(envelope);
						if (payload is null || !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
							return;

						UpdateVipCommandBanner(payload.Signal, payload.Message, payload.IsActive);
					}
				});
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			LogDiagnostic($"SimulationReceiveLoop fatal error: {ex}");
		}
	}

	private async Task RelayRouteSnapshotToDashboardAsync(FieldMessageEnvelope envelope)
	{
		if (socket is null || socket.State != WebSocketState.Open)
			return;

		await SendAsync(envelope, receiveCts?.Token ?? CancellationToken.None);
	}

	private void ApplyAssignedLocationPayload(AssignedLocationPayload payload, string role, string deviceId)
	{
		simulatedLocationDataSource?.SetAssignedLocation(payload.Latitude, payload.Longitude);

		if (!string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase)
			|| !string.Equals(payload.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase))
		{
			return;
		}

		if (!string.IsNullOrWhiteSpace(payload.DirectionToEscort))
		{
			displayedDirection = payload.DirectionToEscort;
		}

		displayedDistance = payload.DistanceMeters.HasValue
			? $"{payload.DistanceMeters.Value,6:F1} m"
			: "  --.- m";
		UpdateEscortMetricsDetail();
	}

	private async Task RelayVipControlToSimulationEngineAsync(VipControlPayload payload)
	{
		if (simulationSocket is null || simulationSocket.State != WebSocketState.Open)
			return;

		var sessionId = connectedSessionId ?? configuredSessionId;
		var deviceId = connectedDeviceId ?? configuredDeviceId;
		var envelope = MessageSerializer.CreateEnvelope(
			MessageTypes.VipControl,
			sessionId,
			deviceId,
			payload);

		await SendSimulationAsync(envelope, receiveCts?.Token ?? CancellationToken.None);
	}

	private async Task SendLoopAsync(string role, string deviceId, string sessionId, CancellationToken cancellationToken)
	{
		try
		{
			while (!cancellationToken.IsCancellationRequested && socket is { State: WebSocketState.Open })
			{
				var location = GetLatestLocation();
				if (location is null)
				{
					await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
					continue;
				}

				var latitude = location.Position.Y;
				var longitude = location.Position.X;

				if (string.Equals(role, "Escort", StringComparison.OrdinalIgnoreCase))
				{
					var escortEnvelope = MessageSerializer.CreateEnvelope(
						MessageTypes.EscortPosition,
						sessionId,
						deviceId,
						new EscortPositionPayload(latitude, longitude));

					try
					{
						var sent = await SendAsync(escortEnvelope, cancellationToken);
						if (!sent)
							throw new WebSocketException("Escort send skipped because websocket is not open.");

						hasSentOutbound = true;
						lastOutboundUtc = DateTimeOffset.UtcNow;
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception ex)
					{
						LogDiagnostic($"SendLoop escort send error: {ex.Message}");
						if (socket is not { State: WebSocketState.Open })
							break;

						await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
						continue;
					}
				}
				else
				{
					double? distanceMeters = null;
					string? directionToEscort = null;
					if (latestEscortLatitude.HasValue && latestEscortLongitude.HasValue)
					{
						distanceMeters = CalculateDistanceMeters(
							latitude,
							longitude,
							latestEscortLatitude.Value,
							latestEscortLongitude.Value);

						directionToEscort = ComputeDirectionCardinal(
							latitude,
							longitude,
							latestEscortLatitude.Value,
							latestEscortLongitude.Value);
					}

					var locationEnvelope = MessageSerializer.CreateEnvelope(
						MessageTypes.LocationUpdate,
						sessionId,
						deviceId,
						new LocationUpdatePayload(latitude, longitude, distanceMeters, directionToEscort));

					try
					{
						var sent = await SendAsync(locationEnvelope, cancellationToken);
						if (!sent)
							throw new WebSocketException("VIP send skipped because websocket is not open.");

						hasSentOutbound = true;
						lastOutboundUtc = DateTimeOffset.UtcNow;
					}
					catch (OperationCanceledException)
					{
						throw;
					}
					catch (Exception ex)
					{
						LogDiagnostic($"SendLoop VIP send error: {ex.Message}");
						if (socket is not { State: WebSocketState.Open })
							break;

						await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
						continue;
					}
				}

				await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken);
			}
		}
		catch (OperationCanceledException)
		{
		}
		catch (Exception ex)
		{
			LogDiagnostic($"SendLoop fatal error: {ex}");
		}

		await HandleConnectionDropAsync("SendLoop");
	}

	private async Task StartLocationDataSourceAsync()
	{
		if (locationDataSource is not null)
		{
			if (locationChangedHandler is not null)
			{
				locationDataSource.LocationChanged -= locationChangedHandler;
			}

			if (locationDataSource.Status == LocationDataSourceStatus.Started)
			{
				await locationDataSource.StopAsync();
			}
		}

		if (simulatedMode)
		{
			const bool passiveAssignedMode = true;
			string? routeDataPath = null;

			simulatedLocationDataSource = new SimulationLocationDataSource(
				configuredRole,
				configuredDeviceId,
				routeDataPath,
				DefaultRouteLayerName,
				passiveAssignedMode: passiveAssignedMode);
			locationDataSource = simulatedLocationDataSource;

			var (initialLatitude, initialLongitude) = GetInitialSimulatedLocation();
			simulatedLocationDataSource.SetAssignedLocation(initialLatitude, initialLongitude);
		}
		else
		{
			simulatedLocationDataSource = null;
			locationDataSource = new SystemLocationDataSource();
		}

		locationChangedHandler = (_, runtimeLocation) =>
		{
			lock (latestLocationGate)
			{
				latestLocation = runtimeLocation;
			}

			if (string.Equals(configuredRole, "VIP", StringComparison.OrdinalIgnoreCase)
				&& (!vipInsideOuterFence.HasValue || !vipInsideInnerFence.HasValue))
			{
				_ = TryInitializeVipFenceStateAsync();
			}
		};

		locationDataSource.LocationChanged += locationChangedHandler;
		await locationDataSource.StartAsync();
	}

	private async Task<string?> ResolveRouteDataPathAsync()
	{
		var safeDeviceId = string.Concat(configuredDeviceId.Select(character => char.IsLetterOrDigit(character) ? character : '-'));
		if (string.IsNullOrWhiteSpace(safeDeviceId))
			safeDeviceId = "device";

		var deployedRoutePath = Path.Combine(FileSystem.Current.AppDataDirectory, $"campus-routes-{safeDeviceId}.geodatabase");
		if (File.Exists(deployedRoutePath))
		{
			LogDiagnostic($"Using deployed route geodatabase: {deployedRoutePath}");
			return deployedRoutePath;
		}

		try
		{
			await using var packageStream = await FileSystem.OpenAppPackageFileAsync(BundledRouteGeodatabaseFileName);
			await using var destinationStream = File.Create(deployedRoutePath);
			await packageStream.CopyToAsync(destinationStream);
			LogDiagnostic($"Copied bundled route geodatabase to {deployedRoutePath}");
			return deployedRoutePath;
		}
		catch (Exception ex)
		{
			LogDiagnostic($"No bundled route geodatabase found ({BundledRouteGeodatabaseFileName}): {ex.Message}");
		}

		if (File.Exists(DeveloperFallbackRouteGeodatabasePath))
		{
			LogDiagnostic($"Using developer fallback route geodatabase: {DeveloperFallbackRouteGeodatabasePath}");
			return DeveloperFallbackRouteGeodatabasePath;
		}

		LogDiagnostic($"Route geodatabase not found at deployed path '{deployedRoutePath}', bundled asset '{BundledRouteGeodatabaseFileName}', or fallback path '{DeveloperFallbackRouteGeodatabasePath}'.");

		return null;
	}

	private (double latitude, double longitude) GetInitialSimulatedLocation()
	{
		const double baseLatitude = 34.0556;
		const double baseLongitude = -117.1825;

		if (string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase))
			return (baseLatitude, baseLongitude);

		var hash = Math.Abs(configuredDeviceId.GetHashCode(StringComparison.OrdinalIgnoreCase));
		var bearingDegrees = hash % 360;
		var basePoint = new MapPoint(baseLongitude, baseLatitude, SpatialReferences.Wgs84);
		var offsetPoint = MovePointGeodetic(basePoint, 22.0, bearingDegrees);
		return (offsetPoint.Y, offsetPoint.X);
	}

	private async Task EnsureVipGeotriggerMonitorAsync(string role)
	{
		if (!string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase)
			|| locationDataSource is null
			|| (vipOuterFenceMonitor is not null && vipInnerFenceMonitor is not null))
			return;

		vipOuterFenceTable ??= new FeatureCollectionTable(
				[Field.CreateString("FenceId", "Fence Id", 64)],
				GeometryType.Point,
				SpatialReferences.Wgs84);

		vipInnerFenceTable ??= new FeatureCollectionTable(
				[Field.CreateString("FenceId", "Fence Id", 64)],
				GeometryType.Point,
				SpatialReferences.Wgs84);

		var outerFenceParameters = new FeatureFenceParameters(vipOuterFenceTable, VipFenceRadiusMeters);
		var outerGeotrigger = new FenceGeotrigger(
			new LocationGeotriggerFeed(locationDataSource),
			FenceRuleType.EnterOrExit,
			outerFenceParameters)
		{
			FeedAccuracyMode = FenceGeotriggerFeedAccuracyMode.UseGeometryWithAccuracy,
			EnterExitSpatialRelationship = FenceEnterExitSpatialRelationship.EnterContainsAndExitDoesNotIntersect
		};

		vipOuterFenceMonitor = new GeotriggerMonitor(outerGeotrigger);
		vipOuterFenceMonitor.Notification += async (_, notificationInfo) =>
		{
			if (notificationInfo is not FenceGeotriggerNotificationInfo fenceNotification)
				return;

			await HandleVipFenceNotificationAsync(fenceNotification, isInnerFence: false);
		};

		var innerFenceParameters = new FeatureFenceParameters(vipInnerFenceTable, VipDangerFenceRadiusMeters);
		var innerGeotrigger = new FenceGeotrigger(
			new LocationGeotriggerFeed(locationDataSource),
			FenceRuleType.EnterOrExit,
			innerFenceParameters)
		{
			FeedAccuracyMode = FenceGeotriggerFeedAccuracyMode.UseGeometryWithAccuracy,
			EnterExitSpatialRelationship = FenceEnterExitSpatialRelationship.EnterContainsAndExitDoesNotIntersect
		};

		vipInnerFenceMonitor = new GeotriggerMonitor(innerGeotrigger);
		vipInnerFenceMonitor.Notification += async (_, notificationInfo) =>
		{
			if (notificationInfo is not FenceGeotriggerNotificationInfo fenceNotification)
				return;

			await HandleVipFenceNotificationAsync(fenceNotification, isInnerFence: true);
		};

		await vipOuterFenceMonitor.StartAsync();
		await vipInnerFenceMonitor.StartAsync();
		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			ApplyStatusVisual(null, "Waiting for escort perimeter");
		});
	}

	private async Task HandleVipFenceNotificationAsync(FenceGeotriggerNotificationInfo fenceNotification, bool isInnerFence)
	{
		bool? isInsideFence = fenceNotification.FenceNotificationType switch
		{
			FenceNotificationType.Entered => true,
			FenceNotificationType.Exited => false,
			_ => (bool?)null
		};

		if (!isInsideFence.HasValue)
			return;

		if (isInnerFence)
			vipInsideInnerFence = isInsideFence.Value;
		else
			vipInsideOuterFence = isInsideFence.Value;

		var nextStatus = DetermineVipStatusFromFenceState();
		if (nextStatus is null)
			return;

		await ApplyVipStatusAsync(nextStatus, allowThrottle: true, countGeotriggerEvent: true);
	}

	private string? DetermineVipStatusFromFenceState()
	{
		if (!vipInsideOuterFence.HasValue || !vipInsideInnerFence.HasValue)
			return null;

		if (!vipInsideOuterFence.Value)
			return "Out";

		return vipInsideInnerFence.Value ? "In" : "Danger";
	}

	private async Task UpdateEscortFenceAsync(double latitude, double longitude)
	{
		if (vipOuterFenceTable is null || vipInnerFenceTable is null)
			return;

		await escortFenceUpdateGate.WaitAsync();
		try
		{
			var geometry = new MapPoint(longitude, latitude, SpatialReferences.Wgs84);
			if (vipOuterFenceFeature is null)
			{
				vipOuterFenceFeature = vipOuterFenceTable.CreateFeature(
					[new KeyValuePair<string, object?>("FenceId", "EscortLead")],
					geometry);

				await vipOuterFenceTable.AddFeatureAsync(vipOuterFenceFeature);
			}
			else
			{
				vipOuterFenceFeature.Geometry = geometry;
				await vipOuterFenceTable.UpdateFeatureAsync(vipOuterFenceFeature);
			}

			if (vipInnerFenceFeature is null)
			{
				vipInnerFenceFeature = vipInnerFenceTable.CreateFeature(
					[new KeyValuePair<string, object?>("FenceId", "EscortLeadInner")],
					geometry);

				await vipInnerFenceTable.AddFeatureAsync(vipInnerFenceFeature);
			}
			else
			{
				vipInnerFenceFeature.Geometry = geometry;
				await vipInnerFenceTable.UpdateFeatureAsync(vipInnerFenceFeature);
			}
		}
		finally
		{
			escortFenceUpdateGate.Release();
		}

		await TryInitializeVipFenceStateAsync();
	}

	private async Task TryInitializeVipFenceStateAsync()
	{
		if (!string.Equals(configuredRole, "VIP", StringComparison.OrdinalIgnoreCase))
			return;

		if (!vipInsideOuterFence.HasValue || !vipInsideInnerFence.HasValue)
			return;

		var nextStatus = DetermineVipStatusFromFenceState();
		if (nextStatus is null)
			return;

		await ApplyVipStatusAsync(nextStatus, allowThrottle: false, countGeotriggerEvent: false);
	}

	private async Task ApplyVipStatusAsync(string nextStatus, bool allowThrottle, bool countGeotriggerEvent)
	{
		if (string.IsNullOrWhiteSpace(nextStatus))
			return;

		var changed = !string.Equals(vipStatus, nextStatus, StringComparison.OrdinalIgnoreCase);
		if (changed && allowThrottle && DateTimeOffset.UtcNow - lastVipStatusChangeUtc < VipStatusTransitionMinimumInterval)
			return;

		vipStatus = nextStatus;
		if (changed)
		{
			lastVipStatusChangeUtc = DateTimeOffset.UtcNow;
		}

		if (countGeotriggerEvent)
		{
			Interlocked.Increment(ref geotriggerEventCount);
		}

		await MainThread.InvokeOnMainThreadAsync(() =>
		{
			ApplyStatusVisual(nextStatus, null);
		});

		if (changed)
		{
			await PublishVipStatusTransitionAsync(nextStatus);
		}
	}

	private Esri.ArcGISRuntime.Location.Location? GetLatestLocation()
	{
		lock (latestLocationGate)
		{
			return latestLocation;
		}
	}

	private async Task<bool> SendAsync(FieldMessageEnvelope envelope, CancellationToken cancellationToken)
	{
		if (socket is null || socket.State != WebSocketState.Open)
			return false;

		await socketSendGate.WaitAsync(cancellationToken);
		try
		{
			if (socket is null || socket.State != WebSocketState.Open)
				return false;

			var json = MessageSerializer.SerializeEnvelope(envelope);
			var bytes = Encoding.UTF8.GetBytes(json);
			await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
			return true;
		}
		finally
		{
			socketSendGate.Release();
		}
	}

	private async Task<bool> SendSimulationAsync(FieldMessageEnvelope envelope, CancellationToken cancellationToken)
	{
		if (simulationSocket is null || simulationSocket.State != WebSocketState.Open)
			return false;

		await socketSendGate.WaitAsync(cancellationToken);
		try
		{
			if (simulationSocket is null || simulationSocket.State != WebSocketState.Open)
				return false;

			var json = MessageSerializer.SerializeEnvelope(envelope);
			var bytes = Encoding.UTF8.GetBytes(json);
			await simulationSocket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken);
			return true;
		}
		finally
		{
			socketSendGate.Release();
		}
	}

	private static async Task<string?> ReadTextMessageAsync(ClientWebSocket websocket, CancellationToken cancellationToken)
	{
		var buffer = new byte[4 * 1024];
		using var stream = new MemoryStream();

		while (true)
		{
			var result = await websocket.ReceiveAsync(buffer, cancellationToken);
			if (result.MessageType == WebSocketMessageType.Close)
				return null;

			stream.Write(buffer, 0, result.Count);
			if (result.EndOfMessage)
				break;
		}

		return Encoding.UTF8.GetString(stream.ToArray());
	}

	private async Task DisconnectAsync()
	{
		if (receiveCts is not null)
		{
			receiveCts.Cancel();
			receiveCts.Dispose();
			receiveCts = null;
		}

		if (watchdogCts is not null)
		{
			watchdogCts.Cancel();
			watchdogCts.Dispose();
			watchdogCts = null;
		}

		receiveTask = null;
		simulationReceiveTask = null;
		simulationConnectTask = null;
		sendTask = null;
		watchdogTask = null;

		if (vipOuterFenceMonitor is not null)
		{
			vipOuterFenceMonitor.Stop();
			vipOuterFenceMonitor = null;
		}

		if (vipInnerFenceMonitor is not null)
		{
			vipInnerFenceMonitor.Stop();
			vipInnerFenceMonitor = null;
		}

		vipOuterFenceFeature = null;
		vipInnerFenceFeature = null;
		vipOuterFenceTable = null;
		vipInnerFenceTable = null;
		vipInsideOuterFence = null;
		vipInsideInnerFence = null;
		vipStatus = null;
		connectedDeviceId = null;
		connectedSessionId = null;
		UpdateVipCommandBanner(null, null, false);

		if (locationDataSource is not null)
		{
			if (locationChangedHandler is not null)
			{
				locationDataSource.LocationChanged -= locationChangedHandler;
			}

			if (locationDataSource.Status == LocationDataSourceStatus.Started)
			{
				await locationDataSource.StopAsync();
			}
		}

		locationDataSource = null;
		locationChangedHandler = null;
		simulatedLocationDataSource = null;
		lock (latestLocationGate)
		{
			latestLocation = null;
		}

		if (socket is not null)
		{
			try
			{
				if (socket.State == WebSocketState.Open)
				{
					await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
				}
			}
			catch
			{
			}

			socket.Dispose();
			socket = null;
		}

		await DisconnectSimulationSocketAsync();

		if (!isShuttingDown)
		{
			ApplyStatusVisual(null, "Disconnected");
		}

		displayedDistance = "  --.- m";
		displayedDirection = "-";
		UpdateEscortMetricsDetail();
	}

	private async Task HandleConnectionDropAsync(string source)
	{
		if (isShuttingDown)
			return;

		if (handlingConnectionDrop)
			return;

		handlingConnectionDrop = true;

		try
		{
			LogDiagnostic($"Connection drop detected by {source}. Beginning reconnect sequence.");
			ApplyStatusVisual(null, "Reconnecting");

			try
			{
				await DisconnectAsync();
			}
			catch (Exception ex)
			{
				LogDiagnostic($"Disconnect during reconnect failed: {ex}");
			}

			await Task.Delay(ReconnectDelay);

			try
			{
				await EnsureConnectedLoopAsync();
			}
			catch (Exception ex)
			{
				LogDiagnostic($"EnsureConnectedLoopAsync failed during reconnect: {ex}");
			}
		}
		finally
		{
			LogDiagnostic($"Reconnect sequence from {source} completed. SocketOpen={socket is { State: WebSocketState.Open }}");
			handlingConnectionDrop = false;
		}
	}

	private async Task EnsureConnectedLoopAsync()
	{
		if (reconnectLoopActive || isShuttingDown)
			return;

		reconnectLoopActive = true;
		try
		{
			while (!isShuttingDown)
			{
				if (socket is { State: WebSocketState.Open })
				{
					LogDiagnostic("EnsureConnectedLoopAsync: socket already open.");
					return;
				}

				var connected = await ConnectAsync();
				if (connected)
				{
					LogDiagnostic("EnsureConnectedLoopAsync: connected successfully.");
					return;
				}

				LogDiagnostic("EnsureConnectedLoopAsync: connect attempt failed; retrying.");
				await Task.Delay(ReconnectDelay);
			}
		}
		finally
		{
			reconnectLoopActive = false;
		}
	}

	private async Task WatchdogLoopAsync(string role, CancellationToken cancellationToken)
	{
		while (!cancellationToken.IsCancellationRequested && !isShuttingDown)
		{
			try
			{
				await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken);
			}
			catch (OperationCanceledException)
			{
				break;
			}

			if (socket is not { State: WebSocketState.Open })
				continue;

			if (receiveTask is { IsCompleted: true } || sendTask is { IsCompleted: true })
			{
				LogDiagnostic("Watchdog detected completed receive/send task while socket is open.");
				await HandleConnectionDropAsync("Watchdog-CompletedTask");
				continue;
			}

			if (string.Equals(role, "VIP", StringComparison.OrdinalIgnoreCase)
				&& hasSentOutbound
				&& DateTimeOffset.UtcNow - lastOutboundUtc > TimeSpan.FromSeconds(8))
			{
				LogDiagnostic($"Watchdog detected outbound stall. Last outbound age={(DateTimeOffset.UtcNow - lastOutboundUtc).TotalSeconds:F1}s");
				await HandleConnectionDropAsync("Watchdog-OutboundStall");
				continue;
			}

			if (simulatedMode)
			{
				if (simulationReceiveTask is { IsCompleted: true })
				{
					LogDiagnostic("Watchdog detected completed simulation receive task. Reconnecting simulation socket.");
					StartSimulationReconnectLoop(role, configuredDeviceId, configuredSessionId, cancellationToken);
					continue;
				}

				if (simulationSocket is not { State: WebSocketState.Open })
				{
					StartSimulationReconnectLoop(role, configuredDeviceId, configuredSessionId, cancellationToken);
				}
			}
		}
	}

	private async Task DisconnectSimulationSocketAsync()
	{
		if (simulationSocket is null)
			return;

		try
		{
			if (simulationSocket.State == WebSocketState.Open)
			{
				await simulationSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Client disconnect", CancellationToken.None);
			}
		}
		catch
		{
		}

		simulationSocket.Dispose();
		simulationSocket = null;
	}

	private async Task PublishVipStatusTransitionAsync(string nextStatus)
	{
		var deviceId = connectedDeviceId;
		var sessionId = connectedSessionId;
		if (string.IsNullOrWhiteSpace(deviceId) || string.IsNullOrWhiteSpace(sessionId))
			return;

		var statusEnvelope = MessageSerializer.CreateEnvelope(
			MessageTypes.StatusUpdate,
			sessionId,
			deviceId,
			new StatusUpdatePayload(nextStatus));

		var sent = await SendAsync(statusEnvelope, receiveCts?.Token ?? CancellationToken.None);
		if (!sent)
		{
			LogDiagnostic("PublishVipStatusTransitionAsync skipped because websocket is not open.");
		}

		if (simulatedMode)
		{
			var simulationSent = await SendSimulationAsync(statusEnvelope, receiveCts?.Token ?? CancellationToken.None);
			if (!simulationSent)
			{
				LogDiagnostic("PublishVipStatusTransitionAsync to simulation engine skipped because simulation websocket is not open.");
			}
		}
	}

	private static double CalculateDistanceMeters(double latitude1, double longitude1, double latitude2, double longitude2)
	{
		var fromPoint = new MapPoint(longitude1, latitude1, SpatialReferences.Wgs84);
		var toPoint = new MapPoint(longitude2, latitude2, SpatialReferences.Wgs84);
		return RuntimeGeoMath.CalculateDistanceMeters(fromPoint, toPoint);
	}

	private static MapPoint MovePointGeodetic(MapPoint startPoint, double distanceMeters, double bearingDegrees) =>
		RuntimeGeoMath.MovePointGeodetic(startPoint, distanceMeters, bearingDegrees);

	private static string ComputeDirectionCardinal(double fromLatitude, double fromLongitude, double toLatitude, double toLongitude)
	{
		var fromPoint = new MapPoint(fromLongitude, fromLatitude, SpatialReferences.Wgs84);
		var toPoint = new MapPoint(toLongitude, toLatitude, SpatialReferences.Wgs84);
		var bearing = RuntimeGeoMath.ComputeBearingDegrees(fromPoint, toPoint);

		var cardinals = new[] { "N", "NE", "E", "SE", "S", "SW", "W", "NW" };
		var index = (int)Math.Round(bearing / 45.0, MidpointRounding.AwayFromZero) % 8;
		return cardinals[index];
	}

	private void ApplyStatusVisual(string? status, string? detail)
	{
		var normalized = status?.Trim();
		if (string.Equals(normalized, "In", StringComparison.OrdinalIgnoreCase))
		{
			BackgroundColor = Color.FromArgb("#2E7D32");
			RoleStateLabel.Text = "IN";
		}
		else if (string.Equals(normalized, "Out", StringComparison.OrdinalIgnoreCase))
		{
			BackgroundColor = Color.FromArgb("#C62828");
			RoleStateLabel.Text = "OUT";
		}
		else if (string.Equals(normalized, "Near", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(normalized, "Edge", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(normalized, "Warning", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(normalized, "Danger", StringComparison.OrdinalIgnoreCase))
		{
			BackgroundColor = Color.FromArgb("#F9A825");
			RoleStateLabel.Text = "WARNING";
		}
		else
		{
			BackgroundColor = Color.FromArgb("#455A64");
			if (string.IsNullOrWhiteSpace(normalized))
			{
				RoleStateLabel.Text = string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase)
					? "ACTIVE"
					: "CONNECTED";
			}
			else
			{
				RoleStateLabel.Text = normalized.ToUpperInvariant();
			}
		}

		if (!string.IsNullOrWhiteSpace(detail))
		{
			if (!string.Equals(configuredRole, "VIP", StringComparison.OrdinalIgnoreCase))
			{
				SetDetailTextIfChanged(detail);
			}
		}
	}

	private void UpdateEscortMetricsDetail()
	{
		var directionToken = string.IsNullOrWhiteSpace(displayedDirection) || string.Equals(displayedDirection, "-", StringComparison.Ordinal)
			? "--"
			: displayedDirection.Trim().ToUpperInvariant();

		if (directionToken.Length > 2)
			directionToken = directionToken[..2];
		else if (directionToken.Length < 2)
			directionToken = directionToken.PadLeft(2, ' ');

		SetDetailTextIfChanged($"Escort: {displayedDistance} {directionToken}");
	}

	private async Task EnsureEscortVipDynamicEntityDataSourceAsync(string role)
	{
		if (!string.Equals(role, "Escort", StringComparison.OrdinalIgnoreCase))
			return;

		if (escortVipDynamicEntityInitialized)
			return;

		await escortVipDynamicEntityDataSource.LoadAsync();
		await escortVipDynamicEntityDataSource.ConnectAsync();
		escortVipDynamicEntityDataSource.DynamicEntityReceived += OnEscortVipDynamicEntityReceived;
		escortVipDynamicEntityDataSource.DynamicEntityPurged += OnEscortVipDynamicEntityPurged;
		escortVipDynamicEntityInitialized = true;
	}

	private void OnEscortVipDynamicEntityReceived(object? sender, DynamicEntityEventArgs eventArgs)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			foreach (var dynamicEntity in EnumerateDynamicEntities(eventArgs))
			{
				TrySubscribeEscortVipDynamicEntityChanged(dynamicEntity);
				UpsertEscortVipStatusFromDynamicEntity(dynamicEntity);
			}
		});
	}

	private void OnEscortVipDynamicEntityPurged(object? sender, DynamicEntityEventArgs eventArgs)
	{
		MainThread.BeginInvokeOnMainThread(() =>
		{
			var changed = false;
			foreach (var dynamicEntity in EnumerateDynamicEntities(eventArgs))
			{
				var entityKey = GetDynamicEntityUniqueKey(dynamicEntity);
				if (entityKey is null)
					continue;

				escortVipStatusByDevice.Remove(entityKey);
				escortVipPositionByDevice.Remove(entityKey);
				changed = true;
			}

			if (changed)
			{
				RebuildEscortVipList();
			}
		});
	}

	private void TrySubscribeEscortVipDynamicEntityChanged(DynamicEntity dynamicEntity)
	{
		lock (escortVipDynamicEntityGate)
		{
			if (!subscribedEscortVipDynamicEntities.Add(dynamicEntity))
				return;

			dynamicEntity.DynamicEntityChanged += (_, _) =>
				MainThread.BeginInvokeOnMainThread(() => UpsertEscortVipStatusFromDynamicEntity(dynamicEntity));
		}
	}

	private void UpsertEscortVipStatusFromDynamicEntity(DynamicEntity dynamicEntity)
	{
		var entityKey = GetDynamicEntityUniqueKey(dynamicEntity);
		if (entityKey is null)
			return;

		var displayName = NormalizeVipDeviceId(ReadDynamicEntityString(dynamicEntity.Attributes, EscortVipDynamicEntityDataSource.EntityIdFieldName))
			?? entityKey;
		var status = ReadDynamicEntityString(dynamicEntity.Attributes, EscortVipDynamicEntityDataSource.StatusFieldName);
		UpsertEscortVipStatus(entityKey, displayName, status);

		var latitude = ReadDynamicEntityDouble(dynamicEntity.Attributes, "latitude");
		var longitude = ReadDynamicEntityDouble(dynamicEntity.Attributes, "longitude");
		if (latitude.HasValue && longitude.HasValue)
		{
			UpdateEscortVipPosition(entityKey, latitude.Value, longitude.Value);
		}
	}

	private static IEnumerable<DynamicEntity> EnumerateDynamicEntities(DynamicEntityEventArgs eventArgs)
	{
		if (eventArgs.DynamicEntity is not null)
		{
			yield return eventArgs.DynamicEntity;
		}
	}

	private static string? ReadDynamicEntityString(IDictionary<string, object?> attributes, string key)
	{
		foreach (var pair in attributes)
		{
			if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
				continue;

			return pair.Value?.ToString();
		}

		return null;
	}

	private static double? ReadDynamicEntityDouble(IDictionary<string, object?> attributes, string key)
	{
		foreach (var pair in attributes)
		{
			if (!string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
				continue;

			if (pair.Value is null)
				return null;

			if (pair.Value is double doubleValue)
				return doubleValue;

			if (double.TryParse(pair.Value.ToString(), out var parsedValue))
				return parsedValue;

			return null;
		}

		return null;
	}

	private void UpsertEscortVipStatus(string entityKey, string displayName, string? status)
	{
		var normalizedEntityKey = NormalizeVipDeviceId(entityKey);
		var normalizedDisplayName = NormalizeVipDeviceId(displayName);
		if (!string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase)
			|| normalizedEntityKey is null
			|| normalizedDisplayName is null)
		{
			return;
		}

		var normalizedStatus = NormalizeVipStatusLabel(status);
		escortVipStatusByDevice[normalizedEntityKey] = new VipStatusEntry(
			normalizedDisplayName,
			normalizedStatus,
			ResolveVipStatusBackground(normalizedStatus),
			DateTimeOffset.UtcNow);

		RebuildEscortVipList();
	}

	private void RebuildEscortVipList()
	{
		var activeCutoff = DateTimeOffset.UtcNow - EscortVipActiveWindow;

		var activeDeviceIds = escortVipStatusByDevice
			.Where(pair => pair.Value.LastSeenUtc >= activeCutoff)
			.Select(pair => pair.Key)
			.OrderBy(deviceId => deviceId, Comparer<string>.Create(CompareVipDeviceIds))
			.ToList();
		var activeDeviceIdSet = new HashSet<string>(activeDeviceIds, StringComparer.OrdinalIgnoreCase);

		var staleDeviceIds = escortVipListItemByDevice.Keys
			.Where(deviceId => !activeDeviceIdSet.Contains(deviceId))
			.ToList();

		foreach (var staleDeviceId in staleDeviceIds)
		{
			if (!escortVipListItemByDevice.TryGetValue(staleDeviceId, out var staleItem))
				continue;

			escortVipStatuses.Remove(staleItem);
			escortVipListItemByDevice.Remove(staleDeviceId);
		}

		for (var targetIndex = 0; targetIndex < activeDeviceIds.Count; targetIndex++)
		{
			var entityKey = activeDeviceIds[targetIndex];
			if (!escortVipStatusByDevice.TryGetValue(entityKey, out var entry))
				continue;

			if (!escortVipListItemByDevice.TryGetValue(entityKey, out var item))
			{
				item = new VipStatusListItem(entry.DisplayName, entry.Status, entry.BackgroundColor);
				escortVipListItemByDevice[entityKey] = item;
				escortVipStatuses.Insert(Math.Min(targetIndex, escortVipStatuses.Count), item);
			}
			else
			{
				var existingIndex = escortVipStatuses.IndexOf(item);
				if (existingIndex >= 0 && existingIndex != targetIndex)
				{
					escortVipStatuses.Move(existingIndex, targetIndex);
				}

				item.DeviceId = entry.DisplayName;
				item.Status = entry.Status;
				item.BackgroundColor = entry.BackgroundColor;
			}
		}

		UpdateEscortOverallStatusBanner();
	}

	private void UpdateEscortOverallStatusBanner()
	{
		if (!string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase))
			return;

		var activeCutoff = DateTimeOffset.UtcNow - EscortVipActiveWindow;
		var activeStatuses = escortVipStatusByDevice
			.Where(pair => pair.Value.LastSeenUtc >= activeCutoff)
			.Select(pair => pair.Value.Status)
			.ToList();

		if (activeStatuses.Count == 0)
		{
			EscortOverallStatusBanner.BackgroundColor = Color.FromArgb("#455A64");
			EscortOverallStatusLabel.Text = "Awaiting VIP telemetry";
			return;
		}

		if (activeStatuses.Any(status => string.Equals(status, "Out", StringComparison.OrdinalIgnoreCase)))
		{
			EscortOverallStatusBanner.BackgroundColor = Color.FromArgb("#C62828");
			EscortOverallStatusLabel.Text = "VIPs Out of Range";
			return;
		}

		if (activeStatuses.Any(status => string.Equals(status, "WARNING", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(status, "Near", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(status, "Danger", StringComparison.OrdinalIgnoreCase)))
		{
			EscortOverallStatusBanner.BackgroundColor = Color.FromArgb("#F9A825");
			EscortOverallStatusLabel.Text = "VIPs in Warning Perimeter";
			return;
		}

		EscortOverallStatusBanner.BackgroundColor = Color.FromArgb("#2E7D32");
		EscortOverallStatusLabel.Text = "All VIPs in Range";
	}

	private void UpdateEscortVipPosition(string entityKey, double latitude, double longitude)
	{
		var normalizedEntityKey = NormalizeVipDeviceId(entityKey);
		if (!string.Equals(configuredRole, "Escort", StringComparison.OrdinalIgnoreCase)
			|| normalizedEntityKey is null)
		{
			return;
		}

		escortVipPositionByDevice[normalizedEntityKey] = new VipPositionEntry(latitude, longitude, DateTimeOffset.UtcNow);
		var activeCutoff = DateTimeOffset.UtcNow - EscortVipActiveWindow;

		var activePositions = escortVipPositionByDevice
			.Where(pair => pair.Value.LastSeenUtc >= activeCutoff)
			.Select(pair => pair.Value)
			.ToList();

		if (activePositions.Count == 0)
		{
			return;
		}

		var centroidLatitude = activePositions.Average(position => position.Latitude);
		var centroidLongitude = activePositions.Average(position => position.Longitude);
		simulatedLocationDataSource?.SetEscortPacingReference(centroidLatitude, centroidLongitude);
	}

	private static int CompareVipDeviceIds(string? left, string? right)
	{
		left ??= string.Empty;
		right ??= string.Empty;

		var leftHasNumericSuffix = TryGetTrailingNumber(left, out var leftNumber);
		var rightHasNumericSuffix = TryGetTrailingNumber(right, out var rightNumber);

		if (leftHasNumericSuffix && rightHasNumericSuffix)
		{
			var textCompare = string.Compare(GetPrefixWithoutTrailingNumber(left), GetPrefixWithoutTrailingNumber(right), StringComparison.OrdinalIgnoreCase);
			if (textCompare != 0)
				return textCompare;

			return leftNumber.CompareTo(rightNumber);
		}

		return string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
	}

	private static bool TryGetTrailingNumber(string value, out int number)
	{
		number = 0;
		if (string.IsNullOrWhiteSpace(value))
			return false;

		var end = value.Length - 1;
		while (end >= 0 && char.IsDigit(value[end]))
		{
			end--;
		}

		if (end == value.Length - 1)
			return false;

		var numericPart = value[(end + 1)..];
		return int.TryParse(numericPart, out number);
	}

	private static string GetPrefixWithoutTrailingNumber(string value)
	{
		if (string.IsNullOrWhiteSpace(value))
			return string.Empty;

		var end = value.Length - 1;
		while (end >= 0 && char.IsDigit(value[end]))
		{
			end--;
		}

		return end >= 0 ? value[..(end + 1)].TrimEnd('-', ' ') : string.Empty;
	}

	private static string? NormalizeVipDeviceId(string? deviceId)
	{
		if (string.IsNullOrWhiteSpace(deviceId))
			return null;

		var normalizedForm = deviceId.Normalize(NormalizationForm.FormKC).Trim();
		if (normalizedForm.Length == 0)
			return null;

		var builder = new StringBuilder(normalizedForm.Length);
		var previousWasWhitespace = false;

		foreach (var character in normalizedForm)
		{
			if (character is '‐' or '‑' or '‒' or '–' or '—' or '―' or '−')
			{
				builder.Append('-');
				previousWasWhitespace = false;
				continue;
			}

			if (char.IsWhiteSpace(character))
			{
				if (previousWasWhitespace)
					continue;

				builder.Append(' ');
				previousWasWhitespace = true;
				continue;
			}

			builder.Append(character);
			previousWasWhitespace = false;
		}

		var canonical = builder.ToString().Trim();
		return canonical.Length == 0 ? null : canonical;
	}

	private static string? GetDynamicEntityUniqueKey(DynamicEntity dynamicEntity)
	{
		var dynamicEntityIdProperty = dynamicEntity.GetType().GetProperty("DynamicEntityId");
		if (dynamicEntityIdProperty?.GetValue(dynamicEntity) is object dynamicEntityId)
		{
			var normalized = NormalizeVipDeviceId(dynamicEntityId.ToString());
			if (normalized is not null)
				return normalized;
		}

		var fallbackTrackId = ReadDynamicEntityString(dynamicEntity.Attributes, EscortVipDynamicEntityDataSource.EntityIdFieldName);
		return NormalizeVipDeviceId(fallbackTrackId);
	}

	private static Color ResolveVipStatusBackground(string status)
	{
		if (string.Equals(status, "In", StringComparison.OrdinalIgnoreCase))
			return Color.FromArgb("#2E7D32");

		if (string.Equals(status, "Near", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(status, "Danger", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(status, "Warning", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(status, "WARN", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(status, "WARNING", StringComparison.OrdinalIgnoreCase))
			return Color.FromArgb("#F9A825");

		if (string.Equals(status, "Out", StringComparison.OrdinalIgnoreCase))
			return Color.FromArgb("#C62828");

		return Color.FromArgb("#455A64");
	}

	private static string NormalizeVipStatusLabel(string? status)
	{
		if (string.IsNullOrWhiteSpace(status))
			return "Unknown";

		var normalizedStatus = status.Trim();
		if (string.Equals(normalizedStatus, "Near", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(normalizedStatus, "Danger", StringComparison.OrdinalIgnoreCase)
			|| string.Equals(normalizedStatus, "Warning", StringComparison.OrdinalIgnoreCase))
		{
			return "WARNING";
		}

		return normalizedStatus;
	}

	private void SetDetailTextIfChanged(string detailText)
	{
		if (string.Equals(lastRenderedDetailText, detailText, StringComparison.Ordinal))
			return;

		lastRenderedDetailText = detailText;
		DetailLabel.Text = detailText;
	}

	private void UpdateVipCommandBanner(string? signal, string? message, bool isActive)
	{
		if (!string.Equals(configuredRole, "VIP", StringComparison.OrdinalIgnoreCase))
		{
			VipCommandBanner.IsVisible = false;
			return;
		}

		if (!isActive)
		{
			VipCommandBanner.IsVisible = false;
			VipCommandLabel.Text = string.Empty;
			return;
		}

		var normalizedSignal = signal?.Trim();
		var normalizedMessage = string.IsNullOrWhiteSpace(message)
			? "Operator command received"
			: message.Trim();

		if (string.Equals(normalizedSignal, "STOP", StringComparison.OrdinalIgnoreCase))
		{
			VipCommandBanner.Background = Color.FromArgb("#CCA61B1B");
			VipCommandBanner.Stroke = Color.FromArgb("#FFEF4444");
			VipCommandLabel.Text = $"🛑 {normalizedMessage}";
		}
		else if (string.Equals(normalizedSignal, "HURRY", StringComparison.OrdinalIgnoreCase))
		{
			VipCommandBanner.Background = Color.FromArgb("#CC1D4ED8");
			VipCommandBanner.Stroke = Color.FromArgb("#FF93C5FD");
			VipCommandLabel.Text = $"⚡ {normalizedMessage}";
		}
		else
		{
			VipCommandBanner.Background = Color.FromArgb("#CC065F46");
			VipCommandBanner.Stroke = Color.FromArgb("#FF86EFAC");
			VipCommandLabel.Text = $"✅ {normalizedMessage}";
		}

		VipCommandBanner.IsVisible = true;
	}

	private static LaunchOptions ParseLaunchOptions(string[] args)
	{
		var options = new LaunchOptions();

		for (var i = 0; i < args.Length; i++)
		{
			var arg = args[i];
			if (!arg.StartsWith("--", StringComparison.Ordinal))
				continue;

			if (string.Equals(arg, "--autoconnect", StringComparison.OrdinalIgnoreCase)
				|| string.Equals(arg, "--exit-on-disconnect", StringComparison.OrdinalIgnoreCase))
			{
				continue;
			}

			if (i >= args.Length - 1)
				continue;

			var value = args[i + 1];
			switch (arg.ToLowerInvariant())
			{
				case "--role":
					options.Role = value;
					i++;
					break;
				case "--mode":
					options.Mode = value;
					i++;
					break;
				case "--device":
					options.DeviceId = value;
					i++;
					break;
				case "--session":
					options.SessionId = value;
					i++;
					break;
				case "--host":
					options.HostUrl = value;
					i++;
					break;
				case "--sim-host":
					options.SimulationUrl = value;
					i++;
					break;
			}
		}

		return options;
	}

	private sealed class VipStatusListItem : System.ComponentModel.INotifyPropertyChanged
	{
		private string deviceId;
		private string status;
		private Color backgroundColor;

		public VipStatusListItem(string deviceId, string status, Color backgroundColor)
		{
			this.deviceId = deviceId;
			this.status = status;
			this.backgroundColor = backgroundColor;
		}

		public string DeviceId
		{
			get => deviceId;
			set
			{
				if (string.Equals(deviceId, value, StringComparison.Ordinal))
					return;

				deviceId = value;
				PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(DeviceId)));
			}
		}

		public string Status
		{
			get => status;
			set
			{
				if (string.Equals(status, value, StringComparison.Ordinal))
					return;

				status = value;
				PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Status)));
			}
		}

		public Color BackgroundColor
		{
			get => backgroundColor;
			set
			{
				if (backgroundColor == value)
					return;

				backgroundColor = value;
				PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(BackgroundColor)));
			}
		}

		public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
	}

	private sealed record VipStatusEntry(string DisplayName, string Status, Color BackgroundColor, DateTimeOffset LastSeenUtc);

	private sealed record VipPositionEntry(double Latitude, double Longitude, DateTimeOffset LastSeenUtc);

	protected override async void OnDisappearing()
	{
		isShuttingDown = true;
		LogDiagnostic("OnDisappearing invoked. Shutting down.");
		await DisconnectAsync();
		base.OnDisappearing();
	}

	private void LogDiagnostic(string message)
	{
		try
		{
			var line = $"{DateTimeOffset.UtcNow:O} [{configuredDeviceId}] {message}";
			System.Diagnostics.Debug.WriteLine(line);

			lock (diagnosticsGate)
			{
				var directory = Path.GetDirectoryName(diagnosticsLogPath);
				if (!string.IsNullOrWhiteSpace(directory))
				{
					Directory.CreateDirectory(directory);
				}

				File.AppendAllText(diagnosticsLogPath, line + Environment.NewLine);
			}
		}
		catch
		{
		}
	}

	private sealed class LaunchOptions
	{
		public string? Role { get; set; }

		public string? Mode { get; set; } = "live";

		public string? DeviceId { get; set; }

		public string? SessionId { get; set; }

		public string? HostUrl { get; set; }

		public string? SimulationUrl { get; set; }
	}
}
