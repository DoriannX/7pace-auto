import { describe, expect, it } from 'vitest'
import type { Entry } from './types'
import { elapsedHoles, lanes, moveSpan, resizeSpan, spanFromDrag, visibleRange } from './timeline'

const entry = (id: number, start: string, end: string): Entry => ({ id, start, end, workItem: 1, label: '', source: 'git' })
const range = { start: 480, end: 1050 }

describe('frise', () => {
  it('couvre les horaires avec 30 minutes de marge, élargie aux créneaux qui en sortent', () => {
    expect(visibleRange([[510, 750], [810, 1020]], [])).toEqual({ start: 480, end: 1050 })
    expect(visibleRange([[510, 1020]], [entry(1, '07:45', '08:30')])).toEqual({ start: 435, end: 1050 })
  })

  it('transforme un glissement en créneau aimanté, un clic en rien', () => {
    expect(spanFromDrag(601, 602, range)).toBeNull()
    expect(spanFromDrag(612, 548, range)).toEqual({ start: 550, end: 610 })
    expect(spanFromDrag(600, 603, range)).toEqual({ start: 600, end: 605 })
    expect(spanFromDrag(470, 2000, range)).toEqual({ start: 480, end: 1050 })
  })

  it('déplace en gardant la durée et étire en gardant au moins 5 minutes', () => {
    expect(moveSpan({ start: 512, end: 572 }, 31, range)).toEqual({ start: 545, end: 605 })
    expect(moveSpan({ start: 1000, end: 1040 }, 60, range)).toEqual({ start: 1010, end: 1050 })
    expect(resizeSpan({ start: 600, end: 660 }, 'end', 603, range)).toEqual({ start: 600, end: 605 })
    expect(resizeSpan({ start: 600, end: 660 }, 'start', 522, range)).toEqual({ start: 520, end: 660 })
  })

  it('range les chevauchements sur des voies distinctes', () => {
    const { lane, columns, count } = lanes([
      entry(1, '08:30', '10:00'),
      entry(2, '09:30', '10:30'),
      entry(3, '10:00', '11:00'),
      entry(4, '10:15', '10:20'),
      entry(5, '14:00', '15:00'),
    ])
    expect(count).toBe(3)
    expect(lane.get(1)).toBe(0)
    expect(lane.get(2)).toBe(1)
    expect(lane.get(3)).toBe(0)
    expect(lane.get(4)).toBe(2)
    expect(columns.get(1)).toBe(3)
    expect(lane.get(5)).toBe(0)
    expect(columns.get(5)).toBe(1)
  })

  it('ne compte comme trous du jour que le temps déjà écoulé', () => {
    const now = new Date(2026, 8, 23, 11, 0)
    expect(elapsedHoles([[510, 540], [600, 750], [810, 1020]], '2026-09-23', now)).toEqual([[510, 540], [600, 660]])
    expect(elapsedHoles([[810, 1020]], '2026-09-22', now)).toEqual([[810, 1020]])
  })
})
