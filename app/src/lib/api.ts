// Seul point de contact avec le collecteur. Dans Tauri, l'appel passe par la commande Rust
// agent_call ; dans un simple navigateur (pnpm dev), il est servi par des données fictives.

export const inTauri = typeof window !== 'undefined' && '__TAURI_INTERNALS__' in window

export class AgentError extends Error {
  constructor(message: string, readonly kind: string) {
    super(message)
  }
}

export async function call<T>(method: string, params: Record<string, unknown> = {}): Promise<T> {
  if (!inTauri) {
    const { mock } = await import('./fixtures/mock')
    return (await mock(method, params)) as T
  }
  const { invoke } = await import('@tauri-apps/api/core')
  try {
    return await invoke<T>('agent_call', { method, params })
  } catch (failure) {
    const detail = failure as { message?: string; kind?: string } | string
    if (typeof detail === 'string') throw new AgentError(detail, 'internal')
    throw new AgentError(detail?.message ?? String(failure), detail?.kind ?? 'internal')
  }
}

async function command<T>(name: string): Promise<T | undefined> {
  if (!inTauri) return undefined
  const { invoke } = await import('@tauri-apps/api/core')
  return invoke<T>(name)
}

export const showMain = () => command('show_main')
export const toWidget = () => command('to_widget')
export const quit = () => command('quit')
export const appPid = async () => (await command<number>('app_pid')) ?? 0

/** Prévient la fenêtre principale qu'elle vient d'être affichée. */
export async function onShown(handler: () => void): Promise<() => void> {
  if (!inTauri) return () => {}
  const { listen } = await import('@tauri-apps/api/event')
  return listen('shown', handler)
}

export async function contextMenu(items: { text: string; action: () => void }[]): Promise<void> {
  if (!inTauri) return
  const { Menu } = await import('@tauri-apps/api/menu')
  const menu = await Menu.new({ items: items.map((item) => ({ id: item.text, text: item.text, action: item.action })) })
  await menu.popup()
}

export function describe(failure: unknown): string {
  return failure instanceof Error ? failure.message : String(failure)
}
