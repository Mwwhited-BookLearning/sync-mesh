import { defineComponent, ref } from 'vue'
import { useAuthStore } from '../stores/authStore'
import { useMeshStore } from '../stores/meshStore'
import { useCommand } from '../composables/useCommand'

// This dashboard doesn't issue bearer tokens itself (see
// docs/adr/0009-ticket-based-signalr-auth.md) — an operator pastes one
// obtained from wherever it's actually issued. Everything past that
// point (minting a ticket per SignalR connection) is authStore's job.
export default defineComponent({
  setup() {
    const auth = useAuthStore()
    const mesh = useMeshStore()
    const tokenInput = ref('')
    const error = ref<string | null>(null)

    const connectCommand = useCommand(
      async () => {
        error.value = null
        auth.setToken(tokenInput.value)
        try {
          await mesh.loadSnapshot()
          mesh.connectLive()
        } catch (caught: unknown) {
          auth.clearToken()
          error.value = caught instanceof Error ? caught.message : 'Failed to connect.'
        }
      },
      () => tokenInput.value.trim().length > 0,
    )

    return { tokenInput, error, connectCommand }
  },
})
