import { ref, computed, type Ref } from 'vue'

export interface Command {
  execute: () => Promise<void>
  isExecuting: Readonly<Ref<boolean>>
  canExecute: Readonly<Ref<boolean>>
}

// Approximates WPF's bindable ICommand (Execute/CanExecute) for Vue
// templates: `:disabled="!cmd.canExecute" @click="cmd.execute"`. Wraps a
// store action so a template never calls one directly — see
// UI-ARCHITECTURE.md's MVVM section for the full rationale.
export function useCommand(action: () => Promise<void>, canExecuteWhen?: () => boolean): Command {
  const isExecuting = ref(false)

  const canExecute = computed(() => !isExecuting.value && (canExecuteWhen ? canExecuteWhen() : true))

  async function execute(): Promise<void> {
    if (!canExecute.value) {
      return
    }

    isExecuting.value = true
    try {
      await action()
    } finally {
      isExecuting.value = false
    }
  }

  return { execute, isExecuting, canExecute }
}
