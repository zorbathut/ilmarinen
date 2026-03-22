{ pkgs ? import <nixpkgs> {} }:

let
  dotnet-sdk = pkgs.dotnetCorePackages.sdk_9_0;
  dotnet-runtime = pkgs.dotnetCorePackages.aspnetcore_9_0;

  mkIlmarinenPackage = { pname, projectFile }: pkgs.buildDotnetModule {
    inherit pname projectFile dotnet-sdk dotnet-runtime;
    version = "0.0.0";
    src = ./.;
    nugetDeps = ./nix/deps.json;
  };
in
{
  worker = mkIlmarinenPackage {
    pname = "ilmarinen-worker";
    projectFile = "src/Ilmarinen.Worker/Ilmarinen.Worker.csproj";
  };

  server = mkIlmarinenPackage {
    pname = "ilmarinen-server";
    projectFile = "src/Ilmarinen.Server/Ilmarinen.Server.csproj";
  };

  cli = mkIlmarinenPackage {
    pname = "ilmarinen-cli";
    projectFile = "src/Ilmarinen.Cli/Ilmarinen.Cli.csproj";
  };

  # Run: nix-build -A fetch-deps && ./result nix/deps.json
  fetch-deps = (mkIlmarinenPackage {
    pname = "ilmarinen-fetch-deps";
    projectFile = "Ilmarinen.sln";
  }).passthru.fetch-deps;
}
