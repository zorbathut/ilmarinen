#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_ROOT="$(cd "$SCRIPT_DIR/../.." && pwd)"
INSTALL_DIR="/opt/ilmarinen-worker"

echo "Building Ilmarinen Worker from source..."
GIT_COMMIT=$(git -C "$REPO_ROOT" rev-parse --short HEAD)

dotnet publish "$REPO_ROOT/src/Ilmarinen.Worker/Ilmarinen.Worker.csproj" \
  -c Release \
  -o /tmp/ilmarinen-worker-publish \
  -p:GitCommit="$GIT_COMMIT"

echo "Installing to $INSTALL_DIR..."
sudo mkdir -p "$INSTALL_DIR"
sudo rm -rf "${INSTALL_DIR:?}"/*
sudo cp -r /tmp/ilmarinen-worker-publish/* "$INSTALL_DIR/"
sudo chown -R ilmarinen:ilmarinen "$INSTALL_DIR"
rm -rf /tmp/ilmarinen-worker-publish

echo ""
echo "Deployed commit $GIT_COMMIT to $INSTALL_DIR"
echo ""
echo "Next steps:"
echo "  sudo nixos-rebuild switch -I nixos-config=./configuration.nix"
echo "  sudo systemctl restart ilmarinen-worker"
echo "  journalctl -u ilmarinen-worker -f"
