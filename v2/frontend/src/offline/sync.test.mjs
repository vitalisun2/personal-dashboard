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
  };
  const transport = {
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
