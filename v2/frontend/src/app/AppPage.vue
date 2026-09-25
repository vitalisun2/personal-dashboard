<script setup lang="ts">
import { computed } from 'vue'
import type { Component } from 'vue'

type Section = 'knowledge' | 'planning' | 'tasks' | 'chat' | 'search' | 'testing'
type ModulePage = { section: Section; component: Component }

const props = defineProps<{ section: Section }>()
const modulePages = Object.values(
  import.meta.glob('../modules/*/index.ts', { eager: true }),
) as ModulePage[]
const registeredPage = computed(() => modulePages.find((page) => page.section === props.section)?.component)
const pages = {
  knowledge: { title: 'База знаний', description: 'Ваши разделы и документы будут собраны здесь.' },
  planning: { title: 'Планирование', description: 'Проекты, вехи и фичи появятся здесь.' },
  tasks: { title: 'Задачи', description: 'Backlog и задачи на сегодня появятся здесь.' },
  chat: { title: 'Агент', description: 'Здесь будет единый чат с доступом к вашим данным.' },
  search: { title: 'Поиск', description: 'Единый поиск по базе знаний, планам и задачам появится здесь.' },
  testing: { title: 'Тестирование', description: '' },
}
const page = computed(() => pages[props.section])
</script>

<template>
  <section class="section-page" :aria-labelledby="`page-${section}`">
    <component :is="registeredPage" v-if="registeredPage" />
    <div v-else-if="section === 'testing'" class="placeholder">Раздел для тестирования — пока не реализован.</div>
    <template v-else>
      <p class="eyebrow">Personal OS</p>
      <h1 :id="`page-${section}`">{{ page.title }}</h1>
      <div class="empty-card">
        <div class="empty-mark" aria-hidden="true">{{ section === 'chat' ? '◌' : '✦' }}</div>
        <p>{{ page.description }}</p>
      </div>
    </template>
  </section>
</template>
