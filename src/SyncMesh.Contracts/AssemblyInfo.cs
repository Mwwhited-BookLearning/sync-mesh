using System.Runtime.CompilerServices;

// LocalIpcListener (SyncMesh.Daemon.Ipc) needs the internal IPC envelope
// types and framing in SyncMesh.Contracts.Ipc — see LocalIpcClient's doc
// comment for why the client-side IPC code lives here instead of in
// SyncMesh.Daemon.
[assembly: InternalsVisibleTo("SyncMesh.Daemon")]
