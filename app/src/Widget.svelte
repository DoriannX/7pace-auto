<script lang="ts">
  import { onMount } from 'svelte'
  import { call, contextMenu, describe, quit, showMain } from './lib/api'
  import type { CurrentDay } from './lib/types'
  import { shouldAutoOpen, widgetStatus } from './lib/state'
  import { chrono } from './lib/time'

  const OPENED = 'autoOpenedOn'

  let day = $state<CurrentDay | null>(null)
  let lastOk = $state<number | null>(null)
  let failure = $state<string | null>(null)
  let now = $state(Date.now())
  let previous: number | null = null
  let polling = false

  const status = $derived(widgetStatus(day, lastOk, new Date(now), failure))
  const quick = $derived(day?.tracking.quickRunning ?? false)

  async function poll() {
    if (polling) return
    polling = true
    try {
      const result = await call<CurrentDay>('currentDay')
      day = result
      lastOk = Date.now()
      failure = null
      if (shouldAutoOpen(previous, result.pending, result.date, localStorage.getItem(OPENED))) {
        localStorage.setItem(OPENED, result.date)
        showMain()
      }
      previous = result.pending
    } catch (error) {
      failure = describe(error)
      lastOk = null
    } finally {
      polling = false
    }
  }

  async function toggleQuick() {
    try {
      await call('setQuick', { running: !quick })
    } catch (error) {
      failure = describe(error)
    }
    await poll()
  }

  function menu(event: MouseEvent) {
    event.preventDefault()
    contextMenu([
      { text: 'Ouvrir', action: () => showMain() },
      { text: 'Quitter', action: () => quit() },
    ])
  }

  onMount(() => {
    poll()
    const polls = setInterval(poll, 5000)
    const clock = setInterval(() => (now = Date.now()), 1000)
    return () => {
      clearInterval(polls)
      clearInterval(clock)
    }
  })
</script>

<svelte:window oncontextmenu={menu} />

<div class="bar tone-{status.tone}">
  <span class="grip" data-tauri-drag-region title="Déplacer">⋮</span>
  <i class="dot"></i>
  <button class="text" title={status.text} onclick={() => showMain()}>{status.text}</button>
  {#if status.chrono !== null}
    <span class="chrono">{chrono(status.chrono)}</span>
  {/if}
  <button
    class="quick"
    class:running={quick}
    disabled={!day || lastOk === null}
    title={quick ? 'Arrêter le chrono rapide' : 'Démarrer un chrono rapide « à attribuer »'}
    onclick={toggleQuick}>{quick ? '\uf04d' : '\uf04b'}</button
  >
</div>

<style>
  .bar {
    height: 100%;
    display: flex;
    align-items: center;
    gap: 8px;
    padding-right: 4px;
    background: var(--glass);
    border: 1px solid var(--border-strong);
  }

  .grip {
    align-self: stretch;
    display: flex;
    align-items: center;
    padding: 0 2px 0 9px;
    color: var(--text-dim);
    cursor: move;
  }

  .dot {
    width: 8px;
    height: 8px;
    flex: none;
    background: var(--dot);
  }

  .tone-ok {
    --dot: var(--success);
  }

  .tone-idle {
    --dot: var(--amber-bright);
  }

  .tone-alert {
    --dot: var(--accent-bright);
    border-color: var(--accent-dim);
  }

  .text {
    flex: 1;
    min-width: 0;
    padding: 0;
    border: none;
    text-align: left;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .tone-alert .text {
    color: var(--accent-bright);
  }

  .chrono {
    color: var(--text-muted);
    font-variant-numeric: tabular-nums;
  }

  .quick {
    border: none;
    padding: 0 7px;
    color: var(--text-muted);
  }

  .quick.running {
    color: var(--amber-bright);
  }
</style>
