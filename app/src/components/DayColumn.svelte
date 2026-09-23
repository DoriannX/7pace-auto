<script lang="ts">
  import type { Entry } from '../lib/types'
  import { duration, toMinutes, toTime } from '../lib/time'
  import { DRAG_THRESHOLD, hourTicks, lanes, moveSpan, resizeSpan, spanFromDrag, type Range, type Span } from '../lib/timeline'
  import { isUnassigned } from '../lib/state'

  interface Props {
    entries: Entry[]
    holes: number[][]
    windows: number[][]
    range: Range
    editable: boolean
    selected: number | null
    now: number | null
    onselect: (id: number | null) => void
    onchange: (entry: Entry, span: Span) => void
    oncreate: (span: Span) => void
  }
  let { entries, holes, windows, range, editable, selected, now, onselect, onchange, oncreate }: Props = $props()

  type Kind = 'move' | 'start' | 'end' | 'create'
  interface Drag {
    kind: Kind
    entry: Entry | null
    origin: number
    span: Span
    preview: Span | null
  }

  const EDGE = 6
  const TONES = ['var(--amber)', 'var(--magenta)', 'var(--blue)', 'var(--cyan)', 'var(--success)', 'var(--info)', 'var(--amber-bright)']

  let area = $state<HTMLDivElement>()
  let drag = $state<Drag | null>(null)

  const layout = $derived(lanes(entries))
  const ticks = $derived(hourTicks(range))

  const pos = (minute: number) => ((minute - range.start) / (range.end - range.start)) * 100
  const keyOf = (entry: Entry, index: number) => entry.id ?? -index - 1
  const spanOf = (entry: Entry): Span => ({ start: toMinutes(entry.start), end: toMinutes(entry.end) })
  const shown = (entry: Entry): Span => (drag?.entry && drag.entry.id === entry.id && drag.preview ? drag.preview : spanOf(entry))
  const tone = (entry: Entry) => (isUnassigned(entry) ? 'var(--accent-bright)' : TONES[(entry.workItem as number) % TONES.length])

  function minuteAt(y: number): number {
    const box = area!.getBoundingClientRect()
    return range.start + ((y - box.top) / box.height) * (range.end - range.start)
  }

  function press(event: PointerEvent) {
    if (event.button !== 0 || !editable) return
    const target = event.target as HTMLElement
    if (target.closest('[data-hole]')) return
    const origin = minuteAt(event.clientY)
    const block = target.closest<HTMLElement>('[data-id]')
    if (block) {
      const entry = entries.find((item) => String(item.id) === block.dataset.id)
      if (!entry || entry.sentAt) return
      const box = block.getBoundingClientRect()
      const y = event.clientY - box.top
      const kind: Kind = y < EDGE ? 'start' : y > box.height - EDGE ? 'end' : 'move'
      drag = { kind, entry, origin, span: spanOf(entry), preview: null }
    } else {
      drag = { kind: 'create', entry: null, origin, span: { start: origin, end: origin }, preview: null }
    }
    area!.setPointerCapture(event.pointerId)
  }

  function follow(event: PointerEvent) {
    if (!drag) return
    const minute = minuteAt(event.clientY)
    if (!drag.preview && Math.abs(minute - drag.origin) < DRAG_THRESHOLD) return
    if (drag.kind === 'move') drag.preview = moveSpan(drag.span, minute - drag.origin, range)
    else if (drag.kind === 'create') drag.preview = spanFromDrag(drag.origin, minute, range)
    else drag.preview = resizeSpan(drag.span, drag.kind, minute, range)
  }

  function release() {
    const done = drag
    drag = null
    if (!done) return
    const { preview, span, entry } = done
    if (!preview || (preview.start === span.start && preview.end === span.end)) {
      onselect(entry?.id ?? null)
      return
    }
    if (entry) onchange(entry, preview)
    else oncreate(preview)
  }
</script>

<div class="day">
  <div class="hours">
    {#each ticks as tick (tick)}
      <span style="top: {pos(tick)}%">{tick / 60}h</span>
    {/each}
  </div>
  <div
    bind:this={area}
    class="area"
    class:editable
    role="presentation"
    onpointerdown={press}
    onpointermove={follow}
    onpointerup={release}
    onpointercancel={() => (drag = null)}
  >
    {#each windows as [from, to] (from)}
      <i class="window" style="top: {pos(from)}%; height: {pos(to) - pos(from)}%"></i>
    {/each}
    {#each ticks as tick (tick)}
      <i class="grid" style="top: {pos(tick)}%"></i>
    {/each}

    {#each holes as hole (hole[0])}
      <button
        class="hole"
        data-hole
        style="top: {pos(hole[0])}%; height: {pos(hole[1]) - pos(hole[0])}%"
        title="{toTime(hole[0])}–{toTime(hole[1])} non couvert{editable ? ' · cliquer pour combler' : ''}"
        onclick={() => editable && oncreate({ start: hole[0], end: hole[1] })}
      ></button>
    {/each}

    {#each entries as entry, index (keyOf(entry, index))}
      {@const span = shown(entry)}
      {@const key = keyOf(entry, index)}
      {@const columns = layout.columns.get(key) ?? 1}
      <div
        class="block"
        data-id={entry.id}
        class:unassigned={isUnassigned(entry)}
        class:locked={!!entry.sentAt}
        class:selected={selected !== null && selected === entry.id}
        style="top: {pos(span.start)}%; height: {pos(span.end) - pos(span.start)}%; left: calc({layout.lane.get(key) ?? 0} * 100% / {columns}); width: calc(100% / {columns} - 3px); --tone: {tone(entry)}"
        title="{toTime(span.start)}–{toTime(span.end)} · {duration(span.end - span.start)}{entry.label ? ' · ' + entry.label : ''}{entry.sentAt ? ' · envoyé' : ''}"
      >
        <span class="line"><b>{isUnassigned(entry) ? 'sans ticket' : `#${entry.workItem}`}</b> {entry.label}</span>
        <small>{toTime(span.start)}–{toTime(span.end)} · {duration(span.end - span.start)}</small>
      </div>
    {/each}

    {#if drag?.kind === 'create' && drag.preview}
      <div class="ghost" style="top: {pos(drag.preview.start)}%; height: {pos(drag.preview.end) - pos(drag.preview.start)}%">
        <small>{toTime(drag.preview.start)}–{toTime(drag.preview.end)}</small>
      </div>
    {/if}

    {#if now !== null && now > range.start && now < range.end}
      <i class="now" style="top: {pos(now)}%"></i>
    {/if}
  </div>
</div>

<style>
  .day {
    height: 100%;
    min-height: 420px;
    display: grid;
    grid-template-columns: 40px 1fr;
  }

  .hours {
    position: relative;
    color: var(--text-dim);
    font-size: 11px;
  }

  .hours span {
    position: absolute;
    right: 8px;
    transform: translateY(-50%);
  }

  .area {
    position: relative;
    touch-action: none;
  }

  .area.editable {
    cursor: crosshair;
  }

  .window {
    position: absolute;
    left: 0;
    right: 0;
    background: rgba(232, 221, 217, 0.03);
    pointer-events: none;
  }

  .grid {
    position: absolute;
    left: 0;
    right: 0;
    height: 1px;
    background: var(--border);
    pointer-events: none;
  }

  .hole {
    position: absolute;
    left: 0;
    right: 0;
    padding: 0;
    border: 1px dashed var(--amber);
    background: rgba(200, 106, 50, 0.06);
    cursor: default;
  }

  .editable .hole {
    cursor: pointer;
  }

  .editable .hole:hover {
    background: rgba(200, 106, 50, 0.16);
  }

  .block {
    position: absolute;
    min-height: 16px;
    padding: 2px 8px;
    display: flex;
    flex-direction: column;
    overflow: hidden;
    white-space: nowrap;
    font-size: 12px;
    line-height: 1.35;
    background: var(--bg-raised);
    border: 1px solid var(--border-strong);
    border-left: 3px solid var(--tone);
  }

  .editable .block {
    cursor: grab;
  }

  .editable .block::before,
  .editable .block::after {
    content: '';
    position: absolute;
    left: 0;
    right: 0;
    height: 6px;
    cursor: ns-resize;
  }

  .block::before {
    top: 0;
  }

  .block::after {
    bottom: 0;
  }

  .line {
    overflow: hidden;
    text-overflow: ellipsis;
    color: var(--text-muted);
  }

  .line b {
    color: var(--tone);
    font-weight: 600;
  }

  .block small {
    color: var(--text-dim);
    font-size: 11px;
  }

  .block.unassigned {
    background: rgba(240, 69, 74, 0.08);
    border-color: var(--accent-bright);
  }

  .block.selected {
    border-color: var(--text);
    border-left-color: var(--tone);
  }

  .block.locked {
    opacity: 0.45;
    cursor: default;
  }

  .block.locked::before,
  .block.locked::after {
    display: none;
  }

  .ghost {
    position: absolute;
    left: 0;
    right: 0;
    padding: 2px 8px;
    border: 1px dashed var(--text-muted);
    background: rgba(232, 221, 217, 0.05);
    pointer-events: none;
  }

  .now {
    position: absolute;
    left: -6px;
    right: 0;
    height: 1px;
    background: var(--accent-bright);
    pointer-events: none;
  }
</style>
