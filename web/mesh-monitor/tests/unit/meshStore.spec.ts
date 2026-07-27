import { describe, it, expect, beforeEach } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useMeshStore } from '../../src/stores/meshStore'
import type { TopologyNode, DaemonStatus, ServerStatus } from '../../src/types/monitor'

function daemonNode(overrides: Partial<DaemonStatus> = {}, lastSeenUtc = new Date().toISOString()): TopologyNode {
  const status: DaemonStatus = {
    nodeKind: 'daemon',
    siteId: 'site-a',
    instanceId: 'daemon-1',
    timestampUtc: lastSeenUtc,
    bufferedEventCount: 0,
    connectedToNearestServer: true,
    nearestServerUrl: 'nats://server-a:4222',
    eventsForwardedCount: 5,
    ...overrides,
  }
  return { nodeKind: 'daemon', siteId: status.siteId, instanceId: status.instanceId, lastSeenUtc, status }
}

function serverNode(overrides: Partial<ServerStatus> = {}, lastSeenUtc = new Date().toISOString()): TopologyNode {
  const status: ServerStatus = {
    nodeKind: 'server',
    siteId: 'server-a',
    instanceId: 'server-a-instance',
    timestampUtc: lastSeenUtc,
    url: 'nats://server-a:4222',
    eventsAppliedCount: 10,
    configuredPeers: [],
    ...overrides,
  }
  return { nodeKind: 'server', siteId: status.siteId, instanceId: status.instanceId, lastSeenUtc, status }
}

describe('useMeshStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it("derives a daemon-to-server edge by matching NearestServerUrl to a server's own Url", () => {
    const store = useMeshStore()
    const server = serverNode()
    const daemon = daemonNode({ nearestServerUrl: (server.status as ServerStatus).url })

    store.upsert(server)
    store.upsert(daemon)

    expect(store.edges).toHaveLength(1)
    expect(store.edges[0]).toMatchObject({ eventsForwarded: 5 })
  })

  it('derives a server-to-server edge from ConfiguredPeers, keyed by peer SiteId', () => {
    const store = useMeshStore()
    const serverB = serverNode({ siteId: 'server-b', url: 'nats://server-b:4222' })
    const serverA = serverNode({
      configuredPeers: [{ peerSiteId: 'server-b', peerUrl: 'nats://server-b:4222', eventsForwardedCount: 42 }],
    })

    store.upsert(serverA)
    store.upsert(serverB)

    expect(store.edges).toHaveLength(1)
    expect(store.edges[0]).toMatchObject({ eventsForwarded: 42 })
  })

  it('does not derive an edge when the target node has not been seen yet', () => {
    const store = useMeshStore()
    store.upsert(daemonNode({ nearestServerUrl: 'nats://unknown:4222' }))

    expect(store.edges).toHaveLength(0)
  })

  it('marks a node stale once it has not been seen for a while', () => {
    const store = useMeshStore()
    const staleTimestamp = new Date(Date.now() - 60_000).toISOString()
    const node = daemonNode({}, staleTimestamp)
    store.upsert(node)

    expect(store.isStale(node)).toBe(true)
  })

  it('does not mark a freshly-seen node stale', () => {
    const store = useMeshStore()
    const node = daemonNode()
    store.upsert(node)

    expect(store.isStale(node)).toBe(false)
  })

  it('upsert replaces the previous status for the same node identity', () => {
    const store = useMeshStore()
    store.upsert(daemonNode({ eventsForwardedCount: 1 }))
    store.upsert(daemonNode({ eventsForwardedCount: 2 }))

    expect(store.daemons).toHaveLength(1)
    expect((store.daemons[0].status as DaemonStatus).eventsForwardedCount).toBe(2)
  })
})
