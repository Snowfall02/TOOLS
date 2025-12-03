# Deep Dive Tool (predeep bonde style) — updates

This branch adds two requested changes:
- Images are stored in Postgres as bytea (image_data column) instead of filesystem paths.
- Background processing of deep-dive jobs using Hangfire + Redis. The WebAPI hosts Hangfire server and dashboard and enqueues background jobs that call the Python chart service, persist image bytes and candles into Postgres using Dapper.

Quick summary of flow
- Client (WPF) POSTs a deep dive request to WebAPI.
- WebAPI upserts the equity, inserts a deep_dives row with status = queued, then enqueues a Hangfire background job (Redis-backed).
- The background job calls the Python chart service (/generate), receives the base64 image + candles, writes image bytes into deep_dives.image_data, inserts candle rows, and updates metadata/status.
- Hangfire Dashboard is available at /hangfire on the WebAPI host (enabled in Development by default, configurable).

Key files changed
- db/schema.sql — deep_dives now includes image_data (bytea), job_id (text), status (text).
- docker-compose.yml — adds Redis service, passes REDIS_URL to WebAPI, ensures Hangfire/Redis connectivity.
- dotnet/WebAPI — updated to net10.0, adds Hangfire + Redis packages, configures Hangfire server + dashboard, uses Dapper/Npgsql as before.
- dotnet/WebAPI/Controllers/DeepDiveController.cs — now enqueues background job and returns deep_dive_id + hangfire job id.
- dotnet/WebAPI/Jobs/DeepDiveJob.cs — the background worker that calls the Python chart service and persists results into Postgres.

Environment variables used (docker-compose sets defaults)
- DATABASE_URL — Npgsql connection string for Postgres (Host=db;Port=5432;Username=deepdive;Password=deepdivepass;Database=deepdive)
- REDIS_URL — Redis connection string (redis:6379)
- CHART_SERVICE_URL — Python chart service endpoint (http://chart-service:8001)
- (OPTIONAL) HANGFIRE_DASHBOARD_AUTH — minimal switch/env var for enabling auth on dashboard (not implemented; recommended to secure in production)

Notes
- Do NOT expose the Hangfire dashboard in public without authentication in production.
- The Python chart service remains stateless and returns base64 image and candles as before.
- For production image sizes and DB load, measure memory/disk and consider storing images in object storage (S3) with only references in DB.

Next actions for you
1. Copy the files below into your local TOOLS repo (feature/deep-dive-tool branch).
2. Commit & push.
3. `docker-compose up --build` will start Postgres, Redis, Python service, and WebAPI (which includes Hangfire server).
4. Initialize DB schema: `psql "host=localhost port=5432 user=deepdive password=deepdivepass dbname=deepdive" -f db/schema.sql`
5. POST a deep-dive request:
   POST http://localhost:8000/DeepDive
   body: {"symbol":"AAPL","interval":"1d","period":"1mo"}
6. Monitor jobs at http://localhost:8000/hangfire (secure it before exposing).

If you want, I can:
- Add minimal auth (API key) for the DeepDive endpoint and the Hangfire dashboard.
- Move from storing images in the deep_dives table to a separate images table if you prefer normalization.
- Add retry/backoff logic and improved error handling in the background job (currently a best-effort example).