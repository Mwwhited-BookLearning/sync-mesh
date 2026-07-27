import { describe, it, expect, vi } from 'vitest'
import { ref } from 'vue'
import { useCommand } from '../../src/composables/useCommand'

describe('useCommand', () => {
  it('is executable by default and reports isExecuting while the action runs', async () => {
    let resolveAction: () => void = () => {}
    const action = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          resolveAction = resolve
        }),
    )
    const command = useCommand(action)

    expect(command.canExecute.value).toBe(true)
    expect(command.isExecuting.value).toBe(false)

    const executePromise = command.execute()
    expect(command.isExecuting.value).toBe(true)
    expect(command.canExecute.value).toBe(false)

    resolveAction()
    await executePromise

    expect(command.isExecuting.value).toBe(false)
    expect(command.canExecute.value).toBe(true)
    expect(action).toHaveBeenCalledTimes(1)
  })

  it('does not run the action while it is already executing (no re-entrant execute)', async () => {
    let resolveAction: () => void = () => {}
    const action = vi.fn(
      () =>
        new Promise<void>((resolve) => {
          resolveAction = resolve
        }),
    )
    const command = useCommand(action)

    const first = command.execute()
    const second = command.execute()

    resolveAction()
    await Promise.all([first, second])

    expect(action).toHaveBeenCalledTimes(1)
  })

  it('resets isExecuting even when the action throws', async () => {
    const action = vi.fn().mockRejectedValue(new Error('boom'))
    const command = useCommand(action)

    await expect(command.execute()).rejects.toThrow('boom')

    expect(command.isExecuting.value).toBe(false)
    expect(command.canExecute.value).toBe(true)
  })

  it('respects an external canExecuteWhen predicate', async () => {
    // Must be a ref (reactive), not a plain closure variable — the
    // computed() inside useCommand can only re-evaluate when its predicate
    // reads something Vue's reactivity system can actually track.
    const allowed = ref(false)
    const action = vi.fn(async () => {})
    const command = useCommand(action, () => allowed.value)

    expect(command.canExecute.value).toBe(false)
    await command.execute()
    expect(action).not.toHaveBeenCalled()

    allowed.value = true
    expect(command.canExecute.value).toBe(true)
    await command.execute()
    expect(action).toHaveBeenCalledTimes(1)
  })
})
