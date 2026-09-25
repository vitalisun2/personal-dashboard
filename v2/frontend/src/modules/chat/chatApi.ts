export type ChatModel = 'Gemma' | 'DeepSeek'
export type ChatScope =
  | { mode: 'general' }
  | { mode: 'entity'; entityType: string; entityId: string; entityVersion: number }

export interface ChatEntityEntry {
  entityType: string
  entityId: string
  entityVersion: number
  label: string
}

export interface ChatMessage {
  id: string
  role: 'User' | 'Assistant' | 'Tool'
  kind: 'Text' | 'ProposalPreview' | 'ProposalStatus' | 'SourceReference'
  content: string
  createdAt: string
  turnId?: string | null
  structuredContent?: unknown
  proposalId?: string | null
}

export interface ChatTurn {
  id: string
  userMessage: string
  assistantMessage: string
  scope: ChatScope
  requestedModel: ChatModel
  actualModel: ChatModel
  fallbackReason?: string | null
  modelRoute?: 'Default' | 'ManualSelection' | 'AutomaticFallback'
  createdAt: string
  sourceReferences: string[]
  sourceDetails?: { title: string; url: string | null; snippet: string }[]
  proposalId?: string | null
  proposalStatus?: 'Pending' | 'Applied' | 'Dismissed' | null
  changes?: ChatProposedChange[]
}

export interface ChatProposedChange {
  id: string
  operation: string
  entityType: string
  entityId: string
  displayName: string
  preview: string
  before?: unknown
  after: unknown
}

export interface ChatConversation {
  id: string
  title: string
  createdAt: string
  updatedAt: string
  messages: ChatMessage[]
  turns: ChatTurn[]
  nextCursor?: string | null
}

export interface ChatHistoryItem {
  id: string
  title: string
  updatedAt: string
}

export interface ChatHistoryPage {
  conversations: ChatHistoryItem[]
  nextCursor: string | null
}

export interface ChatSendResult {
  turn: ChatTurn
}

export interface ProposalConflictAction {
  actionId: string
  entityType: string
  entityId: string
  label: string
  current: unknown
  proposed: unknown
}

export interface ProposalConflictPayload {
  stale: boolean
  expired: boolean
  error: string
  currentValues: ProposalConflictAction[]
}

export class ProposalConflictError extends Error {
  constructor(public readonly conflict: ProposalConflictPayload) {
    super(conflict.error || 'Предложение больше не соответствует текущим данным.')
    this.name = 'ProposalConflictError'
  }
}

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(path, {
    ...init,
    headers: { 'Content-Type': 'application/json', ...init?.headers },
  })
  if (!response.ok) {
    const detail = await response.text()
    if (response.status === 409) {
      try {
        const conflict = JSON.parse(detail) as ProposalConflictPayload
        if (Array.isArray(conflict.currentValues)) throw new ProposalConflictError(conflict)
      } catch (cause) {
        if (cause instanceof ProposalConflictError) throw cause
      }
    }
    let message = detail || `Chat request failed (${response.status})`
    try {
      const parsed = JSON.parse(detail) as { error?: string; message?: string; title?: string }
      message = parsed.error || parsed.message || parsed.title || message
    } catch { /* keep the server's plain-text detail */ }
    throw new Error(message)
  }
  return response.status === 204 ? (undefined as T) : response.json() as Promise<T>
}

export const chatApi = {
  list: (cursor?: string | null) => request<ChatHistoryPage>(`/api/v2/chat/conversations${cursor ? `?cursor=${encodeURIComponent(cursor)}` : ''}`),
  get: (id: string, turnId?: string) => request<ChatConversation>(`/api/v2/chat/conversations/${encodeURIComponent(id)}${turnId ? `?turnId=${encodeURIComponent(turnId)}` : ''}`),
  olderTurns: (id: string, cursor: string) => request<{ turns: ChatTurn[]; nextCursor: string | null }>(`/api/v2/chat/conversations/${encodeURIComponent(id)}/turns?cursor=${encodeURIComponent(cursor)}`),
  create: () => request<ChatConversation>('/api/v2/chat/conversations', { method: 'POST', body: '{}' }),
  delete: (id: string) => request<void>(`/api/v2/chat/conversations/${encodeURIComponent(id)}`, { method: 'DELETE' }),
  send: (id: string, message: string, scope: ChatScope, model: ChatModel) =>
    request<ChatSendResult>(`/api/v2/agent/conversations/${encodeURIComponent(id)}/turns`, {
      method: 'POST', body: JSON.stringify({ message, scope, requestedModel: model }),
    }),
  confirm: (id: string, proposalId: string) =>
    request<void>(`/api/v2/agent/conversations/${encodeURIComponent(id)}/proposals/${encodeURIComponent(proposalId)}/confirm`, { method: 'POST', body: '{}' }),
  dismissProposal: (id: string, proposalId: string) =>
    request<void>(`/api/v2/chat/conversations/${encodeURIComponent(id)}/proposals/${encodeURIComponent(proposalId)}`, { method: 'DELETE' }),
}
