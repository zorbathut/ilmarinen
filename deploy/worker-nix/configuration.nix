{ config, lib, pkgs, ... }:

let
  # Import secrets from a separate file that won't be checked into source control
  ilmarinenSecrets = import ./ilmarinen-secrets.nix;

  # Path where deploy.sh installs the self-contained published worker
  workerDir = "/opt/ilmarinen-worker";
in
{
  imports = [
    # NixOS-WSL support - safe to include on non-WSL systems
    <nixos-wsl/modules>
  ];

  # Set to false on VM/bare metal, true on WSL2
  wsl.enable = true;
  wsl.defaultUser = "nixos";

  # Ilmarinen worker systemd service
  systemd.services.ilmarinen-worker = {
    description = "Ilmarinen CI/CD Worker";
    after = [ "network.target" "docker.service" ];
    requires = [ "docker.service" ];
    wantedBy = [ "multi-user.target" ];

    serviceConfig = {
      Type = "simple";
      User = "ilmarinen";
      Group = "ilmarinen";
      WorkingDirectory = workerDir;
      Restart = "always";
      RestartSec = "30";

      # Grant access to Docker socket
      SupplementaryGroups = [ "docker" ];

      # Security settings - Docker needs these relaxed
      NoNewPrivileges = false;
      PrivateDevices = false;
      ProtectKernelTunables = false;

      ExecStart = "${workerDir}/ilmarinen-worker";
    };

    environment = {
      ILMARINEN_SERVER_URL = ilmarinenSecrets.serverUrl;
      ILMARINEN_WORKER_KEY = ilmarinenSecrets.workerKey;
      DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1";
      HOME = "/var/lib/ilmarinen-worker";
      PATH = lib.mkForce (lib.makeBinPath (with pkgs; [
        git
        docker
        bash
        coreutils
      ]));
    };
  };

  # System packages
  environment.systemPackages = with pkgs; [
    git
    docker
  ];

  # Docker daemon for pipeline execution
  virtualisation.docker = {
    enable = true;
    enableOnBoot = true;

    daemon.settings = {
      hosts = [ "unix:///var/run/docker.sock" ];
      experimental = true;
      log-driver = "json-file";
      log-opts = {
        max-size = "10m";
        max-file = "3";
      };
    };
  };

  # Allow running dynamically linked executables (e.g. self-contained .NET publish)
  programs.nix-ld.enable = true;

  # Firewall - worker only needs outbound connections
  networking.firewall.enable = true;

  # Ilmarinen worker user and group
  users.users.ilmarinen = {
    isSystemUser = true;
    group = "ilmarinen";
    home = "/var/lib/ilmarinen-worker";
    createHome = true;
    extraGroups = [ "docker" ];
    shell = pkgs.bash;
  };

  users.groups.ilmarinen = {};

  # Ensure directories and socket permissions
  systemd.tmpfiles.rules = [
    "d /var/lib/ilmarinen-worker 0755 ilmarinen ilmarinen -"
    "d ${workerDir} 0755 ilmarinen ilmarinen -"
    "z /var/run/docker.sock 0660 root docker -"
  ];

  system.stateVersion = "25.05";
}
