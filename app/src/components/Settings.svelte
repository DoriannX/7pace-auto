<script lang="ts">
  import { onMount } from 'svelte'
  import { AgentError, appPid, call, describe, quit } from '../lib/api'
  import type { LoadedSettings, Outcome, Probe, Settings, UpdateInfo } from '../lib/types'
  import { toMinutes, toTime } from '../lib/time'
  import HoldButton from './HoldButton.svelte'

  interface Props {
    onclose: () => void
    onsaved: (settings: Settings) => void
  }
  let { onclose, onsaved }: Props = $props()

  type ProbeKey = 'repo' | 'azure' | 'token' | 'calendar'

  let loaded = $state<LoadedSettings | null>(null)
  let repoPath = $state('')
  let organization = $state('')
  let account = $state('')
  let token = $state('')
  let calendarLink = $state('')
  let spans = $state<{ start: string; end: string }[]>([])
  let probes = $state<Record<ProbeKey, Probe | 'pending' | null>>({ repo: null, azure: null, token: null, calendar: null })
  let update = $state<UpdateInfo | 'pending' | null>(null)
  let note = $state<{ text: string; ok: boolean } | null>(null)
  let busy = $state(false)
  let stopped = $state(false)

  onMount(async () => {
    try {
      apply(await call<LoadedSettings>('loadSettings'))
    } catch (failure) {
      stopped = failure instanceof AgentError && failure.kind === 'unreachable'
      fail(failure)
    }
  })

  function apply(result: LoadedSettings) {
    loaded = result
    repoPath = result.settings.repoPath
    organization = result.settings.azureOrganization
    account = result.settings.sevenPaceAccount
    calendarLink = ''
    spans = result.settings.workWindows.map(([start, end]) => ({ start: toTime(start), end: toTime(end) }))
  }

  function fail(failure: unknown) {
    note = { text: describe(failure), ok: false }
  }

  async function probe(key: ProbeKey) {
    probes[key] = 'pending'
    try {
      probes[key] =
        key === 'calendar'
          ? await call<Probe>('probeCalendar', { link: calendarLink.trim() })
          : key === 'repo'
          ? await call<Probe>('probeRepo', { path: repoPath })
          : key === 'azure'
            ? await call<Probe>('probeAzure', { organization })
            : await call<Probe>('probeToken', { account, token: token.trim() || null })
    } catch (failure) {
      probes[key] = null
      fail(failure)
    }
  }

  async function save() {
    if (!loaded) return
    const workWindows = spans.map(({ start, end }) => [toMinutes(start), toMinutes(end)])
    if (workWindows.some(([start, end]) => Number.isNaN(start) || Number.isNaN(end) || end <= start)) {
      note = { text: 'Horaires au format HH:MM, la fin après le début.', ok: false }
      return
    }
    busy = true
    note = null
    try {
      if (token.trim()) {
        await call('saveToken', { token: token.trim() })
        token = ''
      }
      if (calendarLink.trim()) {
        await call('saveCalendarLink', { link: calendarLink.trim() })
        calendarLink = ''
      }
      const saved = await call<LoadedSettings>('saveSettings', {
        settings: { ...loaded.settings, repoPath, azureOrganization: organization, sevenPaceAccount: account, workWindows },
      })
      apply(saved)
      onsaved(saved.settings)
      note = { text: 'Réglages enregistrés.', ok: true }
    } catch (failure) {
      fail(failure)
    } finally {
      busy = false
    }
  }

  async function removeCalendar() {
    busy = true
    try {
      const result = await call<{ calendarConfigured: boolean }>('saveCalendarLink', { link: '' })
      if (loaded) loaded = { ...loaded, calendarConfigured: result.calendarConfigured }
      calendarLink = ''
      probes.calendar = null
      note = { text: 'Lien calendrier retiré.', ok: true }
    } catch (failure) {
      fail(failure)
    } finally {
      busy = false
    }
  }

  async function checkUpdate() {
    update = 'pending'
    try {
      update = await call<UpdateInfo>('checkUpdate')
    } catch (failure) {
      update = null
      fail(failure)
    }
  }

  async function install() {
    busy = true
    try {
      const outcome = await call<Outcome>('applyUpdate', { clientPid: await appPid(), reopenApp: true })
      note = { text: outcome.message, ok: outcome.ok }
      if (outcome.ok) {
        // Le script d'installation attend la sortie du collecteur et de l'app, puis relance les deux.
        await call('agent.stop')
        setTimeout(() => quit(), 1500)
      }
    } catch (failure) {
      fail(failure)
    } finally {
      busy = false
    }
  }

  async function stopAgent() {
    try {
      note = { text: (await call<Outcome>('agent.stop')).message, ok: true }
      stopped = true
    } catch (failure) {
      fail(failure)
    }
  }

  async function startAgent() {
    try {
      note = { text: (await call<Outcome>('agent.start')).message, ok: true }
      stopped = false
      if (!loaded) apply(await call<LoadedSettings>('loadSettings'))
    } catch (failure) {
      fail(failure)
    }
  }
</script>

{#snippet verdict(result: Probe | 'pending' | null)}
  {#if result === 'pending'}
    <p class="verdict dim">vérification…</p>
  {:else if result}
    <p class="verdict" class:ok={result.ok} class:error={!result.ok}>{result.message}</p>
  {/if}
{/snippet}

<aside>
  <div class="body">
    <div class="row">
      <label for="repo">Dépôt Git</label>
      <input id="repo" bind:value={repoPath} spellcheck="false" />
      <button onclick={() => probe('repo')}>Vérifier</button>
    </div>
    {@render verdict(probes.repo)}

    <div class="row">
      <label for="azure">Organisation Azure</label>
      <input id="azure" bind:value={organization} spellcheck="false" placeholder="https://dev.azure.com/…" />
      <button onclick={() => probe('azure')}>Vérifier</button>
    </div>
    {@render verdict(probes.azure)}

    <div class="row">
      <label for="account">Compte 7pace</label>
      <input id="account" bind:value={account} spellcheck="false" />
      <span></span>
    </div>
    <div class="row">
      <label for="token">Jeton 7pace</label>
      <input id="token" type="password" bind:value={token} placeholder="inchangé" />
      <button onclick={() => probe('token')}>Vérifier</button>
    </div>
    {@render verdict(probes.token)}

    <div class="row">
      <label for="calendar">Lien ICS Outlook</label>
      <input id="calendar" type="password" bind:value={calendarLink} placeholder={loaded?.calendarConfigured ? 'lien enregistré' : 'https://…/calendar.ics'} spellcheck="false" />
      <button onclick={() => probe('calendar')}>Vérifier</button>
    </div>
    {@render verdict(probes.calendar)}
    {#if loaded?.calendarConfigured}
      <div class="calendar-remove"><button class="ghost" onclick={removeCalendar} disabled={busy}>Retirer le lien</button></div>
    {/if}

    <div class="row">
      <span class="label">Horaires</span>
      <span class="spans">
        {#each spans as span, index (index)}
          <span class="span">
            <input bind:value={span.start} aria-label="Début de la plage {index + 1}" />–<input bind:value={span.end} aria-label="Fin de la plage {index + 1}" />
          </span>
        {/each}
      </span>
      <span></span>
    </div>

    <div class="actions">
      {#if loaded}
        <span class="muted">{loaded.connections.sevenpace.label}</span>
      {/if}
      <span class="grow"></span>
      <button class="primary" onclick={save} disabled={busy || !loaded}>Enregistrer</button>
    </div>

    <hr />

    <div class="row">
      <span class="label">Version</span>
      <span class="muted">
        {#if update === 'pending'}
          vérification…
        {:else if update?.error}
          <span class="error">{update.error}</span>
        {:else if update?.available}
          {update.latest} disponible (installée : {update.current})
        {:else if update}
          {update.current}, à jour
        {/if}
      </span>
      {#if update && update !== 'pending' && update.available}
        <button onclick={install} disabled={busy}>Installer</button>
      {:else}
        <button onclick={checkUpdate}>Vérifier</button>
      {/if}
    </div>

    <div class="row">
      <span class="label">Collecteur</span>
      <span class="muted">{stopped ? 'arrêté' : 'relève la branche en arrière-plan'}</span>
      {#if stopped}
        <button onclick={startAgent}>Relancer</button>
      {:else}
        <HoldButton label="Arrêter" title="Maintenir pour arrêter complètement le suivi" onhold={stopAgent} />
      {/if}
    </div>

    <div class="actions">
      {#if note}
        <span class:ok={note.ok} class:error={!note.ok}>{note.text}</span>
      {/if}
      <span class="grow"></span>
      <button class="ghost" onclick={() => quit()}>Quitter l’app</button>
      <button class="ghost" onclick={onclose}>Fermer</button>
    </div>
  </div>
</aside>

<style>
  aside {
    position: absolute;
    inset: 0;
    overflow: auto;
    background: var(--bg-deep);
    animation: slide 0.14s ease-out;
  }

  @keyframes slide {
    from {
      transform: translateX(28px);
      opacity: 0;
    }
  }

  .body {
    max-width: 680px;
    padding: 18px 22px;
  }

  .row {
    display: grid;
    grid-template-columns: 150px 1fr 96px;
    align-items: center;
    gap: 10px;
    margin: 10px 0 4px;
  }

  label,
  .label {
    color: var(--text-muted);
  }

  .row button,
  .row :global(.hold) {
    width: 100%;
  }

  .spans {
    display: flex;
    align-items: center;
    gap: 18px;
    flex-wrap: wrap;
  }

  .span {
    display: flex;
    align-items: center;
    gap: 4px;
  }

  .span input {
    width: 7ch;
    text-align: center;
  }

  .verdict {
    margin: 2px 0 0 160px;
    font-size: 12px;
  }

  .calendar-remove {
    margin-left: 160px;
  }

  .actions {
    display: flex;
    align-items: center;
    gap: 10px;
    margin-top: 16px;
  }

  .grow {
    flex: 1;
  }

  .primary {
    border-color: var(--accent);
  }

  hr {
    border: none;
    border-top: 1px solid var(--border);
    margin: 22px 0 12px;
  }
</style>
