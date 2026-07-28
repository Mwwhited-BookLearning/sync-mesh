import { defineComponent } from 'vue'
import { useAuthStore } from './stores/authStore'
import { useMeshStore } from './stores/meshStore'
import ConnectView from './views/ConnectView.vue'
import TopologyView from './views/TopologyView.vue'
import DataView from './views/DataView.vue'

export default defineComponent({
  components: { ConnectView, TopologyView, DataView },
  setup() {
    const auth = useAuthStore()
    const store = useMeshStore()

    // loadSnapshot/connectLive now start from ConnectView's "Connect"
    // command instead of firing unconditionally on mount — both need a
    // bearer token, which this dashboard no longer assumes it already
    // has (see docs/adr/0009-ticket-based-signalr-auth.md).
    return { auth, store }
  },
})
