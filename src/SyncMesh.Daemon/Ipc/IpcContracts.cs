namespace SyncMesh.Daemon.Ipc;

// Tier 0 (Local App <-> Local Daemon) wire contracts. Deliberately separate
// from SyncMesh.Contracts.EventEnvelope, which is the cross-tier envelope
// used from the write path onward — this is just the local IPC shape. See
// docs/00-design-document.md §4.1.

public sealed class AppendEventRequest
{
    public required Guid StreamId { get; init; }
    public required string EventType { get; init; }
    public required string PayloadJson { get; init; }
    public int PayloadSchemaVersion { get; init; } = 1;
}

public sealed class AppendEventResponse
{
    public required Guid GlobalEventId { get; init; }
    public required long StreamVersion { get; init; }
    public required long HlcPhysicalTicks { get; init; }
    public required int HlcLogicalCounter { get; init; }
    public required DateTimeOffset RecordedAtUtc { get; init; }
}

public sealed class ReadEventsRequest
{
    public required Guid StreamId { get; init; }
}

// A buffered-read projection of a locally stored event — served entirely
// from the daemon's own store, never proxied to/from the server.
public sealed class RecordedEvent
{
    public required Guid GlobalEventId { get; init; }
    public required Guid StreamId { get; init; }
    public required long StreamVersion { get; init; }
    public required string EventType { get; init; }
    public required string PayloadJson { get; init; }
    public required int PayloadSchemaVersion { get; init; }
    public required DateTimeOffset RecordedAtUtc { get; init; }
}

public sealed class ReadEventsResponse
{
    public required IReadOnlyList<RecordedEvent> Events { get; init; }
}

internal sealed class IpcRequestEnvelope
{
    public required string Operation { get; init; }
    public required string PayloadJson { get; init; }
}

internal sealed class IpcResponseEnvelope
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public string? PayloadJson { get; init; }
}
