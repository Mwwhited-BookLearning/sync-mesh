import { defineStore } from 'pinia'
import { ref, computed } from 'vue'
import { mintTicket } from '../services/auth'

// The "ViewModel" for auth — see UI-ARCHITECTURE.md's MVVM section. This
// project doesn't issue bearer tokens itself (docs/adr/0009-ticket-based-
// signalr-auth.md), so the token is entered directly rather than obtained
// via a login flow; everything past that point (minting a fresh ticket
// per SignalR connection attempt) is this store's job, kept out of
// meshStore so meshStore stays about topology data, not auth.
//
// The token lives in memory only (a plain ref, no localStorage/
// sessionStorage) — deliberately: this is a dev/POC dashboard behind no
// TLS yet (see PRODUCTION-HARDENING.md), and persisting a bearer token to
// browser storage is a real exposure a page reload doesn't need to avoid.
export const useAuthStore = defineStore('auth', () => {
  const bearerToken = ref<string | null>(null)

  const isAuthenticated = computed(() => bearerToken.value !== null)

  function setToken(token: string): void {
    bearerToken.value = token.trim()
  }

  function clearToken(): void {
    bearerToken.value = null
  }

  // Called fresh before every SignalR (re)connection attempt via
  // accessTokenFactory (see services/signalrClient.ts) — a ticket is
  // single-use, so a reconnect must mint a new one, not resend the last.
  async function getSignalRAccessToken(): Promise<string> {
    if (bearerToken.value === null) {
      throw new Error('Cannot mint a ticket without a bearer token.')
    }
    return mintTicket(bearerToken.value)
  }

  function authorizationHeader(): Record<string, string> {
    if (bearerToken.value === null) {
      return {}
    }
    return { Authorization: `Bearer ${bearerToken.value}` }
  }

  return { isAuthenticated, setToken, clearToken, getSignalRAccessToken, authorizationHeader }
})
