# passthru.fixedOutputHashes of buildRunicApplication: a program that prints the hashes of every fixed-output download
# the build makes from an argument (frontend.hash, dotnetTools.hashes), ready to paste:
#
#   nix run .#default.fixedOutputHashes
#
# It builds each download once more under lib.fakeHash, all in one `nix-store --realise --keep-going`, and reads the
# hash that Nix reports for each from the mismatch. A build reports one mismatch and stops, a missing tool hash fails
# evaluation, and a Bun install whose lock file changed under an old hash names an output Nix already has; this
# finds them all in one run, whatever the hashes in the package say.
{
  lib,
  writeShellApplication,
  gawk,
}:
{
  pname,
  # { <system> = <fixed-output derivation>; } of the frontend's dependencies under lib.fakeHash, and whether
  # frontend.hash names systems ({ <system> = <hash>; }) or is one hash.
  frontend ? { },
  frontendPerSystem ? false,
  # { <package ID> = <fixed-output derivation>; } of the tools of dotnetTools under lib.fakeHash.
  tools ? { },
}:
let
  # The derivation file alone: building the program does not build the download.
  drvFile = download: builtins.unsafeDiscardOutputDependency download.drvPath;
  entries =
    lib.mapAttrsToList (system: download: {
      group = "frontend";
      key = system;
      drv = drvFile download;
    }) frontend
    ++ lib.mapAttrsToList (id: download: {
      group = "tools";
      key = lib.strings.escapeNixIdentifier id;
      drv = drvFile download;
    }) tools;
  line = entry: lib.escapeShellArg "${entry.group} ${entry.key} ${entry.drv}";
in
writeShellApplication {
  name = "${pname}-fixed-output-hashes";
  runtimeInputs = [ gawk ];
  # nix-store is the user's own, which talks to their store and daemon.
  text = ''
    entries=(${lib.concatMapStringsSep " " line entries})
    if (( ''${#entries[@]} == 0 )); then
      echo "${pname} downloads nothing by a hash of its arguments (frontend.hash, dotnetTools.hashes)." >&2
      exit 0
    fi
    if ! command -v nix-store > /dev/null; then
      echo "error: nix-store is not on PATH." >&2
      exit 1
    fi
    log=$(mktemp)
    trap 'rm -f "$log"' EXIT
    drvs=()
    for entry in "''${entries[@]}"; do drvs+=("''${entry##* }"); done
    echo "Fetching ''${#drvs[@]} download(s) of ${pname} under lib.fakeHash to learn their hashes..." >&2
    nix-store --realise --keep-going "''${drvs[@]}" > /dev/null 2> "$log" || true

    declare -A got
    failed=0
    for entry in "''${entries[@]}"; do
      drv=''${entry##* }
      # error: hash mismatch in fixed-output derivation '/nix/store/...drv':
      #          specified: sha256-AAAA...
      #             got:    sha256-...
      hash=$(awk -v drv="$drv" -v q="'" 'index($0, "derivation " q drv q) { found = 1; next } found && $1 == "got:" { print $2; exit }' "$log")
      if [[ -z $hash ]]; then
        echo "error: no hash for ''${entry% *}: building $drv did not report one." >&2
        failed=1
      fi
      got[$entry]=$hash
    done
    if (( failed )); then
      cat "$log" >&2
      exit 1
    fi

    print_group() {
      local group=$1 entry rest
      for entry in "''${entries[@]}"; do
        [[ $entry == "$group "* ]] || continue
        rest=''${entry#"$group "}
        printf '  %s = "%s";\n' "''${rest%% *}" "''${got[$entry]}"
      done
    }
    ${lib.optionalString (frontend != { }) (
      if frontendPerSystem then
        ''
          echo "frontend.hash = {"
          print_group frontend
          echo "};"
        ''
      else
        ''
          for entry in "''${entries[@]}"; do
            [[ $entry == "frontend "* ]] && printf 'frontend.hash = "%s";\n' "''${got[$entry]}"
          done
        ''
    )}
    ${lib.optionalString (tools != { }) ''
      echo "dotnetTools.hashes = {"
      print_group tools
      echo "};"
    ''}
  '';
}
