# Personal OS V2 foundation

V2 is a separate application under `v2/`. The repository root solution, Compose project, and JSON data belong to V1.

## Development

- Backend: `dotnet build PersonalDashboard.V2.slnx`
- Frontend: from `frontend/`, use Node.js 24 and run `npm ci`, then `npm run build`.
- EF tools: `dotnet tool restore` from `v2/`. The initial Host migration creates only Platform sync tables. After all domain module mappings are integrated, run:

  ```powershell
  dotnet build PersonalDashboard.V2.slnx
  dotnet tool run dotnet-ef -- migrations add ModulesInitial --project backend/src/Host/PersonalDashboard.V2.Host.csproj --startup-project backend/src/Host/PersonalDashboard.V2.Host.csproj --context PlatformDbContext --output-dir Migrations --no-build
  ```

  `PlatformDbContextFactory` loads each module Infrastructure assembly for EF model discovery; migration generation does not need a live database. Review the generated migration before deployment, including Search's `vector` and `pg_trgm` extension/index SQL. Host applies committed migrations on startup.
- PostgreSQL connection string: `ConnectionStrings:PersonalOsV2`.

The Host calls `Add{Name}Infrastructure(IServiceCollection, IConfiguration)` and `Map{Name}Api(IEndpointRouteBuilder)` for each of Knowledge, Planning, Tasks, Chat, Agent, and Search. Modules keep their EF `IEntityTypeConfiguration<>` and repositories in their own Infrastructure projects. `PlatformDbContext` discovers those configurations from loaded module Infrastructure assemblies; only the owning module changes its tables. Agent batches use `ITransactionRunner` and module Application contracts so all confirmed actions share one database transaction.

The Vue shell discovers `frontend/src/modules/<section>/index.ts` files. Each file exports named `section` and `component` values. Supported sections are `knowledge`, `planning`, `tasks`, `chat`, and `search`. Use `src/shared/chatRoute.ts` to open general or entity-focused chat.

## Offline protocol

IndexedDB stores versioned entity copies, pending manual operations, conflicts, and the pull cursor. A queued operation carries a stable operation ID, entity type and ID, kind, payload, and expected server version. The client pushes operations in order to `POST /api/v2/sync/push`, then pages `GET /api/v2/sync/changes?after=<cursor>&pageSize=<size>`. Server handlers revalidate every operation. Applied operation IDs are durable and idempotent. The change journal has a transactional sequence; deletes appear as tombstones. A conflict leaves local edits queued for explicit resolution. There is no automatic merge or overwrite of pending local work.

Domain modules must append their changed entity snapshot to `IEntityChangeJournal` inside the same `ITransactionRunner` scope as their write. Their `ISyncMutationHandler` uses the same Application validation as manual HTTP actions. Search projections are derived; module `ISearchSourceFeed` implementations support index rebuilds without Search reading foreign tables.

## Isolated container configuration

`compose.yml` uses Compose project `personal-os-v2`, a separate pgvector/PostgreSQL volume, and only `127.0.0.1:8090` for the app. The database has no host port. Copy `.env.example` to a V2-only `.env`, set a strong `V2_DB_PASSWORD`, and set `V2_OPENROUTER_API_KEY_FILE` to an existing external key file. The key is mounted as a Docker secret; it is never copied into the image. Ollama chat and embedding models are configurable through `V2_GEMMA_MODEL` and `V2_EMBED_MODEL`. V2 Compose does not mount or modify V1 data.
