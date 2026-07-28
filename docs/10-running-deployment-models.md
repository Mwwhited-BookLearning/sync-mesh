# Running the Deployment Models Locally

Companion to `docs/08-deployment-models.md` (diagrams) and the automated
proof in `tests/SyncMesh.Bdd.Tests`/`tests/SyncMesh.Sync.Tests`
(`ServerMeshReconciliationTests`, `NearestServerSteps`, etc.). This doc
covers two ways to stand one of the six documented shapes up **by hand**,
locally, to click around in — not for CI or automated verification, which
the test suites already own.

## Option A: `SyncMesh.AppHost` (recommended — one command, no manual wiring)

`SyncMesh.AppHost` now supports all six models directly — no separate
Compose file, no manual `dotnet run` per process, no per-project
environment variables to work out by hand. Select a model by setting
**one** environment variable:

```bash
DeploymentModel=client-isolated dotnet run --project src/SyncMesh.AppHost
```

| Model | `DeploymentModel` value |
|---|---|
| Order Book demo (the original fixed two-site topology — default, unaffected by any of this) | *(leave unset)* |
| 1. Client isolated (no nearest server) | `client-isolated` |
| 2. Client → on-prem server | `client-onprem` |
| 3. Client → cloud server (no on-prem tier) | `client-cloud` |
| 4. Standalone server (zero peers) | `standalone-server` |
| 5. Intra-site mesh + limited gateway (A–B–C, B is the gateway) | `intra-site-mesh` |
| 6. Full mesh everywhere (A–B–C, every node peers every other) | `full-mesh` |

Each value builds exactly that model's resource graph in the Aspire
dashboard (NATS containers + the right number of `ServerHost`/`Daemon`
instances, correctly peered) and tears it all down cleanly on Ctrl+C —
see `ARCHITECTURE.md` → "AppHost: selectable deployment-model topologies"
for the implementation. Server tier is SQLite here (ADR-0001's
Amendment), not Postgres, consistent with the rest of `SyncMesh.AppHost`.

**`src/SyncMesh.AppHost/Properties/launchSettings.json`** has one profile
per model too (`dotnet run --project src/SyncMesh.AppHost --launch-profile
client-isolated`, or pick it from Visual Studio/VS Code's run dropdown) —
purely a local convenience on top of the same env var, **not** something
a fresh clone will have: every `launchSettings.json` in this repo is
gitignored (`**/Properties/launchSettings.json` — a pre-existing,
repo-wide convention, not specific to this file), so this file exists
only on whichever machine created it. The `DeploymentModel` environment
variable above is the one thing guaranteed to work anywhere; recreate
the launch-profile file locally (one profile per row above, each just
setting `DeploymentModel`) if you want the dropdown convenience.

## Option B: standalone Compose + manual `dotnet run`

The fully manual path — real Postgres per model, full control over every
process, useful outside Aspire entirely (e.g. testing from a different
machine, or without Docker Desktop's Aspire integration).

**Note**: earlier versions of this doc referenced named `SyncMesh.Daemon`/
`SyncMesh.ServerHost` launch profiles (`OnPrem`, `ClientToOnPrem`,
`MeshNodeA`, etc.) for this path — those were aspirational and never
actually built (neither project has a `launchSettings.json`). Use the
explicit environment variables below instead; Option A is the easier path
for most purposes now.

Each of the six models has its own standalone Compose file under
`deploy/compose/` — no `--profile` flag needed, just point `-f` at the
one you want. The repo-root `docker-compose.yml` is a composite that
`include:`s all six, so a plain `docker compose up -d` there starts every
model's infrastructure at once (their ports are pre-allocated distinctly,
so nothing conflicts) if you want to compare shapes side by side.

1. Start the NATS (and, where needed, Postgres) infrastructure for one
   model: `docker compose -f deploy/compose/<model>.yml up -d`.
2. Run `SyncMesh.Daemon`/`SyncMesh.ServerHost` directly, setting the
   env vars each model needs (see `deploy/compose/<model>.yml` for the
   exact host ports it publishes) — for example, client-onprem:
   ```bash
   ServerHost__Nats__Url=nats://localhost:24223 \
   ConnectionStrings__EventStore="Host=localhost;Port=25432;Username=postgres;Password=postgres;Database=EventStore" \
   EventStore__Provider=Postgres \
   dotnet run --project src/SyncMesh.ServerHost

   Daemon__Nats__Url=nats://localhost:24224 \
   dotnet run --project src/SyncMesh.Daemon
   ```
3. Optionally point the mesh monitor at it:
   `MeshMonitor__NatsUrls__0=nats://localhost:<hub-port> dotnet run --project src/SyncMesh.MeshMonitor.Api`,
   then open the dashboard (`npm run dev` in `web/mesh-monitor`, or the
   API's own served build).
4. Tear down: `docker compose -f deploy/compose/<model>.yml down`.

Models 5 and 6 need all three `ServerHost` instances running
simultaneously (three separate terminals) — that's the whole point of the
shape. Each gets its own Postgres database (each model's own Compose file
provisions one per node) so convergence is genuinely proven across
independently-stored history, not a shared table.

Write an event through a daemon's IPC pipe (any client using
`SyncMesh.Daemon.Ipc.LocalIpcClient`, pipe name `syncmesh-daemon` by
default) and confirm it lands in that model's Postgres database's
`Events` table within a second or two.

## Cleanup

`docker compose -f deploy/compose/<model>.yml down` removes that model's
containers (or `docker compose down` at the repo root if you started
everything via the composite file). Postgres data isn't persisted to a
named volume in this sandbox (by design — it's meant to be thrown away
between runs), so there's nothing else to clean up beyond the SQLite file
each manually-run `SyncMesh.Daemon` creates next to its working directory
(`daemon-events.db`) — `SyncMesh.AppHost` (Option A) cleans up its own
containers and `.data/` files are gitignored/regenerated per run.
