import type { OfflineEntity, OfflineStore, SyncConflict, SyncOperation } from "./types";

const DATABASE_NAME = "personal-dashboard-v2";
const DATABASE_VERSION = 1;

function requestResult<T>(request: IDBRequest<T>): Promise<T> {
  return new Promise((resolve, reject) => {
    request.onsuccess = () => resolve(request.result);
    request.onerror = () => reject(request.error ?? new Error("IndexedDB request failed"));
  });
}

function transactionDone(transaction: IDBTransaction): Promise<void> {
  return new Promise((resolve, reject) => {
    transaction.oncomplete = () => resolve();
    transaction.onabort = () => reject(transaction.error ?? new Error("IndexedDB transaction aborted"));
    transaction.onerror = () => reject(transaction.error ?? new Error("IndexedDB transaction failed"));
  });
}

export async function openOfflineDb(
  name = DATABASE_NAME,
  factory: IDBFactory = indexedDB,
): Promise<IDBDatabase> {
  const request = factory.open(name, DATABASE_VERSION);
  request.onupgradeneeded = () => {
    const database = request.result;
    if (!database.objectStoreNames.contains("entities")) {
      database.createObjectStore("entities", { keyPath: ["type", "id"] });
    }
    if (!database.objectStoreNames.contains("operations")) {
      const operations = database.createObjectStore("operations", { keyPath: "operationId" });
      operations.createIndex("createdAt", "createdAt");
    }
    if (!database.objectStoreNames.contains("conflicts")) {
      database.createObjectStore("conflicts", { keyPath: "operationId" });
    }
    if (!database.objectStoreNames.contains("metadata")) {
      database.createObjectStore("metadata");
    }
  };
  return requestResult(request);
}

export class IndexedDbOfflineStore implements OfflineStore {
  private readonly database: IDBDatabase;

  constructor(database: IDBDatabase) {
    this.database = database;
  }

  async putEntity(entity: OfflineEntity): Promise<void> {
    await this.write("entities", entity);
  }

  async saveEntityAndQueue(entity: OfflineEntity, operation: SyncOperation): Promise<void> {
    if (entity.type !== operation.type || entity.id !== operation.id) {
      throw new Error("Entity and queued operation must refer to the same record");
    }
    const transaction = this.database.transaction(["entities", "operations"], "readwrite");
    transaction.objectStore("entities").put(entity);
    transaction.objectStore("operations").put(operation);
    await transactionDone(transaction);
  }

  async getEntity<T = unknown>(type: string, id: string): Promise<OfflineEntity<T> | undefined> {
    return this.read("entities", [type, id]);
  }

  async listEntities<T = unknown>(type?: string): Promise<OfflineEntity<T>[]> {
    const transaction = this.database.transaction("entities", "readonly");
    const store = transaction.objectStore("entities");
    const entities = await requestResult(store.getAll()) as OfflineEntity<T>[];
    await transactionDone(transaction);
    return type ? entities.filter(entity => entity.type === type) : entities;
  }

  async enqueueOperation(operation: SyncOperation): Promise<void> {
    await this.write("operations", operation);
  }

  async listPendingOperations(): Promise<SyncOperation[]> {
    const transaction = this.database.transaction("operations", "readonly");
    const operations = await requestResult(transaction.objectStore("operations").getAll()) as SyncOperation[];
    await transactionDone(transaction);
    return operations.sort((a, b) => a.createdAt.localeCompare(b.createdAt) || a.operationId.localeCompare(b.operationId));
  }

  async removeOperation(operationId: string): Promise<void> {
    await this.delete("operations", operationId);
  }

  async putConflict(conflict: SyncConflict): Promise<void> {
    await this.write("conflicts", conflict);
  }

  async getChangeCursor(): Promise<number> {
    return (await this.read<number>("metadata", "changeCursor")) ?? 0;
  }

  async setChangeCursor(sequence: number): Promise<void> {
    await this.write("metadata", sequence, "changeCursor");
  }

  async listConflicts(): Promise<SyncConflict[]> {
    const transaction = this.database.transaction("conflicts", "readonly");
    const conflicts = await requestResult(transaction.objectStore("conflicts").getAll()) as SyncConflict[];
    await transactionDone(transaction);
    return conflicts.sort((a, b) => a.detectedAt.localeCompare(b.detectedAt));
  }

  private async read<T>(storeName: string, key: IDBValidKey): Promise<T | undefined> {
    const transaction = this.database.transaction(storeName, "readonly");
    const result = await requestResult(transaction.objectStore(storeName).get(key)) as T | undefined;
    await transactionDone(transaction);
    return result;
  }

  private async write(storeName: string, value: unknown, key?: IDBValidKey): Promise<void> {
    const transaction = this.database.transaction(storeName, "readwrite");
    if (key === undefined) transaction.objectStore(storeName).put(value);
    else transaction.objectStore(storeName).put(value, key);
    await transactionDone(transaction);
  }

  private async delete(storeName: string, key: IDBValidKey): Promise<void> {
    const transaction = this.database.transaction(storeName, "readwrite");
    transaction.objectStore(storeName).delete(key);
    await transactionDone(transaction);
  }
}
