// Hand-mirrors SyncMesh.Contracts.{DaemonStatus,ServerStatus,PeerConnectionStatus}
// and SyncMesh.MeshMonitor.Api.TopologyNode. Kept in sync by hand — see
// UI-ARCHITECTURE.md for why (small, stable shapes; not worth an
// OpenAPI/codegen toolchain for a dashboard this size). Field names are
// camelCase because ASP.NET Core's default JSON options (both the REST
// snapshot endpoint and SignalR's JSON protocol) use camelCase, unlike the
// PascalCase the .NET services use for their own NATS wire payloads.

export interface DaemonStatus {
  nodeKind: 'daemon'
  siteId: string
  instanceId: string
  timestampUtc: string
  bufferedEventCount: number
  connectedToNearestServer: boolean
  nearestServerUrl: string
  eventsForwardedCount: number
}

export interface PeerConnectionStatus {
  peerSiteId: string
  peerUrl: string
  eventsForwardedCount: number
}

export interface ServerStatus {
  nodeKind: 'server'
  siteId: string
  instanceId: string
  timestampUtc: string
  url: string
  eventsAppliedCount: number
  configuredPeers: PeerConnectionStatus[]
}

export type NodeStatus = DaemonStatus | ServerStatus

export interface TopologyNode {
  nodeKind: 'daemon' | 'server'
  siteId: string
  instanceId: string
  lastSeenUtc: string
  status: NodeStatus
}

export function nodeKey(node: Pick<TopologyNode, 'nodeKind' | 'siteId' | 'instanceId'>): string {
  return `${node.nodeKind}:${node.siteId}:${node.instanceId}`
}
