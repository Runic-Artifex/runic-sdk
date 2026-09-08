import assert from "node:assert/strict";
import {
  mkdtempSync,
  mkdirSync,
  readFileSync,
  readdirSync,
  writeFileSync,
  realpathSync,
} from "node:fs";
import { tmpdir } from "node:os";
import { resolve, join } from "node:path";
import { root, workspace, run, configuration } from "./run.mjs";

export async function verifyPackages() {
  const directory = mkdtempSync(join(tmpdir(), "runic-sdk-consumers-"));
  console.log(`Package-only consumers: ${directory}`);
  const nuget = resolve(root, "artifacts/packages/nuget");
  const npm = resolve(root, "artifacts/packages/npm");
  for (const p of workspace.nuget) {
    assert.ok(
      readdirSync(nuget).includes(`${p.name}.${workspace.version}.nupkg`),
      `Pack ${p.name} first`,
    );
  }
  const archives = workspace.npm.map((p) => {
    const file = `${p.name.replace("@", "").replace("/", "-")}-${workspace.version}.tgz`;
    assert.ok(readdirSync(npm).includes(file), `Pack ${p.name} first`);
    return [p.name, resolve(npm, file)];
  });
  // The temporary tree has no parent workspace, source references, or reusable Runic package cache.
  writeFileSync(
    join(directory, "NuGet.config"),
    `<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear/><add key="candidate" value="${nuget}"/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/><package pattern="dotnet-runic*"/></packageSource><packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping>
</configuration>`,
  );
  const env = {
    NUGET_PACKAGES: join(directory, "nuget-cache"),
    DOTNET_CLI_HOME: join(directory, "dotnet-home"),
  };
  // Separate minimal consumers prevent Application dependencies from concealing missing dependencies
  // in standalone CommandLine, Assets, Desktop, or Translations packages.
  const libraries = workspace.nuget.filter(
    (p) => !p.name.startsWith("dotnet-") && !p.name.endsWith(".Templates"),
  );
  for (const p of libraries) {
    const consumer = join(directory, p.name);
    mkdirSync(consumer);
    writeFileSync(
      join(consumer, "Consumer.csproj"),
      `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><OutputType>Exe</OutputType><ImplicitUsings>enable</ImplicitUsings><TreatWarningsAsErrors>true</TreatWarningsAsErrors></PropertyGroup><ItemGroup><PackageReference Include="${p.name}" Version="${workspace.version}"/>${p.name === "Runic.Translations.Build" ? `<PackageReference Include="Runic.Translations" Version="${workspace.version}"/>` : ""}</ItemGroup></Project>`,
    );
    writeFileSync(
      join(consumer, "Program.cs"),
      p.name.endsWith(".Build")
        ? 'Console.WriteLine("Translation build targets restored.");'
        : `Console.WriteLine(System.Reflection.Assembly.Load("${p.name}").GetName().Name);`,
    );
    if (
      [
        "Runic.Application",
        "Runic.Application.Hosting",
        "Runic.Application.Desktop",
        "Runic.Application.CsWebUi",
        "Runic.Application.Testing",
      ].includes(p.name)
    ) {
      const program = join(consumer, "Program.cs");
      writeFileSync(
        program,
        '[assembly: Runic.Application.RunicApplicationManifest("package-canary")]\n' +
          readFileSync(program, "utf8"),
      );
    }
    run("dotnet", ["run", "--project", "Consumer.csproj", "--configuration", configuration], consumer, env);
    const assets = JSON.parse(
      readFileSync(join(consumer, "obj/project.assets.json"), "utf8"),
    );
    assert.ok(
      Object.values(assets.libraries).every((l) => l.type !== "project"),
      `${p.name} leaked a project reference`,
    );
    if (p.name === "Runic.Application.CsWebUi") {
      assert.ok(!Object.keys(assets.libraries).some(name => name.startsWith("Runic.Desktop/")),
        "CS-WebUI host unexpectedly depends on Desktop");
      const runtime = JSON.parse(readFileSync(join(consumer, `bin/${configuration}/net10.0/Consumer.runtimeconfig.json`), "utf8"));
      const frameworks = runtime.runtimeOptions.frameworks ?? [runtime.runtimeOptions.framework];
      assert.ok(!frameworks.some(framework => framework?.name === "Microsoft.AspNetCore.App"),
        "CS-WebUI host unexpectedly requires ASP.NET Core");
    }
    for (const key of Object.keys(assets.libraries).filter((k) =>
      k.startsWith("Runic."),
    )) {
      assert.equal(
        key.split("/")[1],
        workspace.version,
        `stale internal dependency ${key}`,
      );
    }
  }
  await verifyToolAndTemplatePackages(directory, nuget, env);
  const frontend = join(directory, "frontend");
  mkdirSync(frontend);
  writeFileSync(
    join(frontend, "package.json"),
    JSON.stringify(
      {
        private: true,
        type: "module",
        dependencies: {
          ...Object.fromEntries(
            archives.map(([name, file]) => [name, `file:${file}`]),
          ),
          typescript: "6.0.3",
          "@types/node": "24.10.12",
          effect: "3.22.1",
          svelte: "5.57.0",
          vite: "8.2.2",
          "@angular/core": "22.1.5",
          "@sveltejs/kit": "2.70.2",
          "@sveltejs/adapter-static": "3.0.10",
          "@sveltejs/vite-plugin-svelte": "7.3.0",
        },
      },
      null,
      2,
    ),
  );
  run(
    "npm",
    [
      "install",
      "--ignore-scripts",
      "--no-audit",
      "--no-fund",
      "--package-lock=false",
    ],
    frontend,
  );
  for (const [name] of archives) {
    const path = realpathSync(join(frontend, "node_modules", name));
    assert.ok(
      path.startsWith(frontend),
      `${name} is linked to the source workspace`,
    );
    const manifest = JSON.parse(
      readFileSync(join(path, "package.json"), "utf8"),
    );
    for (const version of Object.values({
      ...manifest.dependencies,
      ...manifest.peerDependencies,
    })) {
      assert.ok(
        !/^(workspace:|file:|link:)/.test(version),
        `${name} has an unpublished dependency ${version}`,
      );
    }
  }
  writeFileSync(
    join(frontend, "consumer.mjs"),
    `
import assert from 'node:assert/strict';
import { defineApplicationBridgeContract } from '@runic-artifex/application-bridge';
import * as compiler from '@runic-artifex/application-bridge-tooling';
import * as desktop from '@runic-artifex/desktop';
import * as vite from '@runic-artifex/vite-plugin-runic';
import * as translations from '@runic-artifex/vite-plugin-runic-translations';
import { createViteApplicationBridgeObserver } from '@runic-artifex/svelte/vite';
import { runicToolkitSpaPageOptions } from '@runic-artifex/sveltekit/page-options';
assert.equal(typeof defineApplicationBridgeContract, 'function');
assert.equal(typeof createViteApplicationBridgeObserver, 'function');
assert.equal(runicToolkitSpaPageOptions.ssr, false);
for (const module of [compiler, desktop, vite, translations]) assert.ok(Object.keys(module).length);
console.log('Packed npm consumers passed.');
`,
  );
  run("node", ["consumer.mjs"], frontend);
  writeFileSync(
    join(frontend, "consumer.ts"),
    archives
      .map(([name], index) => `import * as package${index} from '${name}';`)
      .join("\n") +
      '\nimport type { RunicLocaleCookieOptions } from "@runic-artifex/sveltekit/translations";\nconst cookie: RunicLocaleCookieOptions = { httpOnly: true, sameSite: "lax" };\n',
  );
  writeFileSync(
    join(frontend, "tsconfig.json"),
    JSON.stringify({
      compilerOptions: {
        target: "ES2022",
        module: "NodeNext",
        moduleResolution: "NodeNext",
        strict: true,
        noEmit: true,
        skipLibCheck: true,
      },
      include: ["consumer.ts"],
    }),
  );
  run(
    "node",
    ["node_modules/typescript/bin/tsc", "-p", "tsconfig.json"],
    frontend,
  );
  console.log(
    `All ${libraries.length} NuGet library consumers, 2 tools, 2 template packages, and 8 npm artifacts passed.`,
  );
}

export async function verifyToolAndTemplatePackages(directory, nuget, env) {
  const toolPath = join(directory, "tools");
  const config = join(directory, "NuGet.config");
  for (const [id, command] of [
    ["dotnet-runic", "dotnet-runic"],
    ["dotnet-runic-translations", "runic-translations"],
  ]) {
    run(
      "dotnet",
      [
        "tool",
        "install",
        id,
        "--version",
        workspace.version,
        "--tool-path",
        toolPath,
        "--configfile",
        config,
      ],
      directory,
      env,
    );
    run(
      join(toolPath, command + (process.platform === "win32" ? ".exe" : "")),
      ["--help"],
      directory,
      env,
    );
  }
  for (const name of [
    "Runic.Application.Templates",
    "Runic.Translations.Templates",
  ]) {
    run(
      "dotnet",
      [
        "new",
        "install",
        join(nuget, `${name}.${workspace.version}.nupkg`),
        "--force",
      ],
      directory,
      env,
    );
  }
  const translations = join(directory, "translation-template");
  run(
    "dotnet",
    [
      "new",
      "runic-translations-project",
      "--name",
      "TranslationConsumer",
      "--output",
      translations,
    ],
    directory,
    env,
  );
  run("dotnet", ["tool", "restore", "--configfile", config], translations, env);
  run(
    "dotnet",
    ["build", "TranslationConsumer.csproj", "--nologo"],
    translations,
    env,
  );
}
