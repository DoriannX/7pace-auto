import type { Entry } from './types'
import { STEP, snap, toMinutes } from './time'

export interface Range {
  start: number
  end: number
}

export interface Span {
  start: number
  end: number
}

/** En deçà, un geste est un clic et non un glissement. */
export const DRAG_THRESHOLD = 2

/** Horaires configurés, élargis aux créneaux qui en sortent, avec une marge de chaque côté. */
export function visibleRange(windows: number[][], entries: Entry[], margin = 30): Range {
  let start = Number.POSITIVE_INFINITY
  let end = Number.NEGATIVE_INFINITY
  for (const [from, to] of windows) {
    start = Math.min(start, from)
    end = Math.max(end, to)
  }
  for (const entry of entries) {
    start = Math.min(start, toMinutes(entry.start))
    end = Math.max(end, toMinutes(entry.end))
  }
  if (!Number.isFinite(start) || !Number.isFinite(end)) {
    start = 8 * 60 + 30
    end = 17 * 60
  }
  return { start: Math.max(0, start - margin), end: Math.min(24 * 60, end + margin) }
}

/** Heures pleines comprises dans la plage, pour la graduation. */
export function hourTicks(range: Range): number[] {
  const ticks: number[] = []
  for (let minute = Math.ceil(range.start / 60) * 60; minute <= range.end; minute += 60) ticks.push(minute)
  return ticks
}

/**
 * Répartit les créneaux en voies : deux créneaux qui se chevauchent ne partagent jamais
 * une voie. Premier ajustement, dans l'ordre des débuts. `columns` donne, pour chaque
 * créneau, le nombre de voies de son groupe de chevauchements : un créneau seul garde
 * toute la largeur.
 */
export function lanes(entries: Entry[]): { lane: Map<number, number>; columns: Map<number, number>; count: number } {
  const ordered = entries
    .map((entry, index) => ({ key: entry.id ?? -index - 1, start: toMinutes(entry.start), end: toMinutes(entry.end) }))
    .sort((left, right) => left.start - right.start || left.end - right.end)
  const lane = new Map<number, number>()
  const columns = new Map<number, number>()
  let ends: number[] = []
  let group: number[] = []
  let groupEnd = Number.NEGATIVE_INFINITY
  let count = 0
  const closeGroup = () => {
    for (const key of group) columns.set(key, ends.length)
    count = Math.max(count, ends.length)
    ends = []
    group = []
  }
  for (const item of ordered) {
    if (group.length && item.start >= groupEnd) closeGroup()
    let index = ends.findIndex((end) => end <= item.start)
    if (index < 0) {
      index = ends.length
      ends.push(item.end)
    } else {
      ends[index] = item.end
    }
    lane.set(item.key, index)
    groupEnd = group.length ? Math.max(groupEnd, item.end) : item.end
    group.push(item.key)
  }
  if (group.length) closeGroup()
  return { lane, columns, count }
}

/** Glissement dans le vide : null pour un simple clic, sinon un créneau d'au moins un pas. */
export function spanFromDrag(from: number, to: number, range: Range, step = STEP): Span | null {
  if (Math.abs(to - from) < DRAG_THRESHOLD) return null
  let start = clamp(snap(Math.min(from, to), step), range.start, range.end)
  let end = clamp(snap(Math.max(from, to), step), range.start, range.end)
  if (end - start < step) {
    if (start + step <= range.end) end = start + step
    else start = end - step
  }
  return { start, end }
}

/** Déplacement d'un bloc entier : sa durée est gardée, son début s'aimante. */
export function moveSpan(span: Span, delta: number, range: Range, step = STEP): Span {
  const length = span.end - span.start
  const start = clamp(snap(span.start + delta, step), range.start, Math.max(range.start, range.end - length))
  return { start, end: start + length }
}

/** Étirement d'un bord : il s'aimante, et le bloc garde au moins un pas. */
export function resizeSpan(span: Span, edge: 'start' | 'end', minute: number, range: Range, step = STEP): Span {
  const target = clamp(snap(minute, step), range.start, range.end)
  if (edge === 'start') return { start: Math.min(target, span.end - step), end: span.end }
  return { start: span.start, end: Math.max(target, span.start + step) }
}

/** Sur la journée en cours, seul le temps écoulé peut manquer : l'après-midi à venir n'est pas un trou. */
export function elapsedHoles(holes: number[][], date: string, now: Date): number[][] {
  const today = `${now.getFullYear()}-${String(now.getMonth() + 1).padStart(2, '0')}-${String(now.getDate()).padStart(2, '0')}`
  const limit = date < today ? 24 * 60 : date > today ? 0 : now.getHours() * 60 + now.getMinutes()
  return holes
    .filter((hole) => hole.length === 2 && hole[0] < limit)
    .map(([start, end]) => [start, Math.min(end, limit)])
    .filter(([start, end]) => end > start)
}

function clamp(value: number, low: number, high: number): number {
  return Math.min(high, Math.max(low, value))
}
