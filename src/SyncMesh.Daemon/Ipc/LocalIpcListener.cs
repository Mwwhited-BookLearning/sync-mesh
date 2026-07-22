using System.IO.Pipes;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace SyncMesh.Daemon.Ipc;

// Tier 0 IPC listener: named pipe (works cross-platform via
// System.IO.Pipes — no network sockets for same-machine IPC, per
// docs/00-design-document.md §4.1). One request/response per connection;
// each request is handled in its own DI scope.
public sealed class LocalIpcListener(
    IOptions<DaemonOptions> daemonOptions,
    IServiceScopeFactory scopeFactory,
    ILogger<LocalIpcListener> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var pipeName = daemonOptions.Value.IpcPipeName;

        while (!stoppingToken.IsCancellationRequested)
        {
            var pipe = new NamedPipeServerStream(
                pipeName,
                PipeDirection.InOut,
                NamedPipeServerStream.MaxAllowedServerInstances,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);

            try
            {
                await pipe.WaitForConnectionAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                pipe.Dispose();
                break;
            }

            _ = HandleConnectionAsync(pipe, stoppingToken);
        }
    }

    private async Task HandleConnectionAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        await using (pipe)
        {
            IpcResponseEnvelope response;
            try
            {
                var requestJson = await IpcFraming.ReadMessageAsync(pipe, ct);
                var request = JsonSerializer.Deserialize<IpcRequestEnvelope>(requestJson)
                    ?? throw new InvalidOperationException("Empty IPC request envelope.");

                using var scope = scopeFactory.CreateScope();
                response = request.Operation switch
                {
                    "Append" => await HandleAppendAsync(scope.ServiceProvider, request.PayloadJson, ct),
                    "Read" => await HandleReadAsync(scope.ServiceProvider, request.PayloadJson, ct),
                    _ => new IpcResponseEnvelope { Success = false, ErrorMessage = $"Unknown operation '{request.Operation}'." },
                };
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error handling local IPC connection.");
                response = new IpcResponseEnvelope { Success = false, ErrorMessage = ex.Message };
            }

            try
            {
                await IpcFraming.WriteMessageAsync(pipe, JsonSerializer.Serialize(response), ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Error writing local IPC response.");
            }
        }
    }

    private static async Task<IpcResponseEnvelope> HandleAppendAsync(IServiceProvider services, string payloadJson, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<AppendEventRequest>(payloadJson)
            ?? throw new InvalidOperationException("Empty AppendEventRequest payload.");
        var writer = services.GetRequiredService<LocalEventWriter>();
        var result = await writer.AppendAsync(request, ct);
        return new IpcResponseEnvelope { Success = true, PayloadJson = JsonSerializer.Serialize(result) };
    }

    private static async Task<IpcResponseEnvelope> HandleReadAsync(IServiceProvider services, string payloadJson, CancellationToken ct)
    {
        var request = JsonSerializer.Deserialize<ReadEventsRequest>(payloadJson)
            ?? throw new InvalidOperationException("Empty ReadEventsRequest payload.");
        var reader = services.GetRequiredService<LocalEventReader>();
        var result = await reader.ReadAsync(request, ct);
        return new IpcResponseEnvelope { Success = true, PayloadJson = JsonSerializer.Serialize(result) };
    }
}
