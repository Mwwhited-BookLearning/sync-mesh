import { defineComponent, ref, onMounted, onBeforeUnmount, watch } from 'vue'
import { Network, type Options } from 'vis-network'
import { DataSet } from 'vis-data'
import { useMeshStore } from '../stores/meshStore'
import { nodeKey } from '../types/monitor'
import type { GraphNode, GraphEdge } from './TopologyView.types'

// Physics stays enabled (vis-network's default) rather than fixing node
// positions — the whole point of choosing vis-network was auto-layout, so
// the graph re-settles on its own as nodes/edges come and go.
const networkOptions: Options = {
  physics: { enabled: true, solver: 'forceAtlas2Based' },
  groups: {
    daemon: { shape: 'box', color: { background: '#409eff', border: '#337ecc' } },
    server: { shape: 'ellipse', color: { background: '#67c23a', border: '#4e8e2f' } },
  },
  edges: { arrows: 'to', font: { align: 'middle' } },
}

export default defineComponent({
  setup() {
    const store = useMeshStore()
    const container = ref<HTMLDivElement | null>(null)

    const nodesDataSet = new DataSet<GraphNode>([])
    const edgesDataSet = new DataSet<GraphEdge>([])
    let network: Network | null = null

    function syncGraphData(): void {
      const graphNodes: GraphNode[] = store.nodeList.map((node) => ({
        id: nodeKey(node),
        label: `${node.nodeKind}\n${node.siteId}/${node.instanceId}`,
        group: node.nodeKind,
        color: store.isStale(node) ? { background: '#c0c4cc', border: '#909399' } : undefined,
      }))
      nodesDataSet.clear()
      nodesDataSet.add(graphNodes)

      const graphEdges: GraphEdge[] = store.edges.map((edge) => ({
        id: edge.id,
        from: edge.from,
        to: edge.to,
        label: String(edge.eventsForwarded),
      }))
      edgesDataSet.clear()
      edgesDataSet.add(graphEdges)
    }

    onMounted(() => {
      syncGraphData()
      if (container.value) {
        network = new Network(container.value, { nodes: nodesDataSet, edges: edgesDataSet }, networkOptions)
      }
    })

    watch(() => [store.nodeList, store.edges], syncGraphData, { deep: true })

    onBeforeUnmount(() => {
      network?.destroy()
      network = null
    })

    return { container }
  },
})
