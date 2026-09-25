<script setup lang="ts">
import { computed } from 'vue'
import { RouterLink, RouterView, useRoute } from 'vue-router'

const route = useRoute()
const navigation = [
  { path: '/knowledge', label: 'База знаний', icon: '▤' },
  { path: '/planning', label: 'Планирование', icon: '⌁' },
  { path: '/tasks', label: 'Задачи', icon: '✓' },
]
const currentSection = computed(() => String(route.path.split('/')[1] || 'knowledge'))
</script>

<template>
  <div class="app-frame">
    <aside class="sidebar">
      <RouterLink class="brand" to="/knowledge" aria-label="Personal OS, главная">
        <span class="brand-mark">P</span>
        <span>Personal OS</span>
      </RouterLink>
      <nav class="primary-nav" aria-label="Основные разделы">
        <RouterLink
          v-for="item in navigation"
          :key="item.path"
          :to="item.path"
          class="nav-link"
          :class="{ selected: currentSection === item.path.slice(1) }"
        >
          <span class="nav-icon" aria-hidden="true">{{ item.icon }}</span>
          <span>{{ item.label }}</span>
        </RouterLink>
      </nav>
      <div class="sidebar-bottom">
        <span class="sync-indicator"><span></span>Локальные данные</span>
      </div>
    </aside>

    <main class="main-column">
      <header class="topbar">
        <div class="topbar-title">{{ currentSection === 'chat' ? 'Агент' : 'Ваше пространство' }}</div>
        <RouterLink class="chat-link" to="/chat" :aria-current="currentSection === 'chat' ? 'page' : undefined">
          <span aria-hidden="true">◌</span>
          <span>Чат</span>
        </RouterLink>
      </header>
      <div class="page-content">
        <RouterView />
      </div>
    </main>

    <nav class="mobile-nav" aria-label="Основные разделы">
      <RouterLink
        v-for="item in navigation"
        :key="item.path"
        :to="item.path"
        class="mobile-nav-link"
        :class="{ selected: currentSection === item.path.slice(1) }"
      >
        <span aria-hidden="true">{{ item.icon }}</span>
        <span>{{ item.label }}</span>
      </RouterLink>
    </nav>
  </div>
</template>
