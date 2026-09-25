import type { RouteLocationRaw } from 'vue-router'

export function chatRoute(focus?: {
  entityType: string
  entityId: string
  entityVersion: number
}): RouteLocationRaw {
  return {
    path: '/chat',
    query: focus
      ? {
          mode: 'entity',
          entityType: focus.entityType,
          entityId: focus.entityId,
          entityVersion: String(focus.entityVersion),
        }
      : { mode: 'general' },
  }
}
