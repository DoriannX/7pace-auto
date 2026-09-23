export const STEP = 5

/** « 08:30 » vers 510 ; NaN quand la saisie n'est pas une heure valide. */
export function toMinutes(value: string): number {
  const match = /^(\d{1,2}):(\d{2})$/.exec(value.trim())
  if (!match) return Number.NaN
  const hours = Number(match[1])
  const minutes = Number(match[2])
  if (hours > 23 || minutes > 59) return Number.NaN
  return hours * 60 + minutes
}

export function toTime(minutes: number): string {
  const clamped = Math.max(0, Math.min(24 * 60 - 1, Math.round(minutes)))
  return `${pad(Math.floor(clamped / 60))}:${pad(clamped % 60)}`
}

export function snap(minutes: number, step = STEP): number {
  return Math.round(minutes / step) * step
}

/** Durée compacte : « 7h05 ». */
export function duration(minutes: number): string {
  const total = Math.max(0, Math.round(minutes))
  return `${Math.floor(total / 60)}h${pad(total % 60)}`
}

/** Chrono : « 1:42 ». */
export function chrono(minutes: number): string {
  const total = Math.max(0, Math.floor(minutes))
  return `${Math.floor(total / 60)}:${pad(total % 60)}`
}

export function minuteOfDay(moment: Date): number {
  return moment.getHours() * 60 + moment.getMinutes()
}

export function dateKey(moment: Date): string {
  return `${moment.getFullYear()}-${pad(moment.getMonth() + 1)}-${pad(moment.getDate())}`
}

const WEEKDAYS = ['dimanche', 'lundi', 'mardi', 'mercredi', 'jeudi', 'vendredi', 'samedi']
const MONTHS = [
  'janvier', 'février', 'mars', 'avril', 'mai', 'juin',
  'juillet', 'août', 'septembre', 'octobre', 'novembre', 'décembre',
]

/** « lundi 22 septembre » pour « 2026-09-22 ». */
export function frenchDate(key: string): string {
  const [year, month, day] = key.split('-').map(Number)
  const date = new Date(year, month - 1, day)
  return `${WEEKDAYS[date.getDay()]} ${day === 1 ? '1er' : day} ${MONTHS[month - 1]}`
}

function pad(value: number): string {
  return String(value).padStart(2, '0')
}
