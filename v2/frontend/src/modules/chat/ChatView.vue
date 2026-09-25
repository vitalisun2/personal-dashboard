<script setup lang="ts">
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { chatApi, ProposalConflictError, type ChatConversation, type ChatEntityEntry, type ChatModel, type ChatScope, type ChatTurn, type ProposalConflictPayload } from './chatApi'

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
const selectedModel = ref<ChatModel>('Gemma')
const scopeMode = ref<'entity' | 'general'>(props.entity ? 'entity' : 'general')
const scrollContainer = ref<HTMLElement | null>(null)

const scope = computed<ChatScope>(() => scopeMode.value === 'entity' && props.entity
  ? { mode: 'entity', entityType: props.entity.entityType, entityId: props.entity.entityId, entityVersion: props.entity.entityVersion }
  : { mode: 'general' })
const pendingProposal = computed(() => [...(conversation.value?.turns ?? [])].reverse().find(turn => turn.proposalId && turn.changes?.length && (turn.proposalStatus === 'Pending' || !turn.proposalStatus)))

async function loadConversation() {
  await closePendingProposalWhenLeaving()
  conflictPreview.value = null
  loading.value = true
  error.value = ''
  try {
    if (props.conversationId) conversation.value = await chatApi.get(props.conversationId, props.targetTurnId)
    else conversation.value = await chatApi.create()
    selectedModel.value = 'Gemma'
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
  conflictPreview.value = null
  showHistory.value = false
  loading.value = true
  try {
    conversation.value = await chatApi.create()
    scopeMode.value = props.entity ? 'entity' : 'general'
    selectedModel.value = 'Gemma'
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
    const result = await chatApi.send(conversation.value.id, message, scope.value, selectedModel.value)
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
  return turn.requestedModel === turn.actualModel
    ? turn.modelRoute === 'ManualSelection' ? `${turn.requestedModel} · выбран вручную` : turn.actualModel
    : `${turn.requestedModel} → ${turn.actualModel} · автоматический переход`
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
onMounted(() => { void loadConversation() })
onBeforeUnmount(() => {
  const proposalId = pendingProposal.value?.proposalId
  const conversationId = conversation.value?.id
  if (proposalId && conversationId) void chatApi.dismissProposal(conversationId, proposalId).catch(() => undefined)
})
</script>

<template>
  <section class="chat-view" aria-label="Чат с агентом">
    <header class="chat-header">
      <button class="quiet-button" type="button" aria-label="Назад" @click="emit('back')">← Назад</button>
      <div class="chat-heading">
        <h1>Агент</h1>
        <div v-if="entity" class="scope-switch" role="group" aria-label="Контекст сообщений">
          <button type="button" :aria-pressed="scopeMode === 'entity'" @click="setScopeMode('entity')">{{ entity.label }}</button>
          <button type="button" :aria-pressed="scopeMode === 'general'" @click="setScopeMode('general')">Общий чат</button>
        </div>
      </div>
      <div class="chat-header-actions">
        <label class="model-select-label">
          <span>Модель</span>
          <select v-model="selectedModel" aria-label="Модель следующего ответа">
            <option value="Gemma">Gemma</option>
            <option value="DeepSeek">DeepSeek</option>
          </select>
        </label>
        <button class="quiet-button" type="button" @click="openHistory">История</button>
      </div>
    </header>

    <div ref="scrollContainer" class="chat-scroll" aria-live="polite" :aria-busy="loading || sending">
      <button v-if="conversation?.nextCursor" class="load-older" type="button" :disabled="loadingOlder" @click="loadOlderTurns">
        {{ loadingOlder ? 'Загрузка…' : 'Загрузить ранние сообщения' }}
      </button>
      <p v-if="loading && !conversation" class="chat-state">Загрузка чата…</p>
      <template v-for="turn in conversation?.turns ?? []" :key="turn.id">
        <div :id="`turn-${turn.id}`" class="turn-meta" :class="{ 'target-turn': turn.id === props.targetTurnId }"><span>{{ formatScope(turn) }}</span><span>{{ routeLabel(turn) }}</span></div>
        <article class="message-row user"><div class="message-bubble">{{ turn.userMessage }}</div></article>
        <article class="message-row assistant">
          <div class="message-bubble">{{ turn.assistantMessage }}</div>
        </article>
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
        <div v-if="turn.sourceReferences?.length" class="source-list">
          <span>Источники:</span><a v-for="source in turn.sourceReferences" :key="source" :href="source">{{ source }}</a>
        </div>
        <div v-if="turn.fallbackReason" class="route-note">Автоматический переход: {{ turn.fallbackReason }}</div>
        <div v-if="turn.proposalId && turn.changes?.length" class="proposal-actions">
          <template v-if="pendingProposal?.proposalId === turn.proposalId && (turn.proposalStatus === 'Pending' || !turn.proposalStatus)">
            <button class="confirm-button" type="button" :disabled="sending" @click="confirmProposal(turn)">Да, подтвердить все {{ turn.changes.length }} изменения</button>
            <button class="quiet-button" type="button" :disabled="sending" @click="dismissProposal(turn)">Отклонить</button>
          </template>
          <span v-else class="proposal-status">{{ turn.proposalStatus === 'Applied' ? 'Изменения применены' : 'Предложение закрыто' }}</span>
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

    <form class="chat-composer" @submit.prevent="send">
      <textarea v-model="input" rows="1" :disabled="sending || loading" placeholder="Сообщение агенту…" aria-label="Сообщение агенту" @keydown="onInputKeydown" />
      <button type="submit" :disabled="sending || loading || !input.trim()" aria-label="Отправить">↑</button>
    </form>

    <div v-if="showHistory" class="history-overlay" @click.self="showHistory = false">
      <section class="history-panel" aria-label="История чатов">
        <header><h2>История</h2><button class="quiet-button" type="button" @click="newConversation">Новый чат</button><button class="close-button" type="button" aria-label="Закрыть" @click="showHistory = false">×</button></header>
        <button v-for="item in history" :key="item.id" type="button" class="history-item" :aria-current="item.id === conversation?.id" @click="selectConversation(item.id)">
          <strong>{{ item.title }}</strong><time>{{ new Date(item.updatedAt).toLocaleString() }}</time>
        </button>
        <button v-if="historyCursor" class="load-older" type="button" :disabled="loadingHistory" @click="loadMoreHistory">
          {{ loadingHistory ? 'Загрузка…' : 'Более ранние чаты' }}
        </button>
        <p v-if="!history.length" class="chat-state">Пока нет сохранённых диалогов.</p>
      </section>
    </div>
  </section>
</template>

<style scoped>
.chat-view{display:flex;flex-direction:column;min-height:560px;height:calc(100dvh - 175px);margin:-43px -42px;color:#dce6f0;background:#0d141b}.chat-header{display:flex;align-items:center;gap:14px;padding:12px 16px;border-bottom:1px solid #263441;flex:none}.chat-heading{min-width:0;flex:1}.chat-heading h1{margin:0;font-size:18px}.chat-header-actions{display:flex;align-items:center;gap:10px}.model-select-label{display:flex;align-items:center;gap:7px;color:#91a1b1;font-size:12px}.model-select-label select{min-height:38px;padding:0 10px;border:1px solid #344654;border-radius:10px;background:#17232d;color:#f3f6f8;font-weight:650}.scope-switch{display:flex;gap:4px;margin-top:7px}.scope-switch button,.quiet-button,.close-button,.load-older{border:1px solid #2b3c4a;border-radius:10px;background:#17232d;color:#b9c7d4;padding:8px 11px}.scope-switch button[aria-pressed=true]{background:#294158;border-color:#45647d;color:#fff}.quiet-button{white-space:nowrap}.chat-scroll{flex:1;min-height:0;overflow:auto;padding:18px max(16px,calc((100% - 760px)/2)) 16px}.load-older{display:block;margin:0 auto 12px}.turn-meta{display:flex;justify-content:space-between;gap:12px;margin:15px 0 7px;color:#8698a9;font-size:11px;scroll-margin-top:24px}.turn-meta.target-turn{margin-left:-8px;margin-right:-8px;padding:8px;border:1px solid #6d94bd;border-radius:10px;background:#1d3448;color:#e4f2ff;box-shadow:0 0 0 2px #6d94bd22}.message-row{display:flex;margin:8px 0}.message-row.user{justify-content:flex-end}.message-row.assistant{justify-content:flex-start}.message-bubble{max-width:min(82%,700px);padding:11px 14px;border-radius:16px;background:#151f27;line-height:1.48;white-space:pre-wrap;overflow-wrap:anywhere}.user .message-bubble{background:#2b4255;color:#f5f8fa;border-bottom-right-radius:5px}.assistant .message-bubble{border:1px solid #263744;border-bottom-left-radius:5px}.proposal-card{max-width:700px;margin:8px 0 8px 4px;padding:13px;border:1px solid #526a3a;border-radius:14px;background:#172118}.proposal-heading{margin-bottom:6px;color:#b9d99a;font-size:12px;font-weight:700}.proposal-card p{white-space:pre-wrap}.proposal-card details{border-top:1px solid #344433;padding-top:8px}.proposal-card summary{cursor:pointer}.proposal-card dl,.conflict-action dl{display:grid;grid-template-columns:120px 1fr;gap:6px;font-size:12px}.proposal-card pre,.conflict-action pre{white-space:pre-wrap;overflow-wrap:anywhere;margin:0}.proposal-actions{display:flex;gap:8px;margin:9px 0 14px 4px}.confirm-button{border:0;border-radius:10px;background:#547b43;color:white;padding:11px 14px;font-weight:700}.confirm-button:disabled,.quiet-button:disabled{opacity:.5}.proposal-status,.route-note,.chat-state{color:#8797a6;font-size:13px}.chat-error{color:#ffb4a8;background:#351d1b;border-radius:10px;padding:10px}.conflict-preview{max-width:760px;margin:12px 0;padding:14px;border:1px solid #9a6542;border-radius:14px;background:#2b211b}.conflict-preview h2{margin:0 0 8px;font-size:16px}.conflict-action{padding:10px 0;border-top:1px solid #624431}.conflict-action pre{max-height:220px;overflow:auto}.source-list{display:flex;flex-wrap:wrap;gap:8px;margin:6px 0;color:#92aabc;font-size:12px}.source-list a{color:#9fc6f2;text-decoration:underline}.route-note{margin:4px 0 10px}.chat-composer{display:flex;gap:8px;align-items:flex-end;padding:12px 16px;border-top:1px solid #263441;flex:none}.chat-composer textarea{flex:1;min-width:0;min-height:46px;max-height:140px;resize:vertical;padding:12px;border:1px solid #344654;border-radius:13px;background:#17232d;color:white;font:inherit}.chat-composer button{width:46px;height:46px;border:0;border-radius:13px;background:#355987;color:white;font-size:20px}.chat-composer button:disabled{opacity:.45}.history-overlay{position:fixed;inset:0;z-index:50;display:flex;justify-content:flex-end;background:#0008}.history-panel{width:min(390px,100%);padding:14px;background:#101922;border-left:1px solid #2b3c4a;overflow:auto}.history-panel header{display:flex;align-items:center;gap:8px}.history-panel h2{flex:1;font-size:17px}.close-button{font-size:20px}.history-item{display:flex;width:100%;flex-direction:column;gap:4px;text-align:left;padding:12px;border:0;border-radius:11px;background:transparent;color:#edf3f8}.history-item:hover,.history-item[aria-current=true]{background:#1a2935}.history-item time{color:#8797a6;font-size:11px}.turn-meta span:last-child{text-align:right}@media(max-width:760px){.chat-view{height:calc(100dvh - 128px);margin:-30px -19px;min-height:440px}.chat-header{flex-wrap:wrap;gap:9px;padding:10px}.chat-heading{order:1;flex-basis:100%}.chat-header-actions{margin-left:auto}.chat-header>.quiet-button{margin-right:auto}.chat-scroll{padding:12px}.message-bubble{max-width:92%}.turn-meta{font-size:10px}}
</style>
