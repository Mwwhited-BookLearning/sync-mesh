import type { TopologyNode } from '../types/monitor'

// Relative path — same-origin in production (served from the API's own
// wwwroot) and proxied to the API by Vite's dev server in development
// (see vite.config.ts), so no separate base-URL configuration is needed
// either way.
//
// Sends the real bearer token directly via header — unlike the SignalR
// hub connection, a plain fetch() has no URL constraint, so there's no
// need for the ticket exchange here (see
// docs/adr/0009-ticket-based-signalr-auth.md). authHeaders comes from
// authStore.authorizationHeader().
export async function fetchTopologySnapshot(authHeaders: Record<string, string>): Promise<TopologyNode[]> {
  const response = await fetch('/api/topology', { headers: authHeaders })
  if (!response.ok) {
    throw new Error(`Failed to fetch topology snapshot: ${response.status} ${response.statusText}`)
  }
  return (await response.json()) as TopologyNode[]
}
