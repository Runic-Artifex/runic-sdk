import { expect, test } from "bun:test";
import { readFileSync } from "node:fs";
import { resolve } from "node:path";
import { category, check, fileOs, fixtures, folderBytes, frameworkDependentCheck, measurement, metrics, nativeFormat, otherOsFiles } from "../scaling/aot-size/measure.mjs";
import { viewModelsSource } from "../scaling/aot-size/fixture.mjs";
import { budgetChanges, report, suiteCases } from "../scaling/report.mjs";
import { format, updatedBudgets } from "../scaling/run.mjs";

const root = resolve(import.meta.dir, "../..");

test("the report lists budget changes against the base, improvements and known costs", () => {
  const base = { a: { class: "O(1)", sizes: [1, 4], limit: 2 }, gone: { class: "O(n)", sizes: [1, 4], limit: 9 } };
  const budgets = {
    a: { class: "O(1)", sizes: [1, 4], limit: 3 },
    b: { class: "O(n)", sizes: [1, 4], limit: 8, known: "O(n) per event", issue: "sdk#1" },
  };
  expect(budgetChanges(base, budgets)).toEqual([
    { name: "a", change: "limit", before: "2", after: "3" },
    { name: "b", change: "added", before: "", after: "class O(n), sizes 1 → 4, limit 8" },
    { name: "gone", change: "removed", before: "9", after: "" },
  ]);
  const results = {
    a: { class: "O(1)", sizes: [1, 4], values: [3, 3], limit: 3, status: "ok" },
    b: { class: "O(n)", sizes: [1, 4], values: [1, 4], limit: 8, status: "improved", message: "below 75 % of its limit 8" },
  };
  const { markdown, problems } = report({ results, budgets, base, requireAll: true });
  expect(problems).toEqual([]);
  expect(markdown).toContain("- `b`: O(n) per event (O(n); sdk#1)");
  expect(markdown).toContain("- `b`: below 75 % of its limit 8");
  expect(markdown).toContain("| `a` | limit | 2 | 3 |");
});

test("the report fails on a budget entry without a result and on a result without an entry", () => {
  const budgets = { a: { class: "O(1)", sizes: [1, 4], limit: 1 } };
  const results = { b: { class: "O(1)", sizes: [1, 4], values: [1, 1], limit: 1, status: "ok" } };
  expect(report({ results, budgets, requireAll: true }).problems).toHaveLength(2);
  expect(report({ results: {}, budgets, requireAll: false }).problems).toEqual([]);
});

test("an update records the values and the limit, with the margin, and keeps the class and sizes", () => {
  const budgets = {
    schema: 1,
    cases: {
      count: { class: "O(n)", sizes: [1, 4], limit: 1, measured: [1, 1], note: "kept" },
      bytes: { class: "O(1)", sizes: [1, 4], margin: 0.25 },
      unmeasured: { class: "O(1)", sizes: [1, 4], limit: 5 },
      decided: { class: "O(n)", sizes: [1, 4], limit: 128, fixedLimit: true },
      folder: { class: "total", sizes: [0, 0], margin: 0.02, files: { "gone.dll": 1 } },
    },
  };
  const results = {
    count: { values: [2, 8] }, bytes: { values: [100, 100] }, decided: { values: [19, 76] },
    // An fdd/ result carries the bytes per file, which replace the recorded ones.
    folder: { values: [300, 300], files: { "a.dll": 200, "b.dll": 100 } },
  };
  expect(updatedBudgets(budgets, results).cases).toEqual({
    count: { class: "O(n)", sizes: [1, 4], limit: 8, measured: [2, 8], note: "kept" },
    bytes: { class: "O(1)", sizes: [1, 4], limit: 125, measured: [100, 100], margin: 0.25 },
    unmeasured: { class: "O(1)", sizes: [1, 4], limit: 5 },
    // A fixed limit is a decision: the update records the values and keeps it.
    decided: { class: "O(n)", sizes: [1, 4], limit: 128, measured: [19, 76], fixedLimit: true },
    folder: { class: "total", sizes: [0, 0], limit: 306, measured: [300, 300], margin: 0.02, files: { "a.dll": 200, "b.dll": 100 } },
  });
});

test("budgets.json is written as bun run scaling --update writes it", () => {
  const text = readFileSync(resolve(root, "tests/scaling/budgets.json"), "utf8");
  expect(format(JSON.parse(text))).toBe(text);
});

test("the managed report and the NativeAOT size budgets each take their own entries of budgets.json", () => {
  const cases = { "bridge/a": { class: "O(1)" }, "aot/bridge/b": { class: "O(n)" }, "aot/c": { class: "total" }, "fdd/d": { class: "total" } };
  expect(Object.keys(suiteCases(cases, "managed"))).toEqual(["bridge/a"]);
  expect(Object.keys(suiteCases(cases, "aot-size"))).toEqual(["aot/bridge/b", "aot/c", "fdd/d"]);
  const budgets = JSON.parse(readFileSync(resolve(root, "tests/scaling/budgets.json"), "utf8")).cases;
  expect(Object.keys(suiteCases(budgets, "aot-size")).sort()).toEqual(Object.keys(metrics).sort());
  for (const [name, entry] of Object.entries(suiteCases(budgets, "aot-size"))) {
    expect(entry.limit, name).toBeNumber();
    // #901 is fixed (A1-b) and sdk#1151 too: no aot/ or fdd/ entry is a known cost, and none exempts a file.
    expect(entry.known, name).toBeUndefined();
    expect(entry.exempt, name).toBeUndefined();
  }
  // FDD-6 (fdd-size-0.22.md §7.2): no file of another OS in a linux-x64 framework-dependent publish, and each folder
  // at its measurement plus 2 % with the bytes per file that name a regression.
  for (const name of ["fdd/other-os-bytes", "fdd/desktop/other-os-bytes"]) {
    expect(budgets[name].limit, name).toBe(0);
    expect(budgets[name].margin, name).toBeUndefined();
  }
  for (const name of ["fdd/folder-bytes", "fdd/desktop/folder-bytes"]) {
    const entry = budgets[name];
    expect(entry.class, name).toBe("total");
    expect(entry.margin, name).toBe(0.02);
    expect(entry.limit, name).toBe(Math.ceil(entry.measured[1] * 1.02));
    expect(Object.values(entry.files).reduce((sum, bytes) => sum + bytes, 0), name).toBe(entry.measured[1]);
  }
  // D064.24: no generated-code support type is closed over a ViewModel.
  expect(budgets["aot/bridge/generated-generic-types"].limit).toBe(0);
  // D072.2: no HTTP client or TLS code in the CS-WebUI or Desktop fixture on linux-x64.
  for (const name of Object.keys(metrics).filter(name => /^aot\/(desktop\/)?framework\//.test(name))) {
    expect(budgets[name].limit, name).toBe(0);
    expect(budgets[name].margin, name).toBeUndefined();
  }
  for (const name of Object.keys(metrics)) expect(Object.keys(fixtures), name).toContain(metrics[name].fixture);
});

test("the NativeAOT size measurement counts the records that name a fixture ViewModel", () => {
  const records = [
    "Types\t100\tRunic.Application.Views\tRunic.Application.Views.Generated.PropertyDescriptor<AotSize.Models.Vm0>",
    "Types\t60\tSystem.Private.CoreLib\tSystem.Collections.Generic.Dictionary<System.String, Runic.Application.Views.Generated.PropertyDescriptor<AotSize.Models.Vm12>>",
    "Types\t40\tSystem.Private.CoreLib\tSystem.Func<AotSize.Models.Vm3, System.Object>",
    // Not a fixture ViewModel: the shell, a window type, and a Bridge over the shell.
    "Types\t500\tAotSize\tAotSize.Models.Vm0Window",
    "Types\t500\tRunic.Application.Views\tRunic.Application.Views.Generated.PropertyDescriptor<AotSize.Models.Shell>",
    "Methods\t7\tAotSize\tAotSize.Models.Vm0.get_Step",
    // D072.15: the methods of a fixture ViewModel's generated Bridge and its nested types, not the shell's.
    "Methods\t30\tAotSize\tRunic.Application.Views.Generated.Bridges.Vm0Bridge..ctor",
    "Methods\t40\tAotSize\tRunic.Application.Views.Generated.Bridges.Vm12Bridge+Members.Get(Object,Int32)",
    "Methods\t50\tAotSize\tRunic.Application.Views.Generated.Bridges.ShellBridge..ctor",
    "FrozenObjects\t24\tSystem.Net.Http\tSystem.Byte[] in System.Net.Http.HttpClient",
    "RvaFields\t3\tRunic.Application.Views\tRunic.Application.Views.Data",
    // Blobs have no owner; DeduplicatedMethods carry node ids, not sizes.
    "Blobs\t9\t\tDehydratedData",
    "DeduplicatedMethods\t123456\tSystem.Net.Http\tSystem.Net.Http.HttpClient.Send",
  ].join("\n");
  const build = measurement(1000, records);
  expect(build).toEqual({
    executableBytes: 1000, viewModelTypes: 3, viewModelTypeBytes: 200, generatedGenericTypes: 2, viewModelMethodBytes: 7, viewModelBridgeMethods: 2,
    types: 5, assemblyBytes: { "Runic.Application.Views": 603, "System.Private.CoreLib": 100, AotSize: 627, "System.Net.Http": 24 },
  });
  expect(metrics["aot/framework/System.Net.Http"].value(build)).toBe(24);
  expect(metrics["aot/framework/System.Net.Security"].value(build)).toBe(0);
  expect(metrics["aot/assembly/Runic.Application.Views"].value(build)).toBe(603);
  expect(metrics["aot/bridge/view-model-methods"].value(build)).toBe(2);
  const source = viewModelsSource(2);
  expect(source.match(/public sealed partial class \w+ : ObservableObject/g)).toEqual([
    "public sealed partial class Shell : ObservableObject", "public sealed partial class Vm0 : ObservableObject",
    "public sealed partial class Vm1 : ObservableObject"]);
  expect(source).toContain("public ViewRequest<FocusRow> Focus { get; } = new();");
});

test("the NativeAOT size check adds its noise before the class factor and at the larger size", () => {
  // #924: section alignment put K = 1 at 20960 while K = 9 stayed at 225328; 9 x 20960 x 1.02 + 32768 failed it.
  const executable = { class: "O(n)", sizes: [1, 9], limit: 229835, margin: 0.02, noise: 8192 };
  expect(check("e", executable, [20960, 225328]).result.status).toBe("ok");
  expect(check("e", executable, [20960, 280000]).failure).toContain("grows faster than O(n)");
});

test("the NativeAOT size limit allows the metric's alignment noise", () => {
  // sdk#1120's merge-queue run: the same per-ViewModel code measured 237632 at K = 9 against a limit of 233997 recorded
  // from a build one 4 KiB alignment step lower; the limit allows the entry's noise as the growth check does.
  const executable = { class: "O(n)", sizes: [1, 9], limit: 233997, margin: 0.02, noise: 8192 };
  expect(check("e", executable, [29152, 237632]).result.status).toBe("ok");
  expect(check("e", executable, [29152, 242190]).failure).toContain("over its limit 233997 (+ 8192 noise)");
  const exact = { class: "O(n)", sizes: [1, 9], limit: 900 };
  expect(check("x", exact, [110, 901]).failure).toBe("x is 901 at 9, over its limit 900.");
});

test("the NativeAOT size check applies the class with its slack, the limit and the total's margin", () => {
  const perViewModel = { class: "O(n)", sizes: [1, 9], limit: 1000, slack: 50 };
  expect(check("a", perViewModel, [100, 940]).result.status).toBe("ok");
  expect(check("a", perViewModel, [100, 960]).failure).toContain("grows faster than O(n)");
  expect(check("a", perViewModel, [100, 960], true).failure).toBeNull();
  expect(check("a", { ...perViewModel, limit: 900 }, [100, 940]).failure).toContain("over its limit 900");
  expect(check("a", perViewModel, [10, 40]).result.message).toBe("grows slower than O(n); below 75 % of its limit 1000");
  const total = { class: "total", sizes: [0, 9], limit: 1020, margin: 0.02 };
  expect(check("t", total, [600, 1000]).result.status).toBe("ok");
  expect(check("t", total, [600, 1021]).failure).toContain("over its limit 1020");
  expect(check("t", { ...total, class: "O(n log n)" }, [1, 1]).failure).toContain("unknown class");
});

// A PE image's first bytes: a managed assembly has a CLI header (data directory 14), a native one has none.
function peHeader(managed) {
  const header = Buffer.alloc(512);
  header.write("MZ", 0, "latin1");
  header.writeUInt32LE(0x80, 0x3c);
  header.writeUInt32LE(0x00004550, 0x80);
  const optional = 0x80 + 24;
  header.writeUInt16LE(0x20b, optional);
  header.writeUInt32LE(managed ? 72 : 0, optional + 112 + 14 * 8 + 4);
  return header;
}

test("the framework-dependent publish's files are attributed as attribute.py does", () => {
  expect(nativeFormat(peHeader(true))).toBeNull();
  expect(nativeFormat(peHeader(false))).toBe("pe");
  expect(nativeFormat(Buffer.from([0x7f, 0x45, 0x4c, 0x46, 2, 1]))).toBe("elf");
  expect(nativeFormat(Buffer.from([0xcf, 0xfa, 0xed, 0xfe]))).toBe("macho");
  expect(nativeFormat(Buffer.from("{}"))).toBeNull();
  expect(category("AotSize", "AotSize", true)).toBe("apphost");
  expect(category("AotSize.dll", "AotSize")).toBe("app");
  expect(category("Runic.Desktop.dll", "AotSize")).toBe("runic");
  expect(category("Runic.Desktop.xml", "AotSize")).toBe("xml-doc");
  expect(category("BridgeCodegen.deps.json", "AotSize")).toBe("tool");
  expect(category("AotSize.deps.json", "AotSize")).toBe("deps-runtimeconfig");
  expect(category("www/runic-views.js", "AotSize")).toBe("www");
  expect(category("libwebui-2.so", "AotSize", true)).toBe("native");
  expect(category("Microsoft.Extensions.DependencyInjection.dll", "AotSize")).toBe("ms-extensions");
  expect(category("Tmds.DBus.Protocol.dll", "AotSize")).toBe("third-party");
  // FD5 keeps the XML docs, referenced projects' PDBs and BridgeCodegen out of project-reference publishes, so every
  // file counts, as in a package consumer's publish; one that comes back makes the folder grow.
  expect(folderBytes([
    { path: "AotSize.dll", bytes: 10 }, { path: "Runic.Views.xml", bytes: 100 }, { path: "AotSize.pdb", bytes: 1000 }, { path: "www/a.js", bytes: 5 },
  ])).toBe(1115);
});

test("a linux-x64 framework-dependent publish counts the files that FD1 assigns to another OS", () => {
  const file = (path, native = null) => ({ path, bytes: 1, native });
  expect(fileOs(file("Runic.Platform.Windows.dll"))).toBe("windows");
  expect(fileOs(file("Runic.Platform.MacOS.dll"))).toBe("macos");
  expect(fileOs(file("Runic.Platform.Linux.Portal.dll"))).toBe("linux");
  expect(fileOs(file("Tmds.DBus.Protocol.dll"))).toBe("linux");
  expect(fileOs(file("GirCore.Gtk-4.0.dll"))).toBe("linux");
  expect(fileOs(file("runtimes/win-x64/native/WebView2Loader.dll", "pe"))).toBe("windows");
  expect(fileOs(file("runtimes/osx-arm64/native/libx.dylib", "macho"))).toBe("macos");
  expect(fileOs(file("runic_server.dll", "pe"))).toBe("windows");
  expect(fileOs(file("libwebui-2.so", "elf"))).toBe("linux");
  // Not another OS's: shared assemblies, a managed .dll, and Runic.Desktop.Gtk4, which FD1 copies everywhere (sdk#1150).
  for (const path of ["Runic.Platform.dll", "Runic.Platform.Runtime.dll", "CsWebUi.Native.dll", "Runic.Desktop.Gtk4.dll"]) expect(fileOs(file(path)), path).toBeNull();
  const files = [file("Runic.Platform.dll"), file("Runic.Platform.Linux.dll"), file("Runic.Platform.Windows.dll"), file("libwebui-2.so", "elf"), file("x.dylib", "macho")];
  expect(otherOsFiles(files, "linux").map(entry => entry.path)).toEqual(["Runic.Platform.Windows.dll", "x.dylib"]);
  expect(otherOsFiles(files, "windows").map(entry => entry.path)).toEqual(["Runic.Platform.Linux.dll", "libwebui-2.so", "x.dylib"]);
});

test("a framework-dependent metric that fails names the files behind it", () => {
  const build = { files: [
    { path: "Runic.Platform.Windows.dll", bytes: 1945088, native: null },
    { path: "Runic.Desktop.dll", bytes: 1600000, native: null },
    { path: "Runic.Views.xml", bytes: 999999, native: null },
    { path: "runtimes/win-x64/native/WebView2Loader.dll", bytes: 160000, native: "pe" },
  ] };
  // A non-zero other-OS byte count fails its limit 0 and names every such file.
  const otherOs = { class: "total", sizes: [0, 0], limit: 0 };
  const failed = frameworkDependentCheck("fdd/desktop/other-os-bytes", otherOs, metrics["fdd/desktop/other-os-bytes"].files(build, otherOs));
  expect(failed.result.status).toBe("failed");
  expect(failed.failure).toBe("fdd/desktop/other-os-bytes is 2105088 at 0, over its limit 0. Files of another OS: Runic.Platform.Windows.dll (1945088 B), runtimes/win-x64/native/WebView2Loader.dll (160000 B).");
  const none = frameworkDependentCheck("fdd/other-os-bytes", otherOs, []);
  expect(none.result.status).toBe("ok");
  expect(none.result.files).toBeUndefined();
  // A folder over its limit names the files that grew against the recorded bytes per file, new ones included, largest
  // growth first: an XML doc that a publish carries again is named too.
  const folder = { class: "total", sizes: [0, 0], limit: 3600000, margin: 0.02, files: { "Runic.Platform.Windows.dll": 1945088, "Runic.Desktop.dll": 1500000 } };
  const grown = frameworkDependentCheck("fdd/desktop/folder-bytes", folder, metrics["fdd/desktop/folder-bytes"].files(build, folder));
  expect(grown.result.values).toEqual([4705087, 4705087]);
  expect(grown.failure).toBe("fdd/desktop/folder-bytes is 4705087 at 0, over its limit 3600000. Files that grew: Runic.Views.xml new, 999999 B, runtimes/win-x64/native/WebView2Loader.dll new, 160000 B, Runic.Desktop.dll 1500000 → 1600000 B (+100000).");
  expect(grown.result.files["Runic.Views.xml"]).toBe(999999);
  expect(frameworkDependentCheck("fdd/desktop/folder-bytes", { ...folder, limit: 4800000 }, metrics["fdd/desktop/folder-bytes"].files(build, folder)).result.status).toBe("ok");
  // --update records instead of failing.
  expect(frameworkDependentCheck("fdd/desktop/folder-bytes", folder, metrics["fdd/desktop/folder-bytes"].files(build, folder), true).failure).toBeNull();
});
