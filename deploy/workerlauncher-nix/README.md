# Ilmarinen Worker Launcher — NixOS Deployment

NixOS configuration for running a self-updating Ilmarinen worker: a thin **launcher** downloads the current worker bundle from the server, verifies its signature, and runs the worker as a child process. When the server is redeployed with a new build, the worker swaps to the new bundle automatically — immediately when idle, or right after the current job finishes.

This is the NixOS counterpart to [`../workerlauncher-docker/`](../workerlauncher-docker/) — **read that README's trust section first**: this mode means the server distributes code to this host. For a manually-updated worker on NixOS, use [`../worker-nix/`](../worker-nix/) instead.

> **Untested on a real NixOS target.** The docker variant is the smoke-tested one; this configuration mirrors `../worker-nix/` and the launcher's actual runtime needs, but has not yet run on a NixOS host. Expect to file down an edge or two on first deploy.

## Prerequisites

- A NixOS system (WSL2, VM, or bare metal)
- .NET 9 SDK on the build machine (for `dotnet publish`) — `deploy.sh` gets it from `shell.nix` in this directory
- The Ilmarinen server running with bundle serving enabled (the standard [`../server-docker/`](../server-docker/) image has it built in)

Unlike `../worker-nix/`, the launcher is published **framework-dependent** and runs under the nix-packaged .NET runtime: the worker bundles it downloads are framework-dependent, so the host needs a runtime regardless, and running everything under the nix `dotnet` host is what lets this config skip `programs.nix-ld` (nothing here ever execs a foreign ELF; the bundle's prebuilt libgit2 `.so` is dlopen'ed inside the nix dotnet process, where its libc needs resolve against the already-loaded nix glibc — don't "fix" a native-load failure by copying nix-ld back from worker-nix without checking what actually failed).

## Quick Start

1. **Register a worker** on the Ilmarinen server (via the UI or `POST /api/workers`) and save the worker key.

2. **Configure secrets:**
   ```bash
   cp ilmarinen-secrets.nix.example ilmarinen-secrets.nix
   # Edit ilmarinen-secrets.nix with your server URL and worker key
   ```

3. **Deploy:**
   ```bash
   ./deploy.sh
   ```

   The script enters its own `nix-shell` for the .NET SDK, builds and installs the
   launcher, applies the NixOS configuration, and restarts the service.

4. **Check the service:**
   ```bash
   sudo systemctl status ilmarinen-workerlauncher
   journalctl -u ilmarinen-workerlauncher -f
   ```

   A healthy first start logs the bundle download and verification, then the worker's own startup sequence ending in a `Healthy` diagnostic. (Restarts with a cached bundle skip straight to starting the worker.)

## WSL2 Setup

The configuration includes NixOS-WSL support by default (`wsl.enable = true`).

1. Install [NixOS-WSL](https://github.com/nix-community/NixOS-WSL) in a WSL2 instance
2. Clone this repo and follow the Quick Start steps above

## VM / Bare Metal

Set `wsl.enable = false` in `configuration.nix`:

```nix
wsl.enable = false;
```

You can also remove or comment out the `wsl.defaultUser` line and the `<nixos-wsl/modules>` import if NixOS-WSL is not installed.

## Updating

This is the point of this deployment: **server updates need nothing from this host.** The worker learns the server's current bundle at every reconnect and swaps itself.

Re-run `./deploy.sh` only for the three events the launcher cannot ride out on its own:

- **.NET major upgrades** — the bundles are framework-dependent, so bump `dotnetCorePackages.runtime_9_0` in `configuration.nix` (and `shell.nix`'s SDK) alongside the redeploy. The launcher logs a loud, specific error when the manifest's `targetFramework` doesn't match its runtime.
- **Manifest format bumps** — if a future server bumps the manifest's `formatVersion`, old launchers log "launcher is too old" and keep running their cached bundle until redeployed.
- **Changes to the launcher's own code** — the launcher never self-updates by design; fixes reach this host only through `deploy.sh`.

## Configuration

Secrets are stored in `ilmarinen-secrets.nix` (gitignored):

| Field | Description |
|-------|-------------|
| `serverUrl` | SignalR hub URL (worker port, typically 8081) |
| `workerKey` | Authentication key from server registration |

Downloaded bundles are cached under `/var/lib/ilmarinen-workerlauncher/.local/share/ilmarinen/bundles` (current + previous kept), workspaces beside them under `.../workspaces`.

Note this is a whole-system `configuration.nix`, so a host runs this *or* `../worker-nix/`, never both. Switching modes re-homes the `ilmarinen` user and leaves the old `/var/lib/ilmarinen-worker` directory behind for you to clean up.
