#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
sdk_root="$(cd -- "${script_dir}/../.." && pwd)"
fixture="${sdk_root}/tests/fixtures/application/PostMvvmDiscovery/PostMvvmDiscovery.csproj"
owner="${RUNIC_POST_MVVM_BUILD_OWNER:-$(uuidgen | tr '[:upper:]' '[:lower:]' | tr -d '-')}"
key="${RUNICP_MVVM_OUTPUT_KEY:-ordinary}"
for argument in "$@"; do
  case "${argument}" in
    -p:OutputPath=*|/p:OutputPath=*|--property:OutputPath=*|-p:IntermediateOutputPath=*|/p:IntermediateOutputPath=*|--property:IntermediateOutputPath=*)
      printf '%s\n' 'RUNICPM010: The internal post-MVVM discovery fixture does not accept global OutputPath or IntermediateOutputPath overrides because they bypass its build-owner isolation.' >&2
      exit 2
      ;;
  esac
done

common=("-p:RunicPostMvvmDiscoveryBuildOwner=${owner}" "-p:RunicPostMvvmDiscoveryOwnerDriver=true" "-p:RunicPostMvvmDiscoveryOutputKey=${key}")

dotnet restore "${fixture}" --nologo -m:1 /nr:false "${common[@]}"
dotnet build "${fixture}" --no-restore --nologo -m:1 /nr:false "${common[@]}" "$@"
