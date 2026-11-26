- The frontend install's record moved from the stamp `obj/runic-bridge-frontend-install.stamp`, which still exists, to
  the hash `node_modules/.runic-install` in the folder the install runs in: the install runs again when the install
  command, the lock file or a `package.json` changed, or when `node_modules` is gone, not when a file's time changed.
  With `RunicApplicationFrontendInstallEnabled=false` the build looks for no JavaScript workspace and selects the package
  manager from the frontend's folder, as before. See
  [JavaScript workspaces](../../../packages/dotnet/Runic.Application.Views/README.md#javascript-workspaces).
