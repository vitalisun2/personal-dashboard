<script setup lang="ts">
import { computed, nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import { requestSync, subscribeSyncStatus, type SyncStatus } from '../../offline/runtime'
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
const renameId = ref('')
const createOpen = ref(false)
const createKind = ref<'section' | 'document'>('document')
const createTitle = ref('')
const createParent = ref('')
const parentListOpen = ref(false)
const deleteTarget = ref<KnowledgeNode | null>(null)
const titleEditing = ref(false)
const markdownEditing = ref(false)
const titleInput = ref<HTMLInputElement | null>(null)
const markdownInput = ref<HTMLTextAreaElement | null>(null)
const nameField = ref<HTMLInputElement | null>(null)
const searchInput = ref<HTMLInputElement | null>(null)

const tree = computed(() => {
  const children = (parentId: string | null): KnowledgeNode[] => nodes.value
    .filter(node => node.parentId === parentId)
    .sort((a, b) => a.position - b.position)
  return children(null)
})
const sectionsWithDepth = computed(() => {
  const out: { node: KnowledgeNode; depth: number }[] = []
  const walk = (parentId: string | null, depth: number) => {
    for (const node of childNodes(parentId)) {
      if (node.kind !== 'section') continue
      out.push({ node, depth })
      walk(node.id, depth + 1)
    }
  }
  walk(null, 0)
  return out
})
const documentId = computed(() => {
  const match = route.params.pathMatch
  return Array.isArray(match) ? String(match.at(-1) || '') : String(match || '')
})
const document = computed(() => nodes.value.find(node => node.id === documentId.value && node.kind === 'document'))
const isSearching = computed(() => query.value.trim().length > 0)
const searchGroups = computed(() => {
  const lexical = results.value.filter(hit => hit.matchKind === 'lexical')
  const semantic = results.value.filter(hit => hit.matchKind === 'semantic')
  const groups = [
    { kind: 'lexical', title: 'Точные совпадения', symbol: 'Aa', hits: lexical },
    { kind: 'semantic', title: 'По смыслу', symbol: '≈', hits: semantic },
  ]
  const term = query.value.trim()
  const longQuery = term.split(/\s+/).length >= 4 || term.length > 28
  return (longQuery ? groups.reverse() : groups).filter(group => group.hits.length > 0)
})
const collapseLabel = computed(() => {
  const ids = sectionsWithDepth.value.map(item => item.node.id)
  const allOpen = ids.length > 0 && ids.every(id => expanded.value.has(id))
  return allOpen ? 'Свернуть все разделы' : 'Развернуть все разделы'
})
const collapseIcon = computed(() => {
  const inward = collapseLabel.value === 'Свернуть все разделы'
  return {
    upper: inward ? 'M5 3 10 8 15 3' : 'M5 8 10 3 15 8',
    lower: inward ? 'M5 17 10 12 15 17' : 'M5 12 10 17 15 12',
  }
})
const createWhereLabel = computed(() => {
  if (!createParent.value) return 'Верхний уровень'
  return sectionsWithDepth.value.find(item => item.node.id === createParent.value)?.node.title || 'Верхний уровень'
})

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
      const supplements: SearchHit[] = localHits.filter(node => !seen.has(node.id)).map(node => ({ source: { kind: 'knowledge.document', id: node.id, version: node.version, url: `/knowledge/${node.id}`, title: node.title, path: node.path, snippet: node.markdown.slice(0, 180), updatedAtUtc: '' }, score: 0, matchKind: 'lexical' }))
      localResultIds.value = new Set(supplements.map(hit => hit.source.id))
      results.value = [...response.hits, ...supplements]
    } else {
      localResultIds.value = new Set(localHits.map(node => node.id))
      results.value = localHits.map(node => ({ source: { kind: 'knowledge.document', id: node.id, version: node.version, url: `/knowledge/${node.id}`, title: node.title, path: node.path, snippet: node.markdown.slice(0, 180), updatedAtUtc: '' }, score: 0, matchKind: 'lexical' }))
    }
  } catch (err) {
    if (revision !== searchRevision) return
    setError(err)
    localResultIds.value = new Set(localHits.map(node => node.id))
    results.value = localHits.map(node => ({ source: { kind: 'knowledge.document', id: node.id, version: node.version, url: `/knowledge/${node.id}`, title: node.title, path: node.path, snippet: node.markdown.slice(0, 180), updatedAtUtc: '' }, score: 0, matchKind: 'lexical' }))
  }
}
watch(() => route.query.q, value => { query.value = String(value || ''); void runSearch() })
watch(query, value => {
  const q = value.trim()
  if (q !== String(route.query.q || '')) void router.replace({ query: { ...route.query, ...(q ? { q } : { q: undefined }) } })
  void runSearch()
})
watch(documentId, id => { if (id && !nodes.value.some(node => node.id === id)) void load() })
watch(documentId, () => { titleEditing.value = false; markdownEditing.value = false })
watch(createOpen, open => {
  if (!open) parentListOpen.value = false
  else void nextTick(() => nameField.value?.focus({ preventScroll: true }))
})
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
  const ids = sectionsWithDepth.value.map(item => item.node.id)
  const allOpen = ids.length > 0 && ids.every(id => expanded.value.has(id))
  const next = new Set(expanded.value)
  if (allOpen) ids.forEach(id => next.delete(id))
  else ids.forEach(id => next.add(id))
  expanded.value = next
}
function openDocument(id: string) {
  menuId.value = ''
  void router.push({ path: `/knowledge/${id}`, query: route.query })
}
function backToTree() { void router.push({ path: '/knowledge', query: route.query }) }
function openCreate() {
  menuId.value = ''
  createKind.value = 'document'; createParent.value = ''; createTitle.value = ''; parentListOpen.value = false
  createOpen.value = true
}
function closeCreate() { createOpen.value = false; parentListOpen.value = false }
function selectParent(id: string) {
  createParent.value = id
  parentListOpen.value = false
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
function cancelTitleEdit() { titleEditing.value = false }
function cancelMarkdownEdit() { markdownEditing.value = false }
async function editTitle() { markdownEditing.value = false; titleEditing.value = true; await nextTick(); titleInput.value?.focus(); titleInput.value?.select() }
async function editMarkdown() { titleEditing.value = false; markdownEditing.value = true; await nextTick(); markdownInput.value?.focus() }
function renameFromMenu(node: KnowledgeNode) {
  renameId.value = node.id
  menuId.value = ''
}
async function saveRename(node: KnowledgeNode, title: string) {
  renameId.value = ''
  if (title && title !== node.title) await saveNode(node, title)
}
function cancelRename() { renameId.value = '' }
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
function clearSearch() {
  query.value = ''
  searchInput.value?.focus({ preventScroll: true })
}

const STOP_WORDS = new Set(['где', 'что', 'как', 'мы', 'про', 'это', 'там', 'под', 'для', 'вот', 'когда', 'найди', 'поиск', 'делали', 'было'])
function queryWords(q: string): string[] {
  return q.toLocaleLowerCase().replace(/[.,!?;:()]/g, ' ').split(/\s+/).filter(Boolean)
}
function escapeHtml(value: string): string {
  return String(value).replace(/[&<>"']/g, char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char] || char)
}
function regexEscape(value: string): string {
  return String(value).replace(/[.*+?^${}()|[\]\\]/g, '\\$&')
}
function highlight(text: string, rawQuery: string): string {
  let out = escapeHtml(text)
  for (const word of queryWords(rawQuery)) {
    if (word.length >= 3 && !STOP_WORDS.has(word)) {
      out = out.replace(new RegExp(`(${regexEscape(word)})`, 'ig'), '<mark class="exact">$1</mark>')
    }
  }
  return out
}
function resultPath(hit: SearchHit): string {
  return hit.source.path.split(' / ').slice(0, -1).join(' › ')
}
</script>

<template>
  <section id="knowledgeFX" class="knowledge-section" aria-label="База знаний">
      <p v-if="error" class="knowledge-error" role="alert">{{ error }}</p>

    <div v-if="document" id="docFX" class="scroll">
      <div class="doc-detail-top">
        <button type="button" class="doc-action doc-back doc-detail-back" @click="backToTree">← Назад</button>
      </div>
      <div class="doc-title-wrap">
        <button v-if="!titleEditing" type="button" class="doc-open-title" @click="editTitle">{{ document.title }}</button>
        <input v-else ref="titleInput" class="doc-title-input" type="text" :value="document.title" aria-label="Название документа" @blur="saveDocumentTitle" @keydown.enter="($event.target as HTMLInputElement).blur()" @keydown.esc="cancelTitleEdit">
      </div>
      <div class="doc-content-card">
        <div v-if="!markdownEditing" class="doc-open-content" @click="editMarkdown">{{ document.markdown || 'Новый документ. Содержимое пока пустое.' }}</div>
        <textarea v-else ref="markdownInput" class="doc-content-input" :value="document.markdown" placeholder="Новый документ. Содержимое пока пустое." aria-label="Содержимое документа" @blur="saveMarkdown" @keydown.esc="cancelMarkdownEdit" />
      </div>
      <div class="doc-detail-bottom-actions">
        <button type="button" class="doc-delete-icon" aria-label="Удалить документ" title="Удалить документ" @click="deleteTarget = document"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h16"/><path d="M9 7V4h6v3"/><path d="m6 7 1 13h10l1-13"/><path d="M10 11v5M14 11v5"/></svg></button>
      </div>
    </div>

    <template v-else>
      <div class="toolbar" :class="{ 'search-expanded': isSearching }">
        <label class="search">
          <span aria-hidden="true">⌕</span>
          <input ref="searchInput" v-model="query" type="text" autocomplete="off" placeholder="Поиск в базе знаний…" aria-label="Поиск в базе знаний">
          <button type="button" class="clear" :class="{ show: isSearching }" aria-label="Очистить поиск" @click="clearSearch">×</button>
        </label>
        <button type="button" class="collapse-toggle" :aria-label="collapseLabel" :title="collapseLabel" @click="toggleAll"><svg viewBox="0 0 20 20" aria-hidden="true"><path :d="collapseIcon.upper"/><path :d="collapseIcon.lower"/></svg></button>
        <button type="button" class="order-mode-toggle" :disabled="isSearching" :aria-pressed="orderMode" :aria-label="orderMode ? 'Выключить сортировку' : 'Включить сортировку'" :title="orderMode ? 'Выключить сортировку' : 'Включить сортировку'" @click="orderMode = !orderMode"><svg viewBox="0 0 24 24" aria-hidden="true"><path d="M4 7h11M4 12h11M4 17h11M19 6v12m-2.5-2.5L19 18l2.5-2.5"/></svg></button>
        <button type="button" class="plus" aria-label="Создать документ или раздел" @click="openCreate()">＋</button>
      </div>
      <div class="scroll">
        <div v-if="!isSearching" class="tree">
          <KnowledgeTreeNodes
            :nodes="tree" :all-nodes="nodes" :expanded="expanded"
            :order-mode="orderMode" :menu-id="menuId" :rename-id="renameId"
            @toggle="toggle"
            @open="openDocument"
            @menu="menuId = menuId === $event ? '' : $event"
            @close="menuId = ''"
            @rename="renameFromMenu"
            @rename-save="saveRename"
            @rename-cancel="cancelRename"
            @delete="menuId = ''; deleteTarget = $event"
            @reorder="reorderNode"
          />
          <p v-if="!nodes.length" class="empty">База знаний пока пуста. Создайте первый документ или раздел.</p>
        </div>
        <div v-else class="results">
          <p v-if="localResultIds.size" class="knowledge-local-note">{{ isOnline ? 'Дополнительно найдено в локальной копии.' : 'Поиск выполнен по сохранённым документам (офлайн).' }}</p>
          <section v-for="group in searchGroups" :key="group.kind" class="group" :class="group.kind === 'semantic' ? 'semantic' : 'exact'">
            <div class="group-head"><span class="group-name"><span class="kind">{{ group.symbol }}</span>{{ group.title }}</span><span>{{ group.hits.length }}</span></div>
            <button v-for="hit in group.hits" :key="hit.source.id" type="button" class="result" :class="{ 'is-local-result': localResultIds.has(hit.source.id) }" @click="openDocument(hit.source.id)">
              <div class="result-title" v-html="group.kind === 'lexical' ? highlight(hit.source.title, query) : escapeHtml(hit.source.title)"></div>
              <div class="result-path">{{ resultPath(hit) }}</div>
              <div class="result-snippet" v-html="group.kind === 'lexical' ? highlight(hit.source.snippet, query) : escapeHtml(hit.source.snippet)"></div>
            </button>
          </section>
          <p v-if="!results.length" class="empty">Ничего не найдено</p>
        </div>
      </div>
    </template>

    <div class="overlay" :class="{ open: createOpen }" @click.self="closeCreate"></div>
    <section class="sheet" :class="{ open: createOpen }" aria-label="Создание документа или раздела">
      <div class="sheet-head">
        <div class="sheet-title">{{ createKind === 'document' ? 'Новый документ' : 'Новый раздел' }}</div>
        <button type="button" class="sheet-close" aria-label="Закрыть" @click="closeCreate">×</button>
      </div>
      <div class="type-switch">
        <button type="button" class="type-btn" :class="{ active: createKind === 'document' }" @click="createKind = 'document'">Документ</button>
        <button type="button" class="type-btn" :class="{ active: createKind === 'section' }" @click="createKind = 'section'">Раздел</button>
      </div>
      <input ref="nameField" v-model="createTitle" class="name-field" type="text" maxlength="300" :placeholder="createKind === 'document' ? 'Название документа' : 'Название раздела'">
      <button type="button" class="where" @click="parentListOpen = !parentListOpen">
        <span class="where-value">{{ createWhereLabel }}</span>
        <span class="where-arrow">›</span>
      </button>
      <div class="parent-list" :class="{ open: parentListOpen }">
        <button type="button" class="parent-option" :class="{ selected: !createParent }" @click="selectParent('')">Верхний уровень</button>
        <button v-for="item in sectionsWithDepth" :key="item.node.id" type="button" class="parent-option" :class="{ selected: createParent === item.node.id }" :style="{ paddingLeft: `${9 + item.depth * 15}px` }" @click="selectParent(item.node.id)">{{ item.node.title }}</button>
      </div>
      <button type="button" class="create-submit" :disabled="busy || !createTitle.trim()" @click="createNode">Создать</button>
    </section>

    <div v-if="deleteTarget" class="app-confirm-layer" @click.self="deleteTarget = null">
      <div class="app-confirm-card" role="alertdialog" aria-modal="true" aria-labelledby="kb-confirm-title">
        <div class="app-confirm-title" id="kb-confirm-title">Удалить {{ deleteTarget.kind === 'section' ? 'раздел' : 'документ' }} «{{ deleteTarget.title }}»?</div>
        <div class="app-confirm-body">{{ deleteTarget.kind === 'section' ? 'Все документы внутри раздела тоже будут удалены.' : 'Документ будет удалён из базы знаний.' }}</div>
        <div class="app-confirm-actions">
          <button type="button" class="app-confirm-cancel" @click="deleteTarget = null">Отмена</button>
          <button type="button" class="app-confirm-accept" :disabled="busy" @click="confirmDelete">Удалить</button>
        </div>
      </div>
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
    renameId: String,
  },
  emits: ['toggle', 'open', 'menu', 'close', 'rename', 'rename-save', 'rename-cancel', 'delete', 'reorder'],
  setup(props, { emit }) {
    type Gesture = { id: string; pointerId: number; x: number; y: number; held: boolean; moved: boolean; timer?: number }
    type Drag = { node: KNode; pointerId: number; x: number; y: number; row: HTMLElement; ghost: HTMLElement | null; targetId: string; placement: 'before' | 'after' | 'inside' | null; started: boolean }
    let gesture: Gesture | null = null
    let drag: Drag | null = null
    let suppressedClickUntil = 0
    let ghostEl: HTMLElement | null = null

    const clearGesture = () => { if (gesture?.timer) window.clearTimeout(gesture.timer); gesture = null }
    const pointerDown = (node: KNode, event: PointerEvent) => {
      if (props.orderMode || (event.target as HTMLElement).closest('.handle,.row-menu-trigger,.rename-input')) return
      clearGesture()
      const current: Gesture = { id: node.id, pointerId: event.pointerId, x: event.clientX, y: event.clientY, held: false, moved: false }
      gesture = current
      ;(event.target as HTMLElement).setPointerCapture?.(event.pointerId)
      if (event.pointerType === 'touch' || event.pointerType === 'pen') {
        current.timer = window.setTimeout(() => {
          if (gesture !== current || current.moved) return
          current.held = true
          suppressedClickUntil = Date.now() + 350
          emit('menu', node.id)
        }, 480)
      }
    }
    const pointerMove = (event: PointerEvent) => {
      if (drag?.pointerId === event.pointerId) {
        const current = drag
        if (!current.started && Math.hypot(event.clientX - current.x, event.clientY - current.y) > 6) {
          current.started = true
          current.row.classList.add('drag-source')
          const ghost = document.createElement('div')
          ghost.className = 'reorder-ghost'
          ghost.textContent = current.node.title
          const rect = current.row.getBoundingClientRect()
          ghost.style.width = `${rect.width}px`
          ghost.style.height = `${rect.height}px`
          document.body.append(ghost)
          ghostEl = ghost
          current.ghost = ghost
        }
        if (!current.started || !ghostEl) return
        event.preventDefault()
        ghostEl.style.left = `${event.clientX + 12}px`
        ghostEl.style.top = `${event.clientY + 12}px`
        dropClear()
        const hit = document.elementFromPoint(event.clientX, event.clientY)?.closest<HTMLElement>('.node[data-id]')
        current.targetId = ''
        current.placement = null
        const candidate = hit?.dataset.id ? props.allNodes.find(node => node.id === hit.dataset.id) : undefined
        if (!hit || !candidate || candidate.id === current.node.id) return
        const rect = hit.getBoundingClientRect()
        const ratio = (event.clientY - rect.top) / Math.max(rect.height, 1)
        const placement = candidate.kind === 'section' && ratio >= .2 && ratio <= .8 ? 'inside' : ratio < .5 ? 'before' : 'after'
        const parentId = placement === 'inside' ? candidate.id : candidate.parentId
        if (current.node.kind === 'section' && parentId && (parentId === current.node.id || isUnder(parentId, current.node.id))) return
        hit.classList.add(placement === 'inside' ? 'drop-inside' : placement === 'before' ? 'drop-before' : 'drop-after')
        current.targetId = candidate.id
        current.placement = placement
        return
      }
      if (!gesture || gesture.pointerId !== event.pointerId) return
      const dx = event.clientX - gesture.x
      const dy = event.clientY - gesture.y
      if (Math.hypot(dx, dy) > 8) {
        gesture.moved = true
        if (gesture.timer) { window.clearTimeout(gesture.timer); gesture.timer = undefined }
      }
      if (Math.abs(dy) > Math.abs(dx) && Math.abs(dy) > 10) { gesture = null; return }
      if (dx < -12 && Math.abs(dx) > Math.abs(dy) * 1.1) event.preventDefault()
    }
    const pointerUp = (event: PointerEvent) => {
      if (drag?.pointerId === event.pointerId) {
        const current = drag
        current.row.classList.remove('drag-source')
        ghostEl?.remove(); ghostEl = null; drag = null
        dropClear()
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
      if (current.held) return
      if (Math.abs(dx) < Math.abs(dy) * 1.2) return
      if (dx <= -56) {
        suppressedClickUntil = Date.now() + 320
        emit('menu', current.id)
      } else if (dx >= 45 && props.menuId === current.id) {
        suppressedClickUntil = Date.now() + 320
        emit('close')
      }
    }
    const pointerCancel = () => {
      clearGesture()
      if (drag) { drag.row.classList.remove('drag-source'); ghostEl?.remove(); ghostEl = null; drag = null }
    }
    const dragStart = (node: KNode, event: PointerEvent) => {
      if (!props.orderMode || event.button !== 0) return
      event.preventDefault()
      const row = (event.currentTarget as HTMLElement).closest<HTMLElement>('.node')
      if (!row) return
      ;(event.currentTarget as HTMLElement).setPointerCapture?.(event.pointerId)
      drag = { node, pointerId: event.pointerId, x: event.clientX, y: event.clientY, row, ghost: null, targetId: '', placement: null, started: false }
    }
    const dropClear = () => {
      document.querySelectorAll('.drop-before,.drop-after,.drop-inside').forEach(el => el.classList.remove('drop-before', 'drop-after', 'drop-inside'))
    }
    const isUnder = (id: string, ancestorId: string): boolean => {
      let current = props.allNodes.find(node => node.id === id)
      while (current?.parentId) {
        if (current.parentId === ancestorId) return true
        current = props.allNodes.find(node => node.id === current!.parentId)
      }
      return false
    }
    const closeMenu = () => { if (props.menuId) emit('close') }
    const onDocumentPointerDown = (event: PointerEvent) => {
      if (!props.menuId) return
      const target = event.target as HTMLElement | Document
      if (target instanceof Element && target.closest('.row-context-menu,.row-menu-trigger')) return
      closeMenu()
    }
    const onDocumentKeyDown = (event: KeyboardEvent) => {
      if (event.key === 'Escape' && props.menuId) { event.preventDefault(); closeMenu() }
    }
    const onDocumentScroll = (event: Event) => {
      if (props.menuId && !(event.target as HTMLElement).closest('.row-context-menu')) closeMenu()
    }
    const suppressClick = (event: MouseEvent) => {
      if (Date.now() < suppressedClickUntil && !(event.target as HTMLElement).closest('.row-context-menu,.app-confirm-layer')) {
        event.preventDefault()
        event.stopPropagation()
      }
    }
    document.addEventListener('pointerdown', onDocumentPointerDown, true)
    document.addEventListener('keydown', onDocumentKeyDown, true)
    document.addEventListener('scroll', onDocumentScroll, true)
    document.addEventListener('click', suppressClick, true)
    window.addEventListener('pointermove', pointerMove, { passive: false })
    window.addEventListener('pointerup', pointerUp)
    window.addEventListener('pointercancel', pointerCancel)
    onVueUnmounted(() => {
      document.removeEventListener('pointerdown', onDocumentPointerDown, true)
      document.removeEventListener('keydown', onDocumentKeyDown, true)
      document.removeEventListener('scroll', onDocumentScroll, true)
      document.removeEventListener('click', suppressClick, true)
      window.removeEventListener('pointermove', pointerMove)
      window.removeEventListener('pointerup', pointerUp)
      window.removeEventListener('pointercancel', pointerCancel)
      pointerCancel()
    })
    vueWatch(() => props.menuId, async id => {
      if (!id) return
      await vueNextTick()
      const row = document.querySelector<HTMLElement>(`.node[data-id="${id}"]`)
            const menu = document.querySelector<HTMLElement>('.knowledge-tree-nodes .row-context-menu')
      if (!row || !menu) return
      const rowRect = row.getBoundingClientRect()
      const width = menu.offsetWidth
      const height = menu.offsetHeight
      menu.style.position = 'fixed'
      menu.style.left = `${Math.max(8, Math.min(window.innerWidth - width - 8, rowRect.right - width))}px`
      menu.style.top = `${rowRect.bottom + height + 8 > window.innerHeight ? Math.max(8, rowRect.top - height - 5) : rowRect.bottom + 5}px`
      void menu.offsetWidth
      menu.classList.add('opening')
    })
    vueWatch(() => props.renameId, async id => {
      if (!id) return
      await vueNextTick()
      const input = document.querySelector<HTMLInputElement>(`.rename-input[data-id="${id}"]`)
      input?.focus()
      input?.select()
    })
    const nodeClick = (node: KNode) => {
      if (Date.now() < suppressedClickUntil || props.orderMode) return
      node.kind === 'section' ? emit('toggle', node.id) : emit('open', node.id)
    }
    const renameInputHandlers = (node: KNode) => {
      let done = false
      const finish = (event: FocusEvent | KeyboardEvent, save: boolean) => {
        if (done) return
        done = true
        const input = event.target as HTMLInputElement
        if (save) emit('rename-save', node, input.value.trim())
        else emit('rename-cancel')
      }
      return {
        onKeydown: (event: KeyboardEvent) => {
          if (event.key === 'Enter') { event.preventDefault(); finish(event, true) }
          else if (event.key === 'Escape') { event.preventDefault(); finish(event, false) }
        },
        onBlur: (event: FocusEvent) => finish(event, true),
      }
    }
    const dots = () => h('span', Array.from({ length: 6 }, () => h('i')))
    const draw = (items: KNode[], depth = 0): VNode[] => items.flatMap(node => {
      const rowChildren: VNode[] = []
      if (props.renameId === node.id) {
        rowChildren.push(h('input', { class: 'rename-input', 'data-id': node.id, value: node.title, 'aria-label': 'Название', ...renameInputHandlers(node) }))
      } else {
        if (node.kind === 'section') {
          rowChildren.push(h('button', { class: ['chev', { open: props.expanded.has(node.id) }], type: 'button', 'aria-label': props.expanded.has(node.id) ? 'Свернуть раздел' : 'Раскрыть раздел', onClick: () => { if (Date.now() < suppressedClickUntil || props.orderMode) return; emit('toggle', node.id) } }, '›'))
          rowChildren.push(h('button', { class: 'section-name', type: 'button', onClick: () => nodeClick(node) }, node.title))
        } else {
          rowChildren.push(h('button', { class: 'doc-title', type: 'button', onClick: () => nodeClick(node) }, node.title))
        }
        rowChildren.push(h('button', {
          class: 'row-menu-trigger',
          type: 'button',
          hidden: props.orderMode,
          title: 'Действия',
          'aria-label': `Действия с ${node.title}`,
          'aria-haspopup': 'menu',
          'aria-expanded': props.menuId === node.id,
          onClick: (event: MouseEvent) => { event.stopPropagation(); emit('menu', node.id) },
        }, '⋯'))
        if (props.orderMode) {
          rowChildren.push(h('button', { class: 'handle', type: 'button', 'data-drag': node.id, 'aria-label': `Перетащить ${node.title}`, onPointerdown: (event: PointerEvent) => dragStart(node, event) }, dots()))
        }
      }
      if (props.menuId === node.id && !props.orderMode && props.renameId !== node.id) {
              // меню отрисовывается на уровне дерева (см. return ниже) — не внутри строки
            }
            return [
              h('div', {
                class: ['row-wrap', { 'context-active': props.menuId === node.id }],
                onPointerdown: (event: PointerEvent) => pointerDown(node, event),
                onContextmenu: (event: MouseEvent) => {
                  event.preventDefault()
                  if (!props.orderMode && props.menuId !== node.id) emit('menu', node.id)
                },
              }, [
                h('div', { class: ['node', node.kind === 'section' ? 'section' : 'document'], 'data-id': node.id, style: { paddingLeft: `${depth * 17}px` } }, rowChildren),
              ]),
              ...(node.kind === 'section' && props.expanded.has(node.id)
                ? draw(props.allNodes.filter(child => child.parentId === node.id).sort((a, b) => a.position - b.position), depth + 1)
                : []),
            ]
          })
          const menuRoot = () => {
                      const active = props.allNodes.find(node => node.id === props.menuId)
                      if (!active || props.orderMode || props.renameId === active.id) return []
                      return [h('div', { class: 'row-context-menu', role: 'menu', 'aria-label': `Действия с ${active.title}` }, [
              h('button', { class: 'row-context-item', type: 'button', role: 'menuitem', onClick: () => emit('rename', active) }, 'Переименовать'),
              h('button', { class: ['row-context-item', 'danger'], type: 'button', role: 'menuitem', onClick: () => emit('delete', active) }, active.kind === 'section' ? 'Удалить раздел' : 'Удалить документ'),
            ])]
          }
          return () => h('div', { class: 'knowledge-tree-nodes' }, [...draw(props.nodes), ...menuRoot()])
        },
      })

export default { components: { KnowledgeTreeNodes } }
</script>
