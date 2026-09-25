import { useRouter } from 'vue-router'
import { chatRoute } from '../../shared/chatRoute'

export function useChatNavigation() {
  const router = useRouter()
  return (focus?: { entityType: string; entityId: string; entityVersion: number }) => router.push(chatRoute(focus))
}
