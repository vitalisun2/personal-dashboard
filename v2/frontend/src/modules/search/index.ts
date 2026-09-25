import type { Component } from 'vue'
import SearchPage from './SearchPage.vue'

export const section = 'search' as const
export const component: Component = SearchPage

export { search } from './searchApi'
export type {
  SearchChatContext,
  SearchChatFilter,
  SearchCoverageMode,
  SearchHit,
  SearchMatchKind,
  SearchRequest,
  SearchResponse,
  SearchSourceReference,
} from './searchApi'
