import type { TopologyNode } from '../types/monitor'

// Relative path — same-origin in production (served from the API's own
// wwwroot) and proxied to the API by Vite's dev server in development
// (see vite.config.ts), so no separate base-URL configuration is needed
// either way.
export async function fetchTopologySnapshot(): Promise<TopologyNode[]> {
  const response = await fetch('/api/topology')
  if (!response.ok) {
    throw new Error(`Failed to fetch topology snapshot: ${response.status} ${response.statusText}`)
  }
  return (await response.json()) as TopologyNode[]
}
