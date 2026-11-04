{
  description = "Depot - Self-hosted media download manager";

  inputs = {
    nixpkgs.url = "github:NixOS/nixpkgs/nixos-26.05";
  };

  outputs = { self, nixpkgs, ... }:
    let
      systems = [
        "x86_64-linux"
        "aarch64-linux"
      ];

      forAllSystems = nixpkgs.lib.genAttrs systems;
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
              git
              curl
              jq
              sqlite
            ];

            shellHook = ''
              echo "Depot development environment"
              echo "-----------------------------"
              dotnet --version
              echo "HTML, CSS and JavaScript: native browser support"
            '';
          };
        }
      );
    };
}

