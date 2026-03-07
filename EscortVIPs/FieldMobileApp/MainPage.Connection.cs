using System.Net.WebSockets;
using FieldMobileApp.Location;

namespace FieldMobileApp;

public partial class MainPage
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(1.5);
    private ClientWebSocket? socket;
    private CancellationTokenSource? receiveCts;
    private Task? receiveTask;
    private Task? simulationConnectTask;
    private Task? sendTask;
    private CancellationTokenSource? watchdogCts;
    private bool reconnectLoopActive;
    private bool handlingConnectionDrop;
    private string? connectedDeviceId;
    private string? connectedSessionId;
    private DateTimeOffset lastOutboundUtc;
    private bool hasSentOutbound;

    private SimulationLocationDataSource? ActiveSimulationLocationDataSource => locationDataSource as SimulationLocationDataSource;

    private async void OnPageLoaded(object? sender, EventArgs e)
    {
        Loaded -= OnPageLoaded;
        await StartLocationDataSourceAsync();
        await EnsurePerimeterMonitorsAsync(configuredRole);
        await EnsureEscortVipDynamicEntityDataSourceAsync(configuredRole);
        await ConnectAsync();
    }

    private async Task ConnectAsync()
    {
        connectedDeviceId = configuredDeviceId;
        connectedSessionId = configuredSessionId;

        await DisconnectAsync();

        receiveCts = new CancellationTokenSource();
        watchdogCts = new CancellationTokenSource();

        socket = new ClientWebSocket();
        var hostUri = new Uri(configuredHostUrl);
        LogDiagnostic($"Connecting dashboard socket to {hostUri}");
        await socket.ConnectAsync(hostUri, receiveCts.Token);
        LogDiagnostic("Dashboard socket connected.");

        ApplyStatusVisual("Connected", null);

        receiveTask = Task.Run(() => ReceiveLoopAsync(configuredRole, configuredDeviceId, receiveCts.Token), receiveCts.Token);
        sendTask = Task.Run(() => SendLoopAsync(configuredRole, configuredDeviceId, configuredSessionId, receiveCts.Token), receiveCts.Token);

        if (simulatedMode)
        {
            try
            {
                await ConnectSimulationSocketAsync(receiveCts.Token);
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Initial simulation connect failed: {ex.Message}");
            }

            simulationConnectTask ??= Task.Run(() => EnsureSimulationConnectedLoopAsync(receiveCts.Token), receiveCts.Token);
        }

        _ = Task.Run(() => WatchdogLoopAsync(watchdogCts.Token), watchdogCts.Token);
        _ = Task.Run(() => EnsureConnectedLoopAsync(), CancellationToken.None);
    }

    private async Task DisconnectAsync()
    {
        receiveCts?.Cancel();
        watchdogCts?.Cancel();

        await DisconnectSimulationSocketAsync();

        if (sendTask is not null)
        {
            try
            {
                await sendTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Send task end error: {ex.Message}");
            }
        }

        if (receiveTask is not null)
        {
            try
            {
                await receiveTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Receive task end error: {ex.Message}");
            }
        }

        if (simulationConnectTask is not null)
        {
            try
            {
                await simulationConnectTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Simulation reconnect task end error: {ex.Message}");
            }
        }

        sendTask = null;
        receiveTask = null;
        simulationConnectTask = null;

        receiveCts?.Dispose();
        receiveCts = null;

        watchdogCts?.Dispose();
        watchdogCts = null;

        var existing = socket;
        socket = null;
        if (existing is not null)
        {
            try
            {
                if (existing.State == WebSocketState.Open || existing.State == WebSocketState.CloseReceived)
                    await existing.CloseAsync(WebSocketCloseStatus.NormalClosure, "Disconnect", CancellationToken.None);
            }
            catch
            {
            }
            finally
            {
                existing.Dispose();
            }
        }

        hasSentOutbound = false;
        lastOutboundUtc = default;
        ApplyStatusVisual("Disconnected", null);
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
            LogDiagnostic($"Connection drop detected by {source}. Restarting connection.");
            await DisconnectAsync();
        }
        finally
        {
            handlingConnectionDrop = false;
        }
    }

    private async Task EnsureConnectedLoopAsync()
    {
        if (reconnectLoopActive)
            return;

        reconnectLoopActive = true;
        try
        {
            while (!isShuttingDown)
            {
                if (socket is { State: WebSocketState.Open })
                {
                    await Task.Delay(TimeSpan.FromSeconds(1));
                    continue;
                }

                try
                {
                    await ConnectAsync();
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Reconnect attempt failed: {ex.Message}");
                    ApplyStatusVisual("Reconnecting...", null);
                }

                await Task.Delay(ReconnectDelay);
            }
        }
        finally
        {
            reconnectLoopActive = false;
        }
    }

    private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
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

            if (!hasSentOutbound)
                continue;

            if (DateTimeOffset.UtcNow - lastOutboundUtc <= TimeSpan.FromSeconds(20))
                continue;

            LogDiagnostic("Watchdog detected stalled outbound activity. Triggering reconnect.");
            await HandleConnectionDropAsync("Watchdog");
        }
    }
}
