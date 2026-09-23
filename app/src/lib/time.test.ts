import { describe, expect, it } from 'vitest'
import { chrono, duration, frenchDate, snap, toMinutes, toTime } from './time'

describe('heures', () => {
  it('lit et écrit HH:MM', () => {
    expect(toMinutes('08:30')).toBe(510)
    expect(toMinutes('8:05')).toBe(485)
    expect(toMinutes('24:00')).toBeNaN()
    expect(toMinutes('8h30')).toBeNaN()
    expect(toTime(510)).toBe('08:30')
  })

  it('aimante au pas de 5 minutes', () => {
    expect(snap(512)).toBe(510)
    expect(snap(513)).toBe(515)
    expect(snap(517.4)).toBe(515)
  })

  it('formate durées, chrono et dates', () => {
    expect(duration(425)).toBe('7h05')
    expect(chrono(102)).toBe('1:42')
    expect(frenchDate('2026-09-22')).toBe('mardi 22 septembre')
    expect(frenchDate('2026-10-01')).toBe('jeudi 1er octobre')
  })
})
