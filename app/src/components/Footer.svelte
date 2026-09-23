<script lang="ts">
  import { duration } from '../lib/time'
  import HoldButton from './HoldButton.svelte'

  interface Props {
    total: number
    planned: number
    readOnly: boolean
    block: string | null
    busy: boolean
    status: string
    onsubmit: () => void
    ondiscard: () => void
  }
  let { total, planned, readOnly, block, busy, status, onsubmit, ondiscard }: Props = $props()
</script>

<footer>
  <span class="total">{duration(total)} <span class="dim">/ {duration(planned)}</span></span>
  <span class="grow"></span>
  {#if readOnly}
    <span class="muted">{status}</span>
  {:else}
    {#if block}
      <span class="muted">{block}</span>
    {/if}
    <HoldButton label="Ignorer" title="Maintenir pour ignorer la journée : rien ne part dans 7pace" disabled={busy} onhold={ondiscard} />
    <HoldButton label="Envoyer" tone="accent" title="Maintenir pour envoyer la journée dans 7pace" disabled={busy || block !== null} onhold={onsubmit} />
  {/if}
</footer>

<style>
  footer {
    height: 46px;
    flex: none;
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 0 14px;
    border-top: 1px solid var(--border);
  }

  .total {
    font-weight: 600;
    font-variant-numeric: tabular-nums;
  }

  .grow {
    flex: 1;
  }
</style>
