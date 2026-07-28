# UI Architecture Notes

Frontend counterpart to `ARCHITECTURE.md` (which stays backend/.NET-only).
Living record of UI-specific patterns and decisions for `web/mesh-monitor`
— the mesh-wide monitoring dashboard — updated as new frontend patterns
get established, same spirit as `ARCHITECTURE.md`. `ARCHITECTURE.md` and
`WORKPLAN.md` are the source of truth for backend architecture and phase
status; this file is scoped entirely to the web frontend.

## Stack

- **Vue 3 + Vite + TypeScript**, Composition API throughout (no Options
  API, no `<script setup>` — see "Component file split" below for why).
- **Element Plus** for components/theming — chosen over PrimeVue
  specifically for MIT-license clarity throughout the stack (PrimeVue's
  core library is MIT, but its prebuilt application templates/blocks are
  commercial; Element Plus has no such split).
- **vis-network** (+ `vis-data`) for the topology graph, physics/auto-layout
  left enabled (its default) rather than fixed node positions — vis-network
  is dual-licensed Apache-2.0/MIT.
- **Pinia** for state management (the "ViewModel" layer — see MVVM below).
- **`@microsoft/signalr`** for the live push connection to the backend
  (`SyncMesh.MeshMonitor.Api`).
- **Vitest** + `@vue/test-utils` for unit tests, **Playwright** for a
  minimal automated UI smoke test — see Testing below.

No `vue-router`: the app is two views (`TopologyView`, `DataView`) switched
via an outer `el-tabs`, not route navigation — adding a router would be an
unused abstraction for this shape.

## Component file split: Template + Script + Types (no per-view style)

Every view is three physical files, wired by a thin `.vue`:

```
TopologyView.vue    <template src="./TopologyView.html"></template>
                     <script lang="ts" src="./TopologyView.ts"></script>
TopologyView.html   markup only
TopologyView.ts     component logic
TopologyView.types.ts   TS interfaces specific to this view (e.g. graph node/edge shapes, table row shapes)
```

Styling is deliberately **not** part of this split — one shared
`src/styles/global.css` plus the Element Plus theme cover the whole app.
No per-view `.css` file exists; this was an explicit choice (shared styles
over per-component styles) rather than an oversight.

**`App.vue` doesn't have a `.types.ts`** — it's the root shell, not a
"view" with its own data shapes, so an empty types file would be an
unrequested abstraction. The three-file rule applies to actual views
(`TopologyView`, `DataView`), not the app shell.

### Why plain `<script src>`, not `<script setup src>`

The natural choice for a split-file Composition-API component is
`<script setup lang="ts" src="./View.ts">`. It compiles and runs
correctly, **but** `vue-tsc`'s unused-locals analysis (`noUnusedLocals` /
`noUnusedParameters`, both on in `tsconfig.app.json`) does not correctly
associate template usage with bindings declared in a `src`-loaded
`<script setup>` file — every top-level `const` in the `.ts` file gets
flagged as "declared but never read," even though the template visibly
uses it. This reproduced consistently (confirmed via `npx vue-tsc -b`)
and is a real gap in how Volar/vue-tsc's virtual-file generation handles
`src` imports specifically, not a project misconfiguration — inline
`<script setup>` (no `src`) does not have this problem.

Fix: every view/the app shell uses plain `<script lang="ts" src="./View.ts">`
(no `setup` attribute) with `export default defineComponent({ setup() { ...; return {...} } })`
in the `.ts` file. Every binding used by the template is explicitly
listed in the `return` statement, in the *same file* as the template-
consuming component definition — so there's no cross-file inference for
`vue-tsc` to get wrong, and `noUnusedLocals` stays meaningfully enabled
project-wide. Template refs (`ref="container"`), computed properties, and
everything else work identically to `<script setup>`; only the "returned
object" boilerplate differs.

## MVVM: Pinia stores as ViewModels, `useCommand` as bound commands

- **Pinia setup stores** (`defineStore('name', () => {...})`, Composition-
  API style — consistent with the `defineComponent`/`setup()` pattern used
  everywhere else) are the "ViewModel" layer: reactive state plus derived
  data (`computed`) that views bind to. Views never call `services/*`
  directly — only through a store action.
- **`src/composables/useCommand.ts`** approximates WPF's bindable
  `ICommand` (`Execute`/`CanExecute`) for template binding:
  `:disabled="!cmd.canExecute" @click="cmd.execute"`. It wraps a store
  action with `isExecuting` (auto-disables itself while running — no
  re-entrant double-click execution) and an optional `canExecuteWhen`
  predicate.
  - **Gotcha, confirmed via a failing unit test**: `canExecute` is a Vue
    `computed()`, so it only re-evaluates when the `canExecuteWhen`
    predicate reads something Vue's reactivity system can track — a
    `ref`/`reactive`/store-computed read. A predicate closing over a plain
    mutable variable (`let allowed = false; () => allowed`) will never
    cause the computed to re-evaluate when that variable changes; this
    isn't a bug in `useCommand`, it's how Vue reactivity fundamentally
    works, but it's worth remembering when writing a `canExecuteWhen`
    predicate (always read reactive state, e.g. `() => store.isReady`).

## Auth: authStore holds the bearer token, mints tickets per connection

See `docs/adr/0009-ticket-based-signalr-auth.md` and its full sequence/
component diagrams in `docs/bdd/design/mesh-monitor-ticket-auth.md` for
the *why*; this section is the frontend-specific *how*.

- **`ConnectView`** is a real view (gets the same three-file split as
  `TopologyView`/`DataView`, minus a `.types.ts` — no view-specific data
  shapes beyond a plain string input, same reasoning as `App.vue`'s
  exception) — shown instead of the whole app shell whenever
  `authStore.isAuthenticated` is false (see `App.html`'s `v-if`). This
  project doesn't issue bearer tokens itself, so there's no login
  flow — an operator pastes one obtained from wherever it's actually
  issued.
- **`stores/authStore.ts`** is a second ViewModel alongside `meshStore`,
  kept separate on purpose — `meshStore` stays about topology data, auth
  concerns don't leak into it. Holds the bearer token in a plain `ref`,
  **never persisted to localStorage/sessionStorage** — deliberate: this
  dashboard has no TLS yet (`PRODUCTION-HARDENING.md`), and a page reload
  clearing the token isn't a real cost worth trading for the exposure of
  persisting it.
- **`services/auth.ts`** does the actual ticket exchange: generates a
  one-time secret (`crypto.getRandomValues`), calls `POST /auth/ticket`,
  then computes `HMAC-SHA256(secret, ticketId)` itself via
  `crypto.subtle` (uppercase hex — `Convert.ToHexString` on the .NET side
  is uppercase, and the ticket store's lookup is a plain string
  comparison, so casing must match exactly). `tests/unit/auth.spec.ts`
  cross-checks this against Node's own `crypto` module as an independent
  reference, not just against itself.
- **`signalrClient.ts`'s hub connection uses `accessTokenFactory`**,
  `@microsoft/signalr`'s built-in hook for "mint a fresh credential
  before every (re)connection attempt" — the whole reason to reach for it
  here: a ticket is single-use, so `withAutomaticReconnect()`'s retries
  need a *new* ticket each time, not the URL string captured once at
  `.withUrl()` build time. This is also why the backend accepts a query
  parameter named `access_token` (SignalR sends it under that name,
  non-configurable client-side) in addition to `ticket`.
- **`GET /api/topology` uses the bearer token directly** (an
  `Authorization` header via `authStore.authorizationHeader()`), not a
  ticket — unlike the SignalR hub, a plain `fetch()` has no URL
  constraint, so there's no reason to mint a ticket for it.

## Data loading: REST snapshot + SignalR push, topology derived client-side

- `meshStore.loadSnapshot()` calls `GET /api/topology` once `ConnectView`'s
  Connect command succeeds (so a freshly-opened tab renders immediately
  once authenticated, not just on the next 5s tick).
- `meshStore.connectLive()` opens a SignalR connection (via
  `authStore.getSignalRAccessToken`) and applies each `NodeUpdated` push
  into the same reactive node map.
- **The topology (which node connects to which) is derived entirely from
  what each node self-reports about itself** — a daemon's
  `nearestServerUrl` matched against a server's own `url`; a server's
  `configuredPeers` matched by `peerSiteId` — never from a separately
  maintained topology config file. This mirrors the backend convention
  established in `ARCHITECTURE.md` (server-mesh replication) and
  `docs/adr/0002-nats-leaf-nodes-for-transport.md`'s 2026-07-23 (Phase 3)
  Amendment: config lives on each node, nothing central can drift out of
  sync with it. See `src/stores/meshStore.ts`'s `edges` computed.
- **Staleness is computed client-side**, not pushed by the server: a node
  is "stale" once `Date.now() - lastSeenUtc` exceeds ~3× the default
  publish interval (15s). A killed daemon/server process visibly grays out
  in both the topology graph and the data grids instead of freezing
  forever with old numbers.

## Dev workflow: Vite proxy, no CORS

`vite.config.ts` proxies `/api` and `/hubs` (including WebSocket upgrade)
to `SyncMesh.MeshMonitor.Api`'s default `http` launch profile port
(`5129`) by default, or to `VITE_MESHMONITOR_API_URL` when that
environment variable is set — which `SyncMesh.AppHost` does automatically
when it starts this dev server as its own `mesh-monitor-web` Aspire
resource (`AddViteApp`, see `ARCHITECTURE.md` → "AppHost: `web/mesh-monitor`
runs as its own live dev-server resource"), so the proxy target tracks
the API's real dynamically-assigned port instead of the hardcoded
default. Every browser-side request stays same-origin (Vite's own dev
port), so the backend needs **no CORS configuration at all** — this was a
deliberate simplification over the CORS-policy approach originally
sketched, once the proxy made it unnecessary. In production, the built
app's static output is served directly from the API's own `wwwroot`
(same-origin for a different reason — one deployable unit), so this is
the only place a base URL ever needs to be configured, and it's
configured once, in `vite.config.ts`, not scattered through service code
(`services/api.ts` and `services/signalrClient.ts` both just use relative
paths).

`SyncMesh.MeshMonitor.Api.csproj` has a `BuildFrontend` MSBuild target
(`BeforeTargets="Build"`) that runs `npm run build` and copies
`web/mesh-monitor/dist/**` into `wwwroot`, so **every** `dotnet build`/
`dotnet run` — including via `SyncMesh.AppHost`, so the Aspire dashboard's
URL for this resource actually opens a working dashboard — serves the
real UI, not just `dotnet publish`. It's declared with MSBuild
`Inputs`/`Outputs` (inputs: everything under `web/mesh-monitor/src` plus
its config files; output: `wwwroot/index.html`) so a build with no
frontend changes since the last one is a fast up-to-date check, not a
full `vue-tsc` + `vite build` every time — confirmed by testing (first
build ~15s with the frontend rebuild, second unchanged build ~2.5s,
skipping the rebuild entirely).

This is a **reversal** of an earlier version of this target, which was
deliberately `Publish`-only to avoid slowing down `dotnet build`/`dotnet
run` iteration — before the Aspire-dashboard-URL requirement made
"`dotnet run` alone doesn't serve anything" a real problem, not just a
local-dev inconvenience solved by the Vite dev server. The incremental
`Inputs`/`Outputs` check is what makes running it on every `Build` cheap
enough to be worth that trade-off.

Publish needs no separate copy step: `dotnet publish` invokes `Build`
first, so `BuildFrontend` has already populated `wwwroot` by the time
Publish's own Static Web Assets discovery runs — confirmed by testing
(`dotnet publish` now produces `wwwroot/*.br`/`*.gz` compressed variants
too, proof the standard SWA pipeline picked the content up correctly).
An earlier attempt hooked `AfterTargets="Publish"` instead, back when the
frontend was only built at Publish time — SWA's manifest computation
runs too early in that ordering to pick up wwwroot content added that
late, which is why that hook existed and why a plain `BeforeTargets=
"Publish"` copy silently produced an empty `wwwroot` in the actual
publish output (confirmed by testing: files landed in the source
project's `wwwroot/` but not in `$(PublishDir)`, a 404 the first time
this was tried). Once the frontend build moved to `Build` time, the
separate `Publish`-time step stopped being necessary and was removed.

**During local frontend iteration**, still use `npm run dev` in
`web/mesh-monitor` (`http://localhost:5173`, proxying to the API) rather
than rebuilding via `dotnet build` on every change — or let
`SyncMesh.AppHost` start it for you as the `mesh-monitor-web` resource
(no separate terminal, and the proxy target tracks the API's real port
automatically). Either way the MSBuild target
is what makes `dotnet run`/AppHost serve *something* correct without
extra steps, not a replacement for Vite's hot-reloading dev loop.

## Testing

- **Unit tests** (`tests/unit/*.spec.ts`, run via `npm run test:unit` /
  `vitest run`): the store's derived data (`edges`, `isStale`) and
  `useCommand`'s execute/canExecute/isExecuting state machine. These cover
  the actual logic; they don't touch the DOM.
  - **`auth.spec.ts`**: `computeHashedTicket` is cross-checked against an
    HMAC-SHA256 computed via Node's own `crypto` module (`createHmac`) —
    an independent reference, not just asserting the function agrees with
    itself — plus `mintTicket`'s POST body/header shape, via a stubbed
    `fetch` (`vi.stubGlobal`).
  - **`authStore.spec.ts`**: token lifecycle (`setToken`/`clearToken`/
    `isAuthenticated`), and `getSignalRAccessToken` delegating to
    `services/auth.ts` (mocked via `vi.mock`).
- **One Playwright smoke test** (`tests/e2e/smoke.spec.ts`, run via
  `npm run test:e2e`): mocks `GET /api/topology` **and** `POST
  /auth/ticket` (via `page.route`), drives `ConnectView`'s token input and
  Connect button, then confirms the dashboard renders real data end to
  end — the one thing only a browser can prove. The ticket exchange
  itself is unit-tested directly (`auth.spec.ts`/`authStore.spec.ts`);
  this test mocks it the same way it mocks the topology snapshot, rather
  than re-proving it. SignalR's negotiate request is left to 404
  harmlessly (mocked to return 404) since no backend is running for this
  test; that's fine, live push isn't what this test is for.
  - **Gotcha, confirmed by a failing assertion**: Element Plus's
    `el-tabs` keeps *inactive* `el-tab-pane` content in the DOM (hidden via
    CSS, not unmounted), and Playwright's `getByText` matches substrings
    by default. An unqualified `getByText('daemon-1')` also matched the
    Connections tab's node-key cell (`"daemon:site-a:daemon-1"`) even
    while that tab wasn't the active one — resolved with `{ exact: true }`.
    Worth remembering for any future assertion that names something short
    enough to be a substring of something else on the page.

## Known follow-ups (not blocking, not attempted this pass)

- Element Plus is imported wholesale (`app.use(ElementPlus)`); the
  production bundle warns about chunk size (~1.5MB). On-demand component
  auto-import (`unplugin-vue-components` + `unplugin-auto-import`) would
  shrink this meaningfully if bundle size becomes a real concern — not
  done here since it's an optimization, not a correctness issue.
- ~~`web/mesh-monitor`'s build output isn't yet wired into
  `SyncMesh.MeshMonitor.Api`'s `wwwroot`~~ — resolved: see the
  `BuildFrontend` MSBuild target described in "Dev workflow" above.
