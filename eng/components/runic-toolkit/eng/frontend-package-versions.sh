#!/usr/bin/env bash

runic_resolve_frontend_package_versions() {
  local release_train_version="$1"
  local application_version="$2"
  local source_frontend_integrations="$3"
  local selected_svelte_version="$4"
  local selected_vite_version="$5"
  local selected_desktop_version="$6"

  bridge_npm_version="$application_version"
  svelte_release_version="${selected_svelte_version:-$release_train_version}"
  vite_release_version="${selected_vite_version:-$release_train_version}"
  desktop_release_version="${selected_desktop_version:-$release_train_version}"

  if [[ "$source_frontend_integrations" == "1" ]]; then
    svelte_release_version="$release_train_version"
    vite_release_version="$release_train_version"
    desktop_release_version="$release_train_version"
  fi

  export APPLICATION_BRIDGE_NPM_VERSION="$bridge_npm_version"
  export RUNIC_SVELTE_NPM_VERSION="$svelte_release_version"
  export RUNIC_VITE_NPM_VERSION="$vite_release_version"
  export RUNIC_DESKTOP_NPM_VERSION="$desktop_release_version"
}
