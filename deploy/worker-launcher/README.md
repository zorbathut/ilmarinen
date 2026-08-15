# Ilmarinen Worker Launcher — Self-Updating Worker Deployment

Runs an Ilmarinen worker that updates itself when the server is updated. A thin **launcher** container downloads the current worker bundle from the server, verifies its signature, and runs the worker as a child process; when the server is redeployed with a new build, the worker swaps to the new bundle automatically — immediately when idle, or right after the current job finishes.

Use this instead of [`../worker-docker/`](../worker-docker/) when you want "update the server, workers follow." Everything in that README about networking, security, disk, and operations applies here too; this one covers only what's different.

## The trust decision (read this first)

**This mode means the server distributes code to this host, and this host runs it.** The bundle is ECDSA-signed with the server's key and the launcher verifies the signature against the server public key embedded in the worker key, so a network man-in-the-middle cannot inject unsigned code — but signatures carry no freshness, so a MITM *can* replay an older signed bundle (a downgrade), and a compromised server can sign anything. The channel itself is plaintext HTTP, same trust assumptions as the existing worker channel.

If a worker host must not auto-accept code from the server, keep using `../worker-docker/` — classic workers are fully supported and unaffected. That choice is per host: fleets can mix both modes freely.

## How updating works

- The worker reports its bundle hash when it authenticates; the server replies with the hash it currently serves. The bundle can only change when the server restarts, and a restart drops every worker connection, so workers learn about updates exactly when they reconnect — no polling.
- An idle worker on a stale bundle exits immediately; a busy one finishes its job, reports the result, then exits. The launcher fetches the new bundle, verifies, and relaunches.
- Each swap re-runs the worker's startup diagnostic, so expect a worker to be unavailable for a diagnostic cycle (~image pull + container run) per server deploy.
- If the worker can't complete a handshake at all (e.g. the server deploy changed the protocol shape), it exits for an update after three consecutive failures — this is how the fleet recovers from protocol-breaking deploys without manual rebuilds.
- The server's Workers page shows each worker's bundle hash and whether it is current.

## Quick start

Register a worker on the server first (via the UI or `POST /api/workers`) and save the key it gives you.

```bash
git clone <repo> && cd ilmarinen/deploy/worker-launcher
cp .env.example .env      # add ILMARINEN_SERVER_URL and ILMARINEN_WORKER_KEY
docker compose up -d --build
docker compose logs -f
```

Same-host-as-server works exactly like the classic worker: `ILMARINEN_SERVER_URL=http://server:8081` in `.env`, then

```bash
docker compose -f docker-compose.yml -f docker-compose.same-host.yml up -d --build
```

A healthy start logs the manifest fetch, the bundle download and verification, and then the worker's own startup sequence.

## When you still have to touch this host

The launcher is the one component that cannot update itself — deliberately, so it survives arbitrarily broken bundles. Keep it minimal and expect to rebuild it (`docker compose up -d --build`) only for:

- **.NET major upgrades.** The bundle is framework-dependent; when the server moves to a new .NET major, the launcher image's runtime must follow. The launcher logs a loud, specific error when the manifest's `targetFramework` doesn't match its runtime.
- **Manifest format bumps.** If a future server bumps the manifest's `formatVersion`, old launchers log "launcher is too old" and keep running their cached bundle until rebuilt.

## Operations

- **One launcher per `bundles` volume.** Like worker keys, don't `--scale` or share the volume between launcher containers; a second worker needs its own project, key, and volumes.
- **`stop_grace_period` matters.** The launcher forwards SIGTERM to the worker and gives it ~25s to stop cleanly; the compose file sets `stop_grace_period: 40s` to leave room. Don't lower it — Docker's 10s default would SIGKILL a mid-job worker.
- **Server without a bundle.** If the server has no bundle configured (manifest 404), a launcher with a cached bundle runs the cached one; running workers keep working (a server with no bundle expresses no opinion about staleness). A launcher with an empty cache waits for the manifest to appear.
- The bundle cache keeps the current and previous bundle and prunes the rest.
