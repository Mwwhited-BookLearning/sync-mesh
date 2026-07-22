using System.IO.Pipes;
using System.Text.Json;

namespace SyncMesh.Daemon.Ipc;

// Reference client for the Tier 0 IPC listener — stands in for "the local
// app" until a real one exists (see docs/05-implementation-guide.md Phase 1
// scope: "accepting events from a stub local-app client"). Also used
// directly by tests.
public sealed class LocalIpcClient(string pipeName, string serverName = ".")
{
    public async Task<AppendEventResponse> AppendEventAsync(AppendEventRequest request, CancellationToken ct = default)
    {
        var response = await SendAsync("Append", request, ct);
        return Deserialize<AppendEventResponse>(response);
    }

    public async Task<ReadEventsResponse> ReadEventsAsync(ReadEventsRequest request, CancellationToken ct = default)
    {
        var response = await SendAsync("Read", request, ct);
        return Deserialize<ReadEventsResponse>(response);
    }

    private async Task<IpcResponseEnvelope> SendAsync<TRequest>(string operation, TRequest request, CancellationToken ct)
    {
        await using var pipe = new NamedPipeClientStream(serverName, pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        await pipe.ConnectAsync(ct);

        var envelope = new IpcRequestEnvelope { Operation = operation, PayloadJson = JsonSerializer.Serialize(request) };
        await IpcFraming.WriteMessageAsync(pipe, JsonSerializer.Serialize(envelope), ct);

        var responseJson = await IpcFraming.ReadMessageAsync(pipe, ct);
        var response = JsonSerializer.Deserialize<IpcResponseEnvelope>(responseJson)
            ?? throw new InvalidOperationException("Empty IPC response envelope.");

        if (!response.Success)
        {
            throw new InvalidOperationException($"IPC {operation} failed: {response.ErrorMessage}");
        }

        return response;
    }

    private static T Deserialize<T>(IpcResponseEnvelope response) =>
        JsonSerializer.Deserialize<T>(response.PayloadJson!)
        ?? throw new InvalidOperationException("Empty IPC response payload.");
}
