<script lang="ts">
  import type { Entry } from '../lib/types'
  import { toMinutes, toTime } from '../lib/time'

  interface Props {
    entry: Entry
    suggestions: { workItem: number; label: string }[]
    onsave: (entry: Entry) => void
    ondelete: (id: number) => void
    onerror: (message: string) => void
  }
  let { entry, suggestions, onsave, ondelete, onerror }: Props = $props()

  const SOURCES: Record<string, string> = { git: 'suivi Git', quick: 'chrono rapide', gap: 'interruption', manual: 'saisie' }

  let start = $state('')
  let end = $state('')
  let item = $state('')

  $effect(() => {
    start = entry.start
    end = entry.end
    item = entry.workItem ? String(entry.workItem) : ''
  })

  function read(): Entry | null {
    const from = toMinutes(start)
    const to = toMinutes(end)
    if (Number.isNaN(from) || Number.isNaN(to)) {
      onerror('Indique des horaires au format HH:MM.')
      return null
    }
    const raw = item.trim().replace(/^#/, '')
    let workItem: number | null = null
    if (raw) {
      const number = Number(raw)
      if (!Number.isInteger(number) || number < 1) {
        onerror('Indique un numéro de ticket entier et positif.')
        return null
      }
      workItem = number
    }
    const label = workItem === (entry.workItem ?? null) ? entry.label : (suggestions.find((choice) => choice.workItem === workItem)?.label ?? '')
    return { ...entry, start: toTime(from), end: toTime(to), workItem, label }
  }

  function commit() {
    const next = read()
    if (!next) return
    if (next.start === entry.start && next.end === entry.end && next.workItem === (entry.workItem ?? null)) return
    onsave(next)
  }

  function pick(choice: { workItem: number; label: string }) {
    const next = read()
    if (next) onsave({ ...next, workItem: choice.workItem, label: choice.label })
  }

  function enter(event: KeyboardEvent) {
    if (event.key === 'Enter') (event.currentTarget as HTMLInputElement).blur()
  }
</script>

<section>
  <label>de <input bind:value={start} onblur={commit} onkeydown={enter} size="5" /></label>
  <label>à <input bind:value={end} onblur={commit} onkeydown={enter} size="5" /></label>
  <label># <input bind:value={item} onblur={commit} onkeydown={enter} size="8" placeholder="ticket" inputmode="numeric" /></label>
  {#each suggestions as choice (choice.workItem)}
    <button class="chip" title={choice.label} onclick={() => pick(choice)}>#{choice.workItem} <span class="muted">{choice.label}</span></button>
  {/each}
  <span class="grow"></span>
  <span class="dim" title={entry.label}>{SOURCES[entry.source] ?? entry.source}</span>
  <button class="ghost danger" onclick={() => entry.id !== null && ondelete(entry.id)}>Supprimer</button>
</section>

<style>
  section {
    display: flex;
    flex-wrap: wrap;
    align-items: center;
    gap: 10px;
    margin-top: 18px;
    padding-top: 14px;
    border-top: 1px solid var(--border);
  }

  label {
    display: flex;
    align-items: center;
    gap: 6px;
    color: var(--text-muted);
  }

  .chip {
    max-width: 220px;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .grow {
    flex: 1;
  }

  .danger:hover {
    color: var(--accent-bright);
    border-color: var(--accent-bright);
  }
</style>
