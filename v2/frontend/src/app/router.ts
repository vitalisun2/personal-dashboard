import { createRouter, createWebHistory } from 'vue-router'
import AppPage from './AppPage.vue'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/', redirect: '/knowledge' },
    { path: '/knowledge/:pathMatch(.*)*', component: AppPage, props: { section: 'knowledge' } },
    { path: '/planning/:pathMatch(.*)*', component: AppPage, props: { section: 'planning' } },
    { path: '/tasks/:pathMatch(.*)*', component: AppPage, props: { section: 'tasks' } },
    { path: '/testing', component: AppPage, props: { section: 'testing' } },
    { path: '/chat', component: AppPage, props: { section: 'chat' } },
    { path: '/search', component: AppPage, props: { section: 'search' } },
    { path: '/:pathMatch(.*)*', redirect: '/knowledge' },
  ],
})
