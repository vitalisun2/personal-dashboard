<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, reactive, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { getOfflineStore, saveOfflineMutation } from '../../offline/runtime'
import type { OfflineEntity, SyncOperation } from '../../offline/types'

type Task = { id: string; title: string; description: string; projectId?: string; milestoneId?: string; featureId?: string; location: string; workStatus: string; sectionId?: string; position: number; version: number }
type Section = { id: string; name: string; location: string; position: number; version: number }
type ProjectLabel = { id: string; title: string }
type MenuItem = { label: string; danger?: boolean; action: () => void }
type GroupView = { kind: 'plain'; key: string; title: string; section: Section; tasks: Task[] } | { kind: 'project'; key: string; title: string; projectId: string; tasks: Task[] }
type SwipeState = { key: string; pointerId: number; startX: number; startY: number; moved: boolean; long: boolean; timer: number }
type DragState = { kind: 'task' | 'section'; id: string; pointerId: number; startX: number; startY: number; row: HTMLElement; target: HTMLElement | null; place: 'before' | 'after' | 'inside' | ''; started: boolean; ghost: HTMLDivElement | null; offsetX: number; offsetY: number; width: number; height: number; label: string }

const api = '/api/v2/tasks'
const route = useRoute(), router = useRouter()
const state = reactive({
  bucket: 'Backlog' as 'Backlog' | 'Сегодня', filter: 'all', archive: false, orderMode: false,
  expanded: { Backlog: new Set<string>(), Сегодня: new Set<string>() },
  tasks: [] as Task[], sections: [] as Section[], projects: [] as ProjectLabel[],
  detail: null as Task | null, busy: false, error: '',
  creating: false, createType: 'task' as 'task' | 'section', createListOpen: false,
  title: '', description: '', sectionId: '',
  renaming: null as null | { kind: 'task' | 'section'; id: string; value: string },
  detailEditing: '' as '' | 'title' | 'description',
})
const location = computed(() => state.archive ? 'Archived' : state.bucket === 'Сегодня' ? 'Today' : 'Backlog')
const filtered = computed(() => state.tasks.filter(task => !state.detail || task.id === state.detail.id).filter(task => state.filter === 'all' || statusName(task.workStatus) === state.filter))
const linkedGroups = computed(() => {
  const ids = [...new Set(filtered.value.filter(task => task.projectId).map(task => task.projectId!))]
  return ids.map(projectId => ({ projectId, title: state.projects.find(x => x.id === projectId)?.title || 'Проект', tasks: filtered.value.filter(task => task.projectId === projectId).sort((a, b) => a.position - b.position) }))
})
const groups = computed<GroupView[]>(() => {
  if (state.archive) return []
  const result: GroupView[] = []
  for (const section of state.sections) {
    const tasks = filtered.value.filter(item => item.sectionId === section.id)
    if (state.filter !== 'all' && !tasks.length) continue
    result.push({ kind: 'plain', key: `section:${section.id}`, title: section.name, section, tasks })
  }
  for (const group of linkedGroups.value) {
    if (!group.tasks.length) continue
    result.push({ kind: 'project', key: `project:${group.projectId}`, title: group.title, projectId: group.projectId, tasks: group.tasks })
  }
  return result
})
const allOpen = computed(() => {
  const keys = groups.value.map(g => g.key)
  return keys.length > 0 && keys.every(key => state.expanded[state.bucket].has(key))
})
const showEmpty = computed(() => state.archive ? state.tasks.length === 0 && !state.busy : groups.value.length === 0 && !state.busy)
const emptyText = computed(() => state.busy ? 'Загружаем задачи…' : state.archive ? 'Архив пока пуст.' : 'Здесь пока нет задач.')
const detailOrigin = computed(() => state.detail ? originPath(state.detail) : '')
const detailMoveLabel = computed(() => { const task = state.detail; if (!task) return ''; return isArchived(task) ? 'В Backlog' : isToday(task) ? 'В Backlog' : 'В Сегодня' })
const renamingKey = computed(() => state.renaming ? (state.renaming.kind === 'section' ? `section:${state.renaming.id}` : `task:${state.renaming.id}`) : '')
const renamingValue = computed({ get: () => state.renaming?.value ?? '', set: (value: string) => { if (state.renaming) state.renaming.value = value } })
const filters = [{ value: 'all', label: 'Все', dot: '' }, { value: 'New', label: 'Новые', dot: 'new' }, { value: 'InProgress', label: 'В работе', dot: 'work' }, { value: 'Done', label: 'Готово', dot: 'done' }]

const rootEl = ref<HTMLElement | null>(null)
const groupsEl = ref<HTMLElement | null>(null)
const scrollEl = ref<HTMLElement | null>(null)
const menuEl = ref<HTMLElement | null>(null)
const titleInputEl = ref<HTMLInputElement | null>(null)
const descriptionInputEl = ref<HTMLTextAreaElement | null>(null)
const renameInputEl = ref<HTMLInputElement | null>(null)
const nameFieldEl = ref<HTMLInputElement | null>(null)
const confirmCancelEl = ref<HTMLButtonElement | null>(null)

let swipe: SwipeState | null = null
let drag: DragState | null = null
let suppressOpenUntil = 0
let toastTimer = 0
let renameDone = false

// ---------- toast ----------
const toast = reactive({ text: '', show: false })
function flash(message: string) { clearTimeout(toastTimer); toast.text = message; toast.show = true; toastTimer = window.setTimeout(() => { toast.show = false }, 1600) }

// ---------- confirm dialog ----------
const confirmBox = reactive({ open: false, title: '', body: '', confirmLabel: 'Подтвердить', onConfirm: null as (() => void) | null })
function askConfirm(options: { title: string; body?: string; confirmLabel?: string; onConfirm: () => void }) {
  closeMenu()
  confirmBox.title = options.title; confirmBox.body = options.body || ''; confirmBox.confirmLabel = options.confirmLabel || 'Подтвердить'; confirmBox.onConfirm = options.onConfirm; confirmBox.open = true
}
function closeConfirm() { confirmBox.open = false; confirmBox.onConfirm = null }
function runConfirm() { const callback = confirmBox.onConfirm; closeConfirm(); callback?.() }
watch(() => confirmBox.open, open => { if (open) nextTick(() => confirmCancelEl.value?.focus()) })

// ---------- context menu ----------
const menu = reactive({ open: false, kind: '' as 'task' | 'section' | 'project', key: '', items: [] as MenuItem[], x: 0, y: 0 })
const revealedKey = ref('')
function closeMenu() { if (!menu.open && !revealedKey.value) return; menu.open = false; menu.items = []; revealedKey.value = '' }
const isToday = (task: Task) => String(task.location).toLowerCase() === 'today'
const isArchived = (task: Task) => String(task.location).toLowerCase() === 'archived'
const isLinked = (task: Task) => Boolean(task.projectId && task.featureId)
const taskById = (id: string) => state.tasks.find(t => t.id === id)
function statusName(status: string) { return ({ '0': 'New', '1': 'InProgress', '2': 'Done', 'new': 'New', 'inProgress': 'InProgress', 'done': 'Done', 'in_progress': 'InProgress', 'active': 'InProgress', 'completed': 'Done', 'New': 'New', 'InProgress': 'InProgress', 'Done': 'Done' } as Record<string, string>)[String(status)] || String(status) }
function workState(status: string) { const s = statusName(status); return s === 'Done' ? 'completed' : s === 'InProgress' ? 'in_progress' : 'new' }
function workLabel(status: string) { return ({ 'new': 'Новая', 'in_progress': 'В работе', 'completed': 'Готово' } as Record<string, string>)[workState(status)] || 'Новая' }
function originPath(task: Task) {
  const base = task.projectId ? (state.projects.find(p => p.id === task.projectId)?.title || 'Проект') : (state.sections.find(s => s.id === task.sectionId)?.name || 'Личное')
  return isArchived(task) ? `Архив · ${base}` : base
}
function advanceLabel(task: Task) { const s = statusName(task.workStatus); return s === 'Done' ? 'Завершить и в архив' : s === 'InProgress' ? 'Отметить «Готово»' : 'Отметить «В работе»' }
function menuItemsFor(kind: 'task' | 'section' | 'project', id: string): MenuItem[] {
  if (kind === 'task') {
    const task = taskById(id); if (!task) return []
    const items: MenuItem[] = []
    if (isToday(task)) items.push({ label: advanceLabel(task), action: () => { void advanceTask(task) } })
    items.push({
      label: isArchived(task) ? 'Вернуть в Backlog' : String(task.location).toLowerCase() === 'backlog' ? 'Перенести в Сегодня' : 'Вернуть в Backlog',
      action: () => { if (isArchived(task)) { void mutate(task, 'restore').then(() => flash('Возвращено в Backlog')) } else void moveTaskVia(task, String(task.location).toLowerCase() === 'backlog' ? 'today' : 'backlog') },
    })
    if (String(task.location).toLowerCase() === 'backlog' && isLinked(task)) items.push({ label: 'Вернуть в план', action: () => { void mutate(task, 'planning') } })
    items.push({ label: 'Переименовать', action: () => startRename('task', task.id) })
    if (!isArchived(task)) items.push({ label: 'Убрать в архив', danger: true, action: () => archiveTask(task) })
    return items
  }
  if (kind === 'section') {
    const section = state.sections.find(s => s.id === id); if (!section) return []
    const tasks = state.tasks.filter(t => t.sectionId === section.id)
    return [
      { label: 'Переименовать', action: () => startRename('section', section.id) },
      { label: 'Удалить раздел', danger: true, action: () => deleteSectionFlow(section, tasks) },
    ]
  }
  const tasks = state.tasks.filter(t => t.projectId === id)
  return [{ label: 'Удалить раздел', danger: true, action: () => deleteProjectGroupFlow(id, tasks) }]
}
function openMenuAt(key: string, point: { x: number; y: number } | null) {
  if (state.orderMode) return
  const kind = key.startsWith('task:') ? 'task' : key.startsWith('section:') ? 'section' : 'project'
  const id = kind === 'task' ? key.slice(5) : kind === 'section' ? key.slice(8) : key.slice(8)
  const items = menuItemsFor(kind, id)
  if (!items.length) return
  const wrap = groupsEl.value?.querySelector<HTMLElement>(`[data-reveal-key="${CSS.escape(key)}"]`)
  if (!wrap) return
  const rect = wrap.getBoundingClientRect()
  closeMenu()
  revealedKey.value = key
  menu.kind = kind; menu.key = key; menu.items = items; menu.x = -9999; menu.y = -9999; menu.open = true
  nextTick(() => positionMenu(rect, point))
  if (!point) suppressOpenUntil = Date.now() + 350
}
function positionMenu(anchorRect: DOMRect, point: { x: number; y: number } | null) {
  const el = menuEl.value, root = rootEl.value
  if (!el || !root) return
  const shell = root.getBoundingClientRect()
  const leftLimit = Math.max(shell.left + 8, 8)
  const rightLimit = Math.min(shell.right - 8, window.innerWidth - 8)
  const topLimit = Math.max(shell.top + 8, 8)
  const bottomLimit = Math.min(shell.bottom - 8, window.innerHeight - 8)
  const width = el.offsetWidth, height = el.offsetHeight
  let x, y
  if (point) {
    x = point.x + 8; y = point.y + 8
    if (x + width > rightLimit) x = point.x - width - 8
    if (y + height > bottomLimit) y = point.y - height - 8
  } else {
    x = anchorRect.right - width
    y = anchorRect.top - height - 6
    if (y < topLimit) y = anchorRect.bottom + 6
    if (y + height > bottomLimit) y = anchorRect.top - height - 6
  }
  x = Math.max(leftLimit, Math.min(x, rightLimit - width))
  y = Math.max(topLimit, Math.min(y, bottomLimit - height))
  el.style.left = `${x}px`; el.style.top = `${y}px`
  el.style.setProperty('--menu-enter-y', y < anchorRect.top ? '6px' : '-6px')
  el.classList.remove('opening'); void el.offsetWidth; el.classList.add('opening')
}
function triggerMenu(key: string) {
  if (state.orderMode) return
  if (menu.open && menu.key === key) closeMenu()
  else openMenuAt(key, null)
}
function rowContextMenu(event: MouseEvent, key: string) {
  event.preventDefault()
  if (state.orderMode || menu.open) return
  openMenuAt(key, { x: event.clientX, y: event.clientY })
}
function onDocPointerDown(event: PointerEvent) {
  if (!menu.open) return
  const target = event.target as HTMLElement | null
  if (menuEl.value?.contains(target) || target?.closest('.row-menu-trigger')) return
  closeMenu()
}
function onDocKeyDown(event: KeyboardEvent) {
  if (event.key !== 'Escape') return
  if (menu.open) { event.preventDefault(); closeMenu() }
  else if (confirmBox.open) { event.preventDefault(); closeConfirm() }
}
function suppressCapture(event: MouseEvent) {
  if (Date.now() < suppressOpenUntil && !(event.target as HTMLElement).closest('.row-context-menu')) { event.preventDefault(); event.stopPropagation() }
}
function onTaskOpenClick(task: Task) {
  if (state.orderMode) return
  if (revealedKey.value) { closeMenu(); return }
  state.orderMode = false
  openTask(task)
}

// ---------- gestures (long-press / swipe) ----------
function rowPointerDown(event: PointerEvent, key: string) {
  if (state.orderMode || event.button > 0) return
  if ((event.target as HTMLElement).closest('.handle,.row-menu-trigger,.task-inline-input')) return
  if (swipe?.timer) window.clearTimeout(swipe.timer)
  const current: SwipeState = { key, pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, moved: false, long: false, timer: 0 }
  swipe = current
  if (event.pointerType === 'touch' || event.pointerType === 'pen') {
    current.timer = window.setTimeout(() => {
      if (swipe !== current || current.moved) return
      current.long = true
      openMenuAt(key, null)
    }, 480)
  }
}
function rowPointerMove(event: PointerEvent) {
  if (!swipe || swipe.pointerId !== event.pointerId) return
  const dx = event.clientX - swipe.startX, dy = event.clientY - swipe.startY
  if (Math.hypot(dx, dy) > 8) { swipe.moved = true; window.clearTimeout(swipe.timer) }
  if (Math.abs(dy) > Math.abs(dx) && Math.abs(dy) > 10) { swipe = null; return }
  if (dx < -12 && Math.abs(dx) > Math.abs(dy) * 1.1) event.preventDefault()
}
function rowPointerUp(event: PointerEvent) {
  if (!swipe || swipe.pointerId !== event.pointerId) return
  const current = swipe
  window.clearTimeout(current.timer); swipe = null
  if (current.long) return
  const dx = event.clientX - current.startX, dy = event.clientY - current.startY
  if (Math.abs(dx) < Math.abs(dy) * 1.2) return
  if (dx <= -56) { suppressOpenUntil = Date.now() + 320; openMenuAt(current.key, null) }
  else if (dx >= 45 && menu.open) { suppressOpenUntil = Date.now() + 320; closeMenu() }
}
function rowPointerCancel() { if (swipe) { window.clearTimeout(swipe.timer); swipe = null } }

// ---------- navigation / actions ----------
function openTask(task: Task) { if (!state.orderMode && Date.now() >= suppressOpenUntil) { closeMenu(); state.detailEditing = ''; void router.push(`/tasks/${task.id}`) } }
function closeDetail() { state.detailEditing = ''; void router.push('/tasks') }
function selectBucket(bucket: 'Backlog' | 'Сегодня') { closeMenu(); state.bucket = bucket; state.archive = false; state.orderMode = false; state.filter = 'all'; state.detail = null; state.title = ''; state.description = ''; void router.replace('/tasks').then(refresh) }
function openArchive() { closeMenu(); state.orderMode = false; state.filter = 'all'; state.archive = true; if (route.path !== '/tasks') { void router.replace('/tasks') } else { void refresh() } }
function closeArchive() { closeMenu(); state.orderMode = false; state.filter = 'all'; state.archive = false; void refresh() }
function toggleAllSections() {
  const keys = groups.value.map(g => g.key)
  const open = keys.length > 0 && keys.every(key => state.expanded[state.bucket].has(key))
  state.expanded[state.bucket] = new Set(open ? [] : keys)
}
function toggleSectionFor(key: string) {
  if (state.orderMode) return
  if (revealedKey.value) { closeMenu(); return }
  const open = state.expanded[state.bucket]
  open.has(key) ? open.delete(key) : open.add(key)
}
function isExpanded(key: string) { return state.expanded[state.bucket].has(key) }
function toggleOrderMode() { closeMenu(); swipe = null; clearGhost(); clearDragMarks(); drag = null; state.orderMode = !state.orderMode }
async function moveTaskVia(task: Task, target: 'today' | 'backlog') {
  await mutate(task, target)
  flash(target === 'today' ? 'Добавлено в Сегодня' : 'Возвращено в Backlog')
}
async function advanceTask(task: Task) {
  const s = statusName(task.workStatus)
  if (s === 'Done') { await mutate(task, 'archive'); flash('Задача завершена и отправлена в архив') }
  else { const next = s === 'New' ? 'inProgress' : 'done'; await mutate(task, 'status', 'PUT', { expectedVersion: task.version, status: next }); flash(next === 'inProgress' ? 'Статус: В работе' : 'Статус: Готово') }
}
function archiveTask(task: Task) {
  askConfirm({ title: 'Убрать задачу в архив?', body: `«${task.title}» можно будет найти в архиве.`, confirmLabel: 'В архив', onConfirm: () => { void mutate(task, 'archive').then(() => flash('Задача перемещена в архив')) } })
}
function deleteSectionFlow(section: Section, tasks: Task[]) {
  askConfirm({
    title: `Удалить раздел «${section.name}»?`,
    body: tasks.length ? `${tasks.length} задач будут перемещены в архив.` : 'Раздел будет удалён.',
    confirmLabel: 'Удалить',
    onConfirm: () => { void (async () => { for (const task of tasks) await mutate(task, 'archive'); await deleteSection(section); flash('Раздел удалён') })() },
  })
}
function deleteProjectGroupFlow(projectId: string, tasks: Task[]) {
  const title = state.projects.find(p => p.id === projectId)?.title || 'Проект'
  askConfirm({
    title: `Удалить раздел «${title}»?`,
    body: tasks.length ? `${tasks.length} задач будут перемещены в архив.` : 'Раздел будет удалён.',
    confirmLabel: 'Удалить',
    onConfirm: () => { void (async () => { for (const task of tasks) await mutate(task, 'archive'); flash('Раздел удалён') })() },
  })
}

// ---------- inline rename ----------
function startRename(kind: 'task' | 'section', id: string) {
  const current = kind === 'task' ? taskById(id) : state.sections.find(s => s.id === id)
  if (!current) return
  closeMenu()
  renameDone = false
  state.renaming = { kind, id, value: kind === 'task' ? (current as Task).title : (current as Section).name }
  nextTick(() => { const el = renameInputEl.value; if (el) { el.focus(); el.select() } })
}
async function commitRename(save: boolean) {
  const renaming = state.renaming
  if (!renaming || renameDone) return
  renameDone = true
  const value = renaming.value.trim()
  state.renaming = null
  if (!save || !value) return
  if (renaming.kind === 'task') { const task = taskById(renaming.id); if (task) await renameTask(task, value) }
  else { const section = state.sections.find(s => s.id === renaming.id); if (section) await renameSection(section, value) }
}
async function renameTask(task: Task, title: string) {
  try { await request(`/${task.id}`, { method: 'PUT', body: JSON.stringify({ expectedVersion: task.version, title, description: task.description }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      const local = { ...task, title, version: task.version + 1 }
      await queueTask('tasks.task', task.id, task.version, { operation: 'update', kind: 'task', id: task.id, expectedVersion: task.version, title, description: task.description }, false, local)
      state.tasks = state.tasks.map(x => x.id === task.id ? local : x)
      if (state.detail?.id === task.id) state.detail = local
      await cacheRows('tasks.task', state.tasks, true)
      state.error = 'Нет сети. Изменение сохранено и будет синхронизировано позже.'; return
    }
    state.error = (error as Error).message
  }
}
async function renameSection(section: Section, name: string) {
  if (!name || name === section.name) return
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
  try { await request(`/sections/${section.id}`, { method: 'DELETE', body: JSON.stringify({ expectedVersion: section.version }) }); await refresh() }
  catch (error) {
    if (error instanceof TypeError) {
      await queueTask('tasks.section', section.id, section.version, { operation: 'delete', kind: 'section', id: section.id, bucket: section.location }, true)
      state.sections = state.sections.filter(x => x.id !== section.id); await cacheRows('tasks.section', state.sections, true); state.error = 'Нет сети. Удаление сохранено и будет синхронизировано позже.'; return
    }
    state.error = (error as Error).message
  }
}

// ---------- create sheet ----------
function openCreateSheet() {
  closeMenu(); closeConfirm()
  state.createType = 'task'; state.title = ''; state.description = ''
  if (!state.sections.some(s => s.id === state.sectionId)) state.sectionId = state.sections[0]?.id || ''
  state.createListOpen = false; state.creating = true
  nextTick(() => nameFieldEl.value?.focus())
}
function closeCreateSheet() { state.creating = false; state.createListOpen = false }
async function submitCreate() {
  if (!state.title.trim()) return
  try {
    if (state.createType === 'section') {
      const created = await createSection()
      if (created) { closeCreateSheet(); flash('Раздел создан') }
      return
    }
    const created = await createTask()
    if (created) { closeCreateSheet(); void router.push(`/tasks/${created.id}`) }
  } catch (error) { state.error = (error as Error).message }
}
async function createTask(): Promise<Task | null> {
  if (!state.title.trim()) return null
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
  if (state.bucket === 'Backlog' && created.sectionId) state.expanded[state.bucket].add(created.sectionId)
  if (moveError) { state.bucket = 'Backlog'; await refresh(); state.error = `Задача создана в Backlog, но не перенесена на Сегодня: ${moveError}` }
  else if (!queuedOffline) await refresh()
  return created
}
async function createSection(): Promise<Section | null> {
  if (!state.title.trim()) return null
  let created: Section, queuedOffline = false
  try { created = await request<Section>('/sections', { method: 'POST', body: JSON.stringify({ name: state.title, location: state.bucket === 'Сегодня' ? 'today' : 'backlog' }) }) }
  catch (error) {
    if (!(error instanceof TypeError)) { state.error = (error as Error).message; return null }
    const id = newId(), bucket = state.bucket === 'Сегодня' ? 'today' : 'backlog'
    const payload = { operation: 'create', kind: 'section', id, title: state.title.trim(), bucket, position: state.sections.length }
    const local: Section = { id, name: state.title.trim(), location: bucket, position: state.sections.length, version: 1 }
    await queueTask('tasks.section', id, null, payload, false, local)
    created = { id, name: state.title.trim(), location: bucket, position: state.sections.length, version: 1 }
    queuedOffline = true; state.sections.push(created); await cacheRows('tasks.section', state.sections, true); state.error = 'Нет сети. Раздел сохранён и будет синхронизирован позже.'
  }
  state.title = ''; state.creating = false; if (!queuedOffline) await refresh(); state.sectionId = created.id
  state.expanded[state.bucket].add(created.id)
  return created
}

// ---------- detail inline editing ----------
function startTitleEdit() {
  if (!state.detail || state.detailEditing) return
  finishDescEdit()
  state.detailEditing = 'title'
  nextTick(() => { const el = titleInputEl.value; if (el) { el.focus(); el.select() } })
}
function finishTitleEdit(save = true) {
  if (state.detailEditing !== 'title') return
  const task = state.detail
  if (!task) { state.detailEditing = ''; return }
  if (save) { const title = state.title.trim(); if (title && title !== task.title) { state.title = title; void saveDetail() } else { state.title = task.title; state.description = task.description } }
  else { state.title = task.title; state.description = task.description }
  state.detailEditing = ''
}
function startDescEdit() {
  if (!state.detail || state.detailEditing) return
  finishTitleEdit()
  state.detailEditing = 'description'
  nextTick(() => descriptionInputEl.value?.focus())
}
function finishDescEdit(save = true) {
  if (state.detailEditing !== 'description') return
  const task = state.detail
  if (!task) { state.detailEditing = ''; return }
  if (save) { if (state.description !== task.description) void saveDetail(); else state.title = task.title }
  else { state.title = task.title; state.description = task.description }
  state.detailEditing = ''
}
function detailMove() {
  const task = state.detail; if (!task) return
  if (isArchived(task)) void mutate(task, 'restore').then(() => flash('Возвращено в Backlog'))
  else void moveTaskVia(task, isToday(task) ? 'backlog' : 'today')
}

// ---------- drag & reorder (pointer-based, order mode) ----------
function validTaskDrop(moved: Task, target: Task | { projectId?: string }, place: 'row' | 'inside'): boolean {
  if (!moved) return false
  if (isLinked(moved)) {
    if (place === 'inside') return (target as { projectId?: string }).projectId === moved.projectId
    const t = target as Task
    return isLinked(t) && t.projectId === moved.projectId
  }
  if (place === 'inside') return !(target as { projectId?: string }).projectId
  return !isLinked(target as Task)
}
function onGroupsPointerDown(event: PointerEvent) {
  if (event.button !== 0 || state.archive || !state.orderMode) return
  const handle = (event.target as HTMLElement).closest<HTMLElement>('[data-drag-kind]')
  if (!handle) return
  event.preventDefault()
  closeMenu()
  const kind = handle.dataset.dragKind === 'section' ? 'section' : 'task'
  const id = handle.dataset.dragId || ''
  const row = kind === 'task' ? handle.closest<HTMLElement>('[data-task-id]') : handle.closest<HTMLElement>('[data-section-row-key]')
  if (!row) return
  const rect = row.getBoundingClientRect()
  const label = kind === 'task' ? (taskById(id)?.title || '') : (state.sections.find(s => s.id === id)?.name || '')
  drag = { kind, id, pointerId: event.pointerId, startX: event.clientX, startY: event.clientY, row, target: null, place: '', started: false, ghost: null, offsetX: event.clientX - rect.left, offsetY: event.clientY - rect.top, width: rect.width, height: rect.height, label }
  handle.setPointerCapture?.(event.pointerId)
}
function onGroupsPointerMove(event: PointerEvent) {
  if (!drag || drag.pointerId !== event.pointerId) return
  if (!drag.started && Math.hypot(event.clientX - drag.startX, event.clientY - drag.startY) > 6) {
    drag.started = true
    drag.row.classList.add('task-drag-source')
    const ghost = document.createElement('div')
    ghost.className = 'reorder-ghost'
    ghost.textContent = drag.label
    ghost.style.width = `${drag.width}px`; ghost.style.height = `${drag.height}px`
    document.body.append(ghost)
    drag.ghost = ghost
  }
  if (!drag.started) return
  event.preventDefault()
  const bounds = scrollEl.value?.getBoundingClientRect()
  if (bounds && drag.ghost) {
    const pad = 4
    drag.ghost.style.left = `${Math.max(bounds.left + pad, Math.min(event.clientX - drag.offsetX, bounds.right - drag.width - pad))}px`
    drag.ghost.style.top = `${Math.max(bounds.top + pad, Math.min(event.clientY - drag.offsetY, bounds.bottom - drag.height - pad))}px`
  }
  clearDragMarks()
  drag.target = null; drag.place = ''
  const hit = document.elementFromPoint(event.clientX, event.clientY)
  if (!hit) return
  if (drag.kind === 'task') {
    const taskRow = hit.closest<HTMLElement>('[data-task-id]')
    if (taskRow && taskRow.dataset.taskId !== drag.id) {
      const target = taskById(taskRow.dataset.taskId || ''), moved = taskById(drag.id)
      if (!target || !moved || !validTaskDrop(moved, target, 'row')) return
      const rect = taskRow.getBoundingClientRect()
      drag.target = taskRow; drag.place = event.clientY < rect.top + rect.height / 2 ? 'before' : 'after'
      taskRow.classList.add(drag.place === 'before' ? 'task-drop-before' : 'task-drop-after')
      return
    }
    const sectionRow = hit.closest<HTMLElement>('[data-section-row-key]')
    if (sectionRow && sectionRow.dataset.dragKey !== `project:${drag.id}`) {
      const moved = taskById(drag.id)
      if (!moved || !validTaskDrop(moved, { projectId: sectionRow.dataset.sectionProject || undefined }, 'inside')) return
      drag.target = sectionRow; drag.place = 'inside'
      sectionRow.classList.add('task-drop-inside')
    }
    return
  }
  const sectionRow = hit.closest<HTMLElement>('[data-section-row-key]')
  if (!sectionRow || sectionRow.dataset.sectionProject || sectionRow.dataset.dragKey === `section:${drag.id}`) return
  const rect = sectionRow.getBoundingClientRect()
  drag.target = sectionRow; drag.place = event.clientY < rect.top + rect.height / 2 ? 'before' : 'after'
  sectionRow.classList.add(drag.place === 'before' ? 'task-drop-before' : 'task-drop-after')
}
async function finishDrag(event: PointerEvent) {
  if (!drag || drag.pointerId !== event.pointerId) return
  const current = drag
  clearGhost(); clearDragMarks(); drag = null
  if (!current.started || !current.target || !current.place) return
  if (current.kind === 'section') {
    const targetKey = current.target.dataset.dragKey || ''
    const targetId = targetKey.startsWith('section:') ? targetKey.slice(8) : ''
    if (!targetId) return
    await dropSection(current.id, targetId, current.place === 'after')
    return
  }
  const moved = taskById(current.id)
  if (!moved) return
  if (current.place === 'inside') {
    const key = current.target.dataset.dragKey || ''
    if (key.startsWith('project:')) {
      const projectId = key.slice(8)
      const items = state.tasks.filter(t => t.projectId === projectId).sort((a, b) => a.position - b.position)
      const last = items[items.length - 1]
      if (last && last.id !== moved.id) await reorderProjectTasks(projectId, moved.id, last.id)
    } else {
      const sectionId = current.target.dataset.dragId || ''
      if (sectionId && sectionId !== (moved.sectionId || '')) await moveTaskSection(moved, sectionId)
    }
    return
  }
  const target = taskById(current.target.dataset.taskId || '')
  if (!target) return
  if (isLinked(moved) && isLinked(target) && target.projectId === moved.projectId) { await reorderProjectTasks(moved.projectId!, moved.id, target.id); return }
  if (isLinked(moved) || isLinked(target)) return
  const targetSectionId = target.sectionId || ''
  if ((moved.sectionId || '') === targetSectionId) { await dropTaskBefore(targetSectionId, moved.id, target.id); return }
  if (targetSectionId) {
    await moveTaskSection(moved, targetSectionId)
    const fresh = taskById(moved.id)
    if (fresh) await dropTaskBefore(targetSectionId, fresh.id, target.id)
  } else {
    await mutate(moved, 'section', 'PUT', { expectedVersion: moved.version, sectionId: null as unknown as string })
    const fresh = taskById(moved.id)
    if (fresh) await dropTaskBefore('', fresh.id, target.id)
  }
}
function onGroupsPointerCancel(event: PointerEvent) {
  if (!drag || drag.pointerId !== event.pointerId) return
  clearGhost(); clearDragMarks(); drag = null
}
function clearGhost() { if (drag?.row) drag.row.classList.remove('task-drag-source'); drag?.ghost?.remove(); if (drag) drag.ghost = null }
function clearDragMarks() { groupsEl.value?.querySelectorAll('.task-drop-inside,.task-drop-before,.task-drop-after').forEach(el => el.classList.remove('task-drop-inside', 'task-drop-before', 'task-drop-after')) }

// ---------- API / offline (unchanged semantics) ----------
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
    if (state.detail) {
      state.bucket = state.detail.location === 'today' || state.detail.location === 'Today' ? 'Сегодня' : 'Backlog'
      if (!state.detailEditing) { state.title = state.detail.title; state.description = state.detail.description }
    }
    if (sections.length && !sections.some(x => x.id === state.sectionId)) state.sectionId = sections[0].id
  } catch (error) { state.error = (error as Error).message }
  finally { state.busy = false }
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
async function moveTaskSection(task: Task, sectionId: string) { await mutate(task, 'section', 'PUT', { expectedVersion: task.version, sectionId }) }
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
async function dropTaskBefore(sectionId: string, sourceId: string, targetId: string) {
  if (sourceId === targetId) return
  const items = state.tasks.filter(x => (x.sectionId || '') === sectionId).sort((a, b) => a.position - b.position), from = items.findIndex(x => x.id === sourceId), to = items.findIndex(x => x.id === targetId)
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
async function dropSection(sourceId: string, targetId: string, after = false) {
  if (sourceId === targetId && !after) return
  const sections = [...state.sections], from = sections.findIndex(x => x.id === sourceId), to = sections.findIndex(x => x.id === targetId)
  if (from < 0 || to < 0) return
  const [moved] = sections.splice(from, 1)
  const to2 = sections.findIndex(x => x.id === targetId)
  sections.splice(to2 + (after ? 1 : 0), 0, moved)
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

watch(() => route.fullPath, () => void refresh())
onMounted(() => {
  document.addEventListener('pointerdown', onDocPointerDown, true)
  document.addEventListener('keydown', onDocKeyDown)
  void refresh()
})
onBeforeUnmount(() => {
  document.removeEventListener('pointerdown', onDocPointerDown, true)
  document.removeEventListener('keydown', onDocKeyDown)
  clearTimeout(toastTimer)
  if (swipe?.timer) window.clearTimeout(swipe.timer)
  clearGhost(); drag = null
})
</script>

<template>
  <section class="tasks-section" ref="rootEl" aria-labelledby="tasks-heading" @click.capture="suppressCapture">
    <div v-if="state.error" class="task-error" role="alert">{{ state.error }} <button :aria-label="'Закрыть'" @click="state.error = ''">×</button></div>

    <section v-if="state.detail" class="scroll task-detail">
      <div class="task-detail-top">
        <button type="button" class="doc-action doc-back task-detail-back" @click="closeDetail">← Назад</button>
      </div>
      <div class="task-detail-meta"><span class="task-detail-section">{{ detailOrigin }}</span></div>
      <div class="task-detail-title-wrap">
        <button v-if="state.detailEditing !== 'title'" type="button" class="task-title-open" @click="startTitleEdit">{{ state.detail.title }}</button>
        <input v-else ref="titleInputEl" v-model="state.title" class="task-title-detail-input" type="text" maxlength="160" @keydown.enter.prevent="finishTitleEdit(true)" @keydown.esc.prevent="finishTitleEdit(false)" @blur="finishTitleEdit(true)" @click.stop />
      </div>
      <div class="task-detail-description-card">
        <div v-if="state.detailEditing !== 'description'" class="task-description" @click="startDescEdit">{{ state.detail.description || 'Описание пока не добавлено.' }}</div>
        <textarea v-else ref="descriptionInputEl" v-model="state.description" class="task-description-input" @keydown.esc.prevent="finishDescEdit(false)" @blur="finishDescEdit(true)" @click.stop></textarea>
      </div>
      <div class="task-detail-bottom-actions">
        <button type="button" class="task-detail-pill action-move" @click="detailMove">{{ detailMoveLabel }}</button>
        <button v-if="isToday(state.detail)" type="button" class="task-detail-pill action-status" :class="workState(state.detail.workStatus)" @click="advanceTask(state.detail)">{{ workLabel(state.detail.workStatus) }}</button>
        <button v-if="!isArchived(state.detail)" type="button" class="task-delete-icon" aria-label="Убрать задачу в архив" @click="archiveTask(state.detail)">
          <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16"/><path d="M9 7V4h6v3"/><path d="m6 7 1 13h10l1-13"/><path d="M10 11v5M14 11v5"/></svg>
        </button>
      </div>
    </section>

    <div v-else class="task-board">
      <div v-if="!state.archive" class="task-toolbar">
        <div class="task-tabs" role="tablist" aria-label="Режим задач">
          <button type="button" class="task-tab" :class="{ active: state.bucket === 'Backlog' }" @click="selectBucket('Backlog')">Backlog</button>
          <button type="button" class="task-tab" :class="{ active: state.bucket === 'Сегодня' }" @click="selectBucket('Сегодня')">Сегодня</button>
        </div>
        <div class="task-toolbar-actions">
          <button type="button" class="task-icon-button" :aria-label="allOpen ? 'Свернуть все разделы' : 'Развернуть все разделы'" :title="allOpen ? 'Свернуть все разделы' : 'Развернуть все разделы'" @click="toggleAllSections">
            <svg viewBox="0 0 20 20" aria-hidden="true"><path :d="allOpen ? 'M5 3 10 8 15 3' : 'M5 8 10 3 15 8'"/><path :d="allOpen ? 'M5 17 10 12 15 17' : 'M5 12 10 17 15 12'"/></svg>
          </button>
          <button type="button" class="order-mode-toggle" :aria-pressed="state.orderMode" :aria-label="state.orderMode ? 'Выключить сортировку' : 'Включить сортировку'" :title="state.orderMode ? 'Выключить сортировку' : 'Включить сортировку'" @click="toggleOrderMode">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h11M4 12h11M4 17h11M19 6v12m-2.5-2.5L19 18l2.5-2.5"/></svg>
          </button>
          <button type="button" class="plus" aria-label="Создать задачу или раздел" @click="openCreateSheet">＋</button>
        </div>
      </div>

      <div v-if="state.archive" class="task-archive-top">
        <button type="button" class="task-archive-back" @click="closeArchive">← Назад</button>
        <div class="task-archive-title">Архив</div>
      </div>

      <div v-if="!state.archive && location === 'Today'" class="task-filters">
        <button v-for="filter in filters" :key="filter.value" type="button" class="task-filter" :class="{ active: state.filter === filter.value }" @click="state.filter = filter.value"><span v-if="filter.dot" class="task-filter-dot" :class="filter.dot" />{{ filter.label }}</button>
      </div>

      <div ref="scrollEl" class="scroll task-scroll">
        <div v-if="state.archive" ref="groupsEl" class="task-groups archive-flat">
          <div v-for="task in state.tasks" :key="task.id" class="task-row-wrap" :data-reveal-key="`task:${task.id}`" :class="{ revealed: revealedKey === `task:${task.id}`, 'context-active': revealedKey === `task:${task.id}` }">
            <article class="task-row" :data-task-id="task.id" @contextmenu.prevent="rowContextMenu($event, `task:${task.id}`)" @pointerdown="rowPointerDown($event, `task:${task.id}`)" @pointermove="rowPointerMove" @pointerup="rowPointerUp" @pointercancel="rowPointerCancel">
              <template v-if="renamingKey === `task:${task.id}`">
                <input ref="renameInputEl" v-model="renamingValue" class="task-inline-input" maxlength="160" @keydown.enter.prevent="commitRename(true)" @keydown.esc.prevent="commitRename(false)" @blur="commitRename(true)" @click.stop />
              </template>
              <template v-else>
                <button type="button" class="task-open" @click="onTaskOpenClick(task)">
                  <span v-if="isToday(task)" class="task-status-dot" :class="workState(task.workStatus)" />
                  <span class="task-copy"><span class="task-title">{{ task.title }}</span></span>
                </button>
                <button type="button" class="row-menu-trigger" :hidden="state.orderMode" :aria-label="`Действия с задачей «${task.title}»`" aria-haspopup="menu" @click.stop="triggerMenu(`task:${task.id}`)">⋯</button>
                <button v-if="state.orderMode" type="button" class="handle" data-drag-kind="task" :data-drag-id="task.id" :aria-label="`Перетащить ${task.title}`"><span><i></i><i></i><i></i><i></i><i></i><i></i></span></button>
              </template>
            </article>
          </div>
        </div>
        <div v-else ref="groupsEl" class="task-groups" @pointerdown="onGroupsPointerDown" @pointermove="onGroupsPointerMove" @pointerup="void finishDrag($event)" @pointercancel="onGroupsPointerCancel">
          <section v-for="group in groups" :key="group.key" class="task-group">
            <div class="task-section-wrap" :data-reveal-key="group.key" :class="{ revealed: revealedKey === group.key, 'context-active': revealedKey === group.key }">
              <div class="task-section-row" :data-section-row-key="group.key" :data-drag-key="group.key" :data-section-project="group.kind === 'project' ? group.projectId : ''" @contextmenu.prevent="rowContextMenu($event, group.key)" @pointerdown="rowPointerDown($event, group.key)" @pointermove="rowPointerMove" @pointerup="rowPointerUp" @pointercancel="rowPointerCancel">
                <template v-if="renamingKey === group.key">
                  <input ref="renameInputEl" v-model="renamingValue" class="task-inline-input" maxlength="160" @keydown.enter.prevent="commitRename(true)" @keydown.esc.prevent="commitRename(false)" @blur="commitRename(true)" @click.stop />
                </template>
                <template v-else>
                  <button type="button" class="chev" :class="{ open: isExpanded(group.key) }" @click="toggleSectionFor(group.key)">›</button>
                  <button type="button" class="task-section-title" @click="toggleSectionFor(group.key)">
                    <span v-if="group.kind === 'project'" class="task-project-diamond" aria-hidden="true" />
                    <span class="task-section-label">{{ group.title }}</span>
                  </button>
                  <button type="button" class="row-menu-trigger" :hidden="state.orderMode" :aria-label="`Действия с разделом «${group.title}»`" aria-haspopup="menu" @click.stop="triggerMenu(group.key)">⋯</button>
                  <button v-if="group.kind === 'plain' && state.orderMode" type="button" class="handle" data-drag-kind="section" :data-drag-id="group.section.id" :aria-label="`Перетащить раздел ${group.title}`"><span><i></i><i></i><i></i><i></i><i></i><i></i></span></button>
                </template>
              </div>
            </div>
            <div v-if="isExpanded(group.key)" class="task-list">
              <div v-for="task in group.tasks" :key="task.id" class="task-row-wrap" :data-reveal-key="`task:${task.id}`" :class="{ revealed: revealedKey === `task:${task.id}`, 'context-active': revealedKey === `task:${task.id}` }">
                <article class="task-row" :data-task-id="task.id" @contextmenu.prevent="rowContextMenu($event, `task:${task.id}`)" @pointerdown="rowPointerDown($event, `task:${task.id}`)" @pointermove="rowPointerMove" @pointerup="rowPointerUp" @pointercancel="rowPointerCancel">
                  <template v-if="renamingKey === `task:${task.id}`">
                    <input ref="renameInputEl" v-model="renamingValue" class="task-inline-input" maxlength="160" @keydown.enter.prevent="commitRename(true)" @keydown.esc.prevent="commitRename(false)" @blur="commitRename(true)" @click.stop />
                  </template>
                  <template v-else>
                    <button type="button" class="task-open" @click="onTaskOpenClick(task)">
                      <span v-if="isToday(task)" class="task-status-dot" :class="workState(task.workStatus)" />
                      <span class="task-copy"><span class="task-title">{{ task.title }}</span></span>
                    </button>
                    <button type="button" class="row-menu-trigger" :hidden="state.orderMode" :aria-label="`Действия с задачей «${task.title}»`" aria-haspopup="menu" @click.stop="triggerMenu(`task:${task.id}`)">⋯</button>
                    <button v-if="state.orderMode" type="button" class="handle" data-drag-kind="task" :data-drag-id="task.id" :aria-label="`Перетащить ${task.title}`"><span><i></i><i></i><i></i><i></i><i></i><i></i></span></button>
                  </template>
                </article>
              </div>
            </div>
          </section>
        </div>
        <div v-if="showEmpty" class="task-empty">{{ emptyText }}</div>
      </div>

      <div v-show="location === 'Today' && !state.archive" class="task-archive-bar">
        <button type="button" class="task-archive-link" @click="openArchive">Архив</button>
      </div>
    </div>

    <div class="overlay" :class="{ open: state.creating }" @click="closeCreateSheet" />
    <section class="sheet task-create-sheet" :class="{ open: state.creating }" aria-label="Создание задачи">
      <div class="sheet-head">
        <div class="sheet-title">{{ state.createType === 'task' ? 'Новая задача' : 'Новый раздел' }}</div>
        <button type="button" class="sheet-close" aria-label="Закрыть" @click="closeCreateSheet">×</button>
      </div>
      <div class="type-switch">
        <button type="button" class="type-btn" :class="{ active: state.createType === 'task' }" @click="state.createType = 'task'; state.createListOpen = false">Задача</button>
        <button type="button" class="type-btn" :class="{ active: state.createType === 'section' }" @click="state.createType = 'section'; state.createListOpen = false">Раздел</button>
      </div>
      <input ref="nameFieldEl" v-model="state.title" class="name-field" :placeholder="state.createType === 'task' ? 'Название задачи' : 'Название раздела'" maxlength="160" />
      <textarea v-if="state.createType === 'task'" v-model="state.description" class="task-create-description" rows="3" placeholder="Описание задачи"></textarea>
      <button v-if="state.createType === 'task'" type="button" class="where" @click="state.createListOpen = !state.createListOpen">
        <span class="where-value">{{ state.sections.find(s => s.id === state.sectionId)?.name || 'Личное' }}</span><span class="where-arrow">›</span>
      </button>
      <div v-if="state.createType === 'task'" class="parent-list" :class="{ open: state.createListOpen }">
        <button v-for="section in state.sections" :key="section.id" type="button" class="parent-option" :class="{ selected: state.sectionId === section.id }" @click="state.sectionId = section.id; state.createListOpen = false">{{ section.name }}</button>
      </div>
      <button type="button" class="create-submit" @click="submitCreate">{{ state.createType === 'task' ? 'Создать' : 'Создать раздел' }}</button>
    </section>

    <div v-if="menu.open" ref="menuEl" class="row-context-menu" role="menu" style="position: fixed" :style="{ left: `${menu.x}px`, top: `${menu.y}px` }" :aria-label="`Действия с ${menu.kind === 'task' ? 'задачей' : 'разделом'}`">
      <button v-for="(item, index) in menu.items" :key="index" type="button" class="row-context-item" :class="{ danger: item.danger }" role="menuitem" @click="item.action(); closeMenu()">{{ item.label }}</button>
    </div>

    <div v-if="confirmBox.open" class="app-confirm-layer" @click.self="closeConfirm">
      <div class="app-confirm-card" role="dialog" aria-modal="true" aria-labelledby="task-confirm-title">
        <div class="app-confirm-title" id="task-confirm-title">{{ confirmBox.title }}</div>
        <div v-if="confirmBox.body" class="app-confirm-body">{{ confirmBox.body }}</div>
        <div class="app-confirm-actions">
          <button ref="confirmCancelEl" type="button" class="app-confirm-cancel" @click="closeConfirm">Отмена</button>
          <button type="button" class="app-confirm-accept" @click="runConfirm">{{ confirmBox.confirmLabel }}</button>
        </div>
      </div>
    </div>

    <div class="task-toast" :class="{ show: toast.show }" role="status" aria-live="polite">{{ toast.text }}</div>
  </section>
</template>
