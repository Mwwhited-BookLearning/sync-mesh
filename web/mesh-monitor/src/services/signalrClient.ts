import * as signalR from '@microsoft/signalr'
import type { TopologyNode } from '../types/monitor'

export interface MeshHubCallbacks {
  onNodeUpdated: (node: TopologyNode) => void
  onConnected?: () => void
  onDisconnected?: () => void
}

// Same relative-path reasoning as services/api.ts — same-origin in
// production, proxied by Vite in development.
export function connectMeshHub(callbacks: MeshHubCallbacks): signalR.HubConnection {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/mesh-monitor')
    .withAutomaticReconnect()
    .build()

  connection.on('NodeUpdated', (node: TopologyNode) => callbacks.onNodeUpdated(node))
  connection.onreconnected(() => callbacks.onConnected?.())
  connection.onclose(() => callbacks.onDisconnected?.())

  connection
    .start()
    .then(() => callbacks.onConnected?.())
    .catch((error: unknown) => {
      console.error('SignalR connection to mesh-monitor hub failed', error)
      callbacks.onDisconnected?.()
    })

  return connection
}
