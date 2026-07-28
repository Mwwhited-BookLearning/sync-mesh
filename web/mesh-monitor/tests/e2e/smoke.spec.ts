import { test, expect } from '@playwright/test'

// Minimal automated UI smoke test — per UI-ARCHITECTURE.md, most behavior
// (edge derivation, staleness, command execution) is unit-tested against
// the store/composables directly; this is the one thing only a real
// browser can prove: the app actually renders real data end to end. The
// backend isn't running here — only the REST snapshot is mocked, since
// that alone is enough to prove rendering; SignalR's negotiate request is
// left to fail harmlessly (no live push in this test, which is fine —
// SignalR itself isn't this project's code to re-test).
const topologySnapshot = [
  {
    nodeKind: 'server',
    siteId: 'site-a',
    instanceId: 'server-1',
    lastSeenUtc: new Date().toISOString(),
    status: {
      nodeKind: 'server',
      siteId: 'site-a',
      instanceId: 'server-1',
      timestampUtc: new Date().toISOString(),
      url: 'nats://server-1:4222',
      eventsAppliedCount: 12,
      configuredPeers: [],
    },
  },
  {
    nodeKind: 'daemon',
    siteId: 'site-a',
    instanceId: 'daemon-1',
    lastSeenUtc: new Date().toISOString(),
    status: {
      nodeKind: 'daemon',
      siteId: 'site-a',
      instanceId: 'daemon-1',
      timestampUtc: new Date().toISOString(),
      bufferedEventCount: 0,
      connectedToNearestServer: true,
      nearestServerUrl: 'nats://server-1:4222',
      eventsForwardedCount: 7,
    },
  },
]

test.beforeEach(async ({ page }) => {
  await page.route('**/api/topology', async (route) => {
    await route.fulfill({ json: topologySnapshot })
  })
  await page.route('**/hubs/mesh-monitor/**', async (route) => {
    await route.fulfill({ status: 404, body: '' })
  })
  // The dashboard is now gated behind the ticket exchange (see
  // docs/adr/0009-ticket-based-signalr-auth.md) — mocked here the same
  // way the topology snapshot is, since this test is about rendering,
  // not re-proving the auth flow itself (that's unit-tested directly:
  // tests/unit/auth.spec.ts, tests/unit/authStore.spec.ts).
  await page.route('**/auth/ticket', async (route) => {
    await route.fulfill({ json: { ticketId: 'e2e-test-ticket-id' } })
  })
})

test('loads the dashboard and shows the REST snapshot in both views', async ({ page }) => {
  await page.goto('/')

  await page.getByPlaceholder('Bearer token').fill('e2e-test-bearer-token')
  await page.getByRole('button', { name: 'Connect' }).click()

  await expect(page.getByRole('heading', { name: 'SyncMesh Monitor' })).toBeVisible()

  // Element Plus keeps inactive el-tab-pane content in the DOM (hidden,
  // not unmounted) and getByText matches substrings by default, so an
  // unqualified 'daemon-1' also matches the Connections tab's node-key
  // cell ("daemon:site-a:daemon-1") even while that tab isn't active —
  // exact matching is required to disambiguate.
  await page.getByRole('tab', { name: 'Data' }).click()
  await expect(page.getByText('daemon-1', { exact: true })).toBeVisible()

  await page.getByRole('tab', { name: 'Servers' }).click()
  await expect(page.getByText('server-1', { exact: true })).toBeVisible()

  await page.getByRole('tab', { name: 'Topology', exact: true }).click()
  await expect(page.locator('.topology-graph canvas')).toBeVisible()
})
