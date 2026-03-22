{ pkgs ? import <nixpkgs> {} }:

pkgs.mkShell {
  buildInputs = with pkgs; [
    dotnetCorePackages.sdk_9_0
  ];
}
