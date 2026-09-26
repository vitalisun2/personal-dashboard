import assert from 'node:assert/strict';
import test from 'node:test';
import { syncPendingOperations, toSyncPushRequest } from './sync.ts';

test('a version conflict preserves local work and blocks later edits for that entity', async () => {
  const pending = [
    { operationId: 'edit-0', type: 'task', id: 'b', kind: 'upsert', expectedVersion: 1, payload: { title: 'Other task' }, createdAt: '2026-09-25T10:00:00Z' },
    { operationId: 'edit-1', type: 'task', id: 'a', kind: 'upsert', expectedVersion: 2, payload: { title: 'Local' }, createdAt: '2026-09-25T10:01:00Z' },
    { operationId: 'edit-2', type: 'task', id: 'a', kind: 'upsert', expectedVersion: 3, payload: { title: 'Later local edit' }, createdAt: '2026-09-25T10:02:00Z' },
  ];
  assert.deepEqual(toSyncPushRequest([pending[0]]), {
    operations: [{ operationId: 'edit-0', type: 'task', id: 'b', kind: 'upsert', expectedVersion: 1, payload: { title: 'Other task' } }],
  });
  const conflicts = [];
  const entities = [];
  const removed = [];
  const sent = [];
  const pullRequests = [];
  const store = {
    async listEntities() { return entities; },
    async getEntity(type, id) { return entities.find(entity => entity.type === type && entity.id === id); },
    async listPendingOperations() { return [...pending]; },
    async removeOperation(id) { removed.push(id); pending.splice(pending.findIndex(operation => operation.operationId === id), 1); },
    async putEntity(entity) { entities.push(entity); },
    async putConflict(conflict) { conflicts.push(conflict); },
    async getChangeCursor() { return 0; },
    async setChangeCursor(sequence) { this.cursor = sequence; },
    async getSyncEpoch() { return 'epoch-1'; },
    async setSyncEpoch(epoch) { this.epoch = epoch; },
    async clearLocalData() { throw new Error('Unexpected offline reset'); },
  };
  const transport = {
    async getSyncEpoch() { return 'epoch-1'; },
    async pushOperations(request) {
      sent.push(...request.operations.map(operation => operation.operationId));
      return [
        { operationId: 'edit-0', applied: true, skipped: false, current: { type: 'task', id: 'b', version: 2, payload: { title: 'Other task' }, deleted: false }, conflictReason: null },
        { operationId: 'edit-1', applied: false, skipped: false, current: { type: 'task', id: 'a', version: 4, payload: { title: 'Server' }, deleted: false }, conflictReason: 'Version mismatch' },
        { operationId: 'edit-2', applied: false, skipped: true, current: null, conflictReason: 'Earlier operation needs resolution' },
      ];
    },
    async pullChanges(after, pageSize) {
      pullRequests.push({ after, pageSize });
      return {
        changes: [
          { sequence: 2, snapshot: { type: 'task', id: 'a', version: 5, payload: { title: 'Newer server value' }, deleted: false } },
          { sequence: 3, snapshot: { type: 'task', id: 'c', version: 1, payload: { title: 'Remote task' }, deleted: true } },
        ],
        nextSequence: 3,
        isComplete: true,
      };
    },
  };

  const result = await syncPendingOperations(store, transport, () => new Date('2026-09-25T10:05:00Z'));

  assert.deepEqual(result, { applied: 1, conflicts: 2, pulled: 2 });
  assert.deepEqual(sent, ['edit-0', 'edit-1', 'edit-2']);
  assert.deepEqual(removed, ['edit-0']);
  assert.deepEqual(entities.map(entity => [entity.id, entity.deleted]), [['b', false], ['c', true]]);
  assert.deepEqual(pullRequests, [{ after: 0, pageSize: 100 }]);
  assert.deepEqual(conflicts.map(({ operationId, serverVersion, serverPayload }) => ({ operationId, serverVersion, serverPayload })), [
    { operationId: 'edit-1', serverVersion: 4, serverPayload: { title: 'Server' } },
    { operationId: 'edit-1', serverVersion: 5, serverPayload: { title: 'Newer server value' } },
  ]);
  assert.deepEqual(pending.map(operation => operation.operationId), ['edit-1', 'edit-2']);
  assert.equal(store.cursor, 3);
});

test('an in-flight push does not replace a newer local edit of the same entity', async () => {
  const first = { operationId: 'first', type: 'tasks.task', id: 'task-a', kind: 'upsert', expectedVersion: 1, payload: { title: 'First edit' }, createdAt: '2026-09-25T10:00:00Z' };
  const second = { operationId: 'second', type: 'tasks.task', id: 'task-a', kind: 'upsert', expectedVersion: 2, payload: { title: 'Second edit' }, createdAt: '2026-09-25T10:01:00Z' };
  const pending = [first];
  let local = { type: 'tasks.task', id: 'task-a', version: 3, deleted: false, payload: { title: 'Second edit' } };
  const store = {
    async listPendingOperations() { return [...pending]; },
    async removeOperation(id) { pending.splice(pending.findIndex(operation => operation.operationId === id), 1); },
    async putEntity(entity) { local = entity; },
    async getEntity() { return local; },
    async putConflict() { throw new Error('Unexpected conflict'); },
    async getChangeCursor() { return 0; },
    async setChangeCursor() {},
    async getSyncEpoch() { return 'epoch-1'; },
    async setSyncEpoch() {},
    async clearLocalData() { throw new Error('Unexpected offline reset'); },
  };
  const transport = {
    async getSyncEpoch() { return 'epoch-1'; },
    async pushOperations() {
      pending.push(second);
      return [{ operationId: 'first', applied: true, skipped: false, current: { type: 'tasks.task', id: 'task-a', version: 2, deleted: false, payload: { title: 'First edit' } }, conflictReason: null }];
    },
    async pullChanges() {
      return { changes: [{ sequence: 1, snapshot: { type: 'tasks.task', id: 'task-a', version: 2, deleted: false, payload: { title: 'First edit' } } }], nextSequence: 1, isComplete: true };
    },
  };

  const summary = await syncPendingOperations(store, transport);

  assert.deepEqual(summary, { applied: 1, conflicts: 0, pulled: 1 });
  assert.deepEqual(local.payload, { title: 'Second edit' });
  assert.deepEqual(pending.map(operation => operation.operationId), ['second']);
});

test('a new server epoch clears old cached records and queued mutations before syncing', async () => {
  const entities = [{ type: 'planning.project', id: 'old-project', version: 1, deleted: false, payload: { title: 'stale' } }];
  const pending = [{ operationId: 'stale-op', type: 'planning.project', id: 'old-project', kind: 'upsert', expectedVersion: null, payload: { title: 'stale' }, createdAt: '2026-09-25T10:00:00Z' }];
  const conflicts = [{ operationId: 'stale-conflict' }];
  let localEpoch = 'old-epoch';
  let cursor = 99;
  const pushedEpochs = [];
  const store = {
    async listEntities() { return entities; },
    async getEntity(type, id) { return entities.find(entity => entity.type === type && entity.id === id); },
    async listPendingOperations() { return [...pending]; },
    async removeOperation(id) { pending.splice(pending.findIndex(operation => operation.operationId === id), 1); },
    async putEntity(entity) { entities.push(entity); },
    async putConflict(conflict) { conflicts.push(conflict); },
    async getChangeCursor() { return cursor; },
    async setChangeCursor(sequence) { cursor = sequence; },
    async getSyncEpoch() { return localEpoch; },
    async setSyncEpoch(epoch) { localEpoch = epoch; },
    async clearLocalData() { entities.length = 0; pending.length = 0; conflicts.length = 0; cursor = 0; },
  };
  const transport = {
    async getSyncEpoch() { return 'new-epoch'; },
    async pushOperations(_request, epoch) { pushedEpochs.push(epoch); return []; },
    async pullChanges(after) {
      assert.equal(after, 0);
      return { changes: [{ sequence: 7, snapshot: { type: 'tasks.task', id: 'v1-task', version: 1, payload: { title: 'V1' }, deleted: false } }], nextSequence: 7, isComplete: true };
    },
  };

  const result = await syncPendingOperations(store, transport);

  assert.deepEqual(result, { applied: 0, conflicts: 0, pulled: 1 });
  assert.equal(localEpoch, 'new-epoch');
  assert.deepEqual(pushedEpochs, []);
  assert.deepEqual(entities.map(entity => [entity.id, entity.deleted]), [['v1-task', false]]);
  assert.deepEqual(pending, []);
  assert.deepEqual(conflicts, []);
  assert.equal(cursor, 7);
});
