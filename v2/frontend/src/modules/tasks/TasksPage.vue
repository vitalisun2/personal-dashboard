<script setup lang="ts">
import { computed, onMounted, reactive, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { chatRoute } from '../../shared/chatRoute'
import { getOfflineStore, saveOfflineMutation } from '../../offline/runtime'
import type { OfflineEntity, SyncOperation } from '../../offline/types'

type Task = { id: string; title: string; description: string; projectId?: string; milestoneId?: string; featureId?: string; location: string; workStatus: string; sectionId?: string; position: number; version: number }
type Section = { id: string; name: string; location: string; position: number; version: number }
type ProjectLabel = { id: string; title: string }
const api = '/api/v2/tasks'
const route = useRoute(), router = useRouter()
const state = reactive({ bucket: 'Backlog' as 'Backlog' | 'Сегодня', filter: 'all', archive: false, orderMode: false, contextTaskId: '', contextSectionId: '', expanded: { Backlog: new Set<string>(), Сегодня: new Set<string>() }, tasks: [] as Task[], sections: [] as Section[], projects: [] as ProjectLabel[], detail: null as Task | null, busy: false, error: '', creating: false, creatingSection: false, title: '', description: '', sectionId: '' })
const location = computed(() => state.archive ? 'Archived' : state.bucket === 'Сегодня' ? 'Today' : 'Backlog')
const filtered = computed(() => state.tasks.filter(task => !state.detail || task.id === state.detail.id).filter(task => state.filter === 'all' || statusName(task.workStatus) === state.filter))
const linkedGroups = computed(() => {
  const ids = [...new Set(filtered.value.filter(task => task.projectId).map(task => task.projectId!))]
  return ids.map(projectId => ({ projectId, title: state.projects.find(x => x.id === projectId)?.title || 'Проект', tasks: filtered.value.filter(task => task.projectId === projectId).sort((a, b) => a.position - b.position) }))
})
async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${api}${path}`, { ...init, headers: { 'Content-Type': 'application/json', ...init?.headers } })
  if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.error || `Запрос не выполнен (${response.status})`) }
  return response.status === 204 ? undefined as T : response.json()
}
function newId() { return crypto.randomUUID() }
async function queueTask(type: 'tasks.task' | 'tasks.section', id: string, version: number | null, payload: object, deleted = false, viewPayload?: object) {
  const store = await getOfflineStore(), pending = (await store.listPendingOperations()).filter(x => x.type === type && x.id === id)
  const tail = pending[pending.length - 1]
  const expectedVersion = tail ? (tail.expectedVersion === null ? 1 : tail.expectedVersion + 1) : version
  const payloadWithVersion = { ...payload, ...(expectedVersion === null ? {} : { expectedVersion }) }
    const operationId = newId(), now = new Date().toISOString()
  const entity: OfflineEntity = { type, id, version: expectedVersion === null ? 1 : expectedVersion + 1, payload: viewPayload ?? payloadWithVersion, deleted, updatedAt: now }
  const operation: SyncOperation = { operationId, type, id, expectedVersion, kind: deleted ? 'delete' : 'upsert', payload: payloadWithVersion, createdAt: now }
  await saveOfflineMutation(entity, operation)
  if (viewPayload !== undefined || deleted) await (await getOfflineStore()).putEntity({ type: `${type}.view`, id, version: entity.version, payload: viewPayload ?? null, deleted, updatedAt: now })
}
async function cached<T>(type: 'tasks.task' | 'tasks.section'): Promise<T[]> {
  const store = await getOfflineStore(), rows = await store.listEntities(`${type}.view`)
  return rows.filter(row => !row.deleted).map(row => row.payload as T)
}
async function queuedIds(type: string) {
  const pending = (await (await getOfflineStore()).listPendingOperations()).filter(op => op.type === type), ids = new Set(pending.map(op => op.id))
  for (const op of pending) for (const item of ((op.payload as { order?: Array<{ id: string }> } | undefined)?.order || [])) ids.add(item.id)
  return ids
}
async function cacheRows(type: 'tasks.task' | 'tasks.section' | 'planning.project', rows: Array<{ id: string; version: number; [key: string]: unknown }>, force = false) {
  const store = await getOfflineStore(), now = new Date().toISOString(), pending = await queuedIds(type)
  const viewType = type === 'planning.project' ? 'planning.project.view' : `${type}.view`
  await Promise.all(rows.filter(row => force || !pending.has(row.id)).map(row => store.putEntity({ type: viewType, id: row.id, version: row.version, payload: row, deleted: false, updatedAt: now })))
}
async function cacheBucket(type: 'tasks.task' | 'tasks.section', bucket: string, rows: Array<Task | Section>) {
  const store = await getOfflineStore(), viewType = `${type}.view`, previous = await store.listEntities(viewType), ids = new Set(rows.map(row => row.id)), pending = await queuedIds(type), now = new Date().toISOString()
  for (const entry of previous) {
    const row = entry.payload as Task | Section | null
    if (!row || String(row.location).toLowerCase() !== bucket.toLowerCase() || ids.has(entry.id) || pending.has(entry.id)) continue
    await store.putEntity({ ...entry, deleted: true, updatedAt: now })
  }
  await cacheRows(type, rows)
}
async function refresh() {
  state.busy = true; state.error = ''
  try {
    const requestedId = String(route.params.taskId || '')
    let tasks: Task[], sections: Section[], detail: Task | null
    try {
      [tasks, sections, detail] = await Promise.all([request<Task[]>(`?location=${location.value}`), state.archive ? Promise.resolve([] as Section[]) : request<Section[]>(`/sections?location=${location.value}`), requestedId ? request<Task>(`/${requestedId}`).catch(() => null) : Promise.resolve(null)])
      await cacheBucket('tasks.task', location.value, tasks); if (detail) await cacheRows('tasks.task', [detail]); if (!state.archive) await cacheBucket('tasks.section', location.value, sections)
    } catch {
      const allTasks = await cached<Task>('tasks.task'), allSections = await cached<Section>('tasks.section')
      tasks = allTasks.filter(x => String(x.location).toLowerCase() === location.value.toLowerCase())
      sections = state.archive ? [] : allSections.filter(x => String(x.location).toLowerCase() === location.value.toLowerCase())
      detail = requestedId ? allTasks.find(x => x.id === requestedId) || null : null
      if (!tasks.length && !sections.length && !detail) throw new Error('Нет сети и сохранённых данных для этого списка.')
      state.error = 'Нет сети. Показаны сохранённые данные.'
    }
    const store = await getOfflineStore(), pending = await store.listPendingOperations(), cachedTasks = await cached<Task>('tasks.task'), cachedSections = await cached<Section>('tasks.section')
    const changedTasks = new Set<string>(), changedSections = new Set<string>()
    for (const op of pending) {
      const order = (op.payload as { order?: Array<{ id: string }> } | undefined)?.order || []
      if (op.type === 'tasks.task') { changedTasks.add(op.id); for (const item of order) changedTasks.add(item.id) }
      if (op.type === 'tasks.section') { changedSections.add(op.id); for (const item of order) changedSections.add(item.id) }
    }
    tasks = tasks.filter(task => !changedTasks.has(task.id)).concat(cachedTasks.filter(task => changedTasks.has(task.id) && String(task.location).toLowerCase() === location.value.toLowerCase()))
    sections = sections.filter(section => !changedSections.has(section.id)).concat(cachedSections.filter(section => changedSections.has(section.id) && String(section.location).toLowerCase() === location.value.toLowerCase()))
    state.tasks = tasks; state.sections = sections
    if (!state.archive) {
      try { state.projects = await fetch('/api/v2/planning/projects?includeArchived=false').then(response => response.ok ? response.json() as Promise<ProjectLabel[]> : []); await cacheRows('planning.project', state.projects.map(x => ({ ...x, version: 0 }))) }
      catch { const rows = await (await getOfflineStore()).listEntities('planning.project.view'); state.projects = rows.filter(x => !x.deleted).map(x => x.payload as ProjectLabel) }
    }
    state.detail = detail
    if (state.detail) { state.bucket = state.detail.location === 'today' || state.detail.location === 'Today' ? 'Сегодня' : 'Backlog'; state.title = state.detail.title; state.description = state.detail.description }
    if (sections.length && !sections.some(x => x.id === state.sectionId)) state.sectionId = sections[0].id
  } catch (error) { state.error = (error as Error).message }
  finally { state.busy = false }
}
function openTask(task: Task) { if (!state.orderMode && Date.now() >= suppressOpenUntil) void router.push(`/tasks/${task.id}`) }
let holdTimer = 0, pointerStart: { x: number; y: number; taskId: string; sectionId: string } | null = null, suppressOpenUntil = 0
function contextPointerDown(event: PointerEvent, taskId = '', sectionId = '') {
  if (state.orderMode || event.pointerType !== 'touch') return
  pointerStart = { x: event.clientX, y: event.clientY, taskId, sectionId }
  holdTimer = window.setTimeout(() => { if (taskId) state.contextTaskId = taskId; else state.contextSectionId = sectionId; suppressOpenUntil = Date.now() + 450 }, 550)
}
function contextPointerMove(event: PointerEvent) {
  if (!pointerStart) return
  const dx = event.clientX - pointerStart.x, dy = event.clientY - pointerStart.y
  if (Math.abs(dx) > 8 || Math.abs(dy) > 8) window.clearTimeout(holdTimer)
  if (Math.abs(dx) > 55 && Math.abs(dx) > Math.abs(dy) && dx < 0) {
    if (pointerStart.taskId) state.contextTaskId = pointerStart.taskId
    else state.contextSectionId = pointerStart.sectionId
    suppressOpenUntil = Date.now() + 450
  }
}
function contextPointerUp() { window.clearTimeout(holdTimer); pointerStart = null }
async function createTask() {
  if (!state.title.trim()) return
  let created: Task, queuedOffline = false, moveError = ''
  try { created = await request<Task>('', { method: 'POST', body: JSON.stringify({ title: state.title, description: state.description, projectId: null, milestoneId: null, featureId: null, sectionId: state.bucket === 'Backlog' ? (state.sectionId || null) : null }) }) }
  catch (error) {
    if (!(error instanceof TypeError)) throw error
    const id = newId(), sectionId = state.bucket === 'Backlog' ? state.sectionId || state.sections[0]?.id || null : null
    const payload = { operation: 'create', kind: 'task', id, title: state.title.trim(), description: state.description, placement: 'backlog', workStatus: 'new', sectionId }
    const local: Task = { id, title: state.title.trim(), description: state.description, location: 'backlog', workStatus: 'new', sectionId: sectionId || undefined, position: state.tasks.length, version: 1 }
    await queueTask('tasks.task', id, null, payload, false, local)
    created = { id, title: state.title.trim(), description: state.description, location: 'backlog', workStatus: 'new', sectionId: sectionId || undefined, position: state.tasks.length, version: 1 }
    queuedOffline = true
    state.tasks.push(created); await cacheRows('tasks.task', state.tasks, true); state.error = 'Нет сети. Задача сохранена и будет синхронизирована позже.'
  }
  if (state.bucket === 'Сегодня') {
    if (queuedOffline) {
      const backlogTask = created
      created = { ...created, location: 'today', version: created.version + 1 }
      await queueTask('tasks.task', backlogTask.id, backlogTask.version, { operation: 'move', kind: 'task', id: backlogTask.id, expectedVersion: backlogTask.version, placement: 'today', workStatus: 'new', sectionId: null }, false, created)
      state.tasks = state.tasks.map(x => x.id === created.id ? created : x)
      await cacheRows('tasks.task', state.tasks, true)
    } else {
      try { await request(`/${created.id}/today`, { method: 'POST', body: JSON.stringify({ expectedVersion: created.version }) }) }
      catch (error) {
        if (error instanceof TypeError) {
          const todayTask = { ...created, location: 'today', version: created.version + 1 }
          await queueTask('tasks.task', created.id, created.version, { operation: 'move', kind: 'task', id: created.id, expectedVersion: created.version, placement: 'today', workStatus: 'new', sectionId: null }, false, todayTask)
          state.tasks = state.tasks.map(x => x.id === created.id ? todayTask : x); state.error = 'Нет сети. Создание сохранено, перенос на Сегодня будет синхронизирован позже.'
        } else moveError = (error as Error).message
      }
    }
  }
  state.title = ''; state.description = ''; state.creating = false
  if (moveError) { state.bucket = 'Backlog'; await refresh(); state.error = `Задача создана в Backlog, но не перенесена на Сегодня: ${moveError}` }
  else if (!queuedOffline) await refresh()
}
async function createSection() {
  if (!state.title.trim()) return
  let created: Section, queuedOffline = false
  try { created = await request<Section>('/sections', { method: 'POST', body: JSON.stringify({ name: state.title, location: state.bucket === 'Сегодня' ? 'today' : 'backlog' }) }) }
  catch (error) {
    if (!(error instanceof TypeError)) { state.error = (error as Error).message; return }
    const id = newId(), bucket = state.bucket === 'Сегодня' ? 'today' : 'backlog'
    const payload = { operation: 'create', kind: 'section', id, title: state.title.trim(), bucket, position: state.sections.length }
    const local: Section = { id, name: state.title.trim(), location: bucket, position: state.sections.length, version: 1 }
    await queueTask('tasks.section', id, null, payload, false, local)
    created = { id, name: state.title.trim(), location: bucket, position: state.sections.length, version: 1 }
    queuedOffline = true; state.sections.push(created); await cacheRows('tasks.section', state.sections, true); state.error = 'Нет сети. Раздел сохранён и будет синхронизирован позже.'
  }
  state.title = ''; state.creatingSection = false; if (!queuedOffline) await refresh(); state.sectionId = created.id
}
async function mutate(task: Task, suffix: string, method = 'POST', body: object = { expectedVersion: task.version }) {
  try { await request(`/${task.id}/${suffix}`, { method, body: JSON.stringify(body) }); await refresh() } catch (error) {
    if (error instanceof TypeError) {
      const operation = suffix === 'archive' ? 'archive' : suffix === 'restore' ? 'restore' : suffix === 'section' ? 'update' : suffix === 'status' ? 'setWorkStatus' : 'move'
      const placement = suffix === 'today' ? 'today' : suffix === 'backlog' || suffix === 'restore' ? 'backlog' : suffix === 'planning' ? 'planned' : 'archived'
      const payload = suffix === 'section'
        ? { operation: 'update', kind: 'task', id: task.id, expectedVersion: task.version, sectionId: (body as { sectionId: string }).sectionId }
        : { operation, kind: 'task', id: task.id, expectedVersion: task.version, placement, workStatus: suffix === 'status' ? (body as { status: string }).status : statusName(task.workStatus) === 'InProgress' ? 'inProgress' : statusName(task.workStatus).toLowerCase(), sectionId: suffix === 'planning' ? null : task.sectionId, ...(suffix === 'planning' && task.projectId && task.milestoneId && task.featureId ? { planning: { projectId: task.projectId, milestoneId: task.milestoneId, featureId: task.featureId } } : {}) }
      const local = { ...task, sectionId: suffix === 'section' ? (body as { sectionId: string }).sectionId : task.sectionId, workStatus: suffix === 'status' ? (body as { status: string }).status : suffix === 'restore' || suffix === 'backlog' ? 'new' : task.workStatus, location: suffix === 'section' ? task.location : placement, version: task.version + 1 }
      await queueTask('tasks.task', task.id, task.version, payload, false, local)
      state.tasks = state.tasks.filter(x => x.id !== task.id); if (String(local.location).toLowerCase() === location.value.toLowerCase()) state.tasks.push(local)
      if (state.detail?.id === task.id) state.detail = local
      await cacheRows('tasks.task', state.tasks, true)
      state.error = 'Нет сети. Изменение сохранено и будет синхронизировано позже.'; return
    }
    state.error = (error as Error).message; await refresh()
  }
}
async function saveDetail() {
  if (!state.detail) return
  try { await request(`/${state.detail.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: state.detail.version, title: state.title, description: state.description }) }); await refresh() }
  catch (error) {
    if (!(error instanceof TypeError)) { state.error = (error as Error).message; return }
    const task = state.detail, payload = { operation: 'update', kind: 'task', id: task.id, expectedVersion: task.version, title: state.title, description: state.description }
    state.detail = { ...task, title: state.title, description: state.description, version: task.version + 1 }
    await queueTask('tasks.task', task.id, task.version, payload, false, state.detail); state.tasks = state.tasks.map(x => x.id === task.id ? state.detail! : x); await cacheRows('tasks.task', state.tasks, true)
    state.error = 'Нет сети. Изменение сохранено и будет синхронизировано позже.'
  }
}
async function setStatus(task: Task) {
  const current = statusName(task.workStatus), next = current === 'New' ? 'inProgress' : current === 'InProgress' ? 'done' : 'new'
  await mutate(task, 'status', 'PUT', { expectedVersion: task.version, status: next })
}
function archiveTask(task: Task) {
  if (window.confirm(`Убрать «${task.title}» в архив?`)) void mutate(task, 'archive')
}
async function deleteTask(task: Task) {
  if (!window.confirm(`Удалить «${task.title}» без возможности восстановления?`)) return
  try { await request(`/${task.id}`, { method: 'DELETE', body: JSON.stringify({ expectedVersion: task.version }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      await queueTask('tasks.task', task.id, task.version, { operation: 'delete', kind: 'task', id: task.id, expectedVersion: task.version }, true)
      state.tasks = state.tasks.filter(x => x.id !== task.id); if (state.detail?.id === task.id) state.detail = null
      await cacheRows('tasks.task', state.tasks, true); state.error = 'Нет сети. Удаление сохранено и будет синхронизировано позже.'; return
    }
    state.error = (error as Error).message
  }
}
function statusName(status: string) { return ({ '0': 'New', '1': 'InProgress', '2': 'Done', 'new': 'New', 'inProgress': 'InProgress', 'done': 'Done', 'New': 'New', 'InProgress': 'InProgress', 'Done': 'Done' } as Record<string, string>)[String(status)] || String(status) }
function label(status: string) { return ({ New: 'Новая', InProgress: 'В работе', Done: 'Готово' } as Record<string, string>)[statusName(status)] || status }
function selectBucket(bucket: 'Backlog' | 'Сегодня') { state.bucket = bucket; state.archive = false; state.orderMode = false; state.contextTaskId = ''; state.contextSectionId = ''; state.filter = 'all'; state.detail = null; state.title = ''; state.description = ''; void router.replace('/tasks').then(refresh) }
function toggleAllSections() { const ids = state.sections.map(x => x.id), open = state.expanded[state.bucket].size === ids.length && ids.length > 0; state.expanded[state.bucket] = new Set(open ? [] : ids) }
function toggleSection(id: string) { const open = state.expanded[state.bucket]; open.has(id) ? open.delete(id) : open.add(id) }
async function moveTaskSection(task: Task, sectionId: string) { await mutate(task, 'section', 'PUT', { expectedVersion: task.version, sectionId }) }
async function reorderTasks(sectionId: string, task: Task, delta: number) {
  const items = state.tasks.filter(x => x.sectionId === sectionId).sort((a, b) => a.position - b.position), at = items.findIndex(x => x.id === task.id), other = at + delta
  if (other < 0 || other >= items.length) return
  ;[items[at], items[other]] = [items[other], items[at]]
  const order = items.map(x => ({ id: x.id, expectedVersion: x.version }))
  try { await request('/order', { method: 'PUT', body: JSON.stringify({ location: location.value, sectionId, items: order }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      await queueTask('tasks.task', task.id, task.version, { operation: 'reorder', kind: 'task', id: task.id, expectedVersion: task.version, bucket: location.value.toLowerCase(), sectionId, order }, false, { ...task, position: items.findIndex(x => x.id === task.id), version: task.version + 1 })
      state.tasks = state.tasks.map(x => { const index = items.findIndex(i => i.id === x.id); return index < 0 ? x : { ...x, position: index, version: x.version + 1 } }); await cacheRows('tasks.task', state.tasks, true)
      state.error = 'Нет сети. Порядок сохранён и будет синхронизирован позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function reorderProjectTasks(projectId: string, sourceId: string, targetId: string) {
  if (sourceId === targetId) return
  const items = state.tasks.filter(x => x.projectId === projectId).sort((a, b) => a.position - b.position), from = items.findIndex(x => x.id === sourceId), to = items.findIndex(x => x.id === targetId)
  if (from < 0 || to < 0) return
  const [moved] = items.splice(from, 1); items.splice(to, 0, moved)
  const order = items.map(x => ({ id: x.id, expectedVersion: x.version }))
  try { await request('/order', { method: 'PUT', body: JSON.stringify({ location: location.value, projectId, items: order }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const source = items.find(x => x.id === sourceId)!
      await queueTask('tasks.task', source.id, source.version, { operation: 'reorder', kind: 'task', id: source.id, bucket: location.value.toLowerCase(), planning: { projectId }, order }, false, { ...source, position: items.findIndex(x => x.id === source.id), version: source.version + 1 })
      state.tasks = state.tasks.map(x => { const index = items.findIndex(i => i.id === x.id); return index < 0 ? x : { ...x, position: index, version: x.version + 1 } }); await cacheRows('tasks.task', state.tasks, true)
      state.error = 'Нет сети. Порядок сохранён и будет синхронизирован позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function reorderProjectByDelta(projectId: string, task: Task, delta: number) {
  const items = state.tasks.filter(x => x.projectId === projectId).sort((a, b) => a.position - b.position), index = items.findIndex(x => x.id === task.id), target = items[index + delta]
  if (target) await reorderProjectTasks(projectId, task.id, target.id)
}
async function dropTaskBefore(sectionId: string, sourceId: string, targetId: string) {
  if (sourceId === targetId) return
  const items = state.tasks.filter(x => x.sectionId === sectionId).sort((a, b) => a.position - b.position), from = items.findIndex(x => x.id === sourceId), to = items.findIndex(x => x.id === targetId)
  if (from < 0 || to < 0) return
  const [moved] = items.splice(from, 1); items.splice(to, 0, moved)
  try { await request('/order', { method: 'PUT', body: JSON.stringify({ location: location.value, sectionId, items: items.map(x => ({ id: x.id, expectedVersion: x.version })) }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const changed = items.find(x => x.id === sourceId)!
      await queueTask('tasks.task', changed.id, changed.version, { operation: 'reorder', kind: 'task', id: changed.id, bucket: location.value.toLowerCase(), sectionId, order: items.map(x => ({ id: x.id, expectedVersion: x.version })) }, false, { ...changed, position: items.findIndex(x => x.id === changed.id), version: changed.version + 1 })
      state.tasks = state.tasks.map(x => { const index = items.findIndex(i => i.id === x.id); return index < 0 ? x : { ...x, position: index, version: x.version + 1 } }); await cacheRows('tasks.task', state.tasks, true)
      state.error = 'Нет сети. Порядок сохранён и будет синхронизирован позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function dropSection(sourceId: string, targetId: string) {
  if (sourceId === targetId) return
  const sections = [...state.sections], from = sections.findIndex(x => x.id === sourceId), to = sections.findIndex(x => x.id === targetId)
  if (from < 0 || to < 0) return
  const [moved] = sections.splice(from, 1); sections.splice(to, 0, moved)
  try { await request('/sections/order', { method: 'PUT', body: JSON.stringify({ location: location.value, expectedVersion: Math.max(...sections.map(x => x.version)), ids: sections.map(x => x.id) }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const section = sections.find(x => x.id === sourceId)!
      await queueTask('tasks.section', section.id, section.version, { operation: 'reorder', kind: 'section', id: section.id, bucket: location.value.toLowerCase(), order: sections.map(x => ({ id: x.id, expectedVersion: x.version })) }, false, { ...section, position: sections.findIndex(x => x.id === section.id), version: section.version + 1 })
      state.sections = sections.map((x, position) => ({ ...x, position, version: x.version + 1 })); await cacheRows('tasks.section', state.sections, true); state.error = 'Нет сети. Порядок сохранён и будет синхронизирован позже.'; return
    }
    state.error = (error as Error).message
  }
}
function dragTaskId(event: DragEvent, id: string) { event.dataTransfer?.setData('text/plain', `task:${id}`) }
function onSectionDrop(event: DragEvent, targetId: string) {
  const source = event.dataTransfer?.getData('text/plain') || ''
  if (source.startsWith('section:')) void dropSection(source.slice(8), targetId)
  else if (source.startsWith('task:')) { const task = state.tasks.find(x => x.id === source.slice(5)); if (task) void moveTaskSection(task, targetId) }
}
async function renameSection(section: Section) {
  const name = window.prompt('Название раздела', section.name)?.trim(); if (!name || name === section.name) return
  try { await request(`/sections/${section.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: section.version, name }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const local = { ...section, name, version: section.version + 1 }
      await queueTask('tasks.section', section.id, section.version, { operation: 'update', kind: 'section', id: section.id, expectedVersion: section.version, title: name, bucket: section.location }, false, local)
      state.sections = state.sections.map(x => x.id === section.id ? local : x); state.error = 'Нет сети. Изменение сохранено и будет синхронизировано позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function deleteSection(section: Section) {
  if (!window.confirm(`Удалить раздел «${section.name}»? Сначала перенесите его задачи в другой раздел.`)) return
  try { await request(`/sections/${section.id}`, { method: 'DELETE', body: JSON.stringify({ expectedVersion: section.version }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      await queueTask('tasks.section', section.id, section.version, { operation: 'delete', kind: 'section', id: section.id, bucket: section.location }, true)
      state.sections = state.sections.filter(x => x.id !== section.id); await cacheRows('tasks.section', state.sections, true); state.error = 'Нет сети. Удаление сохранено и будет синхронизировано позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function reorderSection(section: Section, delta: number) {
  const at = state.sections.findIndex(x => x.id === section.id), other = at + delta; if (other < 0 || other >= state.sections.length) return
  const ids = state.sections.map(x => x.id); [ids[at], ids[other]] = [ids[other], ids[at]]
  try { await request('/sections/order', { method: 'PUT', body: JSON.stringify({ location: location.value, expectedVersion: Math.max(...state.sections.map(x => x.version)), ids }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const order = ids.map(id => { const value = state.sections.find(x => x.id === id)!; return { id, expectedVersion: value.version } })
      await queueTask('tasks.section', section.id, section.version, { operation: 'reorder', kind: 'section', id: section.id, expectedVersion: section.version, bucket: location.value.toLowerCase(), order }, false, { ...section, position: ids.indexOf(section.id), version: section.version + 1 })
      state.sections = ids.map((id, position) => ({ ...state.sections.find(x => x.id === id)!, position, version: state.sections.find(x => x.id === id)!.version + 1 })); await cacheRows('tasks.section', state.sections, true)
      state.error = 'Нет сети. Порядок сохранён и будет синхронизирован позже.'; return
    }
    state.error = (error as Error).message
  }
}
watch(() => route.fullPath, () => void refresh())
onMounted(refresh)
</script>

<template>
  <section class="tracker-page" aria-labelledby="tasks-heading">
    <div class="tracker-heading"><div><p class="eyebrow">Действовать сегодня</p><h1 id="tasks-heading">Задачи</h1></div><button class="primary-button" @click="state.creating = !state.creating">＋ Новая задача</button></div>
    <div class="tracker-tabs"><button :class="{ active: !state.archive && state.bucket === 'Backlog' }" @click="selectBucket('Backlog')">Backlog</button><button :class="{ active: !state.archive && state.bucket === 'Сегодня' }" @click="selectBucket('Сегодня')">Сегодня</button><button :class="{ active: state.archive }" @click="state.archive = true; state.orderMode = false; state.filter = 'all'; state.detail = null; void refresh()">Архив</button><span class="tab-spacer"/><button v-if="!state.archive" class="quiet-button" @click="toggleAllSections">{{ state.expanded[state.bucket].size === state.sections.length && state.sections.length ? 'Свернуть все' : 'Развернуть все' }}</button><button v-if="!state.archive" class="quiet-button" :class="{ active: state.orderMode }" :aria-pressed="state.orderMode" @click="state.orderMode = !state.orderMode">{{ state.orderMode ? 'Готово' : 'Сортировка' }}</button><button v-if="!state.archive" class="quiet-button" @click="state.creatingSection = !state.creatingSection">＋ Раздел</button></div>
    <div v-if="state.error" class="tracker-error" role="alert">{{ state.error }} <button @click="state.error = ''">×</button></div>
    <form v-if="state.creating || state.creatingSection" class="tracker-editor" @submit.prevent="state.creating ? createTask() : createSection()">
      <label>{{ state.creatingSection ? 'Название раздела' : 'Название задачи' }}<input v-model="state.title" autofocus maxlength="160" required /></label>
      <label v-if="state.creating">Описание<textarea v-model="state.description" rows="3" /></label>
      <label v-if="state.creating && state.sections.length">Раздел<select v-model="state.sectionId"><option v-for="item in state.sections" :key="item.id" :value="item.id">{{ item.name }}</option></select></label>
      <div class="editor-actions"><button type="button" class="quiet-button" @click="state.creating = false; state.creatingSection = false">Отмена</button><button class="primary-button">Создать</button></div>
    </form>
      <div v-if="state.detail" class="task-detail-card">
      <button class="back-link" @click="router.push('/tasks')">← Все задачи</button>
      <label>Название<input v-model="state.title" maxlength="160" /></label><label>Описание<textarea v-model="state.description" rows="7" /></label>
      <div class="detail-actions"><button class="primary-button" @click="saveDetail">Сохранить</button><button v-if="state.detail.location === 'archived' || state.detail.location === 'Archived'" class="quiet-button" @click="mutate(state.detail!, 'restore')">Восстановить</button><template v-else><button class="quiet-button" @click="mutate(state.detail!, state.detail!.location === 'today' || state.detail!.location === 'Today' ? 'backlog' : 'today')">Перенести {{ state.detail.location === 'today' || state.detail.location === 'Today' ? 'в Backlog' : 'на сегодня' }}</button><button class="quiet-button" @click="archiveTask(state.detail!)">В архив</button></template><button class="quiet-button" @click="deleteTask(state.detail!)">Удалить</button><RouterLink class="quiet-button" :to="chatRoute({ entityType: 'tasks.task', entityId: state.detail.id, entityVersion: state.detail.version })">Обсудить в чате</RouterLink></div>
    </div>
    <div v-else class="tracker-board">
      <div v-if="location === 'Today'" class="task-filters"><button v-for="filter in ['all','New','InProgress','Done']" :key="filter" :class="{ active: state.filter === filter }" @click="state.filter = filter">{{ filter === 'all' ? 'Все' : label(filter) }}</button></div>
      <div v-if="state.busy" class="board-empty">Загружаем задачи…</div>
      <div v-else-if="!filtered.length" class="board-empty">Здесь пока нет задач.</div>
      <div v-else-if="state.archive" class="task-section archive-list"><div v-for="task in filtered" :key="task.id" class="task-row" @contextmenu.prevent="state.contextTaskId = task.id" @pointerdown="contextPointerDown($event, task.id)" @pointermove="contextPointerMove" @pointerup="contextPointerUp" @pointercancel="contextPointerUp"><button class="task-title" @click="openTask(task)">{{ task.title }}</button><span class="task-placement">Архив</span><button class="quiet-button" @click="mutate(task, 'restore')">Восстановить</button><button class="quiet-button" @click="deleteTask(task)">Удалить</button></div></div>
      <section v-else v-for="section in state.sections" :key="section.id" class="task-section">
        <header @contextmenu.prevent="state.contextSectionId = section.id" @pointerdown="contextPointerDown($event, '', section.id)" @pointermove="contextPointerMove" @pointerup="contextPointerUp" @pointercancel="contextPointerUp" @dragover.prevent="state.orderMode && $event.preventDefault()" @drop.prevent="state.orderMode && onSectionDrop($event, section.id)"><h2><button class="quiet-button collapse-toggle" :aria-expanded="state.expanded[state.bucket].has(section.id)" @click="toggleSection(section.id)">{{ state.expanded[state.bucket].has(section.id) ? '⌄' : '›' }}</button>{{ section.name }}</h2><span>{{ filtered.filter(task => task.sectionId === section.id).length }}</span><button v-if="state.orderMode" class="quiet-button drag-handle" draggable="true" @dragstart="$event.dataTransfer?.setData('text/plain', `section:${section.id}`)" aria-label="Перетащить раздел">⠿</button><button class="quiet-button" @click="renameSection(section)">Переименовать</button><button v-if="state.orderMode" class="quiet-button" @click="reorderSection(section, -1)" aria-label="Раздел выше">↑</button><button v-if="state.orderMode" class="quiet-button" @click="reorderSection(section, 1)" aria-label="Раздел ниже">↓</button><button class="quiet-button" @click="state.sectionId = section.id; state.creating = true">＋</button></header>
        <div v-if="state.contextSectionId === section.id" class="context-actions"><button class="quiet-button" @click="renameSection(section); state.contextSectionId = ''">Переименовать</button><button class="quiet-button" @click="toggleSection(section.id); state.contextSectionId = ''">Свернуть / раскрыть</button><button class="quiet-button" @click="deleteSection(section); state.contextSectionId = ''">Удалить раздел</button></div>
        <template v-if="state.expanded[state.bucket].has(section.id)">
        <div v-for="task in filtered.filter(item => item.sectionId === section.id)" :key="task.id" class="task-row" @contextmenu.prevent="state.contextTaskId = task.id" @pointerdown="contextPointerDown($event, task.id)" @pointermove="contextPointerMove" @pointerup="contextPointerUp" @pointercancel="contextPointerUp" @dragover.prevent="state.orderMode && $event.preventDefault()" @drop.prevent="state.orderMode && dropTaskBefore(section.id, ($event.dataTransfer?.getData('text/plain') || '').replace('task:', ''), task.id)">
          <button class="task-title" @click="openTask(task)">{{ task.title }}</button><span v-if="location === 'Today'" class="task-status" :class="`status-${statusName(task.workStatus).toLowerCase()}`">{{ label(task.workStatus) }}</span>
          <select v-if="!task.projectId && state.sections.length > 1" :value="task.sectionId" aria-label="Раздел задачи" @change="moveTaskSection(task, ($event.target as HTMLSelectElement).value)"><option v-for="item in state.sections" :key="item.id" :value="item.id">{{ item.name }}</option></select>
          <button v-if="state.orderMode && !task.projectId" class="quiet-button drag-handle" draggable="true" @dragstart="dragTaskId($event, task.id)" aria-label="Перетащить задачу">⠿</button><button v-if="state.orderMode && !task.projectId" class="quiet-button" @click="reorderTasks(section.id, task, -1)" aria-label="Задача выше">↑</button><button v-if="state.orderMode && !task.projectId" class="quiet-button" @click="reorderTasks(section.id, task, 1)" aria-label="Задача ниже">↓</button>
          <button v-if="location === 'Today'" class="quiet-button" @click="setStatus(task)">{{ statusName(task.workStatus) === 'Done' ? 'Вернуть' : 'Следующий статус' }}</button>
          <button class="quiet-button" @dragover.prevent="state.orderMode && $event.preventDefault()" @drop.prevent="state.orderMode && dropTaskBefore(section.id, $event.dataTransfer?.getData('text/plain') || '', task.id)" @click="mutate(task, location === 'Today' ? 'backlog' : 'today')">{{ location === 'Today' ? 'Backlog' : 'Сегодня' }}</button><button class="quiet-button" @click="deleteTask(task)">Удалить</button>
          <div v-if="state.contextTaskId === task.id" class="context-actions"><button class="quiet-button" @click="openTask(task); state.contextTaskId = ''">Открыть</button><button class="quiet-button" @click="mutate(task, location === 'Today' ? 'backlog' : 'today'); state.contextTaskId = ''">{{ location === 'Today' ? 'Backlog' : 'Сегодня' }}</button><button v-if="task.projectId && task.location === 'backlog'" class="quiet-button" @click="mutate(task, 'planning'); state.contextTaskId = ''">Вернуть в планирование</button><button class="quiet-button" @click="archiveTask(task); state.contextTaskId = ''">В архив</button></div>
        </div>
        </template>
      </section>
      <section v-for="group in linkedGroups" :key="group.projectId" class="task-section linked-section">
        <header><h2>◆ {{ group.title }}</h2><span>{{ group.tasks.length }}</span></header>
        <div v-for="(task, index) in group.tasks" :key="task.id" class="task-row" @contextmenu.prevent="state.contextTaskId = task.id" @pointerdown="contextPointerDown($event, task.id)" @pointermove="contextPointerMove" @pointerup="contextPointerUp" @pointercancel="contextPointerUp" @dragover.prevent="state.orderMode && $event.preventDefault()" @drop.prevent="state.orderMode && reorderProjectTasks(group.projectId, ($event.dataTransfer?.getData('text/plain') || '').replace('project-task:', ''), task.id)">
          <button class="task-title" @click="openTask(task)"><span class="project-diamond" aria-hidden="true">◆</span>{{ task.title }}</button><span v-if="location === 'Today'" class="task-status">{{ label(task.workStatus) }}</span>
          <button v-if="state.orderMode" class="quiet-button drag-handle" draggable="true" aria-label="Перетащить задачу проекта" @dragstart="$event.dataTransfer?.setData('text/plain', `project-task:${task.id}`)">⠿</button><button v-if="state.orderMode" class="quiet-button" :disabled="index === 0" aria-label="Задача выше" @click="reorderProjectByDelta(group.projectId, task, -1)">↑</button><button v-if="state.orderMode" class="quiet-button" :disabled="index === group.tasks.length - 1" aria-label="Задача ниже" @click="reorderProjectByDelta(group.projectId, task, 1)">↓</button>
          <button class="quiet-button" @click="mutate(task, location === 'Today' ? 'backlog' : 'today')">{{ location === 'Today' ? 'Backlog' : 'Сегодня' }}</button><button v-if="task.location === 'backlog'" class="quiet-button" @click="mutate(task, 'planning')">Вернуть в планирование</button><button class="quiet-button" @click="deleteTask(task)">Удалить</button>
          <div v-if="state.contextTaskId === task.id" class="context-actions"><button v-if="task.location === 'backlog'" class="quiet-button" @click="mutate(task, 'planning'); state.contextTaskId = ''">Вернуть в планирование</button><button class="quiet-button" @click="archiveTask(task); state.contextTaskId = ''">В архив</button></div>
        </div>
      </section>
    </div>
  </section>
</template>
