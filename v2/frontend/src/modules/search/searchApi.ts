export type SearchCoverageMode = 'relevant' | 'exhaustive'
export type SearchChatContext = {
  conversationId: string
  turnId: string
  mode: string
  entityType?: string | null
  entityId?: string | null
  entityVersion?: number | null
}
export type SearchChatFilter = Partial<Omit<SearchChatContext, 'turnId'>>

export type SearchRequest = {
  query: string
  mode: SearchCoverageMode
  kinds?: string[]
  context?: SearchChatFilter
  updatedAfterUtc?: string
  updatedBeforeUtc?: string
  cursor?: string
  pageSize?: number
}

export type SearchSourceReference = {
  kind: string
  id: string
  version: number
  url?: string | null
  title: string
  path?: string | null
  snippet: string
  updatedAtUtc: string
  isChatHistory: boolean
  chatContext?: SearchChatContext | null
}

export type SearchMatchKind = 'lexical' | 'semantic'
export type SearchHit = { source: SearchSourceReference; score: number; matchKind: SearchMatchKind }
export type SearchResponse = {
  hits: SearchHit[]
  nextCursor?: string | null
  isComplete: boolean
  coverageNote?: string | null
}

export async function search(request: SearchRequest, signal?: AbortSignal): Promise<SearchResponse> {
  const response = await fetch('/api/v2/search', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(request),
    signal,
  })
  if (!response.ok) {
    const body = await response.text()
    throw new Error(body || `Search failed (${response.status})`)
  }
  return response.json() as Promise<SearchResponse>
}
