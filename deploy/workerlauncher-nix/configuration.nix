{ config, lib, pkgs, ... }:

let
  # Import secrets from a separate file that won't be checked into source control
  ilmarinenSecrets = import ./ilmarinen-secrets.nix;

  # Path where deploy.sh installs the framework-dependent published launcher
  launcherDir = "/opt/ilmarinen-workerlauncher";

  # The worker bundles the launcher downloads are framework-dependent, so a .NET runtime is required on this host regardless — which is why the launcher itself is published framework-dependent too (see README).
  dotnetRuntime = pkgs.dotnetCorePackages.runtime_9_0;
in
{
  imports = [
    # NixOS-WSL support - safe to include on non-WSL systems
    <nixos-wsl/modules>
  ];

  # Set to false on VM/bare metal, true on WSL2
  wsl.enable = true;
  wsl.defaultUser = "nixos";

  # Ilmarinen worker launcher systemd service
  systemd.services.ilmarinen-workerlauncher = {
    description = "Ilmarinen CI/CD Worker Launcher";
    after = [ "network.target" "docker.service" ];
    requires = [ "docker.service" ];
    wantedBy = [ "multi-user.target" ];

    serviceConfig = {
      Type = "simple";
      User = "ilmarinen";
      Group = "ilmarinen";
      WorkingDirectory = launcherDir;
      # Backstop only: the launcher supervises the worker itself and exits only on SIGTERM or a crash.
      Restart = "always";
      RestartSec = "30";

      # Grant access to Docker socket
      SupplementaryGroups = [ "docker" ];

      # Security settings - Docker needs these relaxed
      NoNewPrivileges = false;
      PrivateDevices = false;
      ProtectKernelTunables = false;

      # systemd does not resolve ExecStart via the unit's PATH, so the runtime is named absolutely here.
      ExecStart = "${dotnetRuntime}/bin/dotnet ${launcherDir}/ilmarinen-workerlauncher.dll";
    };

    environment = {
      ILMARINEN_SERVER_URL = ilmarinenSecrets.serverUrl;
      ILMARINEN_WORKER_KEY = ilmarinenSecrets.workerKey;
      DOTNET_SYSTEM_GLOBALIZATION_INVARIANT = "1";
      # Bundle cache and workspaces land under $HOME/.local/share/ilmarinen/ by default.
      HOME = "/var/lib/ilmarinen-workerlauncher";
      # The runtime must also be on PATH: the launcher spawns the worker as `dotnet <bundle>/ilmarinen-worker.dll`, resolved via PATH.
      PATH = lib.mkForce (lib.makeBinPath ([
        dotnetRuntime
      ] ++ (with pkgs; [
        git
        docker
        bash
        coreutils
      ])));
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

  # Unlike worker-nix, nix-ld is NOT needed: nothing here execs a foreign ELF. The launcher and the downloaded worker both run under the nix-packaged dotnet host, and the bundle's prebuilt libgit2 .so is dlopen'ed inside that process, where its libc needs resolve against the already-loaded nix glibc.

  # Firewall - the launcher and worker only need outbound connections
  networking.firewall.enable = true;

  # Ilmarinen worker user and group
  users.users.ilmarinen = {
    isSystemUser = true;
    group = "ilmarinen";
    home = "/var/lib/ilmarinen-workerlauncher";
    createHome = true;
    extraGroups = [ "docker" ];
    shell = pkgs.bash;
  };

  users.groups.ilmarinen = {};

  # Ensure directories and socket permissions
  systemd.tmpfiles.rules = [
    "d /var/lib/ilmarinen-workerlauncher 0755 ilmarinen ilmarinen -"
    "d ${launcherDir} 0755 ilmarinen ilmarinen -"
    "z /var/run/docker.sock 0660 root docker -"
  ];

  system.stateVersion = "25.05";
}
