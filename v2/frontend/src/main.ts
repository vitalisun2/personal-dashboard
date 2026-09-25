import { createApp } from 'vue'
import { registerSW } from 'virtual:pwa-register'
import App from './app/App.vue'
import { router } from './app/router'
import './shared/styles.css'

registerSW({ immediate: true })

createApp(App).use(router).mount('#app')
