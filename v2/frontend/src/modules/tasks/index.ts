import type { Component } from 'vue'
import TasksPage from './TasksPage.vue'
import './tasks.css'

export const section = 'tasks' as const
export const component: Component = TasksPage
