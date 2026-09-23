// Formes JSON rendues par le collecteur (src/SeptPaceAuto.Core/Services/TrackingApp.cs).

export interface Entry {
  id: number | null
  start: string
  end: string
  workItem: number | null
  label: string
  source: string
  sentAt?: string | null
  bug?: number | null
}

export interface DayReview {
  date: string
  entries: Entry[]
  holes: number[][]
  overlaps: number[][]
  totalMinutes: number
  plannedMinutes: number
  unassigned: number
  locked?: number
}

export interface AzureRefresh {
  resolved: number
  failed: number
  message: string | null
}

export interface PendingDay extends DayReview {
  pending: number
  azure?: AzureRefresh
}

export interface NothingPending {
  date: null
  pending: 0
  message: string
}

export interface Tracking {
  branch: string | null
  bug: number | null
  workItem: number | null
  label: string
  quickRunning: boolean
  state: string
}

export interface Health {
  observedAt: string | null
  branchAt: string | null
  writtenAt: string | null
  spanDate: string | null
  spanStart: number | null
  spanEnd: number | null
  error: string | null
  errorAt: string | null
  gitAvailable: boolean
  staleAfterSeconds: number
}

export interface CurrentDay extends DayReview {
  readOnly: true
  notice: string
  tracking: Tracking
  health: Health
  pending: number
}

export interface Settings {
  repoPath: string
  pollSeconds: number
  azureOrganization: string
  sevenPaceAccount: string
  workWindows: number[][]
  updateRepository: string
}

export interface Connection {
  status: string
  label: string
}

export interface LoadedSettings {
  settings: Settings
  connections: { sevenpace: Connection }
  configured: boolean
}

export interface Probe {
  ok: boolean
  message: string
  branch?: string | null
  ticket?: number | null
}

export interface UpdateInfo {
  available: boolean
  current: string
  latest: string | null
  notes: string | null
  error: string | null
}

export interface Outcome {
  ok: boolean
  message: string
  pending?: number
}
