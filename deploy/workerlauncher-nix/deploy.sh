#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
INSTALL_DIR="/opt/ilmarinen-workerlauncher"

# Re-exec inside nix-shell so the .NET SDK is available without the operator
# having to enter one manually. Single-step deploy is the contract.
if [ -z "${IN_NIX_SHELL:-}" ]; then
    exec nix-shell "$SCRIPT_DIR/shell.nix" --run "bash $(printf '%q' "$0")"
fi

if [ ! -f "$SCRIPT_DIR/ilmarinen-secrets.nix" ]; then
    echo "error: $SCRIPT_DIR/ilmarinen-secrets.nix not found" >&2
    echo "       cp ilmarinen-secrets.nix.example ilmarinen-secrets.nix and edit." >&2
    exit 1
fi

# Framework-dependent on purpose: the host needs a .NET runtime anyway for the worker bundles the launcher downloads, and configuration.nix provides it.
echo "Building Ilmarinen Worker Launcher from source..."
dotnet publish "$REPO_ROOT/src/Ilmarinen.WorkerLauncher/Ilmarinen.WorkerLauncher.csproj" \
  -c Release \
  -o /tmp/ilmarinen-workerlauncher-publish

echo "Installing to $INSTALL_DIR..."
sudo mkdir -p "$INSTALL_DIR"
sudo rm -rf "${INSTALL_DIR:?}"/*
sudo cp -r /tmp/ilmarinen-workerlauncher-publish/* "$INSTALL_DIR/"
rm -rf /tmp/ilmarinen-workerlauncher-publish

echo "Applying NixOS configuration..."
sudo nixos-rebuild switch -I nixos-config="$SCRIPT_DIR/configuration.nix"

# After the rebuild, so a first deploy on a fresh host works: the ilmarinen user doesn't exist until the configuration has been applied once.
sudo chown -R ilmarinen:ilmarinen "$INSTALL_DIR"

echo "Restarting worker launcher service..."
sudo systemctl restart ilmarinen-workerlauncher

echo
echo "Deploy complete. Recent logs:"
sudo journalctl -u ilmarinen-workerlauncher -n 20 --no-pager || true
