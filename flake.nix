{
  # Space Hunter for Linux with Nix:  nix run github:knox-101/space-hunter
  description = "Space Hunter: a lightweight, offline disk cleaner";

  inputs.nixpkgs.url = "github:NixOS/nixpkgs/nixos-unstable";

  outputs = { self, nixpkgs }:
    let
      systems = [ "x86_64-linux" "aarch64-linux" ];
      forAll = f: nixpkgs.lib.genAttrs systems (system: f nixpkgs.legacyPackages.${system});
    in {
      packages = forAll (pkgs: {
        default = pkgs.rustPlatform.buildRustPackage {
          pname = "space-hunter";
          version = pkgs.lib.strings.trim (builtins.readFile ./VERSION);
          src = ./.;
          cargoRoot = "linux";
          buildAndTestSubdir = "linux";
          cargoLock.lockFile = ./linux/Cargo.lock;
          nativeBuildInputs = [ pkgs.pkg-config pkgs.wrapGAppsHook4 ];
          buildInputs = [ pkgs.gtk4 pkgs.fontconfig ];
          doCheck = false;
          postInstall = ''
            install -Dm644 mac/assets/AppIcon.iconset/icon_256x256.png $out/share/icons/hicolor/256x256/apps/space-hunter.png
            mkdir -p $out/share/applications
            cat > $out/share/applications/space-hunter.desktop <<EOF
            [Desktop Entry]
            Type=Application
            Name=Space Hunter
            Comment=Find what fills your disk and clean it safely
            Exec=space-hunter
            Icon=space-hunter
            Categories=System;Utility;
            EOF
          '';
          meta = {
            description = "A lightweight, offline disk cleaner";
            homepage = "https://github.com/knox-101/space-hunter";
            license = pkgs.lib.licenses.gpl3Plus;
            mainProgram = "space-hunter";
            platforms = pkgs.lib.platforms.linux;
          };
        };
      });
      apps = forAll (pkgs: {
        default = { type = "app"; program = "${self.packages.${pkgs.stdenv.hostPlatform.system}.default}/bin/space-hunter"; };
      });
    };
}
