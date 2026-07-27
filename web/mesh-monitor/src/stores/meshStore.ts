import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { nodeKey, type TopologyNode, type DaemonStatus, type ServerStatus } from '../types/monitor'
import { fetchTopologySnapshot } from '../services/api'
import { connectMeshHub } from '../services/signalrClient'

// Default publish interval on the .NET side (DaemonMonitorOptions/
// ServerMonitorOptions) is 5s; a node is considered stale once it's missed
// a few ticks in a row rather than exactly one, to tolerate a slow tick
// without flickering the UI.
const STALE_AFTER_MS = 15_000

export interface MeshEdge {
  id: string
  from: string
  to: string
  eventsForwarded: number
}

// The MVVM "ViewModel" layer: reactive state + derived data (getters) that
// views bind to, with actions treated as commands (see composables/
// useCommand.ts). Views never talk to services/* directly.
export const useMeshStore = defineStore('mesh', () => {
  const nodes = ref(new Map<string, TopologyNode>())
  const isConnected = ref(false)

  function upsert(node: TopologyNode): void {
    const next = new Map(nodes.value)
    next.set(nodeKey(node), node)
    nodes.value = next
  }

  const nodeList = computed(() => Array.from(nodes.value.values()))

  const daemons = computed(() =>
    nodeList.value.filter((node): node is TopologyNode & { status: DaemonStatus } => node.nodeKind === 'daemon'),
  )

  const servers = computed(() =>
    nodeList.value.filter((node): node is TopologyNode & { status: ServerStatus } => node.nodeKind === 'server'),
  )

  function isStale(node: TopologyNode): boolean {
    return Date.now() - new Date(node.lastSeenUtc).getTime() > STALE_AFTER_MS
  }

  // Derives every connection from what each node self-reports about
  // itself (NearestServerUrl / ConfiguredPeers) — never from a separately
  // maintained topology config, so it can never drift out of sync with
  // what's actually running. See docs/adr/0002-nats-leaf-nodes-for-
  // transport.md's 2026-07-23 (Phase 3) Amendment for the backend side of
  // this self-reporting convention.
  const edges = computed<MeshEdge[]>(() => {
    const list = nodeList.value
    const result: MeshEdge[] = []

    for (const node of list) {
      if (node.nodeKind === 'daemon') {
        const status = node.status as DaemonStatus
        const target = list.find(
          (candidate) => candidate.nodeKind === 'server' && (candidate.status as ServerStatus).url === status.nearestServerUrl,
        )
        if (target) {
          result.push({
            id: `${nodeKey(node)}->${nodeKey(target)}`,
            from: nodeKey(node),
            to: nodeKey(target),
            eventsForwarded: status.eventsForwardedCount,
          })
        }
      } else {
        const status = node.status as ServerStatus
        for (const peer of status.configuredPeers) {
          const target = list.find(
            (candidate) => candidate.nodeKind === 'server' && (candidate.status as ServerStatus).siteId === peer.peerSiteId,
          )
          if (target) {
            result.push({
              id: `${nodeKey(node)}->${nodeKey(target)}`,
              from: nodeKey(node),
              to: nodeKey(target),
              eventsForwarded: peer.eventsForwardedCount,
            })
          }
        }
      }
    }

    return result
  })

  async function loadSnapshot(): Promise<void> {
    const snapshot = await fetchTopologySnapshot()
    for (const node of snapshot) {
      upsert(node)
    }
  }

  function connectLive(): void {
    connectMeshHub({
      onNodeUpdated: upsert,
      onConnected: () => {
        isConnected.value = true
      },
      onDisconnected: () => {
        isConnected.value = false
      },
    })
  }

  return { nodeList, daemons, servers, edges, isConnected, isStale, upsert, loadSnapshot, connectLive }
})
