<script lang="ts">
  interface Props {
    onsettings: () => void
    onclose: () => void
  }
  let { onsettings, onclose }: Props = $props()
</script>

<header data-tauri-drag-region>
  <span class="grow" data-tauri-drag-region></span>
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
