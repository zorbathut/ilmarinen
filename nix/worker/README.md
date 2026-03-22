# Ilmarinen Worker - NixOS Deployment

NixOS configuration for running an Ilmarinen CI/CD worker. Follows the same pattern as the Moonskrive Jenkins agent NixOS setup.

## Prerequisites

- A NixOS system (WSL2, VM, or bare metal)
- .NET 10 SDK on the build machine (for `dotnet publish --self-contained`)
- The Ilmarinen server running and accessible

The worker is published as a self-contained executable — no .NET runtime is needed on the NixOS target. Run `deploy.sh` from any machine with the .NET SDK (e.g. your dev machine), then apply the NixOS config on the target.

## Quick Start

1. **Register a worker** on the Ilmarinen server (via the UI or `POST /api/workers`) and save the worker key.

2. **Configure secrets:**
   ```bash
   cp ilmarinen-secrets.nix.example ilmarinen-secrets.nix
   # Edit ilmarinen-secrets.nix with your server URL and worker key
   ```

3. **Build and install the worker:**
   ```bash
   ./deploy.sh
   ```

4. **Apply the NixOS configuration:**
   ```bash
   sudo nixos-rebuild switch -I nixos-config=./configuration.nix
   ```

5. **Check the service:**
   ```bash
   sudo systemctl status ilmarinen-worker
   journalctl -u ilmarinen-worker -f
   ```

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

The server validates that workers are built from the same git commit. When the server is updated, re-run `deploy.sh` from the same commit and restart the service:

```bash
git pull
./deploy.sh
sudo systemctl restart ilmarinen-worker
```

## Configuration

Secrets are stored in `ilmarinen-secrets.nix` (gitignored):

| Field | Description |
|-------|-------------|
| `serverUrl` | SignalR hub URL (worker port, typically 8081) |
| `workerKey` | Authentication key from server registration |

The worker is published self-contained, so no .NET runtime is needed on the NixOS machine. The target runtime can be changed by passing a different `-r` flag in `deploy.sh` (default: `linux-x64`).
