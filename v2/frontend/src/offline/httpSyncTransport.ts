import type { EntityChangePage, SyncPushRequest, SyncPushResponse, SyncTransport } from "./types";

export class HttpSyncTransport implements SyncTransport {
  private readonly baseUrl: string;

  constructor(baseUrl = "", private readonly fetcher: typeof fetch = fetch) {
    this.baseUrl = baseUrl.replace(/\/$/, "");
  }

  async pushOperations(request: SyncPushRequest) {
    const response = await this.fetcher(`${this.baseUrl}/api/v2/sync/push`, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: JSON.stringify(request),
    });
    await ensureOk(response);
    const result = await response.json() as SyncPushResponse;
    return result.results;
  }

  async pullChanges(after: number, pageSize: number): Promise<EntityChangePage> {
    const query = new URLSearchParams({ after: String(after), pageSize: String(pageSize) });
    const response = await this.fetcher(`${this.baseUrl}/api/v2/sync/changes?${query}`);
    await ensureOk(response);
    return await response.json() as EntityChangePage;
  }
}

async function ensureOk(response: Response): Promise<void> {
  if (!response.ok) {
    throw new Error(`Sync request failed: ${response.status} ${response.statusText}`.trim());
  }
}
