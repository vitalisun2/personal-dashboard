import { IndexedDbOfflineStore, openOfflineDb } from './db'
import { HttpSyncTransport } from './httpSyncTransport'
import { syncPendingOperations } from './sync'
import type { OfflineEntity, SyncOperation } from './types'

export type SyncStatus = 'ready' | 'syncing' | 'offline' | 'conflict' | 'error'

let storePromise: Promise<IndexedDbOfflineStore> | undefined
let running: Promise<void> | undefined
let rerun = false
let started = false
let status: SyncStatus = 'ready'
const listeners = new Set<(next: SyncStatus) => void>()

export function getOfflineStore(): Promise<IndexedDbOfflineStore> {
  storePromise ??= openOfflineDb().then(database => new IndexedDbOfflineStore(database))
  return storePromise
}

export function subscribeSyncStatus(listener: (next: SyncStatus) => void): () => void {
  listeners.add(listener)
  listener(status)
  return () => listeners.delete(listener)
}

function setStatus(next: SyncStatus): void {
  status = next
  for (const listener of listeners) listener(next)
}

// Domain pages use this after producing a local versioned entity and operation.
export async function saveOfflineMutation(entity: OfflineEntity, operation: SyncOperation): Promise<void> {
  const store = await getOfflineStore()
  await store.saveEntityAndQueue(entity, operation)
  requestSync()
}

export function requestSync(): void {
  if (running) {
    rerun = true
    return
  }

  running = runSync().finally(() => {
    running = undefined
    if (rerun) {
      rerun = false
      requestSync()
    }
  })
}

async function runSync(): Promise<void> {
  if (!navigator.onLine) {
    setStatus('offline')
    return
  }

  setStatus('syncing')
  try {
    const store = await getOfflineStore()
    await syncPendingOperations(store, new HttpSyncTransport())
    setStatus((await store.listConflicts()).length ? 'conflict' : 'ready')
  } catch {
    // Keep the IndexedDB queue intact. A later online/visibility/timer event retries.
    setStatus('error')
  }
}

export function startSyncLifecycle(): void {
  if (started) return
  started = true
  window.addEventListener('online', requestSync)
  window.addEventListener('offline', () => setStatus('offline'))
  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') requestSync()
  })
  window.setInterval(() => {
    if (document.visibilityState === 'visible') requestSync()
  }, 30_000)
  requestSync()
}
