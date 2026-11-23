{
  description = "Depot - self-hosted media organizer for qBittorrent and Jellyfin";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";

  outputs = { nixpkgs, ... }:
    let
      forAllSystems = nixpkgs.lib.genAttrs [ "x86_64-linux" "aarch64-linux" ];
    in
    {
      devShells = forAllSystems (system:
        let
          pkgs = import nixpkgs { inherit system; };
        in
        {
          default = pkgs.mkShell {
            packages = with pkgs; [
              dotnet-sdk_10
              dotnet-ef
              nodejs
              sqlite
              curl
              jq
            ];
          };
        });
    };
}
