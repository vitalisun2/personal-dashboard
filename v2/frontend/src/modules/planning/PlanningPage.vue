<script setup lang="ts">
import { computed, onMounted, reactive, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { chatRoute } from '../../shared/chatRoute'
import { getOfflineStore, saveOfflineMutation } from '../../offline/runtime'
import type { OfflineEntity, SyncOperation } from '../../offline/types'

type Feature = { id: string; title: string; description: string; position: number; version: number; status: string | number }
type Milestone = { id: string; title: string; description: string; position: number; version: number; progressPercent: number; features: Feature[] }
type Project = { id: string; title: string; description: string; version: number; isArchived: boolean; progressPercent: number; milestones: Milestone[] }
type Task = { id: string; title: string; description: string; projectId?: string; milestoneId?: string; featureId?: string; location: string | number; workStatus: string | number; position: number; version: number }
const api = '/api/v2/planning', taskApi = '/api/v2/tasks'
const route = useRoute(), router = useRouter()
const state = reactive({ projects: [] as Project[], tasks: [] as Task[], busy: false, error: '', showArchived: false, orderMode: false, contextKind: '', contextId: '', createType: '', editType: '', editId: '', title: '', description: '' })
const projectId = computed(() => String(route.params.projectId || ''))
const milestoneId = computed(() => String(route.params.milestoneId || ''))
const featureId = computed(() => String(route.params.featureId || ''))
const project = computed(() => state.projects.find(item => item.id === projectId.value) || state.projects.find(item => !item.isArchived) || null)
const milestone = computed(() => project.value?.milestones.find(item => item.id === milestoneId.value) || null)
const feature = computed(() => milestone.value?.features.find(item => item.id === featureId.value) || null)
const depth = computed(() => feature.value ? 3 : milestone.value ? 2 : project.value ? 1 : 0)
const linkedTasks = computed(() => state.tasks.filter(item => item.featureId === featureId.value))
const plannedFeatureTasks = computed(() => linkedTasks.value.filter(item => taskState(item) === 'planned').sort((a, b) => a.position - b.position))
let contextTimer = 0, contextTarget: { x: number; y: number } | null = null, suppressPlanningOpenUntil = 0
function openPlanningContext(kind: 'milestone' | 'feature' | 'task', id: string) { state.contextKind = kind; state.contextId = id; suppressPlanningOpenUntil = Date.now() + 500 }
function planningContextDown(event: PointerEvent, kind: 'milestone' | 'feature' | 'task', id: string) {
  if (state.orderMode || event.pointerType !== 'touch' || (event.target as HTMLElement).closest('.planning-drag-handle')) return
  contextTarget = { x: event.clientX, y: event.clientY }; window.clearTimeout(contextTimer)
  contextTimer = window.setTimeout(() => openPlanningContext(kind, id), 550)
}
function planningContextMove(event: PointerEvent) {
  if (contextTarget && (Math.abs(event.clientX - contextTarget.x) > 8 || Math.abs(event.clientY - contextTarget.y) > 8)) window.clearTimeout(contextTimer)
}
function planningContextUp() { window.clearTimeout(contextTimer); contextTarget = null }
function openLinkedTask(task: Task) { if (Date.now() >= suppressPlanningOpenUntil) void router.push(`/tasks/${task.id}`) }
async function request<T>(base: string, path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`${base}${path}`, { ...init, headers: { 'Content-Type': 'application/json', ...init?.headers } })
  if (!response.ok) { const body = await response.json().catch(() => ({})); throw new Error(body.error || `Запрос не выполнен (${response.status})`) }
  return response.status === 204 ? undefined as T : response.json()
}
function offlineId() { return crypto.randomUUID() }
async function queuePlanning(type: 'planning.project' | 'planning.milestone' | 'planning.feature', id: string, version: number | null, payload: object, deleted = false, viewPayload?: object) {
  const store = await getOfflineStore(), pending = (await store.listPendingOperations()).filter(x => x.type === type && x.id === id)
  const tail = pending[pending.length - 1]
  const expectedVersion = tail ? (tail.expectedVersion === null ? 1 : tail.expectedVersion + 1) : version
  const payloadWithVersion = { ...payload, ...(expectedVersion === null ? {} : { expectedVersion }) }
  const operationId = offlineId(), now = new Date().toISOString()
  const entity: OfflineEntity = { type, id, version: expectedVersion === null ? 1 : expectedVersion + 1, payload: viewPayload ?? payloadWithVersion, deleted, updatedAt: now }
  const operation: SyncOperation = { operationId, type, id, expectedVersion, kind: deleted ? 'delete' : 'upsert', payload: payloadWithVersion, createdAt: now }
  await saveOfflineMutation(entity, operation)
  if (viewPayload !== undefined || deleted) await (await getOfflineStore()).putEntity({ type: `${type}.view`, id, version: entity.version, payload: deleted ? null : viewPayload!, deleted, updatedAt: now })
}
async function queueTaskCreate(id: string, payload: object, viewPayload: Task) {
  const now = new Date().toISOString(), entity: OfflineEntity = { type: 'tasks.task', id, version: 1, payload: viewPayload, deleted: false, updatedAt: now }
  const operation: SyncOperation = { operationId: offlineId(), type: 'tasks.task', id, expectedVersion: null, kind: 'upsert', payload, createdAt: now }
  await saveOfflineMutation(entity, operation)
  await (await getOfflineStore()).putEntity({ type: 'tasks.task.view', id, version: 1, payload: viewPayload, deleted: false, updatedAt: now })
}
async function queueTaskMutation(task: Task, payload: object, viewPayload: object = task) {
  const store = await getOfflineStore(), pending = (await store.listPendingOperations()).filter(x => x.type === 'tasks.task' && x.id === task.id)
  const tail = pending[pending.length - 1]
  const expectedVersion = tail ? (tail.expectedVersion === null ? 1 : tail.expectedVersion + 1) : task.version
  const payloadWithVersion = { ...payload, expectedVersion }
  const now = new Date().toISOString(), entity: OfflineEntity = { type: 'tasks.task', id: task.id, version: expectedVersion + 1, payload: viewPayload, deleted: false, updatedAt: now }
  const operation: SyncOperation = { operationId: offlineId(), type: 'tasks.task', id: task.id, expectedVersion, kind: 'upsert', payload: payloadWithVersion, createdAt: now }
  await saveOfflineMutation(entity, operation)
  await (await getOfflineStore()).putEntity({ type: 'tasks.task.view', id: task.id, version: expectedVersion + 1, payload: viewPayload, deleted: false, updatedAt: now })
}
async function cacheRows(type: string, rows: Array<{ id: string; version: number }>, force = false) {
  const store = await getOfflineStore(), now = new Date().toISOString(), pending = await store.listPendingOperations()
  const affected = new Set(pending.filter(op => op.type === type).map(op => op.id))
  if (type === 'planning.project') for (const op of pending) {
    const payload = op.payload as { projectId?: string } | undefined
    if (op.type === 'planning.milestone' || op.type === 'planning.feature') if (payload?.projectId) affected.add(payload.projectId)
  }
  await Promise.all(rows.filter(row => force || !affected.has(row.id)).map(row => store.putEntity({ type: `${type}.view`, id: row.id, version: row.version, payload: row, deleted: false, updatedAt: now })))
}
async function cachedRows<T>(type: string): Promise<T[]> {
  const rows = await (await getOfflineStore()).listEntities(`${type}.view`)
  return rows.filter(row => !row.deleted).map(row => row.payload as T)
}
async function refresh() {
  state.busy = true; state.error = ''
  try {
    try {
      state.projects = await request<Project[]>(api, `/projects?includeArchived=${state.showArchived}`)
      await cacheRows('planning.project', state.projects)
      for (const p of state.projects) {
        await cacheRows('planning.milestone', p.milestones)
        for (const m of p.milestones) await cacheRows('planning.feature', m.features)
      }
      const lists = await Promise.all(['Planned','Backlog','Today'].map(location => request<Task[]>(taskApi, `?location=${location}`)))
      state.tasks = lists.flat(); await cacheRows('tasks.task', state.tasks)
      const store = await getOfflineStore(), pending = await store.listPendingOperations(), localProjects = await cachedRows<Project>('planning.project'), localTasks = await cachedRows<Task>('tasks.task')
      const localProjectById = new Map(localProjects.map(item => [item.id, item])), affectedProjects = new Set<string>(), deletedProjects = new Set<string>(), affectedTasks = new Set<string>()
      for (const op of pending) {
        const data = op.payload as { projectId?: string; order?: Array<{ id: string }> } | undefined
        if (op.type === 'planning.project') { affectedProjects.add(op.id); if (op.kind === 'delete') deletedProjects.add(op.id) }
        if ((op.type === 'planning.milestone' || op.type === 'planning.feature') && data?.projectId) affectedProjects.add(data.projectId)
        if (op.type === 'tasks.task') affectedTasks.add(op.id)
        for (const item of data?.order || []) { if (op.type === 'tasks.task') affectedTasks.add(item.id) }
      }
      state.projects = state.projects.filter(item => !deletedProjects.has(item.id)).map(item => affectedProjects.has(item.id) ? localProjectById.get(item.id) || item : item)
      for (const id of affectedProjects) if (!state.projects.some(item => item.id === id) && localProjectById.has(id) && !deletedProjects.has(id)) state.projects.push(localProjectById.get(id)!)
      const localTaskById = new Map(localTasks.map(item => [item.id, item]))
      state.tasks = state.tasks.filter(item => !affectedTasks.has(item.id)).concat([...affectedTasks].map(id => localTaskById.get(id)).filter((item): item is Task => !!item))
    } catch {
      state.projects = (await cachedRows<Project>('planning.project')).filter(p => state.showArchived || !p.isArchived)
      state.tasks = await cachedRows<Task>('tasks.task')
      if (!state.projects.length && !state.tasks.length) throw new Error('Нет сети и сохранённых данных для планирования.')
      state.error = 'Нет сети. Показаны сохранённые данные.'
    }
    const active = state.projects.find(item => item.id === projectId.value)
    if (!projectId.value && active) await router.replace(`/planning/projects/${active.id}`)
  } catch (error) { state.error = (error as Error).message }
  finally { state.busy = false }
}
function goProject(id: string) { void router.push(`/planning/projects/${id}`) }
function goMilestone(id: string) { if (project.value) void router.push(`/planning/projects/${project.value.id}/milestones/${id}`) }
function goFeature(id: string) { if (project.value && milestone.value) void router.push(`/planning/projects/${project.value.id}/milestones/${milestone.value.id}/features/${id}`) }
function startCreate(type: string) { state.createType = type; state.editType = ''; state.title = ''; state.description = '' }
function startEdit(type: string, item: Project | Milestone | Feature) { state.editType = type; state.editId = item.id; state.createType = ''; state.title = item.title; state.description = item.description }
async function save() {
  const title = state.title.trim(); if (!title) return
  try {
    if (state.createType === 'project') await request(api, '/projects', { method: 'POST', body: JSON.stringify({ title, description: state.description }) })
    else if (state.createType === 'milestone' && project.value) await request(api, `/projects/${project.value.id}/milestones`, { method: 'POST', body: JSON.stringify({ expectedParentVersion: project.value.version, title, description: state.description }) })
    else if (state.createType === 'feature' && project.value && milestone.value) await request(api, `/projects/${project.value.id}/milestones/${milestone.value.id}/features`, { method: 'POST', body: JSON.stringify({ expectedParentVersion: milestone.value.version, title, description: state.description }) })
    else if (state.editType === 'project' && project.value) await request(api, `/projects/${project.value.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: project.value.version, title, description: state.description }) })
    else if (state.editType === 'milestone' && project.value && milestone.value) await request(api, `/projects/${project.value.id}/milestones/${milestone.value.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: milestone.value.version, title, description: state.description }) })
    else if (state.editType === 'feature' && project.value && milestone.value && feature.value) await request(api, `/projects/${project.value.id}/milestones/${milestone.value.id}/features/${feature.value.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: feature.value.version, title, description: state.description }) })
    state.createType = ''; state.editType = ''; await refresh()
  } catch (error) {
    if (error instanceof TypeError) {
      const kind = state.editType || state.createType
      if (state.editType) {
        const entityType = kind === 'project' ? 'planning.project' : kind === 'milestone' ? 'planning.milestone' : 'planning.feature'
        const current = kind === 'project' ? project.value : kind === 'milestone' ? milestone.value : feature.value
        if (current) {
          const local = { ...current, title, description: state.description, version: current.version + 1 }
          await queuePlanning(entityType, current.id, current.version, { operation: 'update', kind, id: current.id, projectId: project.value?.id, milestoneId: milestone.value?.id, expectedVersion: current.version, title, description: state.description }, false, local)
          if (kind === 'project') state.projects = state.projects.map(x => x.id === current.id ? { ...x, ...local } as Project : x)
          else if (kind === 'milestone' && project.value) project.value.milestones = project.value.milestones.map(x => x.id === current.id ? { ...x, ...local } as Milestone : x)
          else if (kind === 'feature' && milestone.value) milestone.value.features = milestone.value.features.map(x => x.id === current.id ? { ...x, ...local } as Feature : x)
          await cacheRows('planning.project', state.projects, true)
        }
      } else {
        const id = offlineId()
        if (kind === 'project') {
          const local: Project = { id, title, description: state.description, version: 1, isArchived: false, progressPercent: 0, milestones: [] }
          await queuePlanning('planning.project', id, null, { operation: 'create', kind: 'project', id, title, description: state.description }, false, local); state.projects.push(local)
        } else if (kind === 'milestone' && project.value) {
          const local: Milestone = { id, title, description: state.description, version: 1, position: project.value.milestones.length, progressPercent: 0, features: [] }
          await queuePlanning('planning.milestone', id, null, { operation: 'create', kind: 'milestone', id, projectId: project.value.id, expectedParentVersion: project.value.version, title, description: state.description }, false, local); project.value.milestones.push(local); project.value.version += 1
        } else if (kind === 'feature' && project.value && milestone.value) {
          const local: Feature = { id, title, description: state.description, version: 1, position: milestone.value.features.length, status: 'planned' }
          await queuePlanning('planning.feature', id, null, { operation: 'create', kind: 'feature', id, projectId: project.value.id, milestoneId: milestone.value.id, expectedParentVersion: milestone.value.version, title, description: state.description }, false, local); milestone.value.features.push(local); milestone.value.version += 1; project.value.version += 1
        }
        await cacheRows('planning.project', state.projects, true)
      }
      state.createType = ''; state.editType = ''; state.title = ''; state.description = ''
      state.error = 'Нет сети. Изменение сохранено и будет синхронизировано позже.'
      return
    }
    state.error = (error as Error).message
  }
}
async function setStatus(item: Feature, status: number) {
  if (!project.value || !milestone.value) return
  try { await request(api, `/projects/${project.value.id}/milestones/${milestone.value.id}/features/${item.id}/status`, { method: 'PUT', body: JSON.stringify({ expectedVersion: item.version, status: (['planned', 'active', 'done'] as const)[status] }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const featureStatus = (['planned', 'active', 'done'] as const)[status]
      const local = { ...item, status: featureStatus, version: item.version + 1 }
      await queuePlanning('planning.feature', item.id, item.version, { operation: 'setFeatureStatus', kind: 'feature', id: item.id, projectId: project.value.id, milestoneId: milestone.value.id, expectedVersion: item.version, featureStatus }, false, local)
      milestone.value.features = milestone.value.features.map(x => x.id === item.id ? local : x)
      await cacheRows('planning.project', state.projects, true)
      state.error = 'Нет сети. Изменение сохранено и будет синхронизировано позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function remove(type: string, item: Project | Milestone | Feature) {
  if (!window.confirm(`Удалить «${item.title}»?`)) return
  try {
    const path = type === 'project' ? `/projects/${item.id}` : type === 'milestone' ? `/projects/${project.value!.id}/milestones/${item.id}` : `/projects/${project.value!.id}/milestones/${milestone.value!.id}/features/${item.id}`
    await request(api, path, { method: 'DELETE', body: JSON.stringify({ expectedVersion: item.version }) })
    if (type === 'project') await router.replace('/planning')
    else if (type === 'milestone') await router.replace(`/planning/projects/${project.value!.id}`)
    else await router.replace(`/planning/projects/${project.value!.id}/milestones/${milestone.value!.id}`)
    await refresh()
  } catch (error) {
    if (error instanceof TypeError) {
      const entityType = type === 'project' ? 'planning.project' : type === 'milestone' ? 'planning.milestone' : 'planning.feature'
      const kind = type === 'project' ? 'project' : type === 'milestone' ? 'milestone' : 'feature'
      await queuePlanning(entityType, item.id, item.version, { operation: 'delete', kind, id: item.id, projectId: type === 'project' ? undefined : project.value?.id, milestoneId: type === 'feature' ? milestone.value?.id : undefined, expectedVersion: item.version }, true)
      if (type === 'project') state.projects = state.projects.filter(x => x.id !== item.id)
      else if (type === 'milestone' && project.value) project.value.milestones = project.value.milestones.filter(x => x.id !== item.id)
      else if (type === 'feature' && milestone.value) milestone.value.features = milestone.value.features.filter(x => x.id !== item.id)
      await cacheRows('planning.project', state.projects, true)
      state.error = 'Нет сети. Удаление поставлено в очередь.'; return
    }
    state.error = (error as Error).message
  }
}
async function setProjectArchived(archived: boolean) {
  if (!project.value) return
  try { await request(api, `/projects/${project.value.id}/${archived ? 'archive' : 'restore'}`, { method: 'POST', body: JSON.stringify({ expectedVersion: project.value.version }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const local = { ...project.value, isArchived: archived, version: project.value.version + 1 }
      await queuePlanning('planning.project', project.value.id, project.value.version, { operation: archived ? 'archive' : 'restore', kind: 'project', id: project.value.id, expectedVersion: project.value.version }, false, local)
      state.projects = state.projects.map(x => x.id === local.id ? local : x)
      await cacheRows('planning.project', state.projects, true)
      state.error = 'Нет сети. Изменение сохранено и будет синхронизировано позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function addTask() {
  if (!project.value || !milestone.value || !feature.value || !state.title.trim()) return
  try {
    await request(taskApi, '', { method: 'POST', body: JSON.stringify({ title: state.title.trim(), description: state.description, projectId: project.value.id, milestoneId: milestone.value.id, featureId: feature.value.id, sectionId: null }) })
    state.createType = ''; state.title = ''; state.description = ''; await refresh()
  } catch (error) {
    if (error instanceof TypeError) {
      if (!project.value || !milestone.value || !feature.value) { state.error = (error as Error).message; return }
      const id = offlineId(), payload = { operation: 'create', kind: 'task', id, title: state.title.trim(), description: state.description, planning: { projectId: project.value.id, milestoneId: milestone.value.id, featureId: feature.value.id }, placement: 'planned', workStatus: 'new', sectionId: null }
      const local: Task = { id, title: state.title.trim(), description: state.description, projectId: project.value.id, milestoneId: milestone.value.id, featureId: feature.value.id, location: 'planned', workStatus: 'new', position: linkedTasks.value.length, version: 1 }
      await queueTaskCreate(id, payload, local); state.tasks.push(local); state.title = ''; state.description = ''; state.createType = ''; await cacheRows('tasks.task', state.tasks, true); state.error = 'Нет сети. Задача сохранена и будет синхронизирована позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function moveTask(task: Task, suffix: string) {
  try { await request(taskApi, `/${task.id}/${suffix}`, { method: 'POST', body: JSON.stringify({ expectedVersion: task.version }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const placement = suffix === 'backlog' ? 'backlog' : suffix === 'planning' ? 'planned' : 'today'
      const local = { ...task, location: placement, workStatus: 'new', version: task.version + 1 }
      await queueTaskMutation(task, { operation: 'move', kind: 'task', id: task.id, expectedVersion: task.version, placement, workStatus: 'new', sectionId: null }, local)
      state.tasks = state.tasks.map(x => x.id === task.id ? local : x)
      await cacheRows('tasks.task', state.tasks, true)
      state.error = 'Нет сети. Перенос сохранён и будет синхронизирован позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function deleteTask(task: Task) {
  if (!window.confirm(`Удалить «${task.title}» без возможности восстановления?`)) return
  try { await request(taskApi, `/${task.id}`, { method: 'DELETE', body: JSON.stringify({ expectedVersion: task.version }) }); state.tasks = state.tasks.filter(x => x.id !== task.id); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const store = await getOfflineStore(), pending = (await store.listPendingOperations()).filter(x => x.type === 'tasks.task' && x.id === task.id), tail = pending[pending.length - 1]
      const expectedVersion = tail ? (tail.expectedVersion === null ? 1 : tail.expectedVersion + 1) : task.version, now = new Date().toISOString()
      const payload = { operation: 'delete', kind: 'task', id: task.id, expectedVersion }
      await saveOfflineMutation({ type: 'tasks.task', id: task.id, version: expectedVersion! + 1, payload: task, deleted: true, updatedAt: now }, { operationId: offlineId(), type: 'tasks.task', id: task.id, expectedVersion, kind: 'delete', payload, createdAt: now })
      await store.putEntity({ type: 'tasks.task.view', id: task.id, version: expectedVersion! + 1, payload: null, deleted: true, updatedAt: now })
      state.tasks = state.tasks.filter(x => x.id !== task.id); await cacheRows('tasks.task', state.tasks, true); state.error = 'Нет сети. Удаление сохранено и будет синхронизировано позже.'; return
    }
    state.error = (error as Error).message
  }
}
function statusName(status: string | number) { return ({ '0': 'planned', '1': 'active', '2': 'done', planned: 'planned', active: 'active', done: 'done', Planned: 'planned', Active: 'active', Done: 'done' } as Record<string, string>)[String(status)] || 'planned' }
function taskState(task: Task) { const location = String(task.location).toLowerCase(); return location === '0' || location === 'planned' ? 'planned' : location === '2' || location === 'today' ? 'today' : location === '3' || location === 'archived' ? 'archived' : 'backlog' }
async function reorderPlanning(type: 'milestone' | 'feature', id: string, delta: number) {
  if (!project.value) return
  const list = type === 'milestone' ? project.value.milestones : milestone.value?.features || []
  const index = list.findIndex(x => x.id === id), target = index + delta
  if (target < 0 || target >= list.length) return
  await reorderPlanningTo(type, id, list[target].id)
}
async function reorderPlanningTo(type: 'milestone' | 'feature', sourceId: string, targetId: string) {
  if (!project.value || sourceId === targetId) return
  let ids: string[]
  if (type === 'milestone') {
    const list = [...project.value.milestones], from = list.findIndex(x => x.id === sourceId), to = list.findIndex(x => x.id === targetId)
    if (from < 0 || to < 0) return
    const [moved] = list.splice(from, 1); list.splice(to, 0, moved); ids = list.map(x => x.id)
  } else {
    const list = [...(milestone.value?.features || [])], from = list.findIndex(x => x.id === sourceId), to = list.findIndex(x => x.id === targetId)
    if (from < 0 || to < 0) return
    const [moved] = list.splice(from, 1); list.splice(to, 0, moved); ids = list.map(x => x.id)
  }
  const path = type === 'milestone' ? `/projects/${project.value.id}/milestones/order` : `/projects/${project.value.id}/milestones/${milestone.value!.id}/features/order`
  const expectedVersion = type === 'milestone' ? project.value.version : milestone.value!.version
  try { await request(api, path, { method: 'PUT', body: JSON.stringify({ expectedVersion, ids }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      if (type === 'milestone') {
        const original = project.value.milestones
        const local = { ...project.value, version: project.value.version + 1, milestones: ids.map((id, position) => { const item = original.find(row => row.id === id)!; return { ...item, position, version: item.version + (item.position === position ? 0 : 1) } }) }
        const order = local.milestones.map(item => ({ id: item.id, expectedVersion: original.find(row => row.id === item.id)!.version }))
        const source = local.milestones.find(item => item.id === sourceId)!
        const sourceVersion = original.find(item => item.id === sourceId)!.version
        await queuePlanning('planning.milestone', source.id, sourceVersion, { operation: 'reorder', kind: 'milestone', id: source.id, projectId: project.value.id, expectedVersion: sourceVersion, expectedParentVersion: project.value.version, order }, false, source)
        state.projects = state.projects.map(item => item.id === local.id ? local : item)
      } else if (milestone.value) {
        const original = milestone.value.features
        const local = { ...milestone.value, version: milestone.value.version + 1, features: ids.map((id, position) => { const item = original.find(row => row.id === id)!; return { ...item, position, version: item.version + (item.position === position ? 0 : 1) } }) }
        const order = local.features.map(item => ({ id: item.id, expectedVersion: original.find(row => row.id === item.id)!.version }))
        const source = local.features.find(item => item.id === sourceId)!
        const sourceVersion = original.find(item => item.id === sourceId)!.version
        await queuePlanning('planning.feature', source.id, sourceVersion, { operation: 'reorder', kind: 'feature', id: source.id, projectId: project.value.id, milestoneId: milestone.value.id, expectedVersion: sourceVersion, expectedParentVersion: milestone.value.version, order }, false, source)
        project.value.milestones = project.value.milestones.map(item => item.id === local.id ? local : item)
      }
      await cacheRows('planning.project', state.projects, true)
      state.error = 'Нет сети. Порядок сохранён и будет синхронизирован позже.'; return
    }
    state.error = (error as Error).message
  }
}
function planningDragStart(event: DragEvent, type: 'milestone' | 'feature', id: string) { event.dataTransfer?.setData('text/plain', `${type}:${id}`); if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move' }
function planningDrop(event: DragEvent, type: 'milestone' | 'feature', targetId: string) {
  const [sourceType, sourceId] = (event.dataTransfer?.getData('text/plain') || '').split(':')
  if (sourceType === type && sourceId) void reorderPlanningTo(type, sourceId, targetId)
}
function planningTaskDragStart(event: DragEvent, id: string) { event.dataTransfer?.setData('text/plain', `task:${id}`); if (event.dataTransfer) event.dataTransfer.effectAllowed = 'move' }
async function reorderPlannedByDelta(taskId: string, delta: number) {
  const index = plannedFeatureTasks.value.findIndex(x => x.id === taskId), target = plannedFeatureTasks.value[index + delta]
  if (target) await reorderPlannedTasks(taskId, target.id)
}
async function reorderPlannedTasks(sourceId: string, targetId: string) {
  if (!project.value || !milestone.value || !feature.value || sourceId === targetId) return
  const tasks = linkedTasks.value.filter(x => taskState(x) === 'planned'), list = [...tasks]
  const from = list.findIndex(x => x.id === sourceId), to = list.findIndex(x => x.id === targetId)
  if (from < 0 || to < 0) return
  const [moved] = list.splice(from, 1); list.splice(to, 0, moved)
  const order = list.map(x => ({ id: x.id, expectedVersion: x.version }))
  try {
    await request(taskApi, '/order', { method: 'PUT', body: JSON.stringify({ location: 'planned', projectId: project.value.id, milestoneId: milestone.value.id, featureId: feature.value.id, items: order }) })
    await refresh()
  } catch (error) {
    if (error instanceof TypeError) {
      const source = list.find(x => x.id === sourceId)!, local = { ...source, position: list.findIndex(x => x.id === sourceId), version: source.version + 1 }
      await queueTaskMutation(source, { operation: 'reorder', kind: 'task', id: sourceId, placement: 'planned', planning: { projectId: project.value.id, milestoneId: milestone.value.id, featureId: feature.value.id }, order }, local)
      state.tasks = state.tasks.map(x => { const index = list.findIndex(item => item.id === x.id); return index < 0 ? x : { ...x, position: index, version: x.version + 1 } })
      await cacheRows('tasks.task', state.tasks, true)
      state.error = 'Нет сети. Порядок сохранён и будет синхронизирован позже.'; return
    }
    state.error = (error as Error).message
  }
}
watch(() => route.fullPath, () => void refresh())
watch(() => state.showArchived, () => void refresh())
onMounted(refresh)
</script>

<template>
  <section class="planning-page" aria-labelledby="planning-heading">
    <div v-if="state.error" class="planning-error" role="alert">{{ state.error }} <button @click="state.error = ''">×</button></div>
    <form v-if="state.createType || state.editType" class="planning-editor" @submit.prevent="state.createType === 'task' ? addTask() : save()">
      <strong>{{ state.editType ? 'Изменить' : 'Создать' }} {{ state.createType || state.editType }}</strong>
      <label>Название<input v-model="state.title" autofocus maxlength="160" required /></label><label>Описание<textarea v-model="state.description" rows="3" /></label>
      <div><button type="button" class="planning-quiet" @click="state.createType = ''; state.editType = ''">Отмена</button><button class="planning-primary">Сохранить</button></div>
    </form>
    <div class="planning-toolbar"><div class="project-picker"><select id="project-picker" aria-label="Проект" :value="project?.id || ''" @change="goProject(($event.target as HTMLSelectElement).value)"><option v-for="item in state.projects.filter(p => state.showArchived || !p.isArchived)" :key="item.id" :value="item.id">{{ item.title }}{{ item.isArchived ? ' · архив' : '' }}</option></select></div><button class="planning-quiet" @click="state.showArchived = !state.showArchived">{{ state.showArchived ? 'Скрыть архив' : 'Архив' }}</button><button v-if="project" class="planning-quiet" @click="startEdit('project', project)" aria-label="Изменить проект">⋯</button><button v-if="project && !project.isArchived" class="planning-quiet" @click="setProjectArchived(true)" aria-label="В архив">⇩</button><button v-if="project?.isArchived" class="planning-quiet" @click="setProjectArchived(false)">Восстановить</button></div>
    <div v-if="state.busy" class="planning-empty">Загружаем план…</div>
    <div v-else-if="!project" class="planning-empty">Создайте проект, чтобы начать планирование.</div>
    <template v-else-if="depth === 1">
      <div class="progress-card"><div><span>Прогресс проекта</span><strong>{{ project.progressPercent }}%</strong></div><div class="progress-track"><span :style="{ width: `${project.progressPercent}%` }" /></div></div>
      <div class="list-heading"><h2>Вехи</h2><button class="planning-quiet" :aria-pressed="state.orderMode" @click="state.orderMode = !state.orderMode">{{ state.orderMode ? 'Готово' : 'Сортировка' }}</button><button class="planning-quiet" @click="startCreate('milestone')">＋ Добавить веху</button><RouterLink class="planning-quiet" :to="chatRoute({ entityType: 'planning.project', entityId: project.id, entityVersion: project.version })">Чат проекта</RouterLink></div>
      <div v-if="!project.milestones.length" class="planning-empty">Вехи появятся здесь.</div>
      <article v-for="(item, index) in project.milestones" :key="item.id" class="planning-row" @click="!state.orderMode && Date.now() >= suppressPlanningOpenUntil && goMilestone(item.id)" @contextmenu.prevent="openPlanningContext('milestone', item.id)" @pointerdown="planningContextDown($event, 'milestone', item.id)" @pointermove="planningContextMove" @pointerup="planningContextUp" @pointercancel="planningContextUp" @dragover.prevent="state.orderMode && $event.preventDefault()" @drop.prevent="state.orderMode && planningDrop($event, 'milestone', item.id)"><button class="row-open">{{ item.title }}</button><div class="row-progress"><span>{{ item.progressPercent }}%</span><div class="progress-track"><span :style="{ width: `${item.progressPercent}%` }" /></div></div><button v-if="state.orderMode" class="planning-quiet planning-drag-handle" draggable="true" aria-label="Перетащить веху" @click.stop @dragstart="planningDragStart($event, 'milestone', item.id)">⠿</button><button v-if="state.orderMode" class="planning-quiet" :disabled="index === 0" aria-label="Веха выше" @click.stop="reorderPlanning('milestone', item.id, -1)">↑</button><button v-if="state.orderMode" class="planning-quiet" :disabled="index === project.milestones.length - 1" aria-label="Веха ниже" @click.stop="reorderPlanning('milestone', item.id, 1)">↓</button><button class="planning-quiet" @click.stop="startEdit('milestone', item)">Изменить</button><button class="planning-quiet" @click.stop="remove('milestone', item)">Удалить</button><span class="row-arrow">›</span><div v-if="state.contextKind === 'milestone' && state.contextId === item.id" class="planning-context-actions"><button class="planning-quiet" @click.stop="startEdit('milestone', item); state.contextKind = ''">Изменить</button><button class="planning-quiet" @click.stop="remove('milestone', item); state.contextKind = ''">Удалить</button></div></article>
    </template>
    <template v-else-if="milestone && depth === 2">
      <button class="planning-back" @click="goProject(project.id)">← {{ project.title }}</button>
      <div class="progress-card"><div><span>Прогресс вехи</span><strong>{{ milestone.progressPercent }}%</strong></div><div class="progress-track"><span :style="{ width: `${milestone.progressPercent}%` }" /></div><small>{{ milestone.features.filter(f => statusName(f.status) === 'done').length }} из {{ milestone.features.length }} фич завершено</small></div>
      <div class="list-heading"><h2>{{ milestone.title }}</h2><button class="planning-quiet" :aria-pressed="state.orderMode" @click="state.orderMode = !state.orderMode">{{ state.orderMode ? 'Готово' : 'Сортировка' }}</button><button class="planning-quiet" @click="startEdit('milestone', milestone)">Изменить</button><button class="planning-quiet" @click="startCreate('feature')">＋ Добавить фичу</button><RouterLink class="planning-quiet" :to="chatRoute({ entityType: 'planning.milestone', entityId: milestone.id, entityVersion: milestone.version })">Чат вехи</RouterLink></div>
      <article v-for="(item, index) in milestone.features" :key="item.id" class="planning-row" @click="!state.orderMode && Date.now() >= suppressPlanningOpenUntil && goFeature(item.id)" @contextmenu.prevent="openPlanningContext('feature', item.id)" @pointerdown="planningContextDown($event, 'feature', item.id)" @pointermove="planningContextMove" @pointerup="planningContextUp" @pointercancel="planningContextUp" @dragover.prevent="state.orderMode && $event.preventDefault()" @drop.prevent="state.orderMode && planningDrop($event, 'feature', item.id)"><button class="row-open">{{ item.title }}</button><span class="feature-status" :class="statusName(item.status)">{{ statusName(item.status) }}</span><button v-if="state.orderMode" class="planning-quiet planning-drag-handle" draggable="true" aria-label="Перетащить фичу" @click.stop @dragstart="planningDragStart($event, 'feature', item.id)">⠿</button><button v-if="state.orderMode" class="planning-quiet" :disabled="index === 0" aria-label="Фича выше" @click.stop="reorderPlanning('feature', item.id, -1)">↑</button><button v-if="state.orderMode" class="planning-quiet" :disabled="index === milestone.features.length - 1" aria-label="Фича ниже" @click.stop="reorderPlanning('feature', item.id, 1)">↓</button><button class="planning-quiet" @click.stop="startEdit('feature', item)">Изменить</button><button class="planning-quiet" @click.stop="remove('feature', item)">Удалить</button><span class="row-arrow">›</span><div v-if="state.contextKind === 'feature' && state.contextId === item.id" class="planning-context-actions"><button class="planning-quiet" @click.stop="startEdit('feature', item); state.contextKind = ''">Изменить</button><button class="planning-quiet" @click.stop="remove('feature', item); state.contextKind = ''">Удалить</button></div></article>
    </template>
    <template v-else-if="feature && milestone">
      <button class="planning-back" @click="goMilestone(milestone.id)">← {{ milestone.title }}</button>
      <div class="list-heading feature-heading"><div><p class="eyebrow">{{ project.title }} · {{ milestone.title }}</p><h2>{{ feature.title }}</h2></div><button class="planning-quiet" @click="startEdit('feature', feature)">Изменить</button><button class="planning-quiet" @click="remove('feature', feature)">Удалить</button><RouterLink class="planning-quiet" :to="chatRoute({ entityType: 'planning.feature', entityId: feature.id, entityVersion: feature.version })">Чат фичи</RouterLink></div>
      <label class="feature-status-control">Состояние фичи<select :value="statusName(feature.status)" @change="setStatus(feature, ({ planned: 0, active: 1, done: 2 } as Record<string, number>)[($event.target as HTMLSelectElement).value])"><option value="planned">Запланирована</option><option value="active">В работе</option><option value="done">Готова</option></select></label>
      <div class="list-heading"><h2>Задачи</h2><button class="planning-quiet" :aria-pressed="state.orderMode" @click="state.orderMode = !state.orderMode">{{ state.orderMode ? 'Готово' : 'Сортировка' }}</button><button class="planning-quiet" @click="startCreate('task')">＋ Добавить задачу</button></div>
      <div v-if="!linkedTasks.length" class="planning-empty">У этой фичи пока нет задач.</div>
      <article v-for="task in linkedTasks" :key="task.id" class="planning-row task-plan-row" @contextmenu.prevent="openPlanningContext('task', task.id)" @pointerdown="planningContextDown($event, 'task', task.id)" @pointermove="planningContextMove" @pointerup="planningContextUp" @pointercancel="planningContextUp" @dragover.prevent="state.orderMode && taskState(task) === 'planned' && $event.preventDefault()" @drop.prevent="state.orderMode && taskState(task) === 'planned' && reorderPlannedTasks(($event.dataTransfer?.getData('text/plain') || '').replace('task:', ''), task.id)"><button v-if="!state.orderMode" class="row-open" @click="openLinkedTask(task)">{{ task.title }}</button><span v-else class="row-open">{{ task.title }}</span><span class="task-placement">{{ taskState(task) }}</span><button v-if="state.orderMode && taskState(task) === 'planned'" class="planning-quiet planning-drag-handle" draggable="true" aria-label="Перетащить задачу" @dragstart="planningTaskDragStart($event, task.id)">⠿</button><button v-if="state.orderMode && taskState(task) === 'planned'" class="planning-quiet" :disabled="plannedFeatureTasks.findIndex(x => x.id === task.id) === 0" aria-label="Задача выше" @click="reorderPlannedByDelta(task.id, -1)">↑</button><button v-if="state.orderMode && taskState(task) === 'planned'" class="planning-quiet" :disabled="plannedFeatureTasks.findIndex(x => x.id === task.id) === plannedFeatureTasks.length - 1" aria-label="Задача ниже" @click="reorderPlannedByDelta(task.id, 1)">↓</button><button v-if="taskState(task) === 'planned'" class="planning-quiet" @click="moveTask(task, 'backlog')">В Backlog</button><button v-else-if="taskState(task) === 'backlog'" class="planning-quiet" @click="moveTask(task, 'planning')">Вернуть в план</button><button v-else class="planning-quiet" @click="openLinkedTask(task)">Открыть</button><button class="planning-quiet" @click="deleteTask(task)">Удалить</button><div v-if="state.contextKind === 'task' && state.contextId === task.id" class="planning-context-actions"><button class="planning-quiet" @click.stop="openLinkedTask(task); state.contextKind = ''">Открыть</button><button class="planning-quiet" @click.stop="deleteTask(task); state.contextKind = ''">Удалить</button></div></article>
    </template>
  </section>
</template>
