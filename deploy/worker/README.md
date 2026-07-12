# Ilmarinen Worker — Docker Compose Deployment

Runs an Ilmarinen worker on any host with Docker. This is the non-NixOS counterpart to `nix/worker/`; use that one for a NixOS target.

The worker is a *client* — it makes outbound connections to the server and needs no inbound ports of its own.

## Prerequisites

- Docker with Compose v2 (`docker compose version`).
- Network access from this host to the server's **worker port** (see below).
- Enough headroom to build .NET from source: the SDK image alone is ~850 MB on disk, and the build is memory-hungry. The smallest VPS tiers will OOM-kill the compiler with a confusing error.

## The server must publish its worker port

The worker connects to the server's worker port (8081 by default), **not** the public port. The stock `docker-compose.yml` deliberately does not publish 8081 — it is documented as internal-only, on the assumption that workers share the server's Compose network. So a worker on a *separate* host only works once the server operator exposes it. Connection refused in `docker compose logs` here is almost always this.

On the **server** host, publish it from a `docker-compose.override.yml` (that file is gitignored, so the stock compose files stay untouched):

```yaml
services:
  server:
    ports:
      - "8081:8081"
```

Publishing 8081 exposes less than it looks like: `PortFilteringMiddleware` serves only `/hub/workers` on that port and 404s everything else, and workers authenticate with an ECDSA challenge-response. It is still an unencrypted channel — see Security — so prefer a private network (VPN, Tailscale, WireGuard) or a TLS-terminating reverse proxy over the open internet.

## Quick start

Register a worker on the server first (via the UI or `POST /api/workers`) and save the key it gives you.

```bash
git clone <repo> && cd ilmarinen/deploy/worker
cp .env.example .env      # add ILMARINEN_SERVER_URL and ILMARINEN_WORKER_KEY
docker compose up -d --build
docker compose logs -f
```

Run every command from this directory — the `.env` file and the Compose project are resolved relative to it.

A healthy start logs its container ID, the discovered host workspace path, and a `Healthy` startup diagnostic. The worker only accepts jobs once that diagnostic passes, so if it reports unhealthy, read the diagnostic's message — it names the failing check.

## Upgrading

The server rejects workers whose protocol hash doesn't match its own, so the worker must be rebuilt from a ref compatible with the server's:

```bash
git pull                          # or: git checkout <the server's ref>
docker compose up -d --build
```

This recreates the container, which kills any job running on it — the step containers and the job's Docker network are orphaned rather than cleaned up. Check the server UI for a job on this worker before upgrading.

## Security

**The worker host is a trust boundary.** The worker needs the Docker socket to run pipelines, and it mounts that socket into every step container as well — so anyone who can submit a pipeline that lands on this worker effectively has root on this host. Point a worker only at a server whose job submitters you trust, and don't co-locate it with anything you care about.

**The worker↔server channel is unencrypted.** Worker authentication is an ECDSA challenge-response, which proves *identity* but provides no confidentiality: job logs and the per-job Git token both cross the wire in plaintext. Do not expose the worker port to the internet without TLS or a private network.

## Operations

**Disk.** Nothing here prunes anything. Pulled and built pipeline images accumulate, persistent workspaces grow, and a worker killed mid-job leaves its `ilmarinen-<guid>` network behind. Schedule a periodic `docker system prune -af --filter until=168h` and `docker network prune -f`. Pruning a live job's network is safe — the worker attaches itself to every job network, so one in use always has a container attached — but note that `system prune -af` also drops the cached .NET SDK build layers, so the next `--build` re-downloads them.

**Job container logs.** The `logging:` block in `docker-compose.yml` bounds the *worker's* logs, not those of the containers it spawns. Bound those at the daemon level in `/etc/docker/daemon.json`:

```json
{ "log-driver": "json-file", "log-opts": { "max-size": "10m", "max-file": "3" } }
```

**`docker compose down -v` deletes the `workspaces` volume**, and with it every persistent workspace on this host. Plain `down` is safe.

**One container per worker key.** Do not use `--scale`: replicas would share a single identity and a single workspace volume. A second worker on this host needs its own key, its own `.env`, and its own project name:

```bash
docker compose -p ilmarinen-worker-2 --env-file .env.worker2 up -d
```

**Architecture.** The server does not schedule by architecture. An arm64 worker in an otherwise amd64 fleet will intermittently fail pipelines that pin amd64 images.

**File ownership.** Job containers run as the worker process's uid, which is root in this image (the NixOS worker runs as the unprivileged `ilmarinen` user). In a mixed fleet the same pipeline can leave differently-owned files in `/workspace` depending on which worker picked up the job.

## Troubleshooting

| Symptom | Cause |
|---|---|
| Compose refuses to start: `set ILMARINEN_SERVER_URL in .env` | No `.env`, or you ran Compose from a different directory. |
| Connection refused / timeout | The server isn't publishing its worker port, or a firewall is dropping it. See above. |
| Protocol mismatch logged on repeat, forever | Worker built from a ref incompatible with the server's. It retries rather than exiting, so this repeats until you rebuild — see Upgrading. |
| Crash-loops: `no Docker mount backs the workspace path` | The `workspaces` volume's destination and `ILMARINEN_WORKSPACE_PATH` disagree. They must be identical; that equality is how the worker finds the host-side path it bind-mounts into job containers. |
| Crash-loops: cannot connect to the Docker daemon | `/var/run/docker.sock` isn't mounted, or this host runs rootless Docker (whose socket lives elsewhere — unsupported here). |
| Startup diagnostic reports `docker_daemon failed` | The worker reached the daemon but a check against it failed; the diagnostic names the failing step. The worker stays online but takes no jobs until it passes. |
