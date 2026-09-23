import { describe, expect, it } from 'vitest'
import type { CurrentDay, Entry } from './types'
import { chronoMinutes, shouldAutoOpen, submitBlock, widgetStatus } from './state'

const entry = (patch: Partial<Entry>): Entry => ({ id: 1, start: '08:30', end: '09:00', workItem: 12, label: '', source: 'git', ...patch })

function today(patch: { quick?: boolean; state?: string; spanStart?: number | null; observedAt?: string; entries?: Entry[] } = {}): CurrentDay {
  return {
    date: '2026-09-23',
    entries: patch.entries ?? [],
    holes: [],
    overlaps: [],
    totalMinutes: 0,
    plannedMinutes: 450,
    unassigned: 0,
    readOnly: true,
    notice: '',
    pending: 0,
    tracking: { branch: 'b', bug: null, workItem: 48402, label: 'Export PDF', quickRunning: patch.quick ?? false, state: patch.state ?? 'running' },
    health: {
      observedAt: patch.observedAt ?? new Date(2026, 8, 23, 10, 59, 40).toISOString(),
      branchAt: null,
      writtenAt: null,
      spanDate: '2026-09-23',
      spanStart: patch.spanStart === undefined ? 540 : patch.spanStart,
      spanEnd: null,
      error: null,
      errorAt: null,
      gitAvailable: true,
      staleAfterSeconds: 90,
    },
  }
}

const now = new Date(2026, 8, 23, 11, 0)

describe('envoi', () => {
  it('bloque tant qu’un créneau est à attribuer ou que le chrono rapide tourne', () => {
    const day = (entries: Entry[]) => ({ date: 'd', entries, holes: [], overlaps: [], totalMinutes: 0, plannedMinutes: 0, unassigned: 0 })
    expect(submitBlock(day([entry({ workItem: null }), entry({ id: 2, workItem: null })]), false)).toBe('2 créneaux sans ticket')
    expect(submitBlock(day([entry({})]), true)).toBe('Chrono hors ticket en cours')
    expect(submitBlock(day([entry({ sentAt: 'x' })]), false)).toBe('Rien à envoyer')
    expect(submitBlock(day([entry({}), entry({ id: 2, workItem: null, sentAt: 'x' })]), false)).toBeNull()
  })
})

describe('chrono', () => {
  it('mesure le créneau Git ouvert, ou le dernier chrono rapide quand il tourne', () => {
    expect(chronoMinutes(today(), now)).toBe(120)
    expect(chronoMinutes(today({ spanStart: null }), now)).toBeNull()
    const quick = today({ quick: true, entries: [entry({ source: 'quick', start: '09:00' }), entry({ id: 2, source: 'quick', start: '10:20' })] })
    expect(chronoMinutes(quick, now)).toBe(40)
  })
})

describe('widget', () => {
  it('annonce le ticket, le hors horaires et les pannes', () => {
    expect(widgetStatus(today(), now.getTime(), now, null)).toEqual({ tone: 'ok', text: '#48402 Export PDF', chrono: 120 })
    expect(widgetStatus(today({ state: 'outside-hours' }), now.getTime(), now, null).tone).toBe('idle')
    expect(widgetStatus(today({ state: 'no-repo' }), now.getTime(), now, null).tone).toBe('alert')
    expect(widgetStatus(today({ observedAt: new Date(2026, 8, 23, 10, 50).toISOString() }), now.getTime(), now, null).text).toMatch(/figé/)
  })

  it('passe en injoignable sans réponse depuis plus de 10 s', () => {
    expect(widgetStatus(null, null, now, null).tone).toBe('idle')
    expect(widgetStatus(today(), now.getTime() - 11_000, now, null)).toEqual({ tone: 'alert', text: 'Collecteur injoignable', chrono: null })
    expect(widgetStatus(null, null, now, 'Suivi arrêté').text).toBe('Suivi arrêté')
  })

  it('ouvre la fenêtre une seule fois par date', () => {
    expect(shouldAutoOpen(null, 1, '2026-09-23', null)).toBe(true)
    expect(shouldAutoOpen(0, 2, '2026-09-23', '2026-09-22')).toBe(true)
    expect(shouldAutoOpen(null, 1, '2026-09-23', '2026-09-23')).toBe(false)
    expect(shouldAutoOpen(1, 1, '2026-09-23', null)).toBe(false)
    expect(shouldAutoOpen(null, 0, '2026-09-23', null)).toBe(false)
  })
})
