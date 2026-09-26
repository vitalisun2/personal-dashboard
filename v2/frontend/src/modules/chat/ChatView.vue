<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { chatApi, ProposalConflictError, type ChatConversation, type ChatEntityEntry, type ChatScope, type ChatTurn, type ProposalConflictPayload } from './chatApi'
import './chatView.css'

const props = defineProps<{
  conversationId?: string
  targetTurnId?: string
  entity?: ChatEntityEntry
}>()

const emit = defineEmits<{ (event: 'back'): void }>()

const conversation = ref<ChatConversation | null>(null)
const history = ref<{ id: string; title: string; updatedAt: string }[]>([])
const historyCursor = ref<string | null>(null)
const loadingHistory = ref(false)
const showHistory = ref(false)
const loading = ref(false)
const sending = ref(false)
const loadingOlder = ref(false)
const error = ref('')
const conflictPreview = ref<ProposalConflictPayload | null>(null)
const input = ref('')
const scopeMode = ref<'entity' | 'general'>(props.entity ? 'entity' : 'general')
const scrollContainer = ref<HTMLElement | null>(null)
const inputElement = ref<HTMLTextAreaElement | null>(null)

const scope = computed<ChatScope>(() => scopeMode.value === 'entity' && props.entity
  ? { mode: 'entity', entityType: props.entity.entityType, entityId: props.entity.entityId, entityVersion: props.entity.entityVersion }
  : { mode: 'general' })
const pendingProposal = computed(() => [...(conversation.value?.turns ?? [])].reverse().find(turn => turn.proposalId && turn.changes?.length && (turn.proposalStatus === 'Pending' || !turn.proposalStatus)))

function resizeInput() {
  const el = inputElement.value
  if (!el) return
  el.style.height = 'auto'
  el.style.height = `${Math.min(el.scrollHeight, 112)}px`
  el.classList.toggle('is-overflowing', el.scrollHeight > 112)
}

function formatHistoryDate(iso: string) {
  const date = new Date(iso)
  if (Number.isNaN(date.getTime())) return ''
  const startOfDay = (d: Date) => new Date(d.getFullYear(), d.getMonth(), d.getDate()).getTime()
  const diffDays = Math.round((startOfDay(new Date()) - startOfDay(date)) / 86400000)
  if (diffDays <= 0) return 'Сегодня'
  if (diffDays === 1) return 'Вчера'
  return date.toLocaleDateString('ru-RU', { day: 'numeric', month: 'long' })
}

function onDocumentKeydown(event: KeyboardEvent) {
  if (event.key === 'Escape' && showHistory.value) showHistory.value = false
}

async function loadConversation() {
  await closePendingProposalWhenLeaving()
  conflictPreview.value = null
  loading.value = true
  error.value = ''
  try {
    if (props.conversationId) conversation.value = await chatApi.get(props.conversationId, props.targetTurnId)
    else conversation.value = await chatApi.create()
    scopeMode.value = props.entity ? 'entity' : 'general'
    await scrollToTargetOrLatest(props.targetTurnId)
  } catch (cause) {
    error.value = cause instanceof Error ? cause.message : 'Не удалось открыть чат.'
  } finally {
    loading.value = false
  }
}

async function refreshHistory() {
  try {
    const page = await chatApi.list()
    history.value = page.conversations
    historyCursor.value = page.nextCursor
  }
  catch (cause) { error.value = cause instanceof Error ? cause.message : 'Не удалось загрузить историю.' }
}

async function loadMoreHistory() {
  if (!historyCursor.value || loadingHistory.value) return
  loadingHistory.value = true
  try {
    const page = await chatApi.list(historyCursor.value)
    history.value.push(...page.conversations)
    historyCursor.value = page.nextCursor
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Не удалось загрузить историю.' }
  finally { loadingHistory.value = false }
}

async function openHistory() {
  await refreshHistory()
  showHistory.value = true
}

async function selectConversation(id: string) {
  await closePendingProposalWhenLeaving()
  conflictPreview.value = null
  showHistory.value = false
  loading.value = true
  try { conversation.value = await chatApi.get(id); await scrollToLatest() }
  catch (cause) { error.value = cause instanceof Error ? cause.message : 'Не удалось открыть диалог.' }
  finally { loading.value = false }
}

async function newConversation() {
  await closePendingProposalWhenLeaving()
  const previous = conversation.value?.id
  conflictPreview.value = null
  showHistory.value = false
  loading.value = true
  try {
    if (previous) void chatApi.delete(previous).catch(() => undefined)
    conversation.value = await chatApi.create()
    scopeMode.value = props.entity ? 'entity' : 'general'
    input.value = ''
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Не удалось создать диалог.' }
  finally { loading.value = false }
}

async function closePendingProposalWhenLeaving() {
  const proposalId = pendingProposal.value?.proposalId
  const conversationId = conversation.value?.id
  if (!proposalId || !conversationId) return
  try { await chatApi.dismissProposal(conversationId, proposalId) }
  catch { /* server-side expiry and new-turn dismissal remain authoritative */ }
}

async function setScopeMode(mode: 'entity' | 'general') {
  if (scopeMode.value === mode) return
  await closePendingProposalWhenLeaving()
  scopeMode.value = mode
  if (conversation.value) conversation.value = await chatApi.get(conversation.value.id)
}

async function send() {
  const message = input.value.trim()
  if (!message || sending.value || !conversation.value) return
  sending.value = true
  error.value = ''
  conflictPreview.value = null
  input.value = ''
  try {
    const result = await chatApi.send(conversation.value.id, message, scope.value)
    conversation.value = await chatApi.get(conversation.value.id, result.turn.id)
    // The choice applies to this request; keep it selected for the next turn too.
    await refreshHistory()
    await scrollToLatest()
  } catch (cause) {
    input.value = message
    error.value = cause instanceof Error ? cause.message : 'Не удалось отправить сообщение.'
  } finally { sending.value = false }
}

async function loadOlderTurns() {
  const current = conversation.value
  if (!current?.nextCursor || loadingOlder.value) return
  const element = scrollContainer.value
  const previousHeight = element?.scrollHeight ?? 0
  const previousTop = element?.scrollTop ?? 0
  loadingOlder.value = true
  try {
    const page = await chatApi.olderTurns(current.id, current.nextCursor)
    conversation.value = {
      ...current,
      turns: [...page.turns, ...current.turns],
      nextCursor: page.nextCursor,
    }
    await nextTick()
    if (element) element.scrollTop = previousTop + element.scrollHeight - previousHeight
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Не удалось загрузить ранние сообщения.' }
  finally { loadingOlder.value = false }
}

async function confirmProposal(turn: ChatTurn) {
  if (!conversation.value || !turn.proposalId || pendingProposal.value?.proposalId !== turn.proposalId) return
  sending.value = true
  error.value = ''
  conflictPreview.value = null
  try {
    await chatApi.confirm(conversation.value.id, turn.proposalId)
    conversation.value = await chatApi.get(conversation.value.id)
    await scrollToLatest()
  } catch (cause) {
    if (cause instanceof ProposalConflictError) conflictPreview.value = cause.conflict
    error.value = cause instanceof Error ? cause.message : 'Не удалось применить предложение.'
    try { conversation.value = await chatApi.get(conversation.value.id) } catch { /* keep the conflict details visible */ }
  }
  finally { sending.value = false }
}

async function dismissProposal(turn: ChatTurn) {
  if (!conversation.value || !turn.proposalId || pendingProposal.value?.proposalId !== turn.proposalId) return
  try {
    await chatApi.dismissProposal(conversation.value.id, turn.proposalId)
    conversation.value = await chatApi.get(conversation.value.id)
  } catch (cause) { error.value = cause instanceof Error ? cause.message : 'Не удалось отклонить предложение.' }
}

function formatScope(turn: ChatTurn) {
  if (turn.scope.mode === 'general') return 'Общий чат'
  if (!props.entity || turn.scope.entityId !== props.entity.entityId) return `${turn.scope.entityType} · ${turn.scope.entityId}`
  return props.entity.label
}

function routeLabel(turn: ChatTurn) {
  return 'Gemma'
}

function operationLabel(operation: string) {
  return ({
    Create: 'Создать', Update: 'Изменить', Move: 'Переместить', Archive: 'Архивировать',
    Restore: 'Восстановить', Delete: 'Удалить', SetWorkStatus: 'Изменить статус задачи',
    SetFeatureStatus: 'Изменить статус функции', Reorder: 'Изменить порядок',
  } as Record<string, string>)[operation] ?? operation
}

async function scrollToLatest() {
  await nextTick()
  if (scrollContainer.value) scrollContainer.value.scrollTop = scrollContainer.value.scrollHeight
}

async function scrollToTargetOrLatest(turnId?: string) {
  await nextTick()
  const target = turnId ? document.getElementById(`turn-${turnId}`) : null
  if (target) target.scrollIntoView({ block: 'center' })
  else await scrollToLatest()
}

function onInputKeydown(event: KeyboardEvent) {
  if (event.key === 'Enter' && !event.shiftKey) { event.preventDefault(); void send() }
}

watch(() => [props.conversationId, props.entity?.entityId, props.targetTurnId], () => { void loadConversation() })
watch(input, () => { void nextTick(resizeInput) })
onMounted(() => {
  void loadConversation()
  document.addEventListener('keydown', onDocumentKeydown)
  void nextTick(resizeInput)
})
onBeforeUnmount(() => {
  document.removeEventListener('keydown', onDocumentKeydown)
  const proposalId = pendingProposal.value?.proposalId
  const conversationId = conversation.value?.id
  if (proposalId && conversationId) void chatApi.dismissProposal(conversationId, proposalId).catch(() => undefined)
  if (conversationId) void chatApi.delete(conversationId).catch(() => undefined)
})
</script>

<template>
  <section class="chat-view" aria-label="Чат с агентом">
    <div class="chat-back-row">
          <button type="button" class="doc-action doc-back" @click="emit('back')">← Назад</button>
          <div class="chat-back-actions">
            <button type="button" class="chat-new-btn" @click="newConversation" aria-label="Новый чат" title="Новый чат">＋</button>
      </div>
    </div>

    <div v-if="entity" class="scope-switch" role="group" aria-label="Контекст сообщений">
      <button type="button" :aria-pressed="scopeMode === 'entity'" @click="setScopeMode('entity')">{{ entity.label }}</button>
      <button type="button" :aria-pressed="scopeMode === 'general'" @click="setScopeMode('general')">Общий контекст</button>
    </div>

    <div ref="scrollContainer" class="chat-scroll" :aria-busy="loading || sending">
      <div class="chat-list" aria-live="polite">
        <button v-if="conversation?.nextCursor" class="load-older" type="button" :disabled="loadingOlder" @click="loadOlderTurns">
          {{ loadingOlder ? 'Загрузка…' : 'Загрузить ранние сообщения' }}
        </button>
        <p v-if="loading && !conversation" class="chat-state">Загрузка чата…</p>
        <template v-for="turn in conversation?.turns ?? []" :key="turn.id">
          <div class="chat-row user">
            <div class="chat-bubble">{{ turn.userMessage }}</div>
          </div>
          <div class="chat-row agent">
            <div :id="`turn-${turn.id}`" class="chat-answer" :class="{ 'target-turn': turn.id === props.targetTurnId }">
              <div class="chat-bubble">{{ turn.assistantMessage }}</div>
              <div class="turn-meta"><span>{{ formatScope(turn) }}</span><span>{{ routeLabel(turn) }}</span></div>
              <div v-if="turn.sourceDetails?.length || turn.sourceReferences?.length" class="source-list">
                <span>Источники:</span>
                <a v-for="source in turn.sourceDetails ?? []" :key="source.url ?? source.title" :href="source.url ?? '#'" :title="source.snippet">{{ source.title }}</a>
                <a v-if="!turn.sourceDetails?.length" v-for="source in turn.sourceReferences" :key="source" :href="source">{{ source }}</a>
              </div>
              <article v-for="change in turn.changes ?? []" :key="change.id" class="proposal-card">
                <div class="proposal-heading">{{ operationLabel(change.operation) }}</div>
                <strong>{{ change.displayName }}</strong>
                <p>{{ change.preview }}</p>
                <details>
                  <summary>Показать значения</summary>
                  <dl><dt>Было</dt><dd><pre>{{ JSON.stringify(change.before ?? null, null, 2) }}</pre></dd>
                    <dt>Изменяемые значения</dt><dd><pre>{{ JSON.stringify(change.after, null, 2) }}</pre></dd></dl>
                </details>
              </article>
              <div v-if="turn.proposalId && turn.changes?.length" class="proposal-actions">
                <template v-if="pendingProposal?.proposalId === turn.proposalId && (turn.proposalStatus === 'Pending' || !turn.proposalStatus)">
                  <button class="confirm-button" type="button" :disabled="sending" @click="confirmProposal(turn)">Да, подтвердить все {{ turn.changes.length }} изменения</button>
                  <button class="proposal-dismiss" type="button" :disabled="sending" @click="dismissProposal(turn)">Отклонить</button>
                </template>
                <span v-else class="proposal-status">{{ turn.proposalStatus === 'Applied' ? 'Изменения применены' : 'Предложение закрыто' }}</span>
              </div>
            </div>
          </div>
        </template>
        <section v-if="conflictPreview" class="conflict-preview" aria-live="assertive">
          <h2>{{ conflictPreview.expired ? 'Срок предложения истёк' : 'Объекты изменились' }}</h2>
          <p>{{ conflictPreview.error }} Новое предложение нужно подготовить заново; эти значения нельзя подтвердить.</p>
          <article v-for="action in conflictPreview.currentValues" :key="action.actionId" class="conflict-action">
            <strong>{{ action.label }}</strong>
            <dl><dt>Текущее значение</dt><dd><pre>{{ JSON.stringify(action.current ?? null, null, 2) }}</pre></dd>
              <dt>Предлагалось</dt><dd><pre>{{ JSON.stringify(action.proposed ?? null, null, 2) }}</pre></dd></dl>
          </article>
        </section>
        <p v-if="sending" class="chat-state">Агент отвечает…</p>
        <p v-if="error" class="chat-error" role="alert">{{ error }}</p>
      </div>
    </div>

    <form class="chat-composer" @submit.prevent="send">
          <textarea ref="inputElement" v-model="input" class="chat-input" rows="1" :disabled="sending || loading" placeholder="Сообщение агенту…" aria-label="Сообщение агенту" @keydown="onInputKeydown" />
          <button type="submit" class="chat-send" :disabled="sending || loading || !input.trim()" aria-label="Отправить">↑</button>
        </form>
      </section>
    </template>
