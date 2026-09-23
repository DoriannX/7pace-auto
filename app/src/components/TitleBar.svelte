<script lang="ts">
  interface Props {
    title: string
    extra: number
    toggle: string | null
    ontoggle: () => void
    onsettings: () => void
    onclose: () => void
  }
  let { title, extra, toggle, ontoggle, onsettings, onclose }: Props = $props()

  const others = $derived(extra > 1 ? `${extra} autres journées en attente` : 'Une autre journée en attente')
</script>

<header data-tauri-drag-region>
  <span class="title" data-tauri-drag-region>{title}</span>
  {#if extra > 0}
    <span class="extra" title={others}>+{extra}</span>
  {/if}
  <span class="grow" data-tauri-drag-region></span>
  {#if toggle}
    <button class="ghost" onclick={ontoggle}>{toggle}</button>
  {/if}
  <button class="ghost icon" title="Réglages" onclick={onsettings}>{'\uf013'}</button>
  <button class="ghost icon" title="Replier en widget" onclick={onclose}>{'\uf00d'}</button>
</header>

<style>
  header {
    height: 32px;
    flex: none;
    display: flex;
    align-items: center;
    gap: 6px;
    padding: 0 3px 0 14px;
    border-bottom: 1px solid var(--border);
  }

  .title {
    font-weight: 600;
  }

  .extra {
    color: var(--amber-bright);
  }

  .grow {
    flex: 1;
    align-self: stretch;
  }

  .icon {
    width: 32px;
    height: 26px;
    padding: 0;
  }
</style>
