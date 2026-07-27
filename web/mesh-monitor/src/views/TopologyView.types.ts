import type { Edge, Node } from 'vis-network'

export interface GraphNode extends Node {
  id: string
  label: string
  group: 'daemon' | 'server'
}

export interface GraphEdge extends Edge {
  id: string
  from: string
  to: string
  label: string
}
