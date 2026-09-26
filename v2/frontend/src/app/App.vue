<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'
import { subscribeSyncStatus, type SyncStatus } from '../offline/runtime'

const route = useRoute()
const navigation = [
  { path: '/knowledge', label: 'База знаний', icon: '▤' },
  { path: '/planning', label: 'Планирование', icon: '▦' },
  { path: '/tasks', label: 'Задачи', icon: '☑' },
  { path: '/testing', label: 'Тестирование', icon: '◈' },
]
const currentSection = computed(() => String(route.path.split('/')[1] || 'knowledge'))
const titles: Record<string, string> = { knowledge: 'База знаний', planning: 'Планирование', tasks: 'Задачи', chat: 'Агент', search: 'Поиск', testing: 'Тестирование' }
const title = computed(() => titles[currentSection.value] || 'База знаний')
const hideBottomNav = computed(() => currentSection.value === 'chat')
const syncStatus = ref<SyncStatus>('ready')
let unsubscribeSyncStatus: (() => void) | undefined
const appFrame = ref<HTMLElement | null>(null)
let viewport: VisualViewport | null = null

function syncVisibleViewport() {
  const frame = appFrame.value
  if (!frame) return
  frame.style.setProperty('--visible-viewport-height', `${Math.max(0, viewport?.height ?? window.innerHeight)}px`)
  frame.style.setProperty('--visible-viewport-offset-top', `${Math.max(0, viewport?.offsetTop ?? 0)}px`)
}

onMounted(() => {
  unsubscribeSyncStatus = subscribeSyncStatus(next => { syncStatus.value = next })
  document.documentElement.classList.add('keyboard-viewport-lock')
  viewport = window.visualViewport ?? null
  syncVisibleViewport()
  viewport?.addEventListener('resize', syncVisibleViewport)
  viewport?.addEventListener('scroll', syncVisibleViewport)
  window.addEventListener('resize', syncVisibleViewport)
})
onUnmounted(() => {
  unsubscribeSyncStatus?.()
  viewport?.removeEventListener('resize', syncVisibleViewport)
  viewport?.removeEventListener('scroll', syncVisibleViewport)
  window.removeEventListener('resize', syncVisibleViewport)
  document.documentElement.classList.remove('keyboard-viewport-lock')
})
</script>

<template>
  <main ref="appFrame" class="app-frame">
    <div class="phone-shell">
      <div class="app-body">
        <header class="topline">
          <div class="header-copy">
            <div class="eyebrow">Personal OS</div>
            <h1 class="heading">{{ title }}</h1>
          </div>
          <RouterLink class="chat-head-btn" to="/chat" aria-label="Открыть чат с агентом" title="Чат с агентом">
            <svg viewBox="0 0 24 24" aria-hidden="true"><path d="M7 17.2 4 20v-4.9A7.4 7.4 0 0 1 3 11.4C3 7.3 6.8 4 11.5 4S20 7.3 20 11.4s-3.8 7.4-8.5 7.4c-1.6 0-3.1-.4-4.3-1.1Z"/><path d="m16.9 2.7.45 1.15 1.15.45-1.15.45-.45 1.15-.45-1.15-1.15-.45 1.15-.45.45-1.15Z"/></svg>
          </RouterLink>
          <span class="sync-status" :data-status="syncStatus" aria-live="polite">{{ syncStatus }}</span>
        </header>
        <div class="page-content"><RouterView /></div>
      </div>
      <nav v-if="!hideBottomNav" class="bottom-window" aria-label="Навигация">
        <RouterLink v-for="item in navigation" :key="item.path" :to="item.path" class="bottom-item" :class="{ active: currentSection === item.path.slice(1) }">
          <span class="nav-icon" aria-hidden="true">{{ item.icon }}</span><span>{{ item.label }}</span>
        </RouterLink>
      </nav>
    </div>
  </main>
</template>
