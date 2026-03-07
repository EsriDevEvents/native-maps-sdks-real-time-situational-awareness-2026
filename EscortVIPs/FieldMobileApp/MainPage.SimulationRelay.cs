using System.Net.WebSockets;
using System.Text;
using CommandMessaging;

namespace FieldMobileApp;

public partial class MainPage
{
    private readonly SemaphoreSlim simulationConnectGate = new(1, 1);
    private readonly SemaphoreSlim vipControlRelayGate = new(1, 1);
    private ClientWebSocket? simulationSocket;
    private CancellationTokenSource? simulationReceiveCts;
    private Task? simulationReceiveTask;
    private bool simulationReconnectLoopActive;

    private async Task ConnectSimulationSocketAsync(CancellationToken cancellationToken)
    {
        if (!simulatedMode)
            return;

        if (simulationSocket is { State: WebSocketState.Open })
            return;

        await simulationConnectGate.WaitAsync(cancellationToken);
        try
        {
            if (!simulatedMode)
                return;

            if (simulationSocket is { State: WebSocketState.Open })
                return;

            if (simulationSocket is not null)
            {
                try
                {
                    simulationSocket.Abort();
                }
                catch
                {
                }

                simulationSocket.Dispose();
                simulationSocket = null;
            }

            simulationSocket = new ClientWebSocket();
            var uri = new Uri(configuredSimulationUrl);
            LogDiagnostic($"Connecting simulation socket to {uri}");
            await simulationSocket.ConnectAsync(uri, cancellationToken);
            LogDiagnostic("Simulation socket connected.");

            await SendSimulationRegisterAsync(cancellationToken);

            simulationReceiveCts?.Cancel();
            simulationReceiveCts?.Dispose();
            simulationReceiveCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            simulationReceiveTask = Task.Run(
                () => SimulationReceiveLoopAsync(
                    configuredRole,
                    configuredDeviceId,
                    simulationReceiveCts.Token),
                simulationReceiveCts.Token);
        }
        finally
        {
            simulationConnectGate.Release();
        }
    }

    private async Task SendSimulationRegisterAsync(CancellationToken cancellationToken)
    {
        var registerEnvelope = MessageSerializer.CreateEnvelope(
            MessageTypes.Register,
            connectedSessionId ?? configuredSessionId,
            connectedDeviceId ?? configuredDeviceId,
            new RegisterPayload(configuredDeviceId, configuredRole, "field-app"));

        var sent = await SendSimulationAsync(registerEnvelope, cancellationToken);
        if (!sent)
        {
            LogDiagnostic("Simulation register send skipped because simulation websocket is not open.");
        }
    }

    private async Task DisconnectSimulationSocketAsync()
    {
        simulationReceiveCts?.Cancel();

        if (simulationReceiveTask is not null)
        {
            try
            {
                await simulationReceiveTask;
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                LogDiagnostic($"Simulation receive task end error: {ex.Message}");
            }
        }

        simulationReceiveTask = null;
        simulationReceiveCts?.Dispose();
        simulationReceiveCts = null;

        var existing = simulationSocket;
        simulationSocket = null;
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
    }

    private async Task EnsureSimulationConnectedLoopAsync(CancellationToken cancellationToken)
    {
        if (simulationReconnectLoopActive)
            return;

        simulationReconnectLoopActive = true;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!simulatedMode)
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                if (simulationSocket is { State: WebSocketState.Open })
                {
                    await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
                    continue;
                }

                try
                {
                    await ConnectSimulationSocketAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Simulation reconnect failed: {ex.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            }
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
                string? message;
                try
                {
                    message = await ReadTextMessageAsync(simulationSocket, cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    LogDiagnostic($"Simulation receive read error: {ex.Message}");
                    if (simulationSocket is not { State: WebSocketState.Open })
                        break;

                    await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
                    continue;
                }

                if (message is null)
                {
                    LogDiagnostic("Simulation receive close frame received.");
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
                    LogDiagnostic($"Simulation receive deserialize error: {ex.Message}");
                    continue;
                }

                if (envelope is null)
                    continue;

                if (envelope.Type == MessageTypes.AssignedLocation)
                {
                    var payload = MessageSerializer.DeserializePayload<AssignedLocationPayload>(envelope);
                    if (payload is null)
                        continue;

                    await MainThread.InvokeOnMainThreadAsync(() =>
                    {
                        ApplyAssignedLocationPayload(payload, role, deviceId);
                    });

                    await RelayAssignedLocationToDashboardAsync(payload, role, deviceId, cancellationToken);
                    continue;
                }

                if (envelope.Type == MessageTypes.RouteSnapshot)
                {
                    await RelayRouteSnapshotToDashboardAsync(envelope);
                    continue;
                }

                if (envelope.Type == MessageTypes.VipControl)
                {
                    await RelayVipControlToDashboardAsync(envelope);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            LogDiagnostic($"Simulation receive loop fatal error: {ex}");
        }
        finally
        {
            var existing = simulationSocket;
            simulationSocket = null;
            if (existing is not null)
            {
                try
                {
                    existing.Abort();
                }
                catch
                {
                }

                existing.Dispose();
            }
        }
    }

    private async Task RelayVipControlToSimulationEngineAsync(VipControlPayload payload)
    {
        if (!simulatedMode)
            return;

        var envelope = MessageSerializer.CreateEnvelope(
            MessageTypes.VipControl,
            connectedSessionId ?? configuredSessionId,
            connectedDeviceId ?? configuredDeviceId,
            payload);

        var sent = await SendSimulationAsync(envelope, simulationReceiveCts?.Token ?? CancellationToken.None);
        if (!sent)
        {
            LogDiagnostic("VIP control relay skipped because simulation websocket is not open.");
        }
    }

    private async Task<bool> SendSimulationAsync(FieldMessageEnvelope envelope, CancellationToken cancellationToken)
    {
        if (simulationSocket is null || simulationSocket.State != WebSocketState.Open)
            return false;

        await vipControlRelayGate.WaitAsync(cancellationToken);
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
            vipControlRelayGate.Release();
        }
    }
}
