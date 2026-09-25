<script setup lang="ts">
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { search, type SearchHit, type SearchRequest } from './searchApi'

const props = defineProps<{
  context?: SearchRequest['context']
  initialQuery?: string
}>()

const query = ref(props.initialQuery ?? '')
const exhaustive = ref(false)
const selectedKinds = ref<string[]>([])
const hits = ref<SearchHit[]>([])
const resultGroups = computed(() => [
  { key: 'lexical', label: 'Точные совпадения', hits: hits.value.filter(hit => hit.matchKind === 'lexical') },
  { key: 'semantic', label: 'По смыслу', hits: hits.value.filter(hit => hit.matchKind === 'semantic') },
].filter(group => group.hits.length))
const cursor = ref<string | null>(null)
const coverageNote = ref<string | null>(null)
const isComplete = ref(true)
const loading = ref(false)
const error = ref<string | null>(null)
let activeController: AbortController | undefined
let debounceTimer: ReturnType<typeof setTimeout> | undefined
let requestSequence = 0

const kindOptions = [
  ['knowledge.document', 'База знаний'],
  ['planning.project', 'Проекты'],
  ['planning.milestone', 'Вехи'],
  ['planning.feature', 'Фичи'],
  ['tasks.task', 'Задачи'],
  ['chat.turn', 'История чата'],
] as const

const mode = computed(() => exhaustive.value ? 'exhaustive' : 'relevant')

function requestFor(nextCursor?: string): SearchRequest {
  return {
    query: query.value.trim(),
    mode: mode.value,
    kinds: selectedKinds.value.length ? [...selectedKinds.value] : undefined,
    context: props.context,
    cursor: nextCursor,
    pageSize: 30,
  }
}

async function runSearch(append = false, nextCursor?: string) {
  const trimmed = query.value.trim()
  activeController?.abort()
  if (!trimmed) {
    hits.value = []
    cursor.value = null
    coverageNote.value = null
    isComplete.value = true
    error.value = null
    loading.value = false
    return
  }

  const controller = new AbortController()
  activeController = controller
  const sequence = ++requestSequence
  loading.value = true
  error.value = null
  if (!append) {
    hits.value = []
    cursor.value = null
  }

  try {
    const response = await search(requestFor(nextCursor), controller.signal)
    if (sequence !== requestSequence) return
    hits.value = append ? [...hits.value, ...response.hits] : response.hits
    cursor.value = response.nextCursor ?? null
    coverageNote.value = response.coverageNote ?? null
    isComplete.value = response.isComplete
  } catch (cause) {
    if (controller.signal.aborted || sequence !== requestSequence) return
    error.value = cause instanceof Error ? cause.message : 'Не удалось выполнить поиск.'
  } finally {
    if (sequence === requestSequence) loading.value = false
  }
}

function scheduleSearch() {
  if (debounceTimer) clearTimeout(debounceTimer)
  debounceTimer = setTimeout(() => void runSearch(), 250)
}

function toggleKind(kind: string) {
  selectedKinds.value = selectedKinds.value.includes(kind)
    ? selectedKinds.value.filter(value => value !== kind)
    : [...selectedKinds.value, kind]
}

function sourceLabel(kind: string) {
  return kindOptions.find(([value]) => value === kind)?.[1] ?? kind
}

watch([query, exhaustive, selectedKinds], scheduleSearch, { deep: true, immediate: true })
watch(() => props.initialQuery, value => { if (value !== undefined) query.value = value })
onBeforeUnmount(() => {
  if (debounceTimer) clearTimeout(debounceTimer)
  activeController?.abort()
})
</script>

<template>
  <main class="search-page">
    <header class="search-header">
      <h1>Поиск</h1>
      <label class="search-input">
        <span aria-hidden="true">⌕</span>
        <input v-model="query" type="search" autocomplete="off" placeholder="Поиск по знаниям, плану, задачам и чату…" autofocus />
        <button v-if="query" type="button" aria-label="Очистить поиск" @click="query = ''">×</button>
      </label>
      <div class="search-controls">
        <label v-for="[kind, label] in kindOptions" :key="kind" class="filter-chip">
          <input type="checkbox" :checked="selectedKinds.includes(kind)" @change="toggleKind(kind)" />
          {{ label }}
        </label>
        <label class="exhaustive-toggle">
          <input v-model="exhaustive" type="checkbox" />
          Найти всё
        </label>
      </div>
    </header>

    <p v-if="coverageNote" class="coverage-note" role="status">{{ coverageNote }}</p>
    <p v-if="error" class="search-error" role="alert">{{ error }}</p>
    <p v-else-if="loading && !hits.length" class="search-status" role="status">Ищем…</p>
    <p v-else-if="query.trim() && !hits.length" class="search-status">Ничего не найдено.</p>

    <section v-if="hits.length" class="result-groups" aria-label="Результаты поиска">
      <div v-for="group in resultGroups" :key="group.key" class="result-group">
        <h2>{{ group.label }}</h2>
        <ol class="search-results">
          <li v-for="hit in group.hits" :key="`${hit.source.kind}:${hit.source.id}:${hit.source.version}`">
            <a v-if="hit.source.url" class="result-title" :href="hit.source.url">
              <span>{{ hit.source.title || sourceLabel(hit.source.kind) }}</span>
              <span class="result-kind">{{ sourceLabel(hit.source.kind) }}</span>
            </a>
            <div v-else class="result-title result-title-text">
              <span>{{ hit.source.title || sourceLabel(hit.source.kind) }}</span>
              <span class="result-kind">{{ sourceLabel(hit.source.kind) }}</span>
            </div>
            <p class="result-snippet">{{ hit.source.snippet }}</p>
            <p class="result-path">{{ hit.source.path || (hit.source.isChatHistory ? 'История чата' : '') }}</p>
          </li>
        </ol>
      </div>
    </section>
    <button v-if="cursor" class="load-more" type="button" :disabled="loading" @click="runSearch(true, cursor ?? undefined)">
      {{ loading ? 'Загружаем…' : 'Показать ещё' }}
    </button>
    <p v-else-if="hits.length && !isComplete && exhaustive" class="coverage-note" role="status">
      Поиск пока не подтвердил полный охват источников.
    </p>
  </main>
</template>

<style scoped>
.search-page { width: min(100%, 900px); margin: 0 auto; color: #e5edf5; }
.search-header { display: grid; gap: 16px; }
h1 { margin: 0; font-size: 30px; }
.search-input { display: flex; align-items: center; gap: 10px; min-height: 48px; padding: 0 14px; border: 1px solid #293744; border-radius: 12px; background: #18242f; }
.search-input input { flex: 1; min-width: 0; border: 0; outline: 0; background: transparent; color: inherit; font: inherit; }
.search-input input::placeholder { color: #91a0af; }
.search-input button { border: 0; background: none; color: #aab7c4; font-size: 22px; cursor: pointer; }
.search-controls { display: flex; flex-wrap: wrap; gap: 8px; align-items: center; }
.filter-chip, .exhaustive-toggle { display: inline-flex; align-items: center; gap: 6px; padding: 6px 10px; border: 1px solid #293744; border-radius: 999px; background: #18242f; color: #c5d0dc; font-size: 13px; }
.exhaustive-toggle { margin-left: auto; border-color: #42617f; color: #b9d6ff; }
.filter-chip input, .exhaustive-toggle input { accent-color: #83aee5; }
.search-results { display: grid; gap: 14px; margin: 22px 0; padding: 0; list-style: none; }
.result-groups { display: grid; gap: 8px; }
.result-group h2 { margin: 20px 0 0; color: #c5d0dc; font-size: 16px; font-weight: 650; }
.search-results li { padding: 16px; border: 1px solid #293744; border-radius: 12px; background: #18242f; }
.result-title { display: flex; justify-content: space-between; gap: 12px; color: #e5edf5; font-weight: 650; text-decoration: none; }
.result-title:hover { color: #b9d6ff; }
.result-kind, .result-path { color: #91a0af; font-size: 12px; }
.result-snippet { margin: 9px 0 5px; line-height: 1.5; }
.result-path { margin: 0; }
.coverage-note, .search-status { color: #aab7c4; }
.search-error { color: #f09390; }
.load-more { display: block; margin: 0 auto 24px; padding: 10px 18px; border: 1px solid #42617f; border-radius: 9px; background: #223142; color: #b9d6ff; cursor: pointer; }
.load-more:disabled { opacity: .6; cursor: wait; }
@media (max-width: 560px) { .search-page { padding: 16px; } .exhaustive-toggle { margin-left: 0; } .result-title { align-items: flex-start; flex-direction: column; } }
</style>
