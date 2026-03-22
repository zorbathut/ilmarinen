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

### First-time setup

1. Generate `nix/deps.json` if it hasn't been done yet:

   ```bash
   nix-shell nix/shell.nix
   nix-build -A fetch-deps && ./result nix/deps.json
   ```

2. Deploy the worker binary and apply the NixOS configuration:

   ```bash
   ./nix/worker/deploy.sh
   sudo nixos-rebuild switch -I nixos-config=./nix/worker/configuration.nix
   ```

3. Check that it's running:

   ```bash
   sudo systemctl status ilmarinen-worker
   journalctl -u ilmarinen-worker -f
   ```

### Updating

After pulling new changes:

```bash
./nix/worker/deploy.sh
sudo systemctl restart ilmarinen-worker
```

If NuGet dependencies changed, regenerate deps first:

```bash
nix-shell nix/shell.nix
nix-build -A fetch-deps && ./result nix/deps.json
```

### Configuration Notes

- `configuration.nix` assumes NixOS-WSL by default (`wsl.enable = true`).
  Set to `false` for VM or bare metal.
- The worker runs as a dedicated `ilmarinen` system user with Docker access.
- Docker is configured with JSON file logging (10MB max, 3 rotated files).
- The worker needs only outbound network access (firewall enabled, no open ports).
