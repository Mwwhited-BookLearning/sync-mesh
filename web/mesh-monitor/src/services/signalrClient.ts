import * as signalR from '@microsoft/signalr'
import type { TopologyNode } from '../types/monitor'

export interface MeshHubCallbacks {
  onNodeUpdated: (node: TopologyNode) => void
  onConnected?: () => void
  onDisconnected?: () => void
}

// Same relative-path reasoning as services/api.ts — same-origin in
// production, proxied by Vite in development.
//
// getAccessToken is called by @microsoft/signalr itself before every
// (re)connection attempt (accessTokenFactory) — including automatic
// reconnects, which is exactly why this can't just be a fixed query
// string baked into the URL once: a ticket is single-use
// (docs/adr/0009-ticket-based-signalr-auth.md), so a reconnect needs a
// freshly minted one, not the one the first connection already
// consumed. SignalR sends whatever this returns as `?access_token=` —
// despite that name, the value is this dashboard's hashed ticket, never
// the real bearer token (see authStore.getSignalRAccessToken).
export function connectMeshHub(
  callbacks: MeshHubCallbacks,
  getAccessToken: () => Promise<string>,
): signalR.HubConnection {
  const connection = new signalR.HubConnectionBuilder()
    .withUrl('/hubs/mesh-monitor', { accessTokenFactory: getAccessToken })
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
