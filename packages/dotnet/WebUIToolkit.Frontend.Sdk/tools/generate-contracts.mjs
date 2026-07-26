import { readFile, writeFile } from "node:fs/promises";
import { resolve } from "node:path";
import { pathToFileURL } from "node:url";

export async function generateFrontendContracts(options) {
  const sourcePath = resolve(options.sourcePath);
  const csharpPath = resolve(options.csharpPath);
  const typescriptPath = resolve(options.typescriptPath);
  const source = JSON.parse(await readFile(sourcePath, "utf8"));

  if (source.$schema !== "webuitoolkit.mvvm.frontend-contract/1") {
    throw new Error("Unsupported frontend contract schema.");
  }
  validate(source);
  await emit(csharpPath, csharp(source), options.verify === true);
  await emit(typescriptPath, typescript(source.contracts), options.verify === true);
}

async function emit(path, content, verify) {
  if (verify) {
    const actual = await readFile(path, "utf8").catch(() => "");
    if (actual !== content) {
      throw new Error(`Generated frontend contract is stale: ${path}`);
    }
    return;
  }
  await writeFile(path, content, "utf8");
}

function validate(source) {
  if (typeof source.csharp?.namespace !== "string" ||
      typeof source.csharp?.className !== "string") {
    throw new Error("The contract requires csharp.namespace and csharp.className.");
  }
  if (!isCSharpQualifiedIdentifier(source.csharp.namespace) ||
      !isCSharpIdentifier(source.csharp.className)) {
    throw new Error("csharp.namespace and csharp.className must be valid C# identifiers.");
  }
  const contractNames = new Set();
  const clients = new Set();
  if (!Array.isArray(source.contracts) || source.contracts.length === 0) {
    throw new Error("The frontend contract source requires at least one contract.");
  }
  for (const contract of source.contracts) {
    if (typeof contract.name !== "string" || contract.name.length === 0) {
      throw new Error("Every contract requires a non-empty name.");
    }
    if (contractNames.has(contract.name)) {
      throw new Error(`Duplicate contract ${contract.name}.`);
    }
    contractNames.add(contract.name);
    if (!isCSharpIdentifier(contract.client) || clients.has(contract.client)) {
      throw new Error(`Contract client '${contract.client}' must be a unique C# identifier.`);
    }
    clients.add(contract.client);
    if (!isCSharpGlobalQualifiedIdentifier(contract.csharp?.modelType)) {
      throw new Error(
        `Contract ${contract.name} requires csharp.modelType as a fully qualified C# type beginning with 'global::'.`);
    }
    if (!Array.isArray(contract.members) || contract.members.length === 0) {
      throw new Error(`Contract ${contract.name} requires at least one member.`);
    }
    const ids = new Set();
    const names = new Set();
    const generatedMemberNames = new Set();
    for (const member of contract.members) {
      if (!Number.isSafeInteger(member.id) || member.id <= 0) {
        throw new Error(`Member IDs in contract ${contract.name} must be positive integers.`);
      }
      if (ids.has(member.id)) {
        throw new Error(`Duplicate member ID ${member.id} in contract ${contract.name}.`);
      }
      ids.add(member.id);
      if (!isCSharpIdentifier(member.name) ||
          names.has(member.name)) {
        throw new Error(
          `Member name '${member.name}' in contract ${contract.name} must be a unique identifier.`);
      }
      names.add(member.name);
      const generatedMemberName = pascal(member.name);
      if (generatedMemberNames.has(generatedMemberName)) {
        throw new Error(
          `Members in contract ${contract.name} must not collide as generated C# name '${generatedMemberName}'.`);
      }
      generatedMemberNames.add(generatedMemberName);
      if (!["property", "collection", "command"].includes(member.kind)) {
        throw new Error(`Unknown member kind '${member.kind}' for ${contract.name}.${member.name}.`);
      }
      validateCSharpMember(contract, member);
    }
  }
}

function validateCSharpMember(contract, member) {
  const location = `${contract.name}.${member.name}`;
  const metadata = member.csharp;
  if (typeof metadata?.sourceMember !== "string" ||
      !isCSharpIdentifier(metadata.sourceMember)) {
    throw new Error(
      `Member ${location} requires csharp.sourceMember as a valid C# identifier.`);
  }

  if (member.kind === "property") {
    if (typeof member.type !== "string" || member.type.length === 0) {
      throw new Error(`Property ${location} requires a non-empty TypeScript type.`);
    }
    if (!["readonly", "readwrite"].includes(member.access)) {
      throw new Error(`Property ${location} access must be 'readonly' or 'readwrite'.`);
    }
  } else if (member.kind === "collection") {
    if (typeof member.type !== "string" || member.type.length === 0) {
      throw new Error(`Collection ${location} requires a non-empty TypeScript item type.`);
    }
    if (member.access !== undefined) {
      throw new Error(`Collection ${location} is always read-only and must not declare access.`);
    }
  } else {
    if (member.argument !== undefined &&
        (typeof member.argument !== "string" || member.argument.length === 0)) {
      throw new Error(`Command ${location} argument must be a non-empty TypeScript type.`);
    }
    if (member.type !== undefined || member.access !== undefined) {
      throw new Error(`Command ${location} must not declare property type or access metadata.`);
    }
  }

  const expectedBindings =
    member.kind === "property"
      ? member.access === "readonly"
        ? ["readOnlyProperty"]
        : member.access === "readwrite"
          ? ["property"]
          : []
      : member.kind === "collection"
        ? ["collection"]
        : ["command", "asyncCommand"];
  if (!expectedBindings.includes(metadata.binding)) {
    const expected = expectedBindings.length === 0
      ? "a valid property access plus matching binding"
      : expectedBindings.join(" or ");
    throw new Error(
      `Member ${location} has incompatible csharp.binding '${metadata.binding}'; expected ${expected}.`);
  }

  if (member.validation !== undefined && typeof member.validation !== "boolean") {
    throw new Error(`Member ${location} validation must be a boolean.`);
  }
  if (member.validation === true && member.kind === "command") {
    throw new Error(`Command ${location} cannot include property validation.`);
  }

  const requiresJsonTypeInfo =
    member.kind !== "command" || typeof member.argument === "string";
  const hasJsonTypeInfo =
    typeof metadata.jsonTypeInfo === "string" && metadata.jsonTypeInfo.length > 0;
  if (requiresJsonTypeInfo !== hasJsonTypeInfo) {
    throw new Error(requiresJsonTypeInfo
      ? `Member ${location} requires a non-empty csharp.jsonTypeInfo expression.`
      : `Parameterless command ${location} must not declare csharp.jsonTypeInfo.`);
  }
  if (hasJsonTypeInfo && !isCSharpGlobalQualifiedIdentifier(metadata.jsonTypeInfo)) {
    throw new Error(
      `Member ${location} csharp.jsonTypeInfo must be a fully qualified C# member expression beginning with 'global::'.`);
  }
}

function csharp(source) {
  const lines = [
    "// <auto-generated />",
    "#nullable enable",
    "",
    `namespace ${source.csharp.namespace};`,
    "",
    `internal static class ${source.csharp.className}`,
    "{",
  ];
  for (const contract of source.contracts) {
    lines.push(`    internal static class ${contract.client}`, "    {");
    lines.push(`        internal const string Name = ${csharpString(contract.name)};`, "");
    lines.push("        internal static class Members", "        {");
    for (const member of contract.members) {
      lines.push(`            internal const int ${pascal(member.name)} = ${member.id};`);
    }
    lines.push("        }", "");
    lines.push(
      `        internal static global::WebUIToolkit.MVVM.CommunityToolkit.CommunityToolkitMvvmBindingAdapter<${contract.csharp.modelType}> CreateAdapter(`,
      `            ${contract.csharp.modelType} model) =>`,
      `            new global::WebUIToolkit.MVVM.CommunityToolkit.CommunityToolkitMvvmAdapterBuilder<${contract.csharp.modelType}>(model)`);
    for (const member of contract.members) {
      const bindingLines = csharpBinding(member);
      for (const line of bindingLines) {
        lines.push(`                ${line}`);
      }
    }
    lines.push("                .Build();");
    lines.push("    }", "");
  }
  lines.push("}", "");
  return lines.join("\n");
}

function csharpBinding(member) {
  const metadata = member.csharp;
  const arguments_ = [
    `Members.${pascal(member.name)}`,
    csharpString(metadata.sourceMember),
    `static state => state.${metadata.sourceMember}`,
  ];
  if (metadata.binding === "property") {
    arguments_.push(
      `static (state, value) => state.${metadata.sourceMember} = value`,
      metadata.jsonTypeInfo);
  } else if (metadata.binding === "readOnlyProperty" ||
             metadata.binding === "collection") {
    arguments_.push(metadata.jsonTypeInfo);
  } else if (metadata.jsonTypeInfo !== undefined) {
    arguments_.push(metadata.jsonTypeInfo);
  }
  if (member.validation === true) {
    arguments_.push("includeValidation: true");
  }

  return [
    `.${csharpBindingMethod(metadata.binding)}(`,
    ...arguments_.map((argument, index) =>
      `    ${argument}${index === arguments_.length - 1 ? ")" : ","}`),
  ];
}

function csharpBindingMethod(binding) {
  switch (binding) {
    case "property":
      return "BindProperty";
    case "readOnlyProperty":
      return "BindReadOnlyProperty";
    case "collection":
      return "BindCollection";
    case "command":
      return "BindCommand";
    case "asyncCommand":
      return "BindAsyncCommand";
    default:
      throw new Error(`Unsupported C# binding '${binding}'.`);
  }
}

function typescript(contracts) {
  const lines = [
    "// <auto-generated />",
    "import {",
    "  MvvmCollection,",
    "  MvvmCommand,",
    "  MvvmCommandWithArgument,",
    "  MvvmProperty,",
    "  MvvmReadonlyProperty,",
    "  type MvvmProjection,",
    "} from \"@webuitoolkit/mvvm\";",
    "",
  ];
  const emittedTypes = new Set();
  for (const contract of contracts) {
    for (const [name, fields] of Object.entries(contract.types ?? {})) {
      if (emittedTypes.has(name)) continue;
      emittedTypes.add(name);
      lines.push(`export interface ${name} {`);
      for (const [field, type] of Object.entries(fields)) {
        lines.push(`  readonly ${field}: ${type};`);
      }
      lines.push("}", "");
    }
  }
  for (const contract of contracts) {
    lines.push(`export class ${contract.client}Contract {`);
    lines.push(`  public static readonly contractName = "${contract.name}" as const;`, "");
    for (const member of contract.members) {
      lines.push(`  public readonly ${member.name};`);
    }
    lines.push("", "  public constructor(projection: MvvmProjection) {");
    for (const member of contract.members) {
      lines.push(`    this.${member.name} = new ${handle(member)}(projection, ${member.id});`);
    }
    lines.push("  }", "}", "");
  }
  return lines.join("\n");
}

function handle(member) {
  if (member.kind === "collection") return `MvvmCollection<${member.type}>`;
  if (member.kind === "command" && member.argument) {
    return `MvvmCommandWithArgument<${member.argument}>`;
  }
  if (member.kind === "command") return "MvvmCommand";
  if (member.access === "readonly") return `MvvmReadonlyProperty<${member.type}>`;
  return `MvvmProperty<${member.type}>`;
}

function pascal(value) {
  return value[0].toUpperCase() + value.slice(1);
}

function csharpString(value) {
  let escaped = "";
  for (const character of value) {
    const code = character.codePointAt(0);
    if (character === "\\") {
      escaped += "\\\\";
    } else if (character === "\"") {
      escaped += "\\\"";
    } else if (character === "\n") {
      escaped += "\\n";
    } else if (character === "\r") {
      escaped += "\\r";
    } else if (character === "\t") {
      escaped += "\\t";
    } else if (code < 0x20) {
      escaped += `\\u${code.toString(16).padStart(4, "0")}`;
    } else {
      escaped += character;
    }
  }
  return `"${escaped}"`;
}

function isCSharpIdentifier(value) {
  return typeof value === "string" &&
    /^[A-Za-z_][A-Za-z0-9_]*$/.test(value);
}

function isCSharpQualifiedIdentifier(value) {
  return typeof value === "string" &&
    value.split(".").every(isCSharpIdentifier);
}

function isCSharpGlobalQualifiedIdentifier(value) {
  return typeof value === "string" &&
    value.startsWith("global::") &&
    isCSharpQualifiedIdentifier(value.slice("global::".length));
}

if (process.argv[1] !== undefined &&
    import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const values = new Map();
  let verify = false;
  for (let index = 2; index < process.argv.length; index++) {
    const argument = process.argv[index];
    if (argument === "--verify") {
      verify = true;
      continue;
    }
    if (!argument.startsWith("--") || index + 1 >= process.argv.length) {
      throw new Error(`Invalid argument: ${argument}`);
    }
    values.set(argument.slice(2), process.argv[++index]);
  }
  for (const required of ["source", "csharp", "typescript"]) {
    if (!values.has(required)) throw new Error(`--${required} is required.`);
  }
  await generateFrontendContracts({
    sourcePath: values.get("source"),
    csharpPath: values.get("csharp"),
    typescriptPath: values.get("typescript"),
    verify,
  });
}
