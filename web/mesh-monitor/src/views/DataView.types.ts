export interface DaemonRow {
  siteId: string
  instanceId: string
  bufferedEventCount: number
  connectedToNearestServer: boolean
  nearestServerUrl: string
  eventsForwardedCount: number
  stale: boolean
}

export interface ServerRow {
  siteId: string
  instanceId: string
  url: string
  eventsAppliedCount: number
  peerCount: number
  stale: boolean
}

export interface ConnectionRow {
  id: string
  from: string
  to: string
  eventsForwarded: number
}
