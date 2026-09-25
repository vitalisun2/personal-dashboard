import { createApp } from 'vue'
import { registerSW } from 'virtual:pwa-register'
import App from './app/App.vue'
import { router } from './app/router'
import { startSyncLifecycle } from './offline/runtime'
import './shared/styles.css'

registerSW({ immediate: true })
startSyncLifecycle()

createApp(App).use(router).mount('#app')
