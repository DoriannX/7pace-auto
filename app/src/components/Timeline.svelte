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
    onselect: (id: number | null) => void
    onchange: (entry: Entry, span: Span) => void
    oncreate: (span: Span) => void
  }
  let { entries, holes, windows, range, editable, selected, onselect, onchange, oncreate }: Props = $props()

  type Kind = 'move' | 'start' | 'end' | 'create'
  interface Drag {
    kind: Kind
    entry: Entry | null
    origin: number
    span: Span
    preview: Span | null
  }

  const LANE = 46
  const EDGE = 7
  const TONES = ['var(--amber)', 'var(--magenta)', 'var(--blue)', 'var(--cyan)', 'var(--success)', 'var(--info)', 'var(--amber-bright)']

  let track = $state<HTMLDivElement>()
  let drag = $state<Drag | null>(null)

  const layout = $derived(lanes(entries))
  const ticks = $derived(hourTicks(range))
  const rows = $derived(Math.max(1, layout.count))

  const pct = (minute: number) => ((minute - range.start) / (range.end - range.start)) * 100
  const keyOf = (entry: Entry, index: number) => entry.id ?? -index - 1
  const spanOf = (entry: Entry): Span => ({ start: toMinutes(entry.start), end: toMinutes(entry.end) })
  const shown = (entry: Entry): Span => (drag?.entry && drag.entry.id === entry.id && drag.preview ? drag.preview : spanOf(entry))
  const tone = (entry: Entry) => (isUnassigned(entry) ? 'var(--accent-bright)' : TONES[(entry.workItem as number) % TONES.length])

  function minuteAt(x: number): number {
    const box = track!.getBoundingClientRect()
    return range.start + ((x - box.left) / box.width) * (range.end - range.start)
  }

  function press(event: PointerEvent) {
    if (event.button !== 0 || !editable) return
    const target = event.target as HTMLElement
    if (target.closest('[data-hole]')) return
    const origin = minuteAt(event.clientX)
    const block = target.closest<HTMLElement>('[data-id]')
    if (block) {
      const entry = entries.find((item) => String(item.id) === block.dataset.id)
      if (!entry || entry.sentAt) return
      const box = block.getBoundingClientRect()
      const x = event.clientX - box.left
      const kind: Kind = x < EDGE ? 'start' : x > box.width - EDGE ? 'end' : 'move'
      drag = { kind, entry, origin, span: spanOf(entry), preview: null }
    } else {
      drag = { kind: 'create', entry: null, origin, span: { start: origin, end: origin }, preview: null }
    }
    track!.setPointerCapture(event.pointerId)
  }

  function follow(event: PointerEvent) {
    if (!drag) return
    const minute = minuteAt(event.clientX)
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

<div class="timeline">
  <div class="ticks">
    {#each ticks as tick (tick)}
      <span style="left: {pct(tick)}%">{tick / 60}h</span>
    {/each}
  </div>
  <div
    bind:this={track}
    class="track"
    class:editable
    style="height: {rows * LANE + 14}px"
    role="presentation"
    onpointerdown={press}
    onpointermove={follow}
    onpointerup={release}
    onpointercancel={() => (drag = null)}
  >
    {#each ticks as tick (tick)}
      <i class="grid" style="left: {pct(tick)}%"></i>
    {/each}

    {#each windows as [from, to] (from)}
      <i class="window" style="left: {pct(from)}%; width: {pct(to) - pct(from)}%"></i>
    {/each}

    {#each entries as entry, index (keyOf(entry, index))}
      {@const span = shown(entry)}
      <div
        class="block"
        data-id={entry.id}
        class:unassigned={isUnassigned(entry)}
        class:locked={!!entry.sentAt}
        class:selected={selected !== null && selected === entry.id}
        style="left: {pct(span.start)}%; width: {pct(span.end) - pct(span.start)}%; top: {(layout.lane.get(keyOf(entry, index)) ?? 0) * LANE}px; --tone: {tone(entry)}"
        title="{toTime(span.start)}–{toTime(span.end)} · {duration(span.end - span.start)}{entry.label ? ' · ' + entry.label : ''}{entry.sentAt ? ' · envoyé' : ''}"
      >
        <span class="line"><b>{isUnassigned(entry) ? 'à attribuer' : `#${entry.workItem}`}</b> <span class="label">{entry.label}</span></span>
        <small>{toTime(span.start)}–{toTime(span.end)}</small>
      </div>
    {/each}

    {#if drag?.kind === 'create' && drag.preview}
      <div
        class="ghost"
        style="left: {pct(drag.preview.start)}%; width: {pct(drag.preview.end) - pct(drag.preview.start)}%; height: {rows * LANE - 6}px"
      >
        <small>{toTime(drag.preview.start)}–{toTime(drag.preview.end)}</small>
      </div>
    {/if}

    {#each holes as hole (hole[0])}
      <button
        class="hole"
        data-hole
        aria-label="Trou de {toTime(hole[0])} à {toTime(hole[1])}"
        title="Trou {toTime(hole[0])}–{toTime(hole[1])}{editable ? ' · clic pour le combler' : ''}"
        style="left: {pct(hole[0])}%; width: {pct(hole[1]) - pct(hole[0])}%; top: {rows * LANE}px"
        onclick={() => editable && oncreate({ start: hole[0], end: hole[1] })}
      ></button>
    {/each}
  </div>
</div>

<style>
  .ticks {
    position: relative;
    height: 20px;
    color: var(--text-dim);
    font-size: 11px;
  }

  .ticks span {
    position: absolute;
    transform: translateX(-50%);
  }

  .track {
    position: relative;
    touch-action: none;
  }

  .track.editable {
    cursor: crosshair;
  }

  .grid {
    position: absolute;
    top: 0;
    bottom: 0;
    width: 1px;
    background: var(--border);
  }

  /* Horaires de travail : un fond à peine plus clair, la pause reste dans le noir. */
  .window {
    position: absolute;
    top: 0;
    bottom: 12px;
    background: rgba(232, 221, 217, 0.035);
    pointer-events: none;
  }

  .block {
    position: absolute;
    height: 40px;
    padding: 3px 8px;
    display: flex;
    flex-direction: column;
    overflow: hidden;
    white-space: nowrap;
    font-size: 12px;
    line-height: 1.35;
    background: var(--bg-raised);
    border: 1px solid var(--border-strong);
    border-left: 3px solid var(--tone);
    transition: border-color 0.1s;
  }

  .editable .block {
    cursor: grab;
  }

  .editable .block::before,
  .editable .block::after {
    content: '';
    position: absolute;
    top: 0;
    bottom: 0;
    width: 7px;
    cursor: ew-resize;
  }

  .block::before {
    left: 0;
  }

  .block::after {
    right: 0;
  }

  .line {
    overflow: hidden;
    text-overflow: ellipsis;
  }

  .block b {
    color: var(--tone);
    font-weight: 600;
  }

  .label {
    color: var(--text-muted);
  }

  .block small {
    color: var(--text-dim);
    font-size: 11px;
  }

  .block.unassigned {
    background: transparent;
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
    top: 0;
    padding: 3px 8px;
    border: 1px dashed var(--text-muted);
    background: rgba(232, 221, 217, 0.04);
    pointer-events: none;
  }

  .hole {
    position: absolute;
    height: 12px;
    padding: 0;
    border: none;
    border-bottom: 3px solid var(--amber);
    background: transparent;
    cursor: default;
  }

  .editable .hole {
    cursor: pointer;
  }

  .editable .hole:hover {
    background: rgba(200, 106, 50, 0.18);
  }
</style>
