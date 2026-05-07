# Nix Build & Deployment

## Development Shell

```bash
nix-shell nix/shell.nix
```

This gives you a shell with the .NET 9.0 SDK.

## Building Packages

All packages are defined in `default.nix` at the repo root.

```bash
nix-build -A worker   # Ilmarinen Worker
nix-build -A server   # Ilmarinen Server
nix-build -A cli      # Ilmarinen CLI
```

Output lands in `./result/`.

## Updating NuGet Dependencies

After adding, removing, or updating NuGet packages, regenerate `nix/deps.json`:

```bash
nix-build -A fetch-deps && ./result nix/deps.json
```

This restores the entire solution and writes all package hashes into
`nix/deps.json`. Commit the updated file.

## Deploying the Worker on NixOS

### Prerequisites

The worker NixOS configuration lives in `nix/worker/`. You need a secrets file
that is **not** checked into source control:

```bash
# nix/worker/ilmarinen-secrets.nix
{
  serverUrl = "https://your-server-url";
  workerKey = "your-worker-key";
}
```

### Deploying

```bash
./nix/worker/deploy.sh
```

The script enters its own `nix-shell`, builds and installs the worker, applies
the NixOS configuration, and restarts the service. Then check it's running:

```bash
sudo systemctl status ilmarinen-worker
journalctl -u ilmarinen-worker -f
```

If you've changed NuGet dependencies in a `.csproj`, regenerate `nix/deps.json`
and commit it before deploying — see "Updating NuGet Dependencies" above.

### Configuration Notes

- `configuration.nix` assumes NixOS-WSL by default (`wsl.enable = true`).
  Set to `false` for VM or bare metal.
- The worker runs as a dedicated `ilmarinen` system user with Docker access.
- Docker is configured with JSON file logging (10MB max, 3 rotated files).
- The worker needs only outbound network access (firewall enabled, no open ports).
