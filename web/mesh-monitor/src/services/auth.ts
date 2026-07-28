// Client side of the ticket exchange in
// docs/adr/0009-ticket-based-signalr-auth.md: the real bearer token is
// only ever sent once, via header, to POST /auth/ticket. Everywhere else
// (the SignalR connection URL) uses a short-lived, single-use hashed
// ticket instead — HMAC-SHA256(oneTimeSecret, ticketId), computed here
// the same way SyncMesh.MeshMonitor.Api.Auth.TicketHasher computes it
// server-side. The server never returns this hashed value itself; only
// the raw ticketId, useless alone without the secret generated below.

function generateOneTimeSecret(): string {
  // 24 random bytes hex-encoded (48 chars) — comfortably above the
  // server's MinSecretLength (16), matching TicketEndpoints.cs.
  const bytes = crypto.getRandomValues(new Uint8Array(24))
  return toHex(bytes)
}

async function requestTicketId(bearerToken: string, oneTimeSecret: string): Promise<string> {
  const response = await fetch('/auth/ticket', {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${bearerToken}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ oneTimeSecret }),
  })

  if (!response.ok) {
    throw new Error(`Failed to issue a ticket: ${response.status} ${response.statusText}`)
  }

  const { ticketId } = (await response.json()) as { ticketId: string }
  return ticketId
}

export async function computeHashedTicket(oneTimeSecret: string, ticketId: string): Promise<string> {
  const key = await crypto.subtle.importKey(
    'raw',
    new TextEncoder().encode(oneTimeSecret),
    { name: 'HMAC', hash: 'SHA-256' },
    false,
    ['sign'],
  )
  const signature = await crypto.subtle.sign('HMAC', key, new TextEncoder().encode(ticketId))
  // Convert.ToHexString on the .NET side is uppercase — the ticket store
  // key is a plain string comparison, so casing must match exactly.
  return toHex(new Uint8Array(signature)).toUpperCase()
}

function toHex(bytes: Uint8Array): string {
  return Array.from(bytes)
    .map((b) => b.toString(16).padStart(2, '0'))
    .join('')
}

// Mints one fresh, single-use hashed ticket. Called anew for every
// SignalR (re)connection attempt (see stores/authStore.ts) — reusing one
// ticket across multiple connections would fail the second time by
// design (TicketStore.TryRedeem removes it on first use).
export async function mintTicket(bearerToken: string): Promise<string> {
  const oneTimeSecret = generateOneTimeSecret()
  const ticketId = await requestTicketId(bearerToken, oneTimeSecret)
  return computeHashedTicket(oneTimeSecret, ticketId)
}
