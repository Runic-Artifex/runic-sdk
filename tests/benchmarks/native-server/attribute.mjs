// Attributes the compiled code of a NativeAOT executable to assembly families, from the ILC map file
// (-p:IlcGenerateMapFile=true) and the response file next to it, which lists the input assemblies (X1).
//
//   node tests/benchmarks/native-server/attribute.mjs obj/.../native/App.map.xml
//
// Counts method code, GC info and exception-handling info by the assembly that owns the method's type; data,
// metadata and type structures are not attributed. Prints JSON: bytes per family and the total counted.
import { readFileSync } from "node:fs";

const [map] = process.argv.slice(2);
const response = readFileSync(map.replace(/\.map\.xml$/, ".ilc.rsp"), "utf8");
// ILC mangles an assembly name by replacing what is not a letter or digit with an underscore.
const assemblies = [...response.matchAll(/^-r:(.+)$|^([^-].+\.dll)$/gm)]
  .map(match => (match[1] ?? match[2]).split(/[\\/]/).at(-1).replace(/\.dll$/, ""))
  .map(name => ({ name, prefix: `${name.replace(/[^A-Za-z0-9]/g, "_")}_` }))
  // System.Private.CoreLib is S_P_CoreLib_ in mangled names.
  .concat([{ name: "System.Private.CoreLib", prefix: "S_P_CoreLib_" }, { name: "System.Private.CoreLib", prefix: "System_Private_CoreLib_" }])
  .sort((a, b) => b.prefix.length - a.prefix.length);
const family = name =>
  name.startsWith("Microsoft.AspNetCore.Server.Kestrel") ? "Kestrel" :
  name.startsWith("Microsoft.AspNetCore.Http") || name.startsWith("Microsoft.Net.Http.Headers") || name === "Microsoft.AspNetCore.WebUtilities" ? "ASP.NET Core HTTP abstractions" :
  name.startsWith("Microsoft.AspNetCore.") ? "ASP.NET Core hosting, routing and middleware" :
  name.startsWith("Microsoft.Extensions.") ? "Microsoft.Extensions (DI, logging, options, configuration, hosting)" :
  name.startsWith("System.IO.Pipelines") ? "System.IO.Pipelines" :
  name.startsWith("System.Net.") ? "System.Net" :
  name.startsWith("System.") || name === "System" || name === "mscorlib" || name === "netstandard" ? "Base class library" :
  name.startsWith("Runic.") ? "Runic" : "Other";
const sizes = new Map();
let total = 0;
let unattributed = 0;
for (const [, kind, name, length] of readFileSync(map, "utf8")
  .matchAll(/<(MethodCode|GCInfo|MethodExceptionHandlingInfo) Name="([^"]*)" Length="(\d+)"/g)) {
  const bytes = Number(length);
  total += bytes;
  // Unboxing and instantiating stubs carry the method's own mangled name after a short prefix.
  const bare = name.replace(/^(unbox_|__GenericLookupFromDict_|__GenericLookupFromType_)/, "");
  const owner = assemblies.find(assembly => bare.startsWith(assembly.prefix));
  if (!owner) {
    unattributed += bytes;
    continue;
  }
  const key = family(owner.name);
  sizes.set(key, (sizes.get(key) ?? 0) + bytes);
  void kind;
}
console.log(JSON.stringify({ map, codeBytes: total, unattributedBytes: unattributed,
  families: Object.fromEntries([...sizes].sort((a, b) => b[1] - a[1])) }, null, 2));
