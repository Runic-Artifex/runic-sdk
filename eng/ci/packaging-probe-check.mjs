// Checks the receipt that tests/native/Runic.Packaging.Probe writes when its installed package opens its URL scheme
// (see the packaging job in .github/workflows/ci.yml). Usage: bun eng/ci/packaging-probe-check.mjs <receipt> <platform>
// [unpackaged|files], where platform is windows, macos, flatpak, appimage, deb or rpm; "unpackaged" checks a copy run
// outside its package, "files" the receipt of a package opened with two files at once (Windows).
import { existsSync, readFileSync } from "node:fs";

const [path, platform, mode] = process.argv.slice(2);
const receipt = JSON.parse(readFileSync(path, "utf8"));
const failures = [];
const expect = (condition, message) => { if (!condition) failures.push(message); };
const id = "org.runic.tests.PackagingProbe";

expect(receipt.applicationId === id, `applicationId is ${receipt.applicationId}`);
expect(receipt.applicationName === "Runic Packaging Probe", `applicationName is ${receipt.applicationName}`);
// The Windows shell normalizes the URL to runic-packaging-probe://receipt/?path=...
if (mode === "files")
  expect(receipt.url === null, `a file activation carries the URL ${receipt.url}`);
else
  expect(/^runic-packaging-probe:\/\/receipt\/?\?path=/i.test(receipt.url ?? ""), `the probe was opened with ${receipt.url}`);
expect(receipt.contentRootHasIndex === true, `the content root ${receipt.contentRoot} has no index.html`);
if (mode === "unpackaged") {
  expect(receipt.packageIdentity === null, `an unpackaged copy has the package identity ${receipt.packageIdentity}`);
  expect(receipt.loginStartup !== "Unavailable(BackendUnavailable)", `unpackaged login startup is ${receipt.loginStartup}`);
  if (platform === "windows") expect(receipt.openRegistration === "Success(False)", `unpackaged registration reports ${receipt.openRegistration}`);
} else if (mode === "files") {
  // Two files selected together reach one process, through the activation's files (MultiSelectModel="Player").
  const names = (receipt.files ?? []).map(file => file.split(/[\\/]/).pop()).sort();
  expect(names.join(",") === "first.runicprobe,second.runicprobe", `the file activation delivered ${JSON.stringify(receipt.files)}`);
  expect(receipt.activationKind === 3, `the activation kind is ${receipt.activationKind}, not File`);
} else if (platform === "windows") {
  expect(new RegExp(`^${id.replace(/\./g, "\\.")}_[a-z0-9]{13}!App$`).test(receipt.packageIdentity ?? ""),
    `the MSIX package's AppUserModelID is ${receipt.packageIdentity}`);
  // Runic shows toasts under the package's AppUserModelID, which Windows registered at install.
  expect(receipt.notifications === "Success", `notifications in the package report ${receipt.notifications}`);
  // The package's desktop:StartupTask (LoginStartup), off at install, turned on and off again.
  expect(receipt.loginStartup === "Success(False)", `login startup in the package reports ${receipt.loginStartup}`);
  expect(receipt.loginStartupCycle === "Success,Success(True),Success,Success(False)", `the startup task cycle reports ${receipt.loginStartupCycle}`);
  // The package declares its scheme; the registration API leaves it to the package.
  expect(receipt.openRegistration === "Unavailable(BackendUnavailable)", `registration in the package reports ${receipt.openRegistration}`);
  expect(receipt.activationKind === 4, `the activation kind is ${receipt.activationKind}, not Protocol`);
} else if (platform === "macos") {
  expect(receipt.packageIdentity === id, `the bundle identifier is ${receipt.packageIdentity}`);
  expect(/\.app\/Contents\/Resources\/www\/?$/.test(receipt.contentRoot ?? ""), `the bundle's content root is ${receipt.contentRoot}`);
  // probe-data.txt is in Contents/Resources; the link in Contents/MacOS keeps it next to the executable.
  expect(receipt.extraFile === "probe", `the data file next to the executable reads ${receipt.extraFile}`);
} else if (platform === "flatpak") {
  expect(receipt.packageIdentity === id, `FLATPAK_ID is ${receipt.packageIdentity}`);
  expect(receipt.contentRoot === "/app/lib/Runic.Packaging.Probe/www", `the Flatpak's content root is ${receipt.contentRoot}`);
} else if (platform === "deb" || platform === "rpm") {
  // Installed in /usr/lib/<id>, started through the /usr/bin link.
  expect(receipt.packageIdentity === null, `the ${platform} package reports the package identity ${receipt.packageIdentity}`);
  expect(receipt.contentRoot === "/usr/lib/org.runic.tests.PackagingProbe/www", `the ${platform}'s content root is ${receipt.contentRoot}`);
} else if (platform === "appimage") {
  // Extracted or mounted somewhere temporary, with the AppDir's layout.
  expect(receipt.packageIdentity === null, `the AppImage reports the package identity ${receipt.packageIdentity}`);
  expect(/\/usr\/lib\/org\.runic\.tests\.PackagingProbe\/www\/?$/.test(receipt.contentRoot ?? ""), `the AppImage's content root is ${receipt.contentRoot}`);
} else {
  failures.push(`unknown platform ${platform}`);
}
if (receipt.diagnosticsLog && mode === "unpackaged" && platform !== "flatpak") {
  expect(existsSync(receipt.diagnosticsLog) && readFileSync(receipt.diagnosticsLog, "utf8").includes("packaging-probe"),
    `${receipt.diagnosticsLog} does not hold the probe's diagnostic`);
}
if (failures.length > 0) {
  console.error(`The ${platform} ${mode ?? "package"} receipt failed:\n- ${failures.join("\n- ")}\n${JSON.stringify(receipt, null, 2)}`);
  process.exit(1);
}
console.log(`PACKAGING_OK|${platform}|${mode ?? "package"}|${receipt.packageIdentity ?? "unpackaged"}`);
