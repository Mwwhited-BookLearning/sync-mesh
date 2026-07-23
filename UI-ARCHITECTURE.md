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

## Data loading: REST snapshot + SignalR push, topology derived client-side

- `meshStore.loadSnapshot()` calls `GET /api/topology` once on mount (so a
  freshly-opened tab renders immediately, not just on the next 5s tick).
- `meshStore.connectLive()` opens a SignalR connection and applies each
  `NodeUpdated` push into the same reactive node map.
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
(`5129`). Every browser-side request stays same-origin (Vite's own dev
port), so the backend needs **no CORS configuration at all** — this was a
deliberate simplification over the CORS-policy approach originally
sketched, once the proxy made it unnecessary. In production, the built
app's static output is served directly from the API's own `wwwroot`
(same-origin for a different reason — one deployable unit), so this is
the only place a base URL ever needs to be configured, and it's
configured once, in `vite.config.ts`, not scattered through service code
(`services/api.ts` and `services/signalrClient.ts` both just use relative
paths).

## Testing

- **Unit tests** (`tests/unit/*.spec.ts`, run via `npm run test:unit` /
  `vitest run`): the store's derived data (`edges`, `isStale`) and
  `useCommand`'s execute/canExecute/isExecuting state machine. These cover
  the actual logic; they don't touch the DOM.
- **One Playwright smoke test** (`tests/e2e/smoke.spec.ts`, run via
  `npm run test:e2e`): mocks `GET /api/topology` (via `page.route`) and
  loads the real app in a real browser, confirming the dashboard renders
  real data end to end — the one thing only a browser can prove. SignalR's
  negotiate request is left to 404 harmlessly (mocked to return 404) since
  no backend is running for this test; that's fine, live push isn't what
  this test is for.
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
- `web/mesh-monitor`'s build output isn't yet wired into `SyncMesh.MeshMonitor.Api`'s
  `wwwroot` by any build step (`dotnet build`/CI) — today that's a manual
  `npm run build` + copy. Automating it (an MSBuild target, or a CI step)
  is a reasonable follow-up once this dashboard is used beyond local dev.
