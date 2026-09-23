import type { CurrentDay, DayReview, Entry } from './types'
import { minuteOfDay, toMinutes } from './time'

const TRACKING_LABELS: Record<string, string> = {
  running: 'en cours',
  'outside-hours': 'hors horaires',
  'git-unreadable': 'lecture Git en échec',
  'no-repo': 'dépôt introuvable',
}

export function trackingLabel(state: string): string {
  return TRACKING_LABELS[state] ?? state
}

export function isUnassigned(entry: Entry): boolean {
  return entry.workItem == null || entry.workItem < 1
}

/** Motif qui empêche l'envoi, ou null quand la journée peut partir. */
export function submitBlock(day: DayReview, quickRunning: boolean): string | null {
  const open = day.entries.filter((entry) => !entry.sentAt)
  if (open.length === 0) return 'Rien à envoyer'
  const unassigned = open.filter(isUnassigned).length
  if (unassigned === 1) return '1 créneau sans ticket'
  if (unassigned > 1) return `${unassigned} créneaux sans ticket`
  if (quickRunning) return 'Chrono hors ticket en cours'
  return null
}

/** Tickets déjà présents dans la journée, proposés en un clic. */
export function suggestions(entries: Entry[], current: Entry, limit = 6): { workItem: number; label: string }[] {
  const seen = new Map<number, string>()
  for (const entry of entries) {
    if (isUnassigned(entry) || entry.workItem === current.workItem) continue
    const item = entry.workItem as number
    if (!seen.has(item)) seen.set(item, entry.label)
  }
  return [...seen].slice(0, limit).map(([workItem, label]) => ({ workItem, label }))
}

/** Minutes écoulées sur ce qui est compté en ce moment : chrono rapide, sinon créneau Git ouvert. */
export function chronoMinutes(day: CurrentDay, now: Date): number | null {
  const minute = minuteOfDay(now)
  if (day.tracking.quickRunning) {
    const quick = day.entries
      .filter((entry) => entry.source === 'quick' && !entry.sentAt)
      .sort((left, right) => toMinutes(right.start) - toMinutes(left.start))[0]
    return quick ? Math.max(0, minute - toMinutes(quick.start)) : null
  }
  const { spanStart, spanDate } = day.health
  if (spanStart == null || spanDate !== day.date) return null
  return Math.max(0, minute - spanStart)
}

export type Tone = 'ok' | 'idle' | 'alert'

export interface WidgetStatus {
  tone: Tone
  text: string
  chrono: number | null
}

/** Au-delà, un collecteur qui ne répond plus est annoncé injoignable. */
export const SILENCE_MS = 10_000

export function widgetStatus(day: CurrentDay | null, lastOk: number | null, now: Date, error: string | null): WidgetStatus {
  if (!day && lastOk === null && error === null) {
    return { tone: 'idle', text: 'Connexion au collecteur…', chrono: null }
  }
  if (!day || lastOk === null || now.getTime() - lastOk > SILENCE_MS) {
    return { tone: 'alert', text: error ?? 'Collecteur injoignable', chrono: null }
  }
  const { tracking, health } = day
  if (health.observedAt && now.getTime() - Date.parse(health.observedAt) > health.staleAfterSeconds * 1000) {
    return { tone: 'alert', text: 'Suivi figé : aucun relevé récent', chrono: null }
  }
  if (tracking.quickRunning) {
    return { tone: 'ok', text: 'Hors ticket · à attribuer demain', chrono: chronoMinutes(day, now) }
  }
  switch (tracking.state) {
    case 'running':
      return { tone: 'ok', text: ticket(tracking.workItem, tracking.label), chrono: chronoMinutes(day, now) }
    case 'outside-hours':
      return { tone: 'idle', text: 'Hors horaires', chrono: null }
    default:
      return { tone: 'alert', text: tracking.label || trackingLabel(tracking.state), chrono: null }
  }
}

function ticket(workItem: number | null, label: string): string {
  if (!workItem) return label
  const tag = `#${workItem}`
  return label.includes(tag) ? label : `${tag} ${label}`.trim()
}

/** La fenêtre s'ouvre seule quand une journée se met à attendre, une fois par date. */
export function shouldAutoOpen(previous: number | null, pending: number, today: string, lastOpened: string | null): boolean {
  if (pending <= 0 || lastOpened === today) return false
  return previous === null || previous === 0
}
