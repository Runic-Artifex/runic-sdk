#!/usr/bin/env bash
set -euo pipefail

if [[ $# -ne 1 ]]; then
  echo "Usage: $0 <dotnet-runic-executable>" >&2
  exit 2
fi

tool="$1"
migration_root="$(mktemp -d /tmp/runic-tool-migration.XXXXXXXXXX)"
project="$migration_root/Legacy.csproj"

cleanup() {
  case "$migration_root" in
    /tmp/runic-tool-migration.*) rm -rf -- "$migration_root" ;;
    *) echo "Refusing to remove unexpected path: $migration_root" >&2 ;;
  esac
}
trap cleanup EXIT

cat > "$project" <<'XML'
<Project><ItemGroup>
  <PackageReference Include="RunicToolkit.Hosting.CsWebUi" Version="0.1.0" />
  <PackageReference Include="RunicToolkit.Hosting.CsWebUi.App"><Version>0.1.0</Version></PackageReference>
  <PackageReference Include="RunicToolkit.Hosting.CsWebUi.ApplicationBridge" />
  <PackageReference Include="RunicToolkit.Hosting.CsWebUi.ApplicationBridge.Client" />
</ItemGroup></Project>
XML

original="$(<"$project")"

if check_output="$("$tool" migrate --project "$project" --check 2>&1)"; then
  echo "migrate --check unexpectedly accepted a legacy CsWebUi package reference." >&2
  exit 1
else
  check_exit=$?
fi
if [[ "$check_exit" -eq 0 ]] || ! grep -Fq 'RAPPMIG001' <<< "$check_output" || ! grep -Fq 'RunicToolkit.Hosting.CsWebUi -> Runic.Application.Desktop' <<< "$check_output"; then
  echo "migrate --check did not report the exact CsWebUi package migration." >&2
  printf '%s\n' "$check_output" >&2
  exit 1
fi
[[ "$(<"$project")" == "$original" ]]

dry_run_output="$("$tool" migrate --project "$project" --dry-run)"
grep -Fq 'RAPPMIG002: dry-run only' <<< "$dry_run_output"
[[ "$(<"$project")" == "$original" ]]

apply_output="$("$tool" migrate --project "$project" --apply)"
grep -Fq 'RAPPMIG002: applied package-reference migration' <<< "$apply_output"
grep -Fq '<PackageReference Include="Runic.Application.Desktop" Version="0.2.0"' "$project"
grep -Fq '<PackageReference Include="Runic.Application.Desktop"><Version>0.2.0</Version>' "$project"
grep -Fq 'RunicToolkit.Hosting.CsWebUi.ApplicationBridge.Client' "$project"

clean_check_output="$("$tool" migrate --project "$project" --check)"
grep -Fq 'RAPPMIG000: no legacy application references or properties were found.' <<< "$clean_check_output"
