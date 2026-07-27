import { defineComponent, computed } from 'vue'
import { useMeshStore } from '../stores/meshStore'
import { useCommand } from '../composables/useCommand'
import type { DaemonStatus, ServerStatus } from '../types/monitor'
import type { DaemonRow, ServerRow, ConnectionRow } from './DataView.types'

export default defineComponent({
  setup() {
    const store = useMeshStore()

    const daemonRows = computed<DaemonRow[]>(() =>
      store.daemons.map((node) => {
        const status = node.status as DaemonStatus
        return {
          siteId: status.siteId,
          instanceId: status.instanceId,
          bufferedEventCount: status.bufferedEventCount,
          connectedToNearestServer: status.connectedToNearestServer,
          nearestServerUrl: status.nearestServerUrl,
          eventsForwardedCount: status.eventsForwardedCount,
          stale: store.isStale(node),
        }
      }),
    )

    const serverRows = computed<ServerRow[]>(() =>
      store.servers.map((node) => {
        const status = node.status as ServerStatus
        return {
          siteId: status.siteId,
          instanceId: status.instanceId,
          url: status.url,
          eventsAppliedCount: status.eventsAppliedCount,
          peerCount: status.configuredPeers.length,
          stale: store.isStale(node),
        }
      }),
    )

    const connectionRows = computed<ConnectionRow[]>(() =>
      store.edges.map((edge) => ({
        id: edge.id,
        from: edge.from,
        to: edge.to,
        eventsForwarded: edge.eventsForwarded,
      })),
    )

    const refreshCommand = useCommand(() => store.loadSnapshot())

    return { daemonRows, serverRows, connectionRows, refreshCommand }
  },
})
