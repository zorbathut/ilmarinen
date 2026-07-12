#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
INSTALL_DIR="/opt/ilmarinen-worker"

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

echo "Building Ilmarinen Worker from source..."
dotnet publish "$REPO_ROOT/src/Ilmarinen.Worker/Ilmarinen.Worker.csproj" \
  -c Release \
  --self-contained \
  -r linux-x64 \
  -o /tmp/ilmarinen-worker-publish

echo "Installing to $INSTALL_DIR..."
sudo mkdir -p "$INSTALL_DIR"
sudo rm -rf "${INSTALL_DIR:?}"/*
sudo cp -r /tmp/ilmarinen-worker-publish/* "$INSTALL_DIR/"
sudo chown -R ilmarinen:ilmarinen "$INSTALL_DIR"
rm -rf /tmp/ilmarinen-worker-publish

echo "Applying NixOS configuration..."
sudo nixos-rebuild switch -I nixos-config="$SCRIPT_DIR/configuration.nix"

echo "Restarting worker service..."
sudo systemctl restart ilmarinen-worker

echo
echo "Deploy complete. Recent logs:"
sudo journalctl -u ilmarinen-worker -n 20 --no-pager || true
