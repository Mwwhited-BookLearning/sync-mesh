import { describe, it, expect, beforeEach, vi } from 'vitest'
import { setActivePinia, createPinia } from 'pinia'
import { useAuthStore } from '../../src/stores/authStore'

vi.mock('../../src/services/auth', () => ({
  mintTicket: vi.fn(async (bearerToken: string) => `ticket-for:${bearerToken}`),
}))

describe('useAuthStore', () => {
  beforeEach(() => {
    setActivePinia(createPinia())
  })

  it('starts unauthenticated', () => {
    const auth = useAuthStore()
    expect(auth.isAuthenticated).toBe(false)
  })

  it('becomes authenticated once a token is set', () => {
    const auth = useAuthStore()
    auth.setToken('some-token')
    expect(auth.isAuthenticated).toBe(true)
  })

  it('trims whitespace from a pasted token', () => {
    const auth = useAuthStore()
    auth.setToken('  some-token  \n')
    expect(auth.authorizationHeader()).toEqual({ Authorization: 'Bearer some-token' })
  })

  it('clearToken returns to unauthenticated', () => {
    const auth = useAuthStore()
    auth.setToken('some-token')
    auth.clearToken()
    expect(auth.isAuthenticated).toBe(false)
  })

  it('authorizationHeader is empty when no token is set', () => {
    const auth = useAuthStore()
    expect(auth.authorizationHeader()).toEqual({})
  })

  it('getSignalRAccessToken mints a fresh ticket using the current bearer token', async () => {
    const auth = useAuthStore()
    auth.setToken('my-token')

    const ticket = await auth.getSignalRAccessToken()

    expect(ticket).toBe('ticket-for:my-token')
  })

  it('getSignalRAccessToken rejects when no token has been set', async () => {
    const auth = useAuthStore()

    await expect(auth.getSignalRAccessToken()).rejects.toThrow(/without a bearer token/)
  })
})
