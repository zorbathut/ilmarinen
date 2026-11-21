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

**The worker port (8081) is not safe to expose either.** `PortFilteringMiddleware` does restrict it to `/hub/workers`, but that is not the same as authenticating it: the artifact-upload endpoint under that prefix accepts unauthenticated, unbounded writes, and any registered worker can stream log lines into any job. Treat 8081 as private — a VPN address, never a public interface.

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

Workers must speak a worker protocol version this server supports (`WorkerProtocol.MinSupportedVersion` to `WorkerProtocol.Version`; the server logs the range at startup, and `GET /api/version` returns it), or it rejects them when they connect.

### Upgrading

How to deploy a new server depends on whether its worker protocol still accepts the workers you have:

- **Same minimum version** (most changes): just deploy. Workers reconnect and hand over any job results they were holding. Launcher-run workers see the new bundle, finish what they're running, and relaunch on it. Classic workers keep working until you rebuild them.
- **A raised minimum** (a breaking protocol change), or **rolling the server back past a version change**: drain first, so no worker is holding work the new server can't take from it.

  ```bash
  ilmarinen drain --server http://<server>:8080 --wait   # stop dispatching, wait for running jobs
  # deploy the server; the restart ends the drain
  ```

  Launcher-run workers then relaunch on the new bundle by themselves. Classic workers need rebuilding.

## Two pieces of state you cannot regenerate

**`ILMARINEN_SERVER_KEY`.** It is not just an auth secret. It HKDF-derives the AES key that encrypts stored repository credentials, and its public half is embedded in every worker key you have ever issued. Lose it and every stored Git credential is permanently undecryptable. Rotate it and every worker in the fleet must be re-registered. **Back up `.env` with the database**, and treat rotation as a migration, not a chore.

**`POSTGRES_PASSWORD`.** Postgres only applies it at `initdb`, on the first boot with an empty volume. Changing it in `.env` afterwards does *not* change the database — it just means the server can no longer authenticate, permanently, until you `down -v` (which destroys the database). Set it once.

## Waking sleeping workers

The server can wake a worker whose host has gone to sleep when a queued job needs it, with a wake-on-LAN packet. It is off until you tell the server where to send the packet:

```yaml
    environment:
      Server__WakeAddress: 192.168.1.255:9   # the workers' LAN broadcast address; the port defaults to 9
```

Then give each worker that can be woken its host's MAC address on its page (*Wake address*), and check it with the *Wake* button there while the host is asleep.

What happens: a job that has been queued for 30 seconds with nobody to take it gets one sleeping worker woken for it, the highest priority that can take it first. The job stays in the queue, marked *waiting for X to wake*; whichever worker is ready first takes it. A worker that isn't back and ready within `Server__WakeTimeout` (default `00:05:00`) is given up on and left alone for `Server__WakeBackoff` (default `01:00:00`), and the next sleeper is woken instead. A sleeper that can still be woken counts as available to the stuck-jobs alert; once a wake of it has failed it doesn't, until it is next seen, and the alert names it. A worker put to sleep with a job running is left asleep: the job carries on when it next wakes.

**Getting the packet onto the LAN.** The packet is a broadcast, and a broadcast from this compose file's bridged server container stops at the Docker bridge. Either:

- set `Server__WakeAddress` to the LAN's *directed* broadcast address (`192.168.1.255`, not `255.255.255.255`) and let the server host forward it: `sysctl -w net.ipv4.conf.all.bc_forwarding=1 net.ipv4.conf.<lan-interface>.bc_forwarding=1 net.ipv4.conf.<docker-bridge>.bc_forwarding=1` (persist it in `/etc/sysctl.d/`); or
- run the server with host networking, which also means reaching Postgres through a published port rather than as `postgres`.

To check: run `tcpdump -i <lan-interface> udp port 9` on the server host and press *Wake*.

**On the workers' hosts.** Wake-on-LAN needs wired Ethernet, enabled in the firmware and on the interface, persistently: `nmcli connection modify <connection> 802-3-ethernet.wake-on-lan magic`, then check with `ethtool <interface> | grep Wake-on` (`g` means magic packets). Pair it with the sleep-inhibit overlay (`deploy/worker-docker/`), which also has the worker tell the server just before the host sleeps.

## Operations

**Backups.** `docker compose exec postgres pg_dump -U ilmarinen ilmarinen > backup.sql`, plus the `artifacts` volume, plus `.env`. All three, or the backup is not restorable.

**`docker compose down -v` destroys everything** — job history, pipelines, encrypted repository credentials, and artifacts. Plain `down` is safe.

**Disk.** Nothing is pruned automatically. Job logs are rows in PostgreSQL (`JobLogChunk`) and there is no retention policy, so on a busy server the database is the fastest-growing thing on the box. Artifacts accumulate in their volume. Watch both.

**Upgrading.** `git pull && docker compose up -d --build`. Rebuild your workers from the same ref, or they will be rejected on reconnect.
