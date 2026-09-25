#!/usr/bin/env bash
set -euo pipefail

script_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
sdk_root="$(cd -- "${script_dir}/../.." && pwd)"
fixture="${sdk_root}/tests/fixtures/application/PostMvvmDiscovery/PostMvvmDiscovery.csproj"
owner="${RUNIC_POST_MVVM_BUILD_OWNER:-$(uuidgen | tr '[:upper:]' '[:lower:]' | tr -d '-')}"
key="${RUNICP_MVVM_OUTPUT_KEY:-ordinary}"
for argument in "$@"; do
  case "${argument,,}" in
    -p:outputpath=*|/p:outputpath=*|--property:outputpath=*|\
    -p:intermediateoutputpath=*|/p:intermediateoutputpath=*|--property:intermediateoutputpath=*|\
    -p:baseoutputpath=*|/p:baseoutputpath=*|--property:baseoutputpath=*|\
    -p:baseintermediateoutputpath=*|/p:baseintermediateoutputpath=*|--property:baseintermediateoutputpath=*|\
    -p:msbuildprojectextensionspath=*|/p:msbuildprojectextensionspath=*|--property:msbuildprojectextensionspath=*|\
    -p:projectassetsfile=*|/p:projectassetsfile=*|--property:projectassetsfile=*|\
    -p:outdir=*|/p:outdir=*|--property:outdir=*|\
    -p:restoreoutputpath=*|/p:restoreoutputpath=*|--property:restoreoutputpath=*)
      printf '%s\n' 'RUNICPM010: The internal post-MVVM discovery fixture does not accept global output, intermediate, or restore path overrides because they bypass its build-owner isolation.' >&2
      exit 2
      ;;
  esac
done

common=("-p:RunicPostMvvmDiscoveryBuildOwner=${owner}" "-p:RunicPostMvvmDiscoveryOwnerDriver=true" "-p:RunicPostMvvmDiscoveryOutputKey=${key}")

dotnet restore "${fixture}" --nologo -m:1 /nr:false "${common[@]}"
dotnet build "${fixture}" --no-restore --nologo -m:1 /nr:false "${common[@]}" "$@"
