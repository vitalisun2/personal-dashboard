import { getOfflineStore, saveOfflineMutation } from '../../offline/runtime'
import type { OfflineEntity, SyncOperation } from '../../offline/types'

export type KnowledgeNode = {
  id: string
  kind: 'section' | 'document'
  title: string
  markdown: string
  parentId: string | null
  position: number
  version: number
  path: string
  archived: boolean
}

export type SearchHit = {
  source: { kind: string; id: string; version: number; url: string; title: string; path: string; snippet: string; updatedAtUtc: string }
  score: number
  matchKind: 'lexical' | 'semantic'
}

export type SearchResponse = { hits: SearchHit[]; nextCursor: string | null; isComplete: boolean; coverageNote: string | null }

export const ENTITY_TYPE = 'knowledge.node'

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const response = await fetch(url, { ...init, headers: { 'Content-Type': 'application/json', ...init?.headers } })
  if (!response.ok) {
    const body = await response.json().catch(() => null) as { detail?: string } | null
    throw new Error(body?.detail || `Ошибка запроса (${response.status})`)
  }
  return response.json() as Promise<T>
}

export async function getCachedKnowledge(): Promise<KnowledgeNode[]> {
  const store = await getOfflineStore()
  const entities = await store.listEntities<KnowledgeNode>(ENTITY_TYPE)
  const active = entities.filter(entity => !entity.deleted && entity.payload && !entity.payload.archived)
  const byId = new Map(active.map(entity => [entity.id, entity.payload!]))
  const valid = new Set<string>()
  for (const node of byId.values()) {
    if (!node.parentId) {
      valid.add(node.id)
      continue
    }
    const parent = byId.get(node.parentId)
    if (parent?.kind === 'section') valid.add(node.id)
  }
  let changed = true
  while (changed) {
    changed = false
    for (const id of valid) {
      const node = byId.get(id)!
      if (node.parentId && !valid.has(node.parentId)) { valid.delete(id); changed = true }
    }
  }
  return [...byId.values()].filter(node => valid.has(node.id))
}

export async function cacheServerKnowledge(nodes: KnowledgeNode[]): Promise<void> {
  const store = await getOfflineStore()
  const pending = await store.listPendingOperations()
  const pendingIds = new Set(pending.filter(operation => operation.type === ENTITY_TYPE).map(operation => operation.id))
  for (const node of nodes) {
    if (pendingIds.has(node.id)) continue
    const cached = await store.getEntity<KnowledgeNode>(ENTITY_TYPE, node.id)
    if (!cached || node.version >= cached.version) await store.putEntity({ type: ENTITY_TYPE, id: node.id, version: node.version, payload: node, deleted: false })
  }
}

export async function loadKnowledgeTree(): Promise<KnowledgeNode[]> {
  return request<KnowledgeNode[]>('/api/v2/knowledge/tree')
}

export function searchKnowledge(query: string): Promise<SearchResponse> {
  return request<SearchResponse>('/api/v2/search', {
    method: 'POST',
    body: JSON.stringify({ query, mode: 'relevant', kinds: ['knowledge.document'], pageSize: 20 }),
  })
}

async function nextOperationTime(entityId: string): Promise<string> {
  const pending = await (await getOfflineStore()).listPendingOperations()
  const last = pending.filter(operation => operation.type === ENTITY_TYPE && operation.id === entityId)
    .reduce((time, operation) => Math.max(time, Date.parse(operation.createdAt)), 0)
  return new Date(Math.max(Date.now(), last + 1)).toISOString()
}

export async function queueKnowledgeUpsert(node: KnowledgeNode, baseVersion: number | null): Promise<void> {
  const createdAt = await nextOperationTime(node.id)
  const expectedVersion = baseVersion
  const operation: SyncOperation<KnowledgeNode> = {
    operationId: crypto.randomUUID(), type: ENTITY_TYPE, id: node.id,
    expectedVersion, kind: 'upsert', payload: node, createdAt,
  }
  const entity: OfflineEntity<KnowledgeNode> = { type: ENTITY_TYPE, id: node.id, version: expectedVersion === null ? 1 : expectedVersion + 1, payload: node, deleted: false }
  await saveOfflineMutation(entity, operation)
}

export async function queueKnowledgeDelete(node: KnowledgeNode): Promise<void> {
  const createdAt = await nextOperationTime(node.id)
  const operation: SyncOperation = {
    operationId: crypto.randomUUID(), type: ENTITY_TYPE, id: node.id,
    expectedVersion: node.version, kind: 'delete', createdAt,
  }
  await saveOfflineMutation({ type: ENTITY_TYPE, id: node.id, version: node.version + 1, payload: null, deleted: true }, operation)
}

export async function getKnowledgeConflicts() {
  return (await getOfflineStore()).listConflicts().then(conflicts => conflicts.filter(conflict => conflict.type === ENTITY_TYPE))
}

export async function pendingKnowledgeCount(): Promise<number> {
  return (await getOfflineStore()).listPendingOperations().then(operations => operations.filter(operation => operation.type === ENTITY_TYPE).length)
}
