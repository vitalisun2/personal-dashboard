export interface OfflineEntity<T = unknown> {
  type: string;
  id: string;
  version: number;
  payload: T | null;
  deleted: boolean;
  updatedAt?: string;
}

export type SyncOperationKind = "upsert" | "delete";

/** Stored locally; queue metadata is stripped when constructing the push request. */
export interface SyncOperation<T = unknown> {
  operationId: string;
  type: string;
  id: string;
  expectedVersion: number | null;
  kind: SyncOperationKind;
  payload?: T;
  createdAt: string;
}

export interface SyncConflict<T = unknown> {
  operationId: string;
  type: string;
  id: string;
  expectedVersion: number | null;
  localDeleted: boolean;
  localPayload: T | null;
  serverVersion: number | null;
  serverPayload: T | null;
  serverDeleted: boolean | null;
  conflictReason?: string;
  detectedAt: string;
}

export interface SyncPushResult<T = unknown> {
  operationId: string;
  applied: boolean;
  skipped: boolean;
  current: OfflineEntity<T> | null;
  conflictReason: string | null;
}

export interface SyncPushRequest {
  operations: Array<Omit<SyncOperation, "createdAt">>;
}

export interface SyncPushResponse {
  results: SyncPushResult[];
}

export interface EntityChange<T = unknown> {
  sequence: number;
  snapshot: OfflineEntity<T>;
}

export interface EntityChangePage<T = unknown> {
  changes: EntityChange<T>[];
  nextSequence: number;
  isComplete: boolean;
}

export interface SyncTransport {
  pushOperations(request: SyncPushRequest): Promise<SyncPushResult[]>;
  pullChanges(after: number, pageSize: number): Promise<EntityChangePage>;
}

export interface OfflineStore {
  listEntities(): Promise<OfflineEntity[]>;
  getEntity<T = unknown>(type: string, id: string): Promise<OfflineEntity<T> | undefined>;
  putEntity(entity: OfflineEntity): Promise<void>;
  saveEntityAndQueue(entity: OfflineEntity, operation: SyncOperation): Promise<void>;
  enqueueOperation(operation: SyncOperation): Promise<void>;
  listPendingOperations(): Promise<SyncOperation[]>;
  removeOperation(operationId: string): Promise<void>;
  putConflict(conflict: SyncConflict): Promise<void>;
  getChangeCursor(): Promise<number>;
  setChangeCursor(sequence: number): Promise<void>;
}

export interface SyncSummary {
  applied: number;
  conflicts: number;
  pulled: number;
}
