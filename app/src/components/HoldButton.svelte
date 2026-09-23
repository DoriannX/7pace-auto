<script lang="ts">
  interface Props {
    label: string
    onhold: () => void
    disabled?: boolean
    tone?: 'accent' | 'muted'
    ms?: number
    title?: string
  }
  let { label, onhold, disabled = false, tone = 'muted', ms = 1000, title = 'Maintenir pour confirmer' }: Props = $props()

  let progress = $state(0)
  let frame = 0
  let started = 0

  function press(event: PointerEvent) {
    if (disabled || event.button !== 0) return
    started = performance.now()
    frame = requestAnimationFrame(step)
  }

  function step(time: number) {
    progress = Math.min(1, (time - started) / ms)
    if (progress >= 1) {
      release()
      onhold()
      return
    }
    frame = requestAnimationFrame(step)
  }

  function release() {
    cancelAnimationFrame(frame)
    progress = 0
  }
</script>

<button
  class="hold {tone}"
  {disabled}
  {title}
  style="--progress: {progress}"
  onpointerdown={press}
  onpointerup={release}
  onpointerleave={release}
  onpointercancel={release}
>
  <span>{label}</span>
</button>

<style>
  .hold {
    position: relative;
    overflow: hidden;
  }

  .hold::before {
    content: '';
    position: absolute;
    inset: 0;
    transform-origin: left;
    transform: scaleX(var(--progress));
    background: var(--fill);
  }

  .hold span {
    position: relative;
  }

  .accent {
    --fill: var(--accent-dim);
    border-color: var(--accent);
    font-weight: 600;
  }

  .accent:disabled {
    border-color: var(--border-strong);
    font-weight: normal;
  }

  .muted {
    --fill: var(--border-strong);
    color: var(--text-muted);
  }
</style>
