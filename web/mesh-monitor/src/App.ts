import { defineComponent, onMounted } from 'vue'
import { useMeshStore } from './stores/meshStore'
import TopologyView from './views/TopologyView.vue'
import DataView from './views/DataView.vue'

export default defineComponent({
  components: { TopologyView, DataView },
  setup() {
    const store = useMeshStore()

    onMounted(() => {
      void store.loadSnapshot()
      store.connectLive()
    })

    return { store }
  },
})
