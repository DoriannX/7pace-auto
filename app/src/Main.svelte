<script lang="ts">
  import { onMount } from 'svelte'
  import { call, describe, onShown, toWidget } from './lib/api'
  import type { CurrentDay, DayReview, Entry, LoadedSettings, NothingPending, Outcome, PendingDay } from './lib/types'
  import { duration, frenchDate, minuteOfDay, toTime } from './lib/time'
  import { elapsedHoles, visibleRange, type Span } from './lib/timeline'
  import { isUnassigned, submitBlock, suggestions, trackingLabel } from './lib/state'
  import TitleBar from './components/TitleBar.svelte'
  import DayColumn from './components/DayColumn.svelte'
  import Editor from './components/Editor.svelte'
  import HoldButton from './components/HoldButton.svelte'
  import Settings from './components/Settings.svelte'

  type Message = { text: string; tone: 'error' | 'info' }

  let pending = $state<PendingDay | null>(null)
  let today = $state<CurrentDay | null>(null)
  let view = $state<'pending' | 'today'>('pending')
  let selected = $state<number | null>(null)
  let message = $state<Message | null>(null)
  let link = $state<string | null>(null)
  let busy = $state(false)
  let ready = $state(false)
  let settingsOpen = $state(false)
  let windows = $state<number[][]>([])
  let clock = $state(new Date())

  const editing = $derived(view === 'pending' && pending !== null)
  const day: DayReview | null = $derived(editing ? pending : today)
  const holes = $derived(day ? (editing ? day.holes : elapsedHoles(day.holes, day.date, clock)) : [])
  const range = $derived(visibleRange(windows, day?.entries ?? []))
  const current: Entry | null = $derived(editing && pending ? (pending.entries.find((entry) => entry.id === selected) ?? null) : null)
  const block = $derived(pending ? submitBlock(pending, today?.tracking.quickRunning ?? false) : null)
  const missing = $derived(pending ? pending.entries.filter((entry) => !entry.sentAt && isUnassigned(entry)) : [])
  const line: Message | null = $derived(message ?? (link ? { text: link, tone: 'error' } : null))

  const capital = (text: string) => text.charAt(0).toUpperCase() + text.slice(1)
  const others = (count: number) => (count === 1 ? 'puis 1 autre journée' : `puis ${count} autres journées`)

  function report(failure: unknown) {
    message = { text: describe(failure), tone: 'error' }
  }

  async function loadPending() {
    const result = await call<PendingDay | NothingPending>('pendingDay')
    if (result.date === null) {
      pending = null
      return
    }
    pending = result
    const azure = result.azure
    if (azure?.message && (azure.resolved > 0 || azure.failed > 0)) {
      message = { text: azure.message, tone: azure.failed > 0 ? 'error' : 'info' }
    }
  }

  async function loadToday() {
    clock = new Date()
    try {
      today = await call<CurrentDay>('currentDay')
      link = null
    } catch (failure) {
      link = describe(failure)
    }
  }

  async function loadWindows() {
    try {
      windows = (await call<LoadedSettings>('loadSettings')).settings.workWindows
    } catch {
      // Sans réglages, la journée garde les horaires tirés des créneaux.
    }
  }

  async function refresh() {
    busy = true
    try {
      await Promise.all([loadPending().catch(report), loadToday(), loadWindows()])
    } finally {
      busy = false
      ready = true
    }
  }

  async function mutate(method: string, params: Record<string, unknown>): Promise<boolean> {
    if (!pending) return false
    message = null
    busy = true
    try {
      const review = await call<DayReview>(method, { date: pending.date, ...params })
      pending = { ...pending, ...review }
      return true
    } catch (failure) {
      report(failure)
      return false
    } finally {
      busy = false
    }
  }

  async function save(entry: Entry) {
    const before = new Set(pending?.entries.map((item) => item.id))
    if ((await mutate('saveEntry', { entry })) && entry.id === null) {
      selected = pending?.entries.find((item) => !before.has(item.id))?.id ?? null
    }
  }

  async function change(entry: Entry, span: Span) {
    selected = entry.id
    await save({ ...entry, start: toTime(span.start), end: toTime(span.end) })
  }

  const create = (span: Span) => save({ id: null, start: toTime(span.start), end: toTime(span.end), workItem: null, label: '', source: 'manual' })

  async function remove(id: number) {
    if (await mutate('deleteEntry', { id })) selected = null
  }

  async function close(method: 'submitDay' | 'discardDay') {
    if (!pending) return
    busy = true
    message = null
    try {
      const outcome = await call<Outcome>(method, { date: pending.date })
      message = { text: outcome.message, tone: method === 'discardDay' || outcome.ok ? 'info' : 'error' }
      selected = null
      await loadPending()
    } catch (failure) {
      report(failure)
    } finally {
      busy = false
    }
  }

  function show(next: 'pending' | 'today') {
    view = next
    selected = null
  }

  function key(event: KeyboardEvent) {
    if (event.key !== 'Escape') return
    if (settingsOpen) settingsOpen = false
    else selected = null
  }

  function menu(event: MouseEvent) {
    if (!(event.target as HTMLElement).closest('input')) event.preventDefault()
  }

  onMount(() => {
    refresh()
    const timer = setInterval(loadToday, 5000)
    let unlisten = () => {}
    onShown(() => {
      view = 'pending'
      settingsOpen = false
      refresh()
    }).then((stop) => (unlisten = stop))
    return () => {
      clearInterval(timer)
      unlisten()
    }
  })
</script>

<svelte:window onkeydown={key} oncontextmenu={menu} />

<div class="shell">
  <TitleBar onsettings={() => (settingsOpen = !settingsOpen)} onclose={() => toWidget()} />
  <div class="body">
    <main>
      {#if day}
        <DayColumn
          entries={day.entries}
          {holes}
          {windows}
          {range}
          editable={editing}
          {selected}
          now={editing ? null : minuteOfDay(clock)}
          onselect={(id) => (selected = id)}
          onchange={change}
          oncreate={create}
        />
      {:else if ready}
        <p class="error">{link ?? 'Aucune donnée reçue du collecteur.'}</p>
      {/if}
    </main>

    <aside>
      {#if editing && pending}
        <p class="kicker">À envoyer dans 7pace</p>
        <h1>{capital(frenchDate(pending.date))}</h1>
        {#if pending.pending > 1}
          <p class="dim sub">{others(pending.pending - 1)}</p>
        {/if}
        <p class="total"><b>{duration(pending.totalMinutes)}</b> <span class="muted">sur {duration(pending.plannedMinutes)} prévues</span></p>

        {#if current}
          <Editor
            entry={current}
            suggestions={suggestions(pending.entries, current)}
            onsave={save}
            ondelete={remove}
            onclose={() => (selected = null)}
            onerror={(text) => (message = { text, tone: 'error' })}
          />
        {:else}
          {#if missing.length}
            <p class="kicker">À corriger avant l’envoi</p>
            <ul>
              {#each missing as entry (entry.id)}
                <li><button onclick={() => (selected = entry.id)}><span>{entry.start}–{entry.end}</span> <span class="bad">sans ticket</span></button></li>
              {/each}
            </ul>
          {:else}
            <p class="ok">Chaque créneau a son ticket.</p>
          {/if}
          {#if holes.length}
            <p class="kicker">Non couvert (facultatif)</p>
            <ul>
              {#each holes as hole (hole[0])}
                <li><button onclick={() => create({ start: hole[0], end: hole[1] })}><span>{toTime(hole[0])}–{toTime(hole[1])}</span> <span class="dim">ajouter</span></button></li>
              {/each}
            </ul>
          {/if}
          <p class="dim hint">Clique un créneau pour le corriger, glisse dans le vide pour en ajouter.</p>
        {/if}

        <div class="actions">
          {#if line}
            <p class="message {line.tone}">{line.text}</p>
          {/if}
          <HoldButton
            label={block ?? 'Maintenir pour envoyer'}
            tone="accent"
            title="Maintenir une seconde pour envoyer dans 7pace"
            disabled={busy || block !== null}
            onhold={() => close('submitDay')}
          />
          <div class="links">
            <button class="ghost" onclick={() => show('today')}>Voir aujourd’hui</button>
            <HoldButton label="Ignorer cette journée" title="Maintenir une seconde : rien ne part dans 7pace" disabled={busy} onhold={() => close('discardDay')} />
          </div>
        </div>
      {:else if today}
        <p class="kicker">Aujourd’hui · lecture seule</p>
        <h1>{capital(frenchDate(today.date))}</h1>
        <p class="total"><b>{duration(today.totalMinutes)}</b> <span class="muted">relevées</span></p>
        <p>
          Suivi {trackingLabel(today.tracking.state)}{#if today.tracking.state === 'running'} :
            <span class="muted">{today.tracking.workItem ? `#${today.tracking.workItem} ` : ''}{today.tracking.label}</span>{/if}
        </p>
        <p class="dim hint">Cette journée se corrige et s’envoie demain matin.</p>
        <div class="actions">
          {#if line}
            <p class="message {line.tone}">{line.text}</p>
          {/if}
          {#if pending}
            <button class="primary" onclick={() => show('pending')}>Corriger {frenchDate(pending.date)}</button>
          {:else}
            <p class="muted">Rien à envoyer pour l’instant.</p>
          {/if}
        </div>
      {/if}
    </aside>
    {#if settingsOpen}
      <Settings
        onclose={() => (settingsOpen = false)}
        onsaved={(settings) => {
          windows = settings.workWindows
          refresh()
        }}
      />
    {/if}
  </div>
</div>

<style>
  .shell {
    height: 100%;
    display: flex;
    flex-direction: column;
    background: var(--glass);
    border: 1px solid var(--border-strong);
  }

  .body {
    position: relative;
    flex: 1;
    min-height: 0;
    display: grid;
    grid-template-columns: 1fr 320px;
  }

  main {
    position: relative;
    overflow: auto;
    padding: 16px 18px 16px 6px;
  }

  aside {
    display: flex;
    flex-direction: column;
    gap: 10px;
    min-height: 0;
    overflow: auto;
    padding: 18px;
    background: var(--bg-surface);
    border-left: 1px solid var(--border);
  }

  .kicker {
    margin: 6px 0 0;
    color: var(--text-dim);
    font-size: 11px;
  }

  h1 {
    margin: 0;
    font-size: 18px;
    font-weight: 600;
  }

  .sub {
    margin: -6px 0 0;
  }

  p {
    margin: 0;
  }

  .total b {
    font-size: 26px;
    font-variant-numeric: tabular-nums;
  }

  ul {
    margin: 0;
    padding: 0;
    list-style: none;
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

  li button {
    width: 100%;
    display: flex;
    justify-content: space-between;
    text-align: left;
  }

  .bad {
    color: var(--accent-bright);
  }

  .hint {
    font-size: 12px;
  }

  .actions {
    margin-top: auto;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }

  .actions :global(.hold.accent) {
    padding: 8px 10px;
  }

  .links {
    display: flex;
    justify-content: space-between;
  }

  .links button,
  .links :global(.hold) {
    white-space: nowrap;
    font-size: 12px;
    padding: 3px 4px;
  }

  .links :global(.hold) {
    border-color: transparent;
  }

  .message.info {
    color: var(--text-muted);
  }

  .message.error {
    color: var(--error);
  }

  .primary {
    padding: 8px 10px;
    border-color: var(--accent);
  }
</style>
