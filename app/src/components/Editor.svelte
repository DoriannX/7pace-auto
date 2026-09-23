<script lang="ts">
  import type { Entry } from '../lib/types'
  import { toMinutes, toTime } from '../lib/time'

  interface Props {
    entry: Entry
    suggestions: { workItem: number; label: string }[]
    onsave: (entry: Entry) => void
    ondelete: (id: number) => void
    onclose: () => void
    onerror: (message: string) => void
  }
  let { entry, suggestions, onsave, ondelete, onclose, onerror }: Props = $props()

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
      onerror('Horaires au format HH:MM.')
      return null
    }
    const raw = item.trim().replace(/^#/, '')
    let workItem: number | null = null
    if (raw) {
      const number = Number(raw)
      if (!Number.isInteger(number) || number < 1) {
        onerror('Le ticket est un numéro entier.')
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
  <div class="times">
    <input bind:value={start} onblur={commit} onkeydown={enter} aria-label="Début" />
    <span class="dim">à</span>
    <input bind:value={end} onblur={commit} onkeydown={enter} aria-label="Fin" />
  </div>
  <input bind:value={item} onblur={commit} onkeydown={enter} placeholder="n° du ticket" inputmode="numeric" aria-label="Ticket" />
  {#if entry.label}
    <p class="muted label">{entry.label}</p>
  {/if}
  {#if suggestions.length}
    <div class="chips">
      {#each suggestions as choice (choice.workItem)}
        <button title={choice.label} onclick={() => pick(choice)}>#{choice.workItem} <span class="muted">{choice.label}</span></button>
      {/each}
    </div>
  {/if}
  <div class="row">
    <button class="ghost danger" onclick={() => entry.id !== null && ondelete(entry.id)}>Supprimer</button>
    <span class="grow"></span>
    <button onclick={onclose}>OK</button>
  </div>
</section>

<style>
  section {
    display: flex;
    flex-direction: column;
    gap: 10px;
    padding: 14px;
    background: var(--bg-raised);
    border: 1px solid var(--border-strong);
  }

  .times {
    display: flex;
    align-items: center;
    gap: 8px;
  }

  .times input {
    width: 7ch;
    text-align: center;
  }

  .label {
    margin: 0;
    font-size: 12px;
  }

  .chips {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }

  .chips button {
    text-align: left;
    overflow: hidden;
    white-space: nowrap;
    text-overflow: ellipsis;
  }

  .row {
    display: flex;
    align-items: center;
    margin-top: 4px;
  }

  .grow {
    flex: 1;
  }

  .danger:hover {
    color: var(--accent-bright);
    border-color: var(--accent-bright);
  }
</style>
