# Running the Deployment Models Locally

Companion to `docs/08-deployment-models.md` (diagrams) and the automated
proof in `tests/SyncMesh.Bdd.Tests`/`tests/SyncMesh.Sync.Tests`
(`ServerMeshReconciliationTests`, `NearestServerSteps`, etc.). This doc is
for standing one of the six documented shapes up **by hand**, locally, to
click around in — not for CI or automated verification, which the test
suites already own.

Not the same thing as `SyncMesh.AppHost` — AppHost is the single, fixed
local-dev topology used throughout Phases 1-4 (one daemon, one on-prem
server). This is a sandbox for the other five documented shapes, plus that
same on-prem shape stood up independently of AppHost.

## General pattern

1. Start the NATS (and, where needed, Postgres) infrastructure for one
   model: `docker compose --profile <model> up -d`.
2. Run the matching launch profile(s) for `SyncMesh.Daemon` and/or
   `SyncMesh.ServerHost` — from Visual Studio/VS Code's run/debug profile
   dropdown, or `dotnet run --project src/SyncMesh.Daemon --launch-profile <Name>`.
3. Optionally point the mesh monitor at it:
   `MeshMonitor__NatsUrl=nats://localhost:<hub-port> dotnet run --project src/SyncMesh.MeshMonitor.Api`,
   then open the dashboard (`npm run dev` in `web/mesh-monitor`, or the
   API's own served build).
4. Tear down: `docker compose --profile <model> down`.

Only one model's containers need to run at a time, but every model uses
distinct, non-overlapping host ports, so nothing stops you running two
side by side if you want to compare them directly.

## Model reference

| # | Model (docs/08-deployment-models.md) | Compose profile | Daemon launch profile | ServerHost launch profile(s) |
|---|---|---|---|---|
| 1 | Client isolated (no nearest server) | `client-isolated` | `ClientIsolated` | *(none — no server in this shape)* |
| 2 | Client → on-prem server | `client-onprem` | `ClientToOnPrem` | `OnPrem` |
| 3 | Client → cloud server (no on-prem tier) | `client-cloud` | `ClientToCloud` | `Cloud` |
| 4 | Standalone server (zero peers) | `standalone-server` | *(none — no daemon needed to demonstrate this)* | `Standalone` |
| 5 | Intra-site mesh + limited gateway (A–B–C, B is the gateway) | `intra-site-mesh` | *(none — server-mesh only)* | `MeshNodeA`, `MeshNodeB`, `MeshNodeC` (run all three) |
| 6 | Full mesh everywhere (A–B–C, every node peers every other) | `full-mesh` | *(none — server-mesh only)* | `FullMeshNodeA`, `FullMeshNodeB`, `FullMeshNodeC` (run all three) |

Models 5 and 6 need all three `ServerHost` instances running
simultaneously (three separate terminals/launch configurations) — that's
the whole point of the shape. Each gets its own Postgres database
(`docker-compose.yml` provisions one per node) so convergence is
genuinely proven across independently-stored history, not a shared table.

## Example: client → on-prem, end to end

```bash
docker compose --profile client-onprem up -d
# in one terminal
dotnet run --project src/SyncMesh.ServerHost --launch-profile OnPrem
# in another terminal
dotnet run --project src/SyncMesh.Daemon --launch-profile ClientToOnPrem
```

Write an event through the daemon's IPC pipe (any client using
`SyncMesh.Daemon.Ipc.LocalIpcClient`, pipe name `syncmesh-daemon` by
default) and confirm it lands in the `OnPrem` Postgres database's
`Events` table within a second or two. Tear down with
`docker compose --profile client-onprem down` when done.

## Cleanup

`docker compose --profile <model> down` removes that model's containers.
Postgres data isn't persisted to a named volume in this sandbox (by
design — it's meant to be thrown away between runs), so there's nothing
else to clean up beyond the SQLite files each `SyncMesh.Daemon` launch
profile creates next to its working directory
(`daemon-events-<model>.db`).
