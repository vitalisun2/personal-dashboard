import type { Component } from 'vue'
import ChatPage from './ChatPage.vue'

const component: Component = ChatPage

export { component, ChatPage }
export const section = 'chat' as const
