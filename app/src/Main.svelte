<script lang="ts">
  import { onMount } from 'svelte'
  import { call, describe, onShown, toWidget } from './lib/api'
  import type { CurrentDay, DayReview, Entry, LoadedSettings, NothingPending, Outcome, PendingDay } from './lib/types'
  import { frenchDate, toTime } from './lib/time'
  import { elapsedHoles, visibleRange, type Span } from './lib/timeline'
  import { submitBlock, suggestions, trackingLabel } from './lib/state'
  import TitleBar from './components/TitleBar.svelte'
  import Timeline from './components/Timeline.svelte'
  import Editor from './components/Editor.svelte'
  import Footer from './components/Footer.svelte'
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

  const editing = $derived(view === 'pending' && pending !== null)
  const day: DayReview | null = $derived(editing ? pending : today)
  const holes = $derived(day ? (editing ? day.holes : elapsedHoles(day.holes, day.date, new Date())) : [])
  const range = $derived(visibleRange(windows, day?.entries ?? []))
  const current: Entry | null = $derived(editing && pending ? (pending.entries.find((entry) => entry.id === selected) ?? null) : null)
  const block = $derived(pending ? submitBlock(pending, today?.tracking.quickRunning ?? false) : null)
  const title = $derived(day ? frenchDate(day.date) : '7pace auto')
  const extra = $derived(editing && pending ? pending.pending - 1 : 0)
  const toggle = $derived(editing ? 'Aujourd’hui' : pending ? 'À envoyer' : null)
  const status = $derived(link ?? (today ? `Suivi ${trackingLabel(today.tracking.state)}` : ''))
  const line: Message | null = $derived(message ?? (link && editing ? { text: link, tone: 'error' } : null))

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
      // Sans réglages, la frise garde les horaires tirés des créneaux.
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

  function flip() {
    view = editing ? 'today' : 'pending'
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
  <TitleBar {title} {extra} {toggle} ontoggle={flip} onsettings={() => (settingsOpen = !settingsOpen)} onclose={() => toWidget()} />
  <main>
    {#if day}
      <Timeline
        entries={day.entries}
        {holes}
        {windows}
        {range}
        editable={editing}
        {selected}
        onselect={(id) => (selected = id)}
        onchange={change}
        oncreate={create}
      />
      {#if current && pending}
        <Editor
          entry={current}
          suggestions={suggestions(pending.entries, current)}
          onsave={save}
          ondelete={remove}
          onerror={(text) => (message = { text, tone: 'error' })}
        />
      {:else if !editing && today}
        <p class="dim notice">{today.notice}</p>
      {/if}
    {:else if ready}
      <p class="error">{link ?? 'Aucune donnée reçue du collecteur.'}</p>
    {/if}
    {#if settingsOpen}
      <Settings
        onclose={() => (settingsOpen = false)}
        onsaved={(settings) => {
          windows = settings.workWindows
          refresh()
        }}
      />
    {/if}
  </main>
  {#if line}
    <p class="message {line.tone}">{line.text}</p>
  {/if}
  {#if day}
    <Footer
      total={day.totalMinutes}
      planned={day.plannedMinutes}
      readOnly={!editing}
      {block}
      {busy}
      {status}
      onsubmit={() => close('submitDay')}
      ondiscard={() => close('discardDay')}
    />
  {/if}
</div>

<style>
  .shell {
    height: 100%;
    display: flex;
    flex-direction: column;
    background: var(--glass);
    border: 1px solid var(--border-strong);
  }

  main {
    flex: 1;
    position: relative;
    overflow: auto;
    padding: 12px 18px;
  }

  .notice {
    margin-top: 18px;
  }

  .message {
    margin: 0;
    padding: 6px 18px;
    border-top: 1px solid var(--border);
  }

  .message.info {
    color: var(--text-muted);
  }

  .message.error {
    color: var(--error);
  }
</style>
