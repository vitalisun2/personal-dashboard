<script setup lang="ts">
import { computed } from 'vue'
import { useRoute, useRouter } from 'vue-router'
import ChatView from './ChatView.vue'

const route = useRoute()
const router = useRouter()
const conversationId = computed(() => typeof route.query.conversationId === 'string' ? route.query.conversationId : undefined)
const turnId = computed(() => typeof route.query.turnId === 'string' ? route.query.turnId : undefined)
const entity = computed(() => {
  if (route.query.mode !== 'entity' || typeof route.query.entityType !== 'string' || typeof route.query.entityId !== 'string') return undefined
  const version = Number(route.query.entityVersion)
  if (!Number.isInteger(version) || version < 1) return undefined
  const label = route.query.entityType === 'knowledge.document' ? 'Этот документ'
    : route.query.entityType === 'tasks.task' ? 'Эта задача'
      : `Этот объект: ${route.query.entityType}`
  return { entityType: route.query.entityType, entityId: route.query.entityId, entityVersion: version, label }
})

function back() {
  if (window.history.length > 1) router.back()
  else void router.push('/knowledge')
}
</script>

<template>
  <ChatView :conversation-id="conversationId" :target-turn-id="turnId" :entity="entity" @back="back" />
</template>
