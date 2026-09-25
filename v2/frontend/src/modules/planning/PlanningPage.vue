<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getOfflineStore, saveOfflineMutation } from '../../offline/runtime'
import type { OfflineEntity, SyncOperation } from '../../offline/types'

type Feature = { id: string; title: string; description: string; position: number; version: number; status: string | number }
type Milestone = { id: string; title: string; description: string; position: number; version: number; progressPercent: number; features: Feature[] }
type Project = { id: string; title: string; description: string; version: number; isArchived: boolean; progressPercent: number; milestones: Milestone[] }
type Task = { id: string; title: string; description: string; projectId?: string; milestoneId?: string; featureId?: string; location: string | number; workStatus: string | number; position: number; version: number }
type ContextItem = { label: string; danger?: boolean; action: () => void }

const api = '/api/v2/planning', taskApi = '/api/v2/tasks'
const route = useRoute(), router = useRouter()
const state = reactive({
  projects: [] as Project[], tasks: [] as Task[], busy: false, error: '', showArchived: false,
  orderMode: false, pickerOpen: false,
  createType: '', editType: '', editId: '', title: '', description: '',
  detailTaskId: '', editingKind: '' as '' | 'title' | 'description', editDraft: '',
  menuOpen: false, menuKind: '', menuId: '', menuX: 0, menuY: 0, menuPoint: null as { x: number; y: number } | null,
  confirm: null as null | { title: string; body: string; label: string; onConfirm: () => void },
  renderTick: 0,
})
const projectId = computed(() => String(route.params.projectId || ''))
const milestoneId = computed(() => String(route.params.milestoneId || ''))
const featureId = computed(() => String(route.params.featureId || ''))
const project = computed(() => state.projects.find(item => item.id === projectId.value) || state.projects.find(item => !item.isArchived) || null)
const milestone = computed(() => project.value?.milestones.find(item => item.id === milestoneId.value) || null)
const feature = computed(() => milestone.value?.features.find(item => item.id === featureId.value) || null)
const depth = computed(() => feature.value ? 3 : milestone.value ? 2 : project.value ? 1 : 0)
const linkedTasks = computed(() => state.tasks.filter(item => item.featureId === featureId.value))
const plannedFeatureTasks = computed(() => linkedTasks.value.filter(item => taskState(item) === 'planned').sort((a, b) => a.position - b.position))
const detailTask = computed(() => state.detailTaskId ? state.tasks.find(item => item.id === state.detailTaskId) || null : null)
const editMilestone = computed(() => project.value?.milestones.find(item => item.id === state.editId) || null)
const editProject = computed(() => state.projects.find(item => item.id === state.editId) || null)
const editFeature = computed(() => {
  if (!project.value || !state.editId) return null
  for (const m of project.value.milestones) { const f = m.features.find(item => item.id === state.editId); if (f) return { milestone: m, feature: f } }
  return null
})
const currentEditItem = computed(() => {
  if (state.editType === 'task') return state.tasks.find(item => item.id === state.editId) || null
  if (state.editType === 'project') return state.projects.find(item => item.id === state.editId) || null
  if (state.editType === 'milestone') return editMilestone.value
  return editFeature.value?.feature ?? null
})
const visibleProjects = computed(() => state.projects.filter(item => state.showArchived || !item.isArchived))
const completedFeatures = computed(() => milestone.value ? milestone.value.features.filter(f => statusName(f.status) === 'done').length : 0)
const contextItems = computed<ContextItem[]>(() => {
  const kind = state.menuKind, id = state.menuId
  if (kind === 'milestone') {
    const item = project.value?.milestones.find(x => x.id === id); if (!item) return []
    return [
      { label: 'Открыть веху', action: () => void goMilestone(id) },
      { label: 'Изменить', action: () => startEdit('milestone', item) },
      { label: 'Удалить веху', danger: true, action: () => deleteEntity('milestone', item) },
    ]
  }
  if (kind === 'feature') {
    const item = milestone.value?.features.find(x => x.id === id); if (!item) return []
    return [
      { label: 'Открыть фичу', action: () => void goFeature(id) },
      { label: 'Изменить', action: () => startEdit('feature', item) },
      { label: 'Удалить фичу', danger: true, action: () => deleteEntity('feature', item) },
    ]
  }
  const item = state.tasks.find(x => x.id === id); if (!item) return []
  const items: ContextItem[] = [
    { label: 'Открыть задачу', action: () => { state.detailTaskId = id } },
    { label: 'Изменить', action: () => startEdit('task', item) },
  ]
  if (taskState(item) === 'planned') items.push({ label: 'В Backlog', action: () => void moveTask(item, 'backlog') })
  else {
    items.push({ label: 'Открыть в задачах', action: () => openLinkedTask(item) })
    if (taskState(item) === 'backlog' && item.projectId && item.featureId) items.push({ label: 'Вернуть в план', action: () => void moveTask(item, 'planning') })
  }
  items.push({ label: 'Удалить задачу', danger: true, action: () => deleteTask(item) })
  return items
})
const contextMenuTitle = computed(() => {
  const kind = state.menuKind, id = state.menuId
  const item = kind === 'milestone' ? project.value?.milestones.find(x => x.id === id) : kind === 'feature' ? milestone.value?.features.find(x => x.id === id) : state.tasks.find(x => x.id === id)
  return item?.title || ''
})
const sheetNames = { project: 'проект', milestone: 'веху', feature: 'фичу', task: 'задачу' } as Record<string, string>
const sheetTitle = computed(() => state.editType ? `Изменить ${sheetNames[state.editType] || ''}` : ({ project: 'Новый проект', milestone: 'Новая веха', feature: 'Новая фича', task: 'Новая задача' } as Record<string, string>)[state.createType] || '')
const namePlaceholder = computed(() => state.createType === 'project' || state.editType === 'project' ? 'Название проекта' : state.createType === 'milestone' || state.editType === 'milestone' ? 'Название вехи' : state.createType === 'feature' || state.editType === 'feature' ? 'Название фичи' : 'Название задачи')
const saveButtonLabel = computed(() => state.editType ? 'Сохранить' : state.createType === 'task' ? 'Создать задачу' : 'Создать')

const sectionRef = ref<HTMLElement | null>(null)
const scrollRef = ref<HTMLElement | null>(null)
const contextMenuRef = ref<HTMLElement | null>(null)
const nameInputRef = ref<HTMLInputElement | null>(null)
const confirmCancelRef = ref<HTMLButtonElement | null>(null)
const titleInputRef = ref<HTMLInputElement | null>(null)
const descInputRef = ref<HTMLTextAreaElement | null>(null)

let contextTarget: { row: HTMLElement | null; kind: string; id: string; pointerId: number; startX: number; startY: number; moved: boolean; long: boolean; timer: number } | null = null
let suppressClickUntil = 0
let menuAnchorEl: HTMLElement | null = null
let dragCleanup: (((commit: boolean) => void) | null) = null

function taskState(task: Task) { const location = String(task.location).toLowerCase(); return location === '0' || location === 'planned' ? 'planned' : location === '2' || location === 'today' ? 'today' : location === '3' || location === 'archived' ? 'archived' : 'backlog' }
function workLower(status: string | number) { const w = String(status).toLowerCase(); return w === '2' || w === 'done' || w === 'completed' ? 'done' : w === '1' || w === 'active' || w === 'inprogress' || w === 'in_progress' || w === 'in progress' ? 'inprogress' : 'new' }
function taskMeta(task: Task): { label: string; kind: string } | null {
  const st = taskState(task)
  if (st === 'planned') return null
  const ws = workLower(task.workStatus)
  if (st === 'today' && ws === 'done') return { label: 'Готово', kind: 'completed' }
  if (st === 'today' && ws === 'inprogress') return { label: 'В работе', kind: 'in_progress' }
  if (st === 'today') return { label: 'Сегодня', kind: 'today' }
  if (st === 'archived') return { label: 'В архиве', kind: 'archived' }
  return { label: 'Backlog', kind: 'backlog' }
}
function statusName(status: string | number) { return ({ '0': 'planned', '1': 'active', '2': 'done', planned: 'planned', active: 'active', done: 'done', Planned: 'planned', Active: 'active', Done: 'done' } as Record<string, string>)[String(status)] || 'planned' }
function isContextActive(kind: string, id: string) { return state.menuOpen && state.menuKind === kind && state.menuId === id }

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
  await Promise.all(rows.filter(row => force || !affected.has(row.id)).map(row => store.putEntity({ type: `${type}.view`, id: row.id, version: row.version, payload: JSON.parse(JSON.stringify(row)), deleted: false, updatedAt: now })))
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
        const lists = await Promise.all(['Planned', 'Backlog', 'Today'].map(location => request<Task[]>(taskApi, `?location=${location}`)))
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
function goProject(id: string) { closeContextMenu(); state.detailTaskId = ''; state.orderMode = false; state.pickerOpen = false; void router.push(`/planning/projects/${id}`) }
function goMilestone(id: string) { closeContextMenu(); state.detailTaskId = ''; state.orderMode = false; state.pickerOpen = false; if (project.value) void router.push(`/planning/projects/${project.value.id}/milestones/${id}`) }
function goFeature(id: string) { closeContextMenu(); state.detailTaskId = ''; state.orderMode = false; state.pickerOpen = false; if (project.value && milestone.value) void router.push(`/planning/projects/${project.value.id}/milestones/${milestone.value.id}/features/${id}`) }
function openMilestone(id: string) { if (Date.now() >= suppressClickUntil) goMilestone(id) }
function openFeature(id: string) { if (Date.now() >= suppressClickUntil) goFeature(id) }
function openDetailTask(task: Task) { if (Date.now() >= suppressClickUntil) state.detailTaskId = task.id }
function openLinkedTask(task: Task) { if (Date.now() >= suppressClickUntil) void router.push(`/tasks/${task.id}`) }

function startCreate(type: string) { state.createType = type; state.editType = ''; state.editId = ''; state.title = ''; state.description = ''; state.pickerOpen = false; closeContextMenu(); void nextTick(() => nameInputRef.value?.focus({ preventScroll: true })) }
function startEdit(type: string, item: Project | Milestone | Feature | Task) { state.editType = type; state.editId = item.id; state.createType = ''; state.title = item.title; state.description = item.description; state.pickerOpen = false; closeContextMenu(); void nextTick(() => nameInputRef.value?.focus({ preventScroll: true })) }
function closeEditor() { state.createType = ''; state.editType = ''; state.editId = ''; state.title = ''; state.description = '' }
async function saveEditor() {
  if (!state.title.trim()) return
  if (state.createType === 'task') await addTask(); else await save()
  closeEditor()
}
async function save() {
  const title = state.title.trim(); if (!title) return
  let createdProjectId: string | null = null
    let createdMilestoneId: string | null = null
    let createdFeatureId: string | null = null
    try {
      if (state.createType === 'project') { const created = await request<{ id: string }>(api, '/projects', { method: 'POST', body: JSON.stringify({ title, description: state.description }) }); createdProjectId = created.id }
      else if (state.createType === 'milestone' && project.value) { const created = await request<{ id: string }>(api, `/projects/${project.value.id}/milestones`, { method: 'POST', body: JSON.stringify({ expectedParentVersion: project.value.version, title, description: state.description }) }); createdMilestoneId = created.id }
      else if (state.createType === 'feature' && project.value && milestone.value) { const created = await request<{ id: string }>(api, `/projects/${project.value.id}/milestones/${milestone.value.id}/features`, { method: 'POST', body: JSON.stringify({ expectedParentVersion: milestone.value.version, title, description: state.description }) }); createdFeatureId = created.id }
    else if (state.editType === 'project' && editProject.value) await request(api, `/projects/${editProject.value.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: editProject.value.version, title, description: state.description }) })
    else if (state.editType === 'milestone' && project.value && editMilestone.value) await request(api, `/projects/${project.value.id}/milestones/${editMilestone.value.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: editMilestone.value.version, title, description: state.description }) })
    else if (state.editType === 'feature' && project.value && editFeature.value) await request(api, `/projects/${project.value.id}/milestones/${editFeature.value.milestone.id}/features/${editFeature.value.feature.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: editFeature.value.feature.version, title, description: state.description }) })
    else if (state.editType === 'task') { const item = state.tasks.find(t => t.id === state.editId); if (item) await updateTask(item, { title, description: state.description }) }
    state.createType = ''; state.editType = ''; await refresh()
        if (createdProjectId && !route.path.startsWith(`/planning/projects/${createdProjectId}`)) await router.replace(`/planning/projects/${createdProjectId}`)
        if (createdMilestoneId && project.value) await router.push(`/planning/projects/${project.value.id}/milestones/${createdMilestoneId}`)
        else if (createdFeatureId && project.value && milestone.value) await router.push(`/planning/projects/${project.value.id}/milestones/${milestone.value.id}/features/${createdFeatureId}`)
      } catch (error) {
    if (error instanceof TypeError) {
      const kind = state.editType || state.createType
      if (state.editType) {
        const entityType = kind === 'project' ? 'planning.project' : kind === 'milestone' ? 'planning.milestone' : 'planning.feature'
        const current = kind === 'project' ? editProject.value : kind === 'milestone' ? editMilestone.value : editFeature.value?.feature ?? null
                if (current) {
                  const local = { ...current, title, description: state.description, version: current.version + 1 }
                  await queuePlanning(entityType, current.id, current.version, { operation: 'update', kind, id: current.id, projectId: kind === 'project' ? current.id : project.value?.id, milestoneId: kind === 'feature' ? editFeature.value?.milestone.id : kind === 'milestone' ? project.value?.id : undefined, expectedVersion: current.version, title, description: state.description }, false, local)
          if (kind === 'project') state.projects = state.projects.map(x => x.id === current.id ? { ...x, ...local } as Project : x)
          else if (kind === 'milestone' && project.value) project.value.milestones = project.value.milestones.map(x => x.id === current.id ? { ...x, ...local } as Milestone : x)
          else if (kind === 'feature' && editFeature.value) editFeature.value.milestone.features = editFeature.value.milestone.features.map(x => x.id === current.id ? { ...x, ...local } as Feature : x)
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
async function updateTask(task: Task, patch: { title?: string; description?: string }) {
  try {
    await request(taskApi, `/${task.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: task.version, ...patch }) })
    await refresh()
  } catch (error) {
    if (error instanceof TypeError) {
      const local = { ...task, ...patch, version: task.version + 1 }
      await queueTaskMutation(task, { operation: 'update', kind: 'task', id: task.id, expectedVersion: task.version, ...patch }, local)
      state.tasks = state.tasks.map(x => x.id === task.id ? local : x)
      await cacheRows('tasks.task', state.tasks, true)
      state.error = 'Нет сети. Изменение сохранено и будет синхронизировано позже.'
      return
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
function confirmAction(title: string, body: string, label: string, onConfirm: () => void) {
  state.confirm = { title, body, label, onConfirm }
  void nextTick(() => confirmCancelRef.value?.focus({ preventScroll: true }))
}
function confirmAccept() { const current = state.confirm; state.confirm = null; current?.onConfirm() }
function deleteEntity(type: 'project' | 'milestone' | 'feature', item: Project | Milestone | Feature) {
  confirmAction(`Удалить «${item.title}»?`, 'Плановые задачи будут удалены. Задачи из Backlog и Сегодня останутся в разделе проекта.', 'Удалить', () => void remove(type, item))
}
async function remove(type: string, item: Project | Milestone | Feature) {
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
function deleteTask(task: Task) {
  const sent = taskState(task) !== 'planned'
  confirmAction(`Удалить «${task.title}» из планирования?`, sent ? 'Задача останется в Task Tracker без связи с планом.' : 'Задача будет удалена из плана.', 'Удалить', () => void performDeleteTask(task))
}
async function performDeleteTask(task: Task) {
  try { await request(taskApi, `/${task.id}`, { method: 'DELETE', body: JSON.stringify({ expectedVersion: task.version }) }); state.tasks = state.tasks.filter(x => x.id !== task.id); if (state.detailTaskId === task.id) state.detailTaskId = ''; await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const store = await getOfflineStore(), pending = (await store.listPendingOperations()).filter(x => x.type === 'tasks.task' && x.id === task.id), tail = pending[pending.length - 1]
      const expectedVersion = tail ? (tail.expectedVersion === null ? 1 : tail.expectedVersion + 1) : task.version, now = new Date().toISOString()
      const payload = { operation: 'delete', kind: 'task', id: task.id, expectedVersion }
      await saveOfflineMutation({ type: 'tasks.task', id: task.id, version: expectedVersion! + 1, payload: task, deleted: true, updatedAt: now }, { operationId: offlineId(), type: 'tasks.task', id: task.id, expectedVersion, kind: 'delete', payload, createdAt: now })
      await store.putEntity({ type: 'tasks.task.view', id: task.id, version: expectedVersion! + 1, payload: null, deleted: true, updatedAt: now })
      state.tasks = state.tasks.filter(x => x.id !== task.id); if (state.detailTaskId === task.id) state.detailTaskId = ''; await cacheRows('tasks.task', state.tasks, true); state.error = 'Нет сети. Удаление сохранено и будет синхронизировано позже.'; return
    }
    state.error = (error as Error).message
  }
}
function startInlineEdit(kind: 'title' | 'description') {
  const task = detailTask.value; if (!task) return
  state.editingKind = kind
  state.editDraft = kind === 'title' ? task.title : (task.description || '')
  void nextTick(() => {
    const input = kind === 'title' ? titleInputRef.value : descInputRef.value
    input?.focus({ preventScroll: true })
    if (kind === 'title') titleInputRef.value?.select()
  })
}
function finishInlineEdit(kind: 'title' | 'description', save: boolean) {
  if (state.editingKind !== kind) return
  state.editingKind = ''
  const task = detailTask.value; if (!task || !save) return
  const value = state.editDraft.trim()
  if (kind === 'title') { if (value && value !== task.title) void updateTask(task, { title: value }) }
  else if (value !== (task.description || '')) void updateTask(task, { description: value })
}
function deleteFromSheet() {
  const type = state.editType, item = currentEditItem.value
  if (!type || !item) return
  closeEditor()
  if (type === 'task') deleteTask(item as Task)
  else deleteEntity(type as 'project' | 'milestone' | 'feature', item as Project | Milestone | Feature)
}

// —— context menu + gestures ——
function openContextMenu(kind: string, id: string, row: HTMLElement | null, point: { x: number; y: number } | null) {
  if (state.orderMode) return
  if (!state.tasks.some(t => t.id === id) && !(kind === 'milestone' ? project.value?.milestones.some(m => m.id === id) : kind === 'feature' ? milestone.value?.features.some(f => f.id === id) : false)) return
  menuAnchorEl = row
  state.menuPoint = point
  state.menuKind = kind
  state.menuId = id
  state.menuOpen = true
  void nextTick(positionContextMenu)
}
function toggleContextMenu(event: Event, kind: string, id: string) {
  if (state.menuOpen && state.menuKind === kind && state.menuId === id) { closeContextMenu(); return }
  const row = (event.currentTarget as HTMLElement).closest('.planning-context-row') as HTMLElement | null
  openContextMenu(kind, id, row, null)
}
function closeContextMenu() { state.menuOpen = false; state.menuKind = ''; state.menuId = ''; state.menuPoint = null; menuAnchorEl = null }
function positionContextMenu() {
  const menuEl = contextMenuRef.value; if (!menuEl || !state.menuOpen) return
  const phone = document.querySelector<HTMLElement>('.phone-shell'); if (!phone) return
  const phoneRect = phone.getBoundingClientRect()
  const width = menuEl.offsetWidth || 224, height = menuEl.offsetHeight
  const leftLimit = 8, rightLimit = phoneRect.width - 8
  const topline = document.querySelector<HTMLElement>('.topline')?.getBoundingClientRect()
  const navTop = document.querySelector<HTMLElement>('.bottom-window')?.getBoundingClientRect().top ?? phoneRect.bottom
  const topLimit = Math.max((topline?.bottom ?? phoneRect.top) - phoneRect.top + 8, 8)
  const bottomLimit = Math.max(navTop - 8 - phoneRect.top, topLimit + 40)
  let x: number, y: number
  if (state.menuPoint) {
    x = state.menuPoint.x + 8 - phoneRect.left
    y = state.menuPoint.y + 8 - phoneRect.top
    if (x + width > rightLimit) x = state.menuPoint.x - width - 8 - phoneRect.left
    if (y + height > bottomLimit) y = state.menuPoint.y - height - 8 - phoneRect.top
  } else {
    const anchor = menuAnchorEl?.getBoundingClientRect()
    if (anchor) {
      x = anchor.right - width - phoneRect.left
      y = anchor.top - height - 6 - phoneRect.top
      if (y < topLimit) y = anchor.bottom + 6 - phoneRect.top
      if (y + height > bottomLimit) y = anchor.top - height - 6 - phoneRect.top
    } else { x = rightLimit - width - 8; y = topLimit + 40 }
  }
  state.menuX = Math.max(leftLimit, Math.min(x, rightLimit - width))
  state.menuY = Math.max(topLimit, Math.min(y, bottomLimit - height))
}
function menuKeydown(event: KeyboardEvent) {
  const buttons = [...(contextMenuRef.value?.querySelectorAll<HTMLButtonElement>('button') || [])]
  const index = buttons.indexOf(document.activeElement as HTMLButtonElement)
  if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
    event.preventDefault()
    buttons[(index + (event.key === 'ArrowDown' ? 1 : -1) + buttons.length) % buttons.length]?.focus()
  } else if (event.key === 'Home' || event.key === 'End') {
    event.preventDefault(); buttons[event.key === 'Home' ? 0 : buttons.length - 1]?.focus()
  }
}
function runContextAction(item: ContextItem) { closeContextMenu(); item.action() }
function rowContextMenu(event: MouseEvent, kind: string, id: string) {
  if (state.orderMode) return
  const row = (event.currentTarget as HTMLElement).closest('.planning-context-row') as HTMLElement | null
  openContextMenu(kind, id, row, { x: event.clientX, y: event.clientY })
}
function rowPointerDown(event: PointerEvent, kind: string, id: string) {
  if (state.orderMode || event.button > 0 || (event.target as HTMLElement).closest('.planning-menu-trigger')) return
  const row = (event.currentTarget as HTMLElement).closest('.planning-context-row') as HTMLElement | null
  contextTarget = { row, kind, id, pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, moved: false, long: false, timer: 0 }
  if (event.pointerType === 'touch' || event.pointerType === 'pen') {
    contextTarget.timer = window.setTimeout(() => {
      if (!contextTarget || contextTarget.moved) return
      contextTarget.long = true
      suppressClickUntil = Date.now() + 400
      openContextMenu(kind, id, contextTarget.row, null)
    }, 480)
  }
  try { (event.currentTarget as HTMLElement).setPointerCapture?.(event.pointerId) } catch { /* ignore */ }
}
function rowPointerMove(event: PointerEvent) {
  const current = contextTarget; if (!current || current.pointerId !== event.pointerId) return
  const dx = event.clientX - current.startX, dy = event.clientY - current.startY
  if (Math.hypot(dx, dy) > 8) { current.moved = true; window.clearTimeout(current.timer) }
  if (Math.abs(dy) > Math.abs(dx) && Math.abs(dy) > 10) { contextTarget = null; return }
  if (dx < -12 && Math.abs(dx) > Math.abs(dy) * 1.1) event.preventDefault()
}
function rowPointerUp(event: PointerEvent) {
  const current = contextTarget; if (!current || current.pointerId !== event.pointerId) return
  window.clearTimeout(current.timer); contextTarget = null
  if (current.long) return
  const dx = event.clientX - current.startX, dy = event.clientY - current.startY
  if (Math.abs(dx) < Math.abs(dy) * 1.2) return
  if (dx <= -56) { suppressClickUntil = Date.now() + 400; openContextMenu(current.kind, current.id, current.row, null) }
  else if (dx >= 45 && state.menuOpen && state.menuKind === current.kind && state.menuId === current.id) { suppressClickUntil = Date.now() + 400; closeContextMenu() }
}
function suppressClick(event: Event) {
  if (Date.now() >= suppressClickUntil) return
  const target = event.target as HTMLElement
  if (target.closest('.row-context-menu, .app-confirm-layer, .planning-sheet, .planning-picker-wrap')) return
  event.preventDefault(); event.stopPropagation()
}
function onDocumentPointerDown(event: PointerEvent) {
  const target = event.target as HTMLElement
  if (state.pickerOpen && !target.closest('.planning-picker-wrap')) state.pickerOpen = false
  if (state.menuOpen && contextMenuRef.value && !contextMenuRef.value.contains(target) && !target.closest('.planning-menu-trigger')) closeContextMenu()
}
function onDocumentKeydown(event: KeyboardEvent) {
  if (event.key !== 'Escape') return
  if (state.confirm) state.confirm = null
  else if (state.menuOpen) closeContextMenu()
  else if (state.createType || state.editType) closeEditor()
}

// —— order mode drag ——
function toggleOrderMode() { state.orderMode = !state.orderMode; closeContextMenu(); state.pickerOpen = false }
function orderDragStart(event: PointerEvent, _id: string) {
  if (!state.orderMode || dragCleanup || event.button > 0) return
  const handle = event.target as HTMLElement
  const row = handle.closest<HTMLElement>('.planning-order-row'); if (!row) return
  event.preventDefault()
  const sectionRoot = sectionRef.value, scroll = scrollRef.value
  if (!sectionRoot || !scroll) return
  const rect = row.getBoundingClientRect()
  const ghost = row.cloneNode(true) as HTMLElement
  ghost.classList.add('planning-order-ghost', 'reorder-ghost')
  ghost.style.width = `${rect.width}px`; ghost.style.height = `${rect.height}px`
  sectionRoot.append(ghost)
  row.classList.add('planning-order-source')
  const pointerId = event.pointerId, offsetX = rect.left - event.clientX, offsetY = rect.top - event.clientY
  const move = (e: PointerEvent) => {
    if (e.pointerId !== pointerId) return
    const bounds = scroll.getBoundingClientRect(), pad = 4
    const x = Math.max(bounds.left + pad, Math.min(e.clientX + offsetX, bounds.right - rect.width - pad))
    const y = Math.max(bounds.top + pad, Math.min(e.clientY + offsetY, bounds.bottom - rect.height - pad))
    ghost.style.left = `${x}px`; ghost.style.top = `${y}px`
    const candidates = [...scroll.querySelectorAll('.planning-order-row')].filter(candidate => candidate !== row)
    const target = candidates.find(candidate => { const r = candidate.getBoundingClientRect(); return e.clientY < r.top + r.height / 2 })
    if (target) row.before(target)
    else (scroll.querySelector('.planning-order-row:last-child') as HTMLElement | null)?.after(row)
  }
  const cleanup = (commit: boolean) => {
    if (!dragCleanup) return
    window.removeEventListener('pointermove', move, true)
    window.removeEventListener('pointerup', up, true)
    window.removeEventListener('pointercancel', cancel, true)
    window.removeEventListener('blur', onBlur)
    window.removeEventListener('keydown', onKey, true)
    dragCleanup = null
    ghost.remove(); row.classList.remove('planning-order-source')
    if (commit) commitOrderFromDom()
    else state.renderTick += 1
  }
  const up = (e: PointerEvent) => { if (e.pointerId === pointerId) cleanup(true) }
  const cancel = (e: PointerEvent) => { if (e.pointerId === pointerId) cleanup(false) }
  const onBlur = () => cleanup(false)
  const onKey = (e: KeyboardEvent) => { if (e.key === 'Escape') cleanup(false) }
  dragCleanup = cleanup
  window.addEventListener('pointermove', move, true)
  window.addEventListener('pointerup', up, true)
  window.addEventListener('pointercancel', cancel, true)
  window.addEventListener('blur', onBlur)
  window.addEventListener('keydown', onKey, true)
}
function commitOrderFromDom() {
  const scroll = scrollRef.value; if (!scroll) return
  const rows = [...scroll.querySelectorAll<HTMLElement>('.planning-order-row')]
  if (rows.length < 2) return
  if (depth.value === 1) {
    const ids = rows.map(row => row.dataset.orderId || '').filter(Boolean)
    if (ids.length === (project.value?.milestones.length ?? 0)) void commitPlanOrder('milestone', ids)
  } else if (depth.value === 2) {
    const ids = rows.map(row => row.dataset.orderId || '').filter(Boolean)
    if (ids.length === (milestone.value?.features.length ?? 0)) void commitPlanOrder('feature', ids)
  } else if (depth.value === 3) {
    const ids = rows.filter(row => row.querySelector('.planning-order-handle')).map(row => row.dataset.orderId || '').filter(Boolean)
    void commitTaskOrder(ids)
  }
}
async function commitPlanOrder(type: 'milestone' | 'feature', ids: string[]) {
  if (!project.value) return
  const current = type === 'milestone' ? project.value.milestones.map(x => x.id) : (milestone.value?.features.map(x => x.id) || [])
  if (ids.length !== current.length || ids.every((id, index) => id === current[index])) return
  const path = type === 'milestone' ? `/projects/${project.value.id}/milestones/order` : `/projects/${project.value.id}/milestones/${milestone.value!.id}/features/order`
  const expectedVersion = type === 'milestone' ? project.value.version : milestone.value!.version
  try { await request(api, path, { method: 'PUT', body: JSON.stringify({ expectedVersion, ids }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      if (type === 'milestone') {
        const original = project.value.milestones
        const local = { ...project.value, version: project.value.version + 1, milestones: ids.map((id, position) => { const item = original.find(row => row.id === id)!; return { ...item, position, version: item.version + (item.position === position ? 0 : 1) } }) }
        const order = local.milestones.map(item => ({ id: item.id, expectedVersion: original.find(row => row.id === item.id)!.version }))
        const moved = local.milestones.find((item, index) => original[index]?.id !== item.id) || local.milestones[0]
        const sourceVersion = original.find(item => item.id === moved.id)!.version
        await queuePlanning('planning.milestone', moved.id, sourceVersion, { operation: 'reorder', kind: 'milestone', id: moved.id, projectId: project.value.id, expectedVersion: sourceVersion, expectedParentVersion: project.value.version, order }, false, moved)
        state.projects = state.projects.map(item => item.id === local.id ? local : item)
      } else if (milestone.value) {
        const original = milestone.value.features
        const local = { ...milestone.value, version: milestone.value.version + 1, features: ids.map((id, position) => { const item = original.find(row => row.id === id)!; return { ...item, position, version: item.version + (item.position === position ? 0 : 1) } }) }
        const order = local.features.map(item => ({ id: item.id, expectedVersion: original.find(row => row.id === item.id)!.version }))
        const moved = local.features.find((item, index) => original[index]?.id !== item.id) || local.features[0]
        const sourceVersion = original.find(item => item.id === moved.id)!.version
        await queuePlanning('planning.feature', moved.id, sourceVersion, { operation: 'reorder', kind: 'feature', id: moved.id, projectId: project.value.id, milestoneId: milestone.value.id, expectedVersion: sourceVersion, expectedParentVersion: milestone.value.version, order }, false, moved)
        project.value.milestones = project.value.milestones.map(item => item.id === local.id ? local : item)
      }
      await cacheRows('planning.project', state.projects, true)
      state.error = 'Нет сети. Порядок сохранён и будет синхронизирован позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function commitTaskOrder(ids: string[]) {
  if (!project.value || !milestone.value || !feature.value) return
  const tasks = linkedTasks.value.filter(x => taskState(x) === 'planned')
  if (ids.length !== tasks.length || ids.every((id, index) => id === tasks[index].id)) return
  const order = ids.map(id => { const item = tasks.find(t => t.id === id)!; return { id, expectedVersion: item.version } })
  try {
    await request(taskApi, '/order', { method: 'PUT', body: JSON.stringify({ location: 'planned', projectId: project.value.id, milestoneId: milestone.value.id, featureId: feature.value.id, items: order }) })
    await refresh()
  } catch (error) {
    if (error instanceof TypeError) {
      const index = ids.findIndex((id, i) => tasks[i]?.id !== id)
      const source = tasks.find(t => t.id === (ids[index] || ids[0]))!
      const local = { ...source, position: ids.indexOf(source.id), version: source.version + 1 }
      await queueTaskMutation(source, { operation: 'reorder', kind: 'task', id: source.id, placement: 'planned', planning: { projectId: project.value.id, milestoneId: milestone.value.id, featureId: feature.value.id }, order }, local)
      state.tasks = state.tasks.map(x => { const pos = ids.indexOf(x.id); return pos < 0 ? x : { ...x, position: pos, version: x.version + 1 } })
      await cacheRows('tasks.task', state.tasks, true)
      state.error = 'Нет сети. Порядок сохранён и будет синхронизирован позже.'; return
    }
    state.error = (error as Error).message
  }
}
function onUnmountedCleanup() { dragCleanup?.(false); dragCleanup = null; window.clearTimeout(contextTarget?.timer || 0) }
watch(() => route.fullPath, () => { state.detailTaskId = ''; state.orderMode = false; state.pickerOpen = false; closeContextMenu(); void refresh() })
onMounted(() => {
  document.addEventListener('pointerdown', onDocumentPointerDown, true)
  document.addEventListener('keydown', onDocumentKeydown)
  void refresh()
})
onBeforeUnmount(onUnmountedCleanup)
</script>

<template>
  <section ref="sectionRef" class="planning-section planning-page" aria-label="Планирование" @click.capture="suppressClick">
    <div v-if="state.error" class="planning-error" role="alert">{{ state.error }}<button aria-label="Закрыть сообщение" @click="state.error = ''">×</button></div>

    <div v-if="depth <= 1" class="planning-toolbar">
      <div class="planning-picker-wrap">
        <button class="planning-picker" type="button" aria-haspopup="listbox" :aria-expanded="state.pickerOpen ? 'true' : 'false'" @click="state.pickerOpen = !state.pickerOpen">
          <span>{{ project?.title || 'Нет проектов' }}</span><span class="planning-picker-arrow">⌄</span>
        </button>
        <div v-if="state.pickerOpen" class="planning-picker-menu open">
          <div v-for="item in visibleProjects" :key="item.id" class="planning-picker-option" :class="{ active: item.id === project?.id }">
            <button type="button" @click="goProject(item.id)">{{ item.title }}</button>
            <button type="button" class="planning-picker-edit" :aria-label="`Редактировать ${item.title}`" @click="startEdit('project', item)">✎</button>
          </div>
        </div>
      </div>
      <button class="plus" type="button" aria-label="Добавить проект" @click="startCreate('project')">＋</button>
    </div>

    <div v-if="state.busy" class="planning-empty">Загружаем план…</div>
    <div v-else-if="!project" class="planning-empty">Создайте проект, чтобы начать планирование.</div>
    <div v-else ref="scrollRef" class="scroll planning-scroll">
      <template v-if="depth === 1">
        <div class="planning-project-summary">
          <div class="planning-summary-top"><span>Прогресс проекта</span><strong>{{ project.progressPercent }}%</strong></div>
          <div class="planning-progress-track"><div class="planning-progress-fill" :style="{ width: `${project.progressPercent}%` }" /></div>
        </div>
        <div class="planning-list-head"><span>Вехи</span><div class="planning-list-actions"><button class="order-mode-toggle planning-order-toggle" type="button" :aria-pressed="state.orderMode ? 'true' : 'false'" :aria-label="state.orderMode ? 'Готово' : 'Включить сортировку'" @click="toggleOrderMode"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h11M4 12h11M4 17h11M19 6v12m-2.5-2.5L19 18l2.5-2.5" /></svg></button><button type="button" aria-label="Добавить веху" @click="startCreate('milestone')">＋</button></div></div>
        <div v-if="!project.milestones.length" class="planning-empty">Вехи появятся здесь.</div>
        <div :key="state.renderTick" class="planning-roadmap">
          <template v-if="state.orderMode">
            <div v-for="item in project.milestones" :key="'order-' + item.id" class="planning-milestone-row planning-order-row" :data-order-id="item.id">
              <span class="planning-milestone-title">{{ item.title }}</span>
              <span class="planning-milestone-progress"><strong>{{ item.progressPercent }}%</strong><div class="planning-progress-track"><div class="planning-progress-fill" :style="{ width: `${item.progressPercent}%` }" /></div></span>
              <span class="planning-order-handle handle" aria-hidden="true" @pointerdown="orderDragStart($event, item.id)"><span><i></i><i></i><i></i><i></i><i></i><i></i></span></span>
            </div>
          </template>
          <template v-else>
            <div v-for="item in project.milestones" :key="item.id" class="planning-milestone-row planning-context-row" :class="{ 'context-active': isContextActive('milestone', item.id) }" @pointerdown="rowPointerDown($event, 'milestone', item.id)" @pointermove="rowPointerMove" @pointerup="rowPointerUp" @pointercancel="rowPointerUp" @contextmenu.prevent="rowContextMenu($event, 'milestone', item.id)">
              <button class="planning-row-main" type="button" @click="openMilestone(item.id)">
                <span class="planning-milestone-title">{{ item.title }}</span>
                <span class="planning-milestone-progress"><strong>{{ item.progressPercent }}%</strong><div class="planning-progress-track"><div class="planning-progress-fill" :style="{ width: `${item.progressPercent}%` }" /></div></span>
              </button>
              <span class="planning-chevron-slot"><span class="planning-row-arrow" aria-hidden="true">›</span><button class="planning-menu-trigger" type="button" aria-haspopup="menu" :aria-expanded="isContextActive('milestone', item.id) ? 'true' : 'false'" :aria-label="`Действия с «${item.title}»`" @click.stop="toggleContextMenu($event, 'milestone', item.id)">⋯</button></span>
            </div>
          </template>
        </div>
      </template>

      <template v-else-if="milestone && depth === 2">
        <button class="doc-action doc-back planning-inline-back" type="button" @click="goProject(project.id)">← Назад</button>
        <div class="planning-head-card">
          <div class="planning-kicker">{{ project.title }}</div>
          <div class="planning-head-main">
            <button class="planning-head-title" type="button" @click="startEdit('milestone', milestone)">{{ milestone.title }}</button>
            <div class="planning-head-actions"><button class="order-mode-toggle planning-order-toggle" type="button" :aria-pressed="state.orderMode ? 'true' : 'false'" :aria-label="state.orderMode ? 'Готово' : 'Включить сортировку'" @click="toggleOrderMode"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h11M4 12h11M4 17h11M19 6v12m-2.5-2.5L19 18l2.5-2.5" /></svg></button><button class="planning-head-plus" type="button" aria-label="Добавить фичу" @click="startCreate('feature')">＋</button></div>
          </div>
          <div class="planning-milestone-progress-block">
            <div class="planning-progress-top"><span>Прогресс вехи</span><strong>{{ completedFeatures }} из {{ milestone.features.length }} фич</strong></div>
            <div class="planning-progress-track"><div class="planning-progress-fill" :style="{ width: `${milestone.progressPercent}%` }" /></div>
          </div>
        </div>
        <div v-if="!milestone.features.length" class="planning-empty">Фичи появятся здесь.</div>
        <div :key="state.renderTick" class="planning-feature-list">
          <template v-if="state.orderMode">
            <div v-for="item in milestone.features" :key="'order-' + item.id" class="planning-feature-row planning-order-row" :data-order-id="item.id">
              <span>{{ item.title }}</span>
              <span class="planning-order-handle handle" aria-hidden="true" @pointerdown="orderDragStart($event, item.id)"><span><i></i><i></i><i></i><i></i><i></i><i></i></span></span>
            </div>
          </template>
          <template v-else>
            <div v-for="item in milestone.features" :key="item.id" class="planning-feature-row planning-context-row" :class="{ 'context-active': isContextActive('feature', item.id) }" @pointerdown="rowPointerDown($event, 'feature', item.id)" @pointermove="rowPointerMove" @pointerup="rowPointerUp" @pointercancel="rowPointerUp" @contextmenu.prevent="rowContextMenu($event, 'feature', item.id)">
              <button class="planning-row-main" type="button" @click="openFeature(item.id)"><span>{{ item.title }}</span></button>
              <span class="planning-chevron-slot"><span class="planning-row-arrow" aria-hidden="true">›</span><button class="planning-menu-trigger" type="button" aria-haspopup="menu" :aria-expanded="isContextActive('feature', item.id) ? 'true' : 'false'" :aria-label="`Действия с «${item.title}»`" @click.stop="toggleContextMenu($event, 'feature', item.id)">⋯</button></span>
            </div>
          </template>
        </div>
      </template>

      <template v-else-if="feature && milestone">
        <template v-if="state.detailTaskId && detailTask">
          <button class="doc-action doc-back planning-inline-back" type="button" @click="state.detailTaskId = ''">← Назад</button>
          <div class="planning-task-context">{{ project.title }} · {{ milestone.title }} · {{ feature.title }}</div>
          <button v-if="state.editingKind !== 'title'" class="planning-detail-title" type="button" @click="startInlineEdit('title')">{{ detailTask.title }}</button>
          <input v-else ref="titleInputRef" class="planning-detail-title-input" type="text" maxlength="120" v-model="state.editDraft" @blur="finishInlineEdit('title', true)" @keydown.enter.prevent="finishInlineEdit('title', true)" @keydown.esc.stop.prevent="finishInlineEdit('title', false)" />
          <div class="planning-description-card">
            <button v-if="state.editingKind !== 'description'" class="planning-detail-description" type="button" @click="startInlineEdit('description')">{{ detailTask.description || 'Описание пока не добавлено.' }}</button>
            <textarea v-else ref="descInputRef" class="planning-detail-description-input" v-model="state.editDraft" @blur="finishInlineEdit('description', true)" @keydown.esc.stop.prevent="finishInlineEdit('description', false)" />
          </div>
          <div class="planning-task-actions">
            <span></span>
            <div class="planning-task-center">
              <button v-if="taskMeta(detailTask) === null" class="planning-task-backlog" type="button" @click="moveTask(detailTask, 'backlog')">Backlog</button>
              <button v-else class="planning-task-detail-status" :class="taskMeta(detailTask)?.kind" type="button" @click="openLinkedTask(detailTask)">{{ taskMeta(detailTask)!.label === 'Backlog' ? 'В бэклоге' : taskMeta(detailTask)!.label }}</button>
            </div>
            <button class="planning-task-delete" type="button" aria-label="Удалить задачу" @click="deleteTask(detailTask)"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M9 4.75h6m-8.5 2.5h10m-8.5 0-.35 10.1c-.03.84.64 1.55 1.48 1.55h4.74c.84 0 1.51-.71 1.48-1.55l-.35-10.1M10.25 10v5.5M13.75 10v5.5" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" /></svg></button>
          </div>
        </template>
        <template v-else>
          <button class="doc-action doc-back planning-inline-back" type="button" @click="goMilestone(milestone.id)">← Назад</button>
          <div class="planning-head-card planning-feature-head">
            <div class="planning-kicker">{{ project.title }} · {{ milestone.title }}</div>
            <div class="planning-head-main">
              <button class="planning-head-title" type="button" @click="startEdit('feature', feature)">{{ feature.title }}</button>
              <span class="planning-task-count" :aria-label="`${linkedTasks.length} задач`">{{ linkedTasks.length }}</span>
            </div>
          </div>
          <div class="planning-list-head planning-feature-list-head"><span>Задачи</span><div class="planning-list-actions"><button class="order-mode-toggle planning-order-toggle" type="button" :aria-pressed="state.orderMode ? 'true' : 'false'" :aria-label="state.orderMode ? 'Готово' : 'Включить сортировку'" @click="toggleOrderMode"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h11M4 12h11M4 17h11M19 6v12m-2.5-2.5L19 18l2.5-2.5" /></svg></button><button type="button" aria-label="Добавить задачу" @click="startCreate('task')">＋</button></div></div>
          <div v-if="!linkedTasks.length" class="planning-empty">У этой фичи пока нет задач.</div>
          <div :key="state.renderTick" class="planning-feature-tasks">
            <template v-if="state.orderMode">
              <div v-for="item in linkedTasks" :key="'order-' + item.id" class="planning-feature-task planning-order-row" :data-order-id="item.id">
                <span class="planning-feature-task-title">{{ item.title }}</span>
                <span v-if="taskState(item) === 'planned'" class="planning-order-handle handle" aria-hidden="true" @pointerdown="orderDragStart($event, item.id)"><span><i></i><i></i><i></i><i></i><i></i><i></i></span></span>
              </div>
            </template>
            <template v-else>
              <div v-for="task in linkedTasks" :key="task.id" class="planning-feature-task planning-context-row" :class="{ 'context-active': isContextActive('task', task.id) }" @pointerdown="rowPointerDown($event, 'task', task.id)" @pointermove="rowPointerMove" @pointerup="rowPointerUp" @pointercancel="rowPointerUp" @contextmenu.prevent="rowContextMenu($event, 'task', task.id)">
                <button class="planning-feature-task-title" type="button" @click="openDetailTask(task)">{{ task.title }}</button>
                <button v-if="taskState(task) === 'planned'" class="planning-send-backlog" type="button" @click="moveTask(task, 'backlog')">Backlog</button>
                <button v-else class="planning-task-state" :class="taskMeta(task)?.kind" type="button" @click="openLinkedTask(task)">{{ taskMeta(task)?.label }}</button>
                <button class="planning-menu-trigger planning-task-menu-trigger" type="button" aria-haspopup="menu" :aria-expanded="isContextActive('task', task.id) ? 'true' : 'false'" :aria-label="`Действия с «${task.title}»`" @click.stop="toggleContextMenu($event, 'task', task.id)">⋯</button>
              </div>
            </template>
          </div>
        </template>
      </template>
    </div>

    <div v-if="state.createType || state.editType" class="overlay open" @click="closeEditor"></div>
    <section v-if="state.createType || state.editType" class="sheet planning-sheet open" aria-label="Редактор планирования">
      <div class="sheet-head"><div class="sheet-title">{{ sheetTitle }}</div><button class="sheet-close" type="button" aria-label="Закрыть" @click="closeEditor">×</button></div>
      <div class="planning-sheet-context"></div>
      <label class="planning-field-label" for="planning-name">Название</label>
      <input id="planning-name" ref="nameInputRef" class="name-field" type="text" maxlength="90" :placeholder="namePlaceholder" :aria-label="namePlaceholder" v-model="state.title" @keydown.enter.prevent="saveEditor" @keydown.esc.prevent="closeEditor" />
      <label v-if="state.createType === 'task' || state.editType" class="planning-field-label" for="planning-description">Описание</label>
      <textarea v-if="state.createType === 'task' || state.editType" id="planning-description" class="planning-description-field" rows="3" maxlength="600" placeholder="Описание" aria-label="Описание" v-model="state.description" @keydown.esc.prevent="closeEditor"></textarea>
      <button class="create-submit" type="button" @click="saveEditor">{{ saveButtonLabel }}</button>
      <button v-if="state.editType" class="planning-sheet-delete" type="button" @click="deleteFromSheet">Удалить</button>
    </section>

    <div v-if="state.menuOpen" ref="contextMenuRef" class="row-context-menu" role="menu" :aria-label="`Действия с «${contextMenuTitle}»`" :style="{ left: `${state.menuX}px`, top: `${state.menuY}px` }" @keydown="menuKeydown">
      <button v-for="item in contextItems" :key="item.label" class="row-context-item" :class="{ danger: item.danger }" type="button" role="menuitem" @click="runContextAction(item)">{{ item.label }}</button>
    </div>

    <div v-if="state.confirm" class="app-confirm-layer" @pointerdown.self="state.confirm = null">
      <div class="app-confirm-card" role="dialog" aria-modal="true" aria-labelledby="planning-confirm-title">
        <div id="planning-confirm-title" class="app-confirm-title">{{ state.confirm.title }}</div>
        <div class="app-confirm-body">{{ state.confirm.body }}</div>
        <div class="app-confirm-actions">
          <button ref="confirmCancelRef" class="app-confirm-cancel" type="button" @click="state.confirm = null">Отмена</button>
          <button class="app-confirm-accept" type="button" @click="confirmAccept">{{ state.confirm.label }}</button>
        </div>
      </div>
    </div>
  </section>
</template>
