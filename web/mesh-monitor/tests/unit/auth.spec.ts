import { describe, it, expect, vi, afterEach } from 'vitest'
import { createHmac } from 'node:crypto'
import { computeHashedTicket, mintTicket } from '../../src/services/auth'

describe('computeHashedTicket', () => {
  it('matches an independently-computed HMAC-SHA256(secret, ticketId), uppercase hex', async () => {
    const secret = 'a-long-enough-secret-value'
    const ticketId = 'ticket-1'

    // Independent reference: Node's own crypto module, not the
    // src/services/auth.ts implementation under test (which uses
    // globalThis.crypto.subtle — the browser WebCrypto API) — this is the
    // same construction SyncMesh.MeshMonitor.Api.Auth.TicketHasher.Compute
    // uses server-side (HMACSHA256.HashData + Convert.ToHexString,
    // uppercase), cross-checked live against the real backend earlier.
    const expected = createHmac('sha256', secret).update(ticketId).digest('hex').toUpperCase()

    const actual = await computeHashedTicket(secret, ticketId)

    expect(actual).toBe(expected)
  })

  it('is deterministic', async () => {
    const first = await computeHashedTicket('some-secret-value', 'ticket-x')
    const second = await computeHashedTicket('some-secret-value', 'ticket-x')

    expect(first).toBe(second)
  })

  it('produces different hashes for different secrets', async () => {
    const first = await computeHashedTicket('secret-one', 'ticket-x')
    const second = await computeHashedTicket('secret-two', 'ticket-x')

    expect(first).not.toBe(second)
  })
})

describe('mintTicket', () => {
  afterEach(() => {
    vi.unstubAllGlobals()
  })

  it('POSTs the bearer token via header and a one-time secret via body, returning the recomputed hash', async () => {
    let capturedRequest: { headers: Record<string, string>; body: string } | undefined

    vi.stubGlobal(
      'fetch',
      vi.fn(async (_url: string, init: RequestInit) => {
        capturedRequest = {
          headers: init.headers as Record<string, string>,
          body: init.body as string,
        }
        const { oneTimeSecret } = JSON.parse(init.body as string) as { oneTimeSecret: string }
        const ticketId = 'server-issued-ticket-id'
        return {
          ok: true,
          json: async () => ({ ticketId }),
          // stash for the assertion below
          _oneTimeSecret: oneTimeSecret,
        } as unknown as Response
      }),
    )

    const hashedTicket = await mintTicket('my-bearer-token')

    expect(capturedRequest?.headers.Authorization).toBe('Bearer my-bearer-token')
    const sentSecret = (JSON.parse(capturedRequest!.body) as { oneTimeSecret: string }).oneTimeSecret
    expect(sentSecret.length).toBeGreaterThanOrEqual(16)

    const expected = createHmac('sha256', sentSecret).update('server-issued-ticket-id').digest('hex').toUpperCase()
    expect(hashedTicket).toBe(expected)
  })

  it('throws when the server rejects the ticket request', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({ ok: false, status: 401, statusText: 'Unauthorized' }) as unknown as Response),
    )

    await expect(mintTicket('an-invalid-token')).rejects.toThrow(/Failed to issue a ticket/)
  })
})
