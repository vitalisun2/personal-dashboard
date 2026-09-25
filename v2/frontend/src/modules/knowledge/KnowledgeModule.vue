<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { requestSync, subscribeSyncStatus, type SyncStatus } from '../../offline/runtime'
import { chatRoute } from '../../shared/chatRoute'
import {
  cacheServerKnowledge, getCachedKnowledge, getKnowledgeConflicts, loadKnowledgeTree,
  pendingKnowledgeCount, queueKnowledgeDelete, queueKnowledgeUpsert, searchKnowledge,
  type KnowledgeNode, type SearchHit,
} from './knowledgeApi'
import './knowledge.css'

const route = useRoute()
const router = useRouter()
const nodes = ref<KnowledgeNode[]>([])
const results = ref<SearchHit[]>([])
const localResultIds = ref(new Set<string>())
const expanded = ref(new Set<string>())
const query = ref(String(route.query.q || ''))
const orderMode = ref(false)
const busy = ref(false)
const error = ref('')
const conflicts = ref(0)
const pendingCount = ref(0)
const isOnline = ref(typeof navigator === 'undefined' || navigator.onLine)
const syncStatus = ref<SyncStatus>('ready')
const conflictItems = ref<Awaited<ReturnType<typeof getKnowledgeConflicts>>>([])
const menuId = ref('')
const createOpen = ref(false)
const createKind = ref<'section' | 'document'>('document')
const createTitle = ref('')
const createParent = ref('')
const deleteTarget = ref<KnowledgeNode | null>(null)
const renameTarget = ref<KnowledgeNode | null>(null)
const renameValue = ref('')
const titleEditing = ref(false)
const markdownEditing = ref(false)
const titleInput = ref<HTMLInputElement | null>(null)
const markdownInput = ref<HTMLTextAreaElement | null>(null)

const tree = computed(() => {
  const children = (parentId: string | null): KnowledgeNode[] => nodes.value
    .filter(node => node.parentId === parentId)
    .sort((a, b) => a.position - b.position)
  return children(null)
})
const sections = computed(() => nodes.value.filter(node => node.kind === 'section').sort((a, b) => a.path.localeCompare(b.path)))
const documentId = computed(() => {
  const match = route.params.pathMatch
  return Array.isArray(match) ? String(match.at(-1) || '') : String(match || '')
})
const document = computed(() => nodes.value.find(node => node.id === documentId.value && node.kind === 'document'))
const isSearching = computed(() => query.value.trim().length > 0)

function childrenOf(parentId: string) { return nodes.value.filter(node => node.parentId === parentId).sort((a, b) => a.position - b.position) }
function childNodes(parentId: string | null): KnowledgeNode[] { return nodes.value.filter(node => node.parentId === parentId).sort((a, b) => a.position - b.position) }
function setError(err: unknown) { error.value = err instanceof Error ? err.message : 'Не удалось выполнить действие' }
function nodePath(node: KnowledgeNode, all = nodes.value): string {
  const parent = node.parentId ? all.find(item => item.id === node.parentId) : undefined
  return parent ? `${nodePath(parent, all)} / ${node.title}` : node.title
}
function refreshPaths() {
  const byId = new Map(nodes.value.map(node => [node.id, node]))
  const pathFor = (node: KnowledgeNode): string => {
    const parent = node.parentId ? byId.get(node.parentId) : undefined
    return parent ? `${pathFor(parent)} / ${node.title}` : node.title
  }
  nodes.value = nodes.value.map(node => ({ ...node, path: pathFor(node) }))
}
async function commitNode(node: KnowledgeNode, baseVersion: number | null) {
  await queueKnowledgeUpsert(node, baseVersion)
  nodes.value = nodes.value.some(item => item.id === node.id)
    ? nodes.value.map(item => item.id === node.id ? node : item)
    : [...nodes.value, node]
  if (navigator.onLine) {
    requestSync()
  }
  await refreshSyncState()
}
async function load() {
  try { nodes.value = await getCachedKnowledge() } catch (err) { setError(err) }
  try {
    if (navigator.onLine) {
      const serverNodes = await loadKnowledgeTree()
      await cacheServerKnowledge(serverNodes)
      nodes.value = await getCachedKnowledge()
      await refreshSyncState()
    }
    if (isSearching.value) await runSearch()
  } catch (err) { if (navigator.onLine) setError(err) }
}
async function refreshSyncState() {
  try {
    const [pending, list] = await Promise.all([pendingKnowledgeCount(), getKnowledgeConflicts()])
    pendingCount.value = pending
    conflictItems.value = list
    conflicts.value = list.length
  } catch (err) { setError(err) }
}
async function retrySync() {
  if (!navigator.onLine) return
  error.value = ''
  requestSync()
}
function handleSyncStatus(status: SyncStatus) {
  syncStatus.value = status
  if (status === 'ready' || status === 'conflict') {
    void getCachedKnowledge().then(cached => { nodes.value = cached; return refreshSyncState() }).catch(setError)
  }
}
let searchRevision = 0
async function runSearch() {
  const revision = ++searchRevision
  error.value = ''
  results.value = []
  localResultIds.value = new Set()
  if (!query.value.trim()) return
  orderMode.value = false
  const term = query.value.trim().toLocaleLowerCase()
  const localHits = nodes.value.filter(node => node.kind === 'document' && `${node.title} ${node.markdown}`.toLocaleLowerCase().includes(term))
  try {
    if (navigator.onLine) {
      const response = await searchKnowledge(query.value.trim())
      if (revision !== searchRevision) return
      const seen = new Set(response.hits.map(hit => hit.source.id))
      const supplements = localHits.filter(node => !seen.has(node.id)).map(node => ({ source: { kind: 'knowledge.document', id: node.id, version: node.version, url: `/knowledge/${node.id}`, title: node.title, path: node.path, snippet: node.markdown.slice(0, 180), updatedAtUtc: '' }, score: 0 }))
      localResultIds.value = new Set(supplements.map(hit => hit.source.id))
      results.value = [...response.hits, ...supplements]
    } else {
      localResultIds.value = new Set(localHits.map(node => node.id))
      results.value = localHits.map(node => ({ source: { kind: 'knowledge.document', id: node.id, version: node.version, url: `/knowledge/${node.id}`, title: node.title, path: node.path, snippet: node.markdown.slice(0, 180), updatedAtUtc: '' }, score: 0 }))
    }
  } catch (err) {
    if (revision !== searchRevision) return
    setError(err)
    localResultIds.value = new Set(localHits.map(node => node.id))
    results.value = localHits.map(node => ({ source: { kind: 'knowledge.document', id: node.id, version: node.version, url: `/knowledge/${node.id}`, title: node.title, path: node.path, snippet: node.markdown.slice(0, 180), updatedAtUtc: '' }, score: 0 }))
  }
}
watch(() => route.query.q, value => { query.value = String(value || ''); void runSearch() })
watch(query, value => {
  const q = value.trim()
  if (q !== String(route.query.q || '')) void router.replace({ query: { ...route.query, ...(q ? { q } : { q: undefined }) } })
  void runSearch()
})
watch(documentId, id => { if (id && !nodes.value.some(node => node.id === id)) void load() })
let unsubscribeSync: () => void = () => undefined
const onOnline = () => { isOnline.value = true }
const onOffline = () => { isOnline.value = false }
onMounted(() => {
  void load()
  unsubscribeSync = subscribeSyncStatus(handleSyncStatus)
  void refreshSyncState()
  window.addEventListener('online', onOnline)
  window.addEventListener('offline', onOffline)
})
onUnmounted(() => {
  unsubscribeSync()
  window.removeEventListener('online', onOnline)
  window.removeEventListener('offline', onOffline)
})

function toggle(id: string) {
  const next = new Set(expanded.value)
  next.has(id) ? next.delete(id) : next.add(id)
  expanded.value = next
}
function toggleAll() {
  const all = sections.value.every(node => expanded.value.has(node.id))
  expanded.value = all ? new Set() : new Set(sections.value.map(node => node.id))
}
function openDocument(id: string) {
  menuId.value = ''
  void router.push({ path: `/knowledge/${id}`, query: route.query })
}
function backToTree() { void router.push({ path: '/knowledge', query: route.query }) }
function openCreate(parentId = '') {
  createKind.value = 'document'; createParent.value = parentId; createTitle.value = ''; createOpen.value = true
}
async function createNode() {
  if (!createTitle.value.trim()) return
  busy.value = true; error.value = ''
  try {
    const id = crypto.randomUUID()
    const siblings = childNodes(createParent.value || null)
    const node: KnowledgeNode = { id, kind: createKind.value, title: createTitle.value.trim(), markdown: '', parentId: createParent.value || null, position: siblings.length, version: 1, path: '', archived: false }
    node.path = nodePath(node)
    await commitNode(node, null)
    createOpen.value = false
    if (node.parentId) expanded.value = new Set(expanded.value).add(node.parentId)
    if (node.kind === 'document') openDocument(node.id)
  } catch (err) { setError(err) } finally { busy.value = false }
}
async function saveNode(node: KnowledgeNode, title: string, markdown = node.markdown) {
  if (!title.trim() || (title.trim() === node.title && markdown === node.markdown)) return
  try {
    const updated = { ...node, title: title.trim(), markdown, version: node.version + 1 }
    updated.path = nodePath(updated)
    await commitNode(updated, node.version)
    refreshPaths()
  } catch (err) { setError(err) }
}
async function saveDocumentTitle(event: FocusEvent) {
  if (!document.value) return
  const input = event.target as HTMLInputElement
  await saveNode(document.value, input.value)
  titleEditing.value = false
}
async function saveMarkdown(event: FocusEvent) {
  if (!document.value) return
  const input = event.target as HTMLTextAreaElement
  await saveNode(document.value, document.value.title, input.value)
  markdownEditing.value = false
}
async function editTitle() { markdownEditing.value = false; titleEditing.value = true; await nextTick(); titleInput.value?.focus(); titleInput.value?.select() }
async function editMarkdown() { titleEditing.value = false; markdownEditing.value = true; await nextTick(); markdownInput.value?.focus() }
async function renameFromMenu(node: KnowledgeNode) {
  renameTarget.value = node
  renameValue.value = node.title
  menuId.value = ''
}
async function confirmRename() {
  if (renameTarget.value && renameValue.value.trim()) await saveNode(renameTarget.value, renameValue.value)
  renameTarget.value = null
}
async function confirmDelete() {
  if (!deleteTarget.value) return
  busy.value = true
  try {
    const deletingId = deleteTarget.value.id
    const deletePath = deleteTarget.value.path
    await queueKnowledgeDelete(deleteTarget.value)
    deleteTarget.value = null
    nodes.value = nodes.value.filter(node => node.id !== deletingId && !node.path.startsWith(`${deletePath} / `))
    if (navigator.onLine) requestSync()
    await refreshSyncState()
    if (documentId.value === deletingId || nodes.value.every(node => node.id !== documentId.value)) backToTree()
  } catch (err) { setError(err) } finally { busy.value = false }
}
function isDescendant(id: string, possibleAncestorId: string): boolean {
  let parentId = nodes.value.find(item => item.id === id)?.parentId
  while (parentId) {
    if (parentId === possibleAncestorId) return true
    parentId = nodes.value.find(item => item.id === parentId)?.parentId
  }
  return false
}
async function reorderNode(node: KnowledgeNode, targetId: string, placement: 'before' | 'after' | 'inside') {
  const target = nodes.value.find(item => item.id === targetId)
  if (!target || target.id === node.id) return
  let parentId: string | null
  let position: number
  if (placement === 'inside') {
    if (target.kind !== 'section') return
    parentId = target.id
    position = childNodes(parentId).filter(item => item.id !== node.id).length
  } else {
    parentId = target.parentId
    const siblings = childNodes(parentId).filter(item => item.id !== node.id)
    const targetIndex = siblings.findIndex(item => item.id === target.id)
    if (targetIndex < 0) return
    position = targetIndex + (placement === 'after' ? 1 : 0)
  }
  if (node.kind === 'section' && parentId && (parentId === node.id || isDescendant(parentId, node.id))) return
  if (node.parentId === parentId && node.position === position) return
  busy.value = true
  try {
    const updated = { ...node, parentId, position, version: node.version + 1 }
    updated.path = nodePath(updated)
    await commitNode(updated, node.version)
    const ordered = childNodes(parentId).filter(item => item.id !== node.id)
    ordered.splice(position, 0, updated)
    nodes.value = nodes.value.map(item => item.id === node.id ? updated : item.parentId === parentId ? { ...item, position: ordered.findIndex(sibling => sibling.id === item.id) } : item)
    refreshPaths()
    if (placement === 'inside') expanded.value = new Set(expanded.value).add(parentId!)
  } catch (err) { setError(err) } finally { busy.value = false }
}
function focusChat() {
  const node = document.value
  void router.push(chatRoute(node ? { entityType: 'knowledge.document', entityId: node.id, entityVersion: node.version } : undefined))
}
watch(documentId, () => { titleEditing.value = false; markdownEditing.value = false })
</script>

<template>
  <section class="knowledge-module" aria-labelledby="knowledge-heading">
    <header class="knowledge-header">
      <div><p class="eyebrow">Personal OS</p><h1 id="knowledge-heading">{{ document ? 'База знаний' : 'База знаний' }}</h1></div>
      <button class="knowledge-chat" type="button" aria-label="Открыть чат с документом" @click="focusChat">◌</button>
    </header>
    <div class="knowledge-sync" role="status">
      <span>{{ syncStatus === 'offline' || !isOnline ? 'Офлайн: локальная копия' : syncStatus === 'syncing' ? 'Синхронизация…' : syncStatus === 'error' ? 'Ошибка синхронизации' : conflicts ? 'Нужна проверка конфликта' : pendingCount ? 'Ожидает синхронизации' : 'Синхронизировано' }}</span>
      <span v-if="pendingCount">{{ pendingCount }} в очереди</span>
      <button type="button" :disabled="!isOnline || syncStatus === 'syncing'" @click="retrySync">Повторить</button>
    </div>
    <p v-if="error" class="knowledge-error" role="alert">{{ error }}</p>
    <section v-if="conflicts" class="knowledge-conflict" aria-live="polite">
      <strong>Есть {{ conflicts }} конфликт(а) синхронизации</strong>
      <p>Локальные изменения сохранены. Проверьте документ перед повторным изменением.</p>
      <ul><li v-for="conflict in conflictItems" :key="conflict.operationId">{{ (conflict.localPayload as KnowledgeNode | null)?.title || conflict.id }} — {{ conflict.conflictReason || 'Версия на сервере изменилась' }}</li></ul>
    </section>

    <template v-if="document">
      <div class="knowledge-detail-top"><button type="button" class="knowledge-back" @click="backToTree">← Назад</button><span>{{ document.path }}</span></div>
      <article class="knowledge-document">
        <h2 v-if="!titleEditing" class="knowledge-title-read" @click="editTitle">{{ document.title }}</h2>
        <input v-else ref="titleInput" class="knowledge-title-input" :value="document.title" aria-label="Название документа" @blur="saveDocumentTitle" @keydown.enter="($event.target as HTMLInputElement).blur()" @keydown.esc="($event.target as HTMLInputElement).blur()">
        <div v-if="!markdownEditing" class="knowledge-markdown-read" @click="editMarkdown">{{ document.markdown || 'Новый документ. Содержимое пока пустое.' }}</div>
        <textarea v-else ref="markdownInput" class="knowledge-markdown" :value="document.markdown" placeholder="Новый документ. Содержимое пока пустое." aria-label="Содержимое документа" @blur="saveMarkdown" @keydown.esc="($event.target as HTMLTextAreaElement).blur()" />
        <button class="knowledge-delete" type="button" @click="deleteTarget = document">Удалить документ</button>
      </article>
    </template>
    <template v-else>
      <div class="knowledge-toolbar" :class="{ 'is-searching': isSearching }">
        <label class="knowledge-search"><span aria-hidden="true">⌕</span><input v-model="query" type="search" placeholder="Поиск в базе знаний…" aria-label="Поиск в базе знаний"><button v-if="query" type="button" aria-label="Очистить поиск" @click="query = ''">×</button></label>
        <button type="button" class="knowledge-icon-button" :aria-label="expanded.size === sections.length ? 'Свернуть все разделы' : 'Развернуть все разделы'" @click="toggleAll">⌄</button>
        <button type="button" class="knowledge-icon-button" :aria-pressed="orderMode" :title="orderMode ? 'Выключить сортировку' : 'Включить сортировку'" @click="orderMode = !orderMode">↕</button>
        <button type="button" class="knowledge-add" aria-label="Создать документ или раздел" @click="openCreate()">＋</button>
      </div>
      <div v-if="isSearching" class="knowledge-results">
        <p v-if="localResultIds.size" class="knowledge-local-search">{{ isOnline ? 'Дополнительные локальные результаты по сохранённым документам.' : 'Локальные результаты: поиск выполнен по сохранённым документам.' }}</p>
        <button v-for="hit in results" :key="hit.source.id" type="button" class="knowledge-result" :class="{ 'is-local-result': localResultIds.has(hit.source.id) }" @click="openDocument(hit.source.id)">
          <strong>{{ hit.source.title }}</strong><small>{{ hit.source.path }}</small><span>{{ hit.source.snippet }}</span>
        </button>
        <p v-if="!results.length && isOnline" class="knowledge-empty">Ничего не найдено</p>
        <p v-else-if="!results.length" class="knowledge-empty">Нет совпадений в сохранённых документах.</p>
      </div>
      <div v-else class="knowledge-tree">
        <KnowledgeTreeNodes :nodes="tree" :all-nodes="nodes" :expanded="expanded" :order-mode="orderMode" :menu-id="menuId" @toggle="toggle" @open="openDocument" @menu="menuId = menuId === $event ? '' : $event" @create="openCreate" @rename="renameFromMenu" @delete="deleteTarget = $event" @reorder="reorderNode" />
        <p v-if="!nodes.length" class="knowledge-empty">База знаний пока пуста. Создайте первый документ или раздел.</p>
      </div>
    </template>

    <div v-if="createOpen" class="knowledge-overlay" @click.self="createOpen = false">
      <form class="knowledge-sheet" @submit.prevent="createNode">
        <div class="sheet-heading"><h2>{{ createKind === 'document' ? 'Новый документ' : 'Новый раздел' }}</h2><button type="button" aria-label="Закрыть" @click="createOpen = false">×</button></div>
        <div class="knowledge-kind"><button type="button" :class="{ active: createKind === 'document' }" @click="createKind = 'document'">Документ</button><button type="button" :class="{ active: createKind === 'section' }" @click="createKind = 'section'">Раздел</button></div>
        <label class="sheet-label">Где создать<select v-model="createParent"><option value="">Верхний уровень</option><option v-for="node in sections" :key="node.id" :value="node.id">{{ node.path }}</option></select></label>
        <label class="sheet-label">Название<input v-model="createTitle" autofocus maxlength="300" :placeholder="createKind === 'document' ? 'Название документа' : 'Название раздела'"></label>
        <button class="knowledge-submit" type="submit" :disabled="busy || !createTitle.trim()">Создать</button>
      </form>
    </div>
    <div v-if="deleteTarget" class="knowledge-overlay" @click.self="deleteTarget = null">
      <section class="knowledge-confirm" role="alertdialog" aria-modal="true" aria-labelledby="delete-title"><h2 id="delete-title">Удалить «{{ deleteTarget.title }}»?</h2><p>Элемент и его содержимое будут удалены из базы знаний.</p><div><button type="button" @click="deleteTarget = null">Отмена</button><button type="button" class="danger" :disabled="busy" @click="confirmDelete">Удалить</button></div></section>
    </div>
    <div v-if="renameTarget" class="knowledge-overlay" @click.self="renameTarget = null">
      <form class="knowledge-sheet" @submit.prevent="confirmRename"><div class="sheet-heading"><h2>Переименовать</h2><button type="button" aria-label="Закрыть" @click="renameTarget = null">×</button></div><label class="sheet-label">Название<input v-model="renameValue" autofocus maxlength="300"></label><button class="knowledge-submit" type="submit" :disabled="!renameValue.trim()">Сохранить</button></form>
    </div>
  </section>
</template>

<script lang="ts">
import { defineComponent, h, nextTick as vueNextTick, onUnmounted as onVueUnmounted, watch as vueWatch, type PropType, type VNode } from 'vue'
import type { KnowledgeNode as KNode } from './knowledgeApi'

const KnowledgeTreeNodes = defineComponent({
  name: 'KnowledgeTreeNodes',
  props: {
    nodes: { type: Array as PropType<KNode[]>, required: true },
    allNodes: { type: Array as PropType<KNode[]>, required: true },
    expanded: { type: Object as PropType<Set<string>>, required: true },
    orderMode: Boolean,
    menuId: String,
  },
  emits: ['toggle', 'open', 'menu', 'create', 'rename', 'delete', 'reorder'],
  setup(props, { emit }) {
    type Gesture = { id: string; pointerId: number; x: number; y: number; held: boolean; moved: boolean; timer?: number }
    type Drag = { node: KNode; pointerId: number; x: number; y: number; row: HTMLElement; ghost: HTMLElement | null; targetId: string; placement: 'before' | 'after' | 'inside' | null; started: boolean }
    let gesture: Gesture | null = null
    let drag: Drag | null = null
    let suppressedClickUntil = 0
    let ghost: HTMLElement | null = null
    const clearGesture = () => { if (gesture?.timer) window.clearTimeout(gesture.timer); gesture = null }
    const pointerDown = (node: KNode, event: PointerEvent) => {
      if (props.orderMode || (event.target as HTMLElement).closest('.knowledge-menu-trigger,.knowledge-chevron,.knowledge-context-menu')) return
      clearGesture()
      const current: Gesture = { id: node.id, pointerId: event.pointerId, x: event.clientX, y: event.clientY, held: false, moved: false }
      gesture = current
      if (event.pointerType === 'touch' || event.pointerType === 'pen') {
        current.timer = window.setTimeout(() => {
          if (gesture !== current || current.moved) return
          current.held = true
          suppressedClickUntil = Date.now() + 700
          emit('menu', node.id)
        }, 480)
      }
    }
    const pointerMove = (event: PointerEvent) => {
      if (drag?.pointerId === event.pointerId) {
        if (!drag.started && Math.hypot(event.clientX - drag.x, event.clientY - drag.y) > 6) {
          drag.started = true
          drag.row.classList.add('knowledge-drag-source')
          ghost = document.createElement('div')
          ghost.className = 'knowledge-drag-ghost'
          ghost.textContent = drag.node.title
          document.body.append(ghost)
          drag.ghost = ghost
        }
        if (!drag.started || !ghost) return
        event.preventDefault()
        ghost.style.left = `${event.clientX + 12}px`
        ghost.style.top = `${event.clientY + 12}px`
        document.querySelectorAll('.knowledge-drop-before,.knowledge-drop-after,.knowledge-drop-inside').forEach(row => row.classList.remove('knowledge-drop-before', 'knowledge-drop-after', 'knowledge-drop-inside'))
        const hit = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('.knowledge-row[data-id]')
        drag.targetId = ''
        drag.placement = null
        const candidate = hit?.dataset.id ? props.allNodes.find(node => node.id === hit.dataset.id) : undefined
        if (!hit || !candidate || candidate.id === drag.node.id) return
        const rect = hit.getBoundingClientRect()
        const ratio = (event.clientY - rect.top) / Math.max(rect.height, 1)
        const placement = candidate.kind === 'section' && ratio >= .24 && ratio <= .76 ? 'inside' : ratio < .5 ? 'before' : 'after'
        const parentId = placement === 'inside' ? candidate.id : candidate.parentId
        if (drag.node.kind === 'section' && parentId && (parentId === drag.node.id || isUnder(parentId, drag.node.id))) return
        hit.classList.add(placement === 'inside' ? 'knowledge-drop-inside' : placement === 'before' ? 'knowledge-drop-before' : 'knowledge-drop-after')
        drag.targetId = candidate.id
        drag.placement = placement
        return
      }
      if (!gesture || gesture.pointerId !== event.pointerId) return
      const dx = event.clientX - gesture.x
      const dy = event.clientY - gesture.y
      if (Math.hypot(dx, dy) > 9) {
        gesture.moved = true
        if (gesture.timer) window.clearTimeout(gesture.timer)
        gesture.timer = undefined
      }
    }
    const pointerUp = (event: PointerEvent) => {
      if (drag?.pointerId === event.pointerId) {
        const current = drag
        current.row.classList.remove('knowledge-drag-source')
        ghost?.remove(); ghost = null
        document.querySelectorAll('.knowledge-drop-before,.knowledge-drop-after,.knowledge-drop-inside').forEach(row => row.classList.remove('knowledge-drop-before', 'knowledge-drop-after', 'knowledge-drop-inside'))
        drag = null
        if (current.started) {
          suppressedClickUntil = Date.now() + 700
          if (current.targetId && current.placement) emit('reorder', current.node, current.targetId, current.placement)
        }
      }
      if (!gesture || gesture.pointerId !== event.pointerId) return
      const current = gesture
      if (current.timer) window.clearTimeout(current.timer)
      const dx = event.clientX - current.x
      const dy = event.clientY - current.y
      gesture = null
      if (!current.held && Math.hypot(dx, dy) > 38 && dx < -35 && Math.abs(dy) < 45) {
        suppressedClickUntil = Date.now() + 700
        emit('menu', current.id)
      }
    }
    const pointerCancel = () => { clearGesture(); if (drag) { drag.row.classList.remove('knowledge-drag-source'); ghost?.remove(); ghost = null; drag = null } }
    const isUnder = (id: string, ancestorId: string): boolean => {
      let current = props.allNodes.find(node => node.id === id)
      while (current?.parentId) {
        if (current.parentId === ancestorId) return true
        current = props.allNodes.find(node => node.id === current!.parentId)
      }
      return false
    }
    const startDrag = (node: KNode, event: PointerEvent) => {
      if (!props.orderMode || event.button !== 0) return
      event.preventDefault()
      const row = (event.currentTarget as HTMLElement).closest<HTMLElement>('.knowledge-row')
      if (!row) return
      ;(event.currentTarget as HTMLElement).setPointerCapture?.(event.pointerId)
      drag = { node, pointerId: event.pointerId, x: event.clientX, y: event.clientY, row, ghost: null, targetId: '', placement: null, started: false }
    }
    window.addEventListener('pointermove', pointerMove, { passive: false })
    window.addEventListener('pointerup', pointerUp)
    window.addEventListener('pointercancel', pointerCancel)
    onVueUnmounted(() => {
      window.removeEventListener('pointermove', pointerMove)
      window.removeEventListener('pointerup', pointerUp)
      window.removeEventListener('pointercancel', pointerCancel)
      pointerCancel()
    })
    vueWatch(() => props.menuId, async id => {
      if (!id) return
      await vueNextTick()
      const row = document.querySelector<HTMLElement>(`.knowledge-row[data-id="${id}"]`)
      const menu = row?.querySelector<HTMLElement>('.knowledge-context-menu')
      if (!row || !menu) return
      const rowRect = row.getBoundingClientRect()
      const width = menu.offsetWidth
      const height = menu.offsetHeight
      menu.style.left = `${Math.max(8, Math.min(window.innerWidth - width - 8, rowRect.right - width))}px`
      menu.style.top = `${rowRect.bottom + height + 8 > window.innerHeight ? Math.max(8, rowRect.top - height - 5) : rowRect.bottom + 5}px`
    })
    const draw = (items: KNode[], depth = 0): VNode[] => items.flatMap((node, index) => {
      const row = h('div', { class: ['knowledge-row', `is-${node.kind}`], 'data-id': node.id, style: { '--depth': depth }, onPointerdown: (event: PointerEvent) => pointerDown(node, event), onContextmenu: (event: MouseEvent) => { event.preventDefault(); if (!props.orderMode) emit('menu', node.id) } }, [
        node.kind === 'section' ? h('button', { class: 'knowledge-chevron', type: 'button', onClick: () => emit('toggle', node.id), 'aria-label': props.expanded.has(node.id) ? 'Свернуть раздел' : 'Раскрыть раздел' }, props.expanded.has(node.id) ? '⌄' : '›') : h('span', { class: 'knowledge-file-icon', 'aria-hidden': 'true' }, '▤'),
        h('button', { class: 'knowledge-node-name', type: 'button', onClick: () => { if (Date.now() < suppressedClickUntil || props.orderMode) return; node.kind === 'section' ? emit('toggle', node.id) : emit('open', node.id) } }, node.title),
        props.orderMode ? h('button', { class: 'knowledge-drag-handle', type: 'button', 'aria-label': `Перетащить ${node.title}`, onPointerdown: (event: PointerEvent) => startDrag(node, event) }, '⠿') : h('button', { class: 'knowledge-menu-trigger', type: 'button', 'aria-label': `Действия: ${node.title}`, 'aria-expanded': props.menuId === node.id, onClick: () => emit('menu', node.id) }, '⋯'),
        props.menuId === node.id && !props.orderMode ? h('div', { class: 'knowledge-context-menu', role: 'menu' }, [
          h('button', { type: 'button', role: 'menuitem', onClick: () => emit('rename', node) }, 'Переименовать'),
          node.kind === 'section' ? h('button', { type: 'button', role: 'menuitem', onClick: () => emit('create', node.id) }, 'Создать внутри') : null,
          h('button', { type: 'button', role: 'menuitem', class: 'danger', onClick: () => emit('delete', node) }, 'Удалить'),
        ]) : null,
      ])
      const nested = node.kind === 'section' && props.expanded.has(node.id)
        ? draw(props.allNodes.filter(child => child.parentId === node.id).sort((a, b) => a.position - b.position), depth + 1)
        : []
      return [row, ...nested]
    })
    return () => h('div', { class: 'knowledge-tree-nodes' }, draw(props.nodes))
  },
})

export default { components: { KnowledgeTreeNodes } }
</script>
