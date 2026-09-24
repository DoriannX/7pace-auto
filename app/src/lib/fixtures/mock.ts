// Collecteur fictif pour itérer sur l'interface dans un navigateur (pnpm dev).
import pendingSeed from './pending.json'
import currentSeed from './current.json'
import settingsSeed from './settings.json'
import type { DayReview, Entry, Settings } from '../types'
import { dateKey, minuteOfDay, toMinutes, toTime } from '../time'

let settings: Settings = structuredClone(settingsSeed)
let calendarConfigured = false
let pending: Entry[] = structuredClone(pendingSeed.entries)
let pendingCount = pendingSeed.pending
let quickStart: number | null = null
const today: Entry[] = structuredClone(currentSeed.entries)

const yesterday = () => dateKey(new Date(Date.now() - 86_400_000))

function review(date: string, entries: Entry[]): DayReview {
  const spans = entries.map((entry) => [toMinutes(entry.start), toMinutes(entry.end)] as const)
  return {
    date,
    entries: structuredClone(entries).sort((left, right) => toMinutes(left.start) - toMinutes(right.start)),
    holes: holes(spans),
    overlaps: overlaps(spans),
    totalMinutes: spans.reduce((sum, [start, end]) => sum + end - start, 0),
    plannedMinutes: settings.workWindows.reduce((sum, [start, end]) => sum + end - start, 0),
    unassigned: entries.filter((entry) => !entry.workItem).length,
    locked: entries.filter((entry) => entry.sentAt).length,
  }
}

function holes(spans: (readonly [number, number])[]): number[][] {
  const free: number[][] = []
  for (const [from, to] of settings.workWindows) {
    const covered = spans
      .map(([start, end]) => [Math.max(start, from), Math.min(end, to)])
      .filter(([start, end]) => end > start)
      .sort((left, right) => left[0] - right[0])
    let cursor = from
    for (const [start, end] of covered) {
      if (start > cursor) free.push([cursor, start])
      cursor = Math.max(cursor, end)
    }
    if (cursor < to) free.push([cursor, to])
  }
  return free
}

function overlaps(spans: (readonly [number, number])[]): number[][] {
  const found: number[][] = []
  spans.forEach(([start, end], index) => {
    for (const [otherStart, otherEnd] of spans.slice(index + 1)) {
      const from = Math.max(start, otherStart)
      const to = Math.min(end, otherEnd)
      if (to > from) found.push([from, to])
    }
  })
  return found
}

function refuse(message: string): never {
  throw Object.assign(new Error(message), { kind: 'domain' })
}

function current() {
  const now = new Date()
  const minute = minuteOfDay(now)
  const git = today.find((entry) => entry.source === 'git')
  if (git) git.end = toTime(Math.max(toMinutes(git.start) + 1, minute))
  const entries = [...today]
  if (quickStart !== null) {
    entries.push({ id: 99, start: toTime(quickStart), end: toTime(Math.max(quickStart + 1, minute)), workItem: null, label: 'Hors ticket · à attribuer', source: 'quick' })
  }
  return {
    ...review(dateKey(now), entries),
    readOnly: true,
    notice: 'Journée en cours : consultation seule, elle se corrige et s’envoie demain matin.',
    tracking: { ...currentSeed.tracking, quickRunning: quickStart !== null },
    health: {
      observedAt: now.toISOString(),
      branchAt: now.toISOString(),
      writtenAt: now.toISOString(),
      spanDate: dateKey(now),
      spanStart: git ? toMinutes(git.start) : null,
      spanEnd: minute,
      error: null,
      errorAt: null,
      gitAvailable: true,
      staleAfterSeconds: 90,
    },
    pending: pendingCount,
  }
}

const handlers: Record<string, (params: Record<string, unknown>) => unknown> = {
  bootstrap: () => ({ today: dateKey(new Date()), pending: pendingCount }),
  currentDay: current,
  pendingDay: () =>
    pendingCount === 0
      ? { date: null, pending: 0, message: 'Aucune journée à envoyer : tout est à jour.' }
      : { ...review(yesterday(), pending), pending: pendingCount, azure: { resolved: 0, failed: 0, message: null } },
  saveEntry: (params) => {
    const incoming = params.entry as Entry
    if (toMinutes(incoming.end) <= toMinutes(incoming.start)) refuse('La fin doit être après le début du créneau.')
    if (incoming.id == null) {
      pending.push({ ...incoming, id: Math.max(0, ...pending.map((entry) => entry.id ?? 0)) + 1, source: 'manual' })
    } else {
      pending = pending.map((entry) => (entry.id === incoming.id ? { ...entry, ...incoming, source: 'manual' } : entry))
    }
    return review(yesterday(), pending)
  },
  deleteEntry: (params) => {
    pending = pending.filter((entry) => entry.id !== params.id)
    return review(yesterday(), pending)
  },
  setQuick: (params) => {
    quickStart = params.running ? minuteOfDay(new Date()) : null
    return { tracking: current().tracking }
  },
  submitDay: () => {
    if (pending.some((entry) => !entry.workItem)) refuse('Des créneaux sont encore à attribuer.')
    pendingCount -= 1
    pending = structuredClone(pendingSeed.entries)
    return { ok: true, closed: true, sent: [], message: 'Journée envoyée dans 7pace.', pending: pendingCount }
  },
  discardDay: () => {
    pendingCount -= 1
    pending = structuredClone(pendingSeed.entries)
    return { message: 'Journée ignorée : rien n’a été envoyé dans 7pace.', pending: pendingCount }
  },
  loadSettings: () => ({ settings, connections: { sevenpace: { status: 'ready', label: '7pace · authentifié, droits d’écriture non prouvés' } }, configured: true, calendarConfigured }),
  saveSettings: (params) => {
    settings = params.settings as Settings
    return handlers.loadSettings(params)
  },
  saveToken: () => ({ connections: { sevenpace: { status: 'ready', label: '7pace · authentifié' } } }),
  saveCalendarLink: (params) => {
    calendarConfigured = Boolean(params.link)
    return { calendarConfigured }
  },
  probeCalendar: () => ({ ok: true, message: 'Calendrier accessible : 2 créneaux occupés aujourd’hui.' }),
  probeRepo: () => ({ ok: true, branch: 'feature/48400-export-pdf', ticket: 48400, message: 'Dépôt lu : branche feature/48400-export-pdf.' }),
  probeAzure: () => ({ ok: true, message: 'Organisation joignable.' }),
  probeToken: () => ({ ok: true, message: 'Jeton accepté.' }),
  checkUpdate: () => ({ available: false, current: '2.0.0', latest: '2.0.0', notes: null, error: null }),
  applyUpdate: () => ({ ok: false, message: 'Aucune mise à jour à installer.' }),
  'agent.stop': () => ({ ok: true, message: 'Suivi en arrière-plan arrêté : plus aucune minute n’est collectée.' }),
  'agent.start': () => ({ message: 'Suivi en arrière-plan relancé.' }),
}

export async function mock(method: string, params: Record<string, unknown>): Promise<unknown> {
  await new Promise((resolve) => setTimeout(resolve, 120))
  const handler = handlers[method]
  if (!handler) refuse(`Méthode inconnue : ${method}.`)
  return structuredClone(handler(params))
}
