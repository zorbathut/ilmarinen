# Ilmarinen Server — Docker Compose Deployment

Runs the Ilmarinen server and its PostgreSQL database on any host with Docker. Workers connect to it from here or from other machines (see [`../worker-docker/`](../worker-docker/) and [`../worker-nix/`](../worker-nix/), or the self-updating variants [`../workerlauncher-docker/`](../workerlauncher-docker/) and [`../workerlauncher-nix/`](../workerlauncher-nix/)).

## Read this first: there is no authentication

**Ilmarinen has no login, no API keys, and no authorization of any kind.** Anyone who can reach the public port can:

- `POST /api/jobs` — run an arbitrary pipeline script, which executes on a worker that has the Docker socket mounted. **That is root on every worker in your fleet.**
- `POST /api/workers` — mint themselves a worker key, join the fleet, and receive jobs (including the Git tokens those jobs carry).
- Delete jobs, workers, pipelines, and repository credentials.

So both ports **bind to `127.0.0.1` by default**. Widening either is a deliberate act:

- **Put something that authenticates in front of the public port** — a reverse proxy with basic auth or SSO, terminating TLS — or put the host on a private network (VPN, Tailscale, WireGuard). Only then set `BIND_ADDR`.
- **A host firewall will not save you.** Published Docker ports are DNAT'd in the `nat`/`PREROUTING` chain and filtered in the `FORWARD` path via Docker's own chain — they never traverse `INPUT` at all, which is where ufw and firewalld put their rules. A "firewalled" box with a published port is still open to the internet, and no amount of reordering ufw rules changes that. Bind to a private address instead (`BIND_ADDR=10.0.0.5`).
- **The proxy must upgrade WebSockets.** The dashboard is Blazor Server plus a SignalR log hub; a proxy config without the `Upgrade`/`Connection` headers gives you a dashboard that loads and then silently does nothing.

**The worker port (8081) is not safe to expose either.** `PortFilteringMiddleware` does restrict it to `/hub/workers`, but that is not the same as authenticating it: the artifact-upload endpoint under that prefix accepts unauthenticated, unbounded writes, and several hub methods (`JobStarted`, `ReportCommit`, `StreamLogs`) don't check the caller's identity at all. Treat 8081 as private — a VPN address, never a public interface.

**The best answer is to publish nothing.** A worker on this same host doesn't need either port: it joins this Compose project's network and reaches the server as `server:8081`. See [`../worker-docker/docker-compose.same-host.yml`](../worker-docker/docker-compose.same-host.yml). Only a *remote* worker needs `WORKER_BIND_ADDR`.

## Quick start

```bash
cd deploy/server-docker
cp .env.example .env
openssl rand -base64 32   # -> ILMARINEN_SERVER_KEY
openssl rand -hex 32      # -> POSTGRES_PASSWORD
docker compose up -d --build
docker compose logs -f
```

Database migrations run automatically at boot — there is no separate migration step. The dashboard is then on `http://127.0.0.1:8080`.

If you skip the `openssl` steps above and leave `ILMARINEN_SERVER_KEY` as the `REPLACE_WITH_GENERATED_KEY` template value, the server exits immediately with `ILMARINEN_SERVER_KEY must be valid base64` and — under `restart: unless-stopped` — restarts into the same error. That is deliberate: a key that isn't a real key fails at boot with a one-line reason, rather than booting a healthy-looking server that errors on the first worker that tries to register. The same applies to a key that is valid base64 but the wrong length, or that isn't a usable P-256 scalar.

## Registering a worker

Workers need a key minted by the server. From the server host:

```bash
curl -sX POST http://127.0.0.1:8080/api/workers \
  -H 'Content-Type: application/json' \
  -d '{"name":"worker-1"}' | jq -r .workerKey
```

That prints the key (`{name}:{guid}:{workerPrivBase64}:{serverPubBase64}`) — put it in the worker's `ILMARINEN_WORKER_KEY`. One key per worker; don't reuse one across machines. Worker names must be unique, so re-registering an existing worker means deleting it first (`DELETE /api/workers/{workerId}`).

Then point the worker at this server:
- **Same host**: `ILMARINEN_SERVER_URL=http://server:8081` plus the same-host overlay — nothing needs publishing.
- **Remote host**: set `WORKER_BIND_ADDR` here to a private address the worker can reach, and point `ILMARINEN_SERVER_URL` at `http://<that-address>:8081`.

Workers must be built from a ref whose protocol hash matches this server's, or the server rejects them at registration. Rebuild both from the same ref when you upgrade.

## Two pieces of state you cannot regenerate

**`ILMARINEN_SERVER_KEY`.** It is not just an auth secret. It HKDF-derives the AES key that encrypts stored repository credentials, and its public half is embedded in every worker key you have ever issued. Lose it and every stored Git credential is permanently undecryptable. Rotate it and every worker in the fleet must be re-registered. **Back up `.env` with the database**, and treat rotation as a migration, not a chore.

**`POSTGRES_PASSWORD`.** Postgres only applies it at `initdb`, on the first boot with an empty volume. Changing it in `.env` afterwards does *not* change the database — it just means the server can no longer authenticate, permanently, until you `down -v` (which destroys the database). Set it once.

## Operations

**Backups.** `docker compose exec postgres pg_dump -U ilmarinen ilmarinen > backup.sql`, plus the `artifacts` volume, plus `.env`. All three, or the backup is not restorable.

**`docker compose down -v` destroys everything** — job history, pipelines, encrypted repository credentials, and artifacts. Plain `down` is safe.

**Disk.** Nothing is pruned automatically. Job logs are rows in PostgreSQL (`JobLogChunk`) and there is no retention policy, so on a busy server the database is the fastest-growing thing on the box. Artifacts accumulate in their volume. Watch both.

**Upgrading.** `git pull && docker compose up -d --build`. Rebuild your workers from the same ref, or they will be rejected on reconnect.
