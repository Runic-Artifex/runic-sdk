#!/usr/bin/env python3
"""Verify packed GTK4 runtime behavior outside the checkout using the SDK shell."""
import argparse
import json
import os
from pathlib import Path
import subprocess
import tempfile
from xml.sax.saxutils import escape

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--missing-runtime', action='store_true',
                    help='Also test without LD_LIBRARY_PATH (NixOS: no globally installed GTK4).')
args = parser.parse_args()
root = Path(__file__).resolve().parents[4]
version = json.loads((root / 'eng/workspace.json').read_text())['version']
feed = root / 'artifacts/packages/nuget'
for package in ('Runic.Desktop', 'Runic.Desktop.Gtk4'):
    if not (feed / f'{package}.{version}.nupkg').is_file():
        raise SystemExit(f'Pack the current {package} candidate first.')

with tempfile.TemporaryDirectory(prefix='runic-gtk4-consumer-') as temporary:
    directory = Path(temporary)
    env = dict(os.environ, NUGET_PACKAGES=str(directory / 'packages'))
    (directory / 'NuGet.config').write_text(f'''<configuration>
<packageSources><clear/><add key="candidate" value="{escape(str(feed), {chr(34): '&quot;'})}"/>
<add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources>
<packageSourceMapping><clear/><packageSource key="candidate"><package pattern="Runic.*"/></packageSource>
<packageSource key="nuget.org"><package pattern="*"/></packageSource></packageSourceMapping></configuration>''')
    def run(command, environment=env):
        subprocess.run(command, cwd=directory, env=environment, check=True, timeout=180)
    for package in ('Runic.Desktop', 'Runic.Desktop.Gtk4'):
        consumer = directory / package
        consumer.mkdir()
        project = consumer / 'Consumer.csproj'
        project.write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup>
<OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings>
<Nullable>enable</Nullable><TreatWarningsAsErrors>true</TreatWarningsAsErrors><InvariantGlobalization>true</InvariantGlobalization>
</PropertyGroup><ItemGroup><PackageReference Include="{package}" Version="{version}"/></ItemGroup></Project>''')
        program = '''using Runic.Desktop;
await using var host = await DesktopHost.StartAsync();
Console.WriteLine("PASS base Desktop package without GTK4");
'''
        if package.endswith('.Gtk4'):
            program = '''using Runic.Desktop;
using Runic.Desktop.Gtk4;
if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
if (args.Contains("--missing-runtime"))
{
    if (new Gtk4WindowHostFactory().IsSupported) throw new Exception("GTK4 runtime is still visible; missing-runtime check is invalid.");
    try { Gtk4Application.Run(() => Task.FromResult(0)); }
    catch (PlatformNotSupportedException e) when (e.Message.Contains("GTK 4.12") && e.Message.Contains("WebKitGTK 6"))
    { Console.WriteLine("PASS actionable missing-native-runtime diagnostic"); return 0; }
    throw new Exception("Missing runtime was not diagnosed.");
}
return Gtk4Application.Run(async () =>
{
    if (!OperatingSystem.IsLinux()) throw new PlatformNotSupportedException();
    var factory = new Gtk4WindowHostFactory();
    if (!factory.IsSupported) throw new Exception("GTK4 provider unavailable");
    await using var window = factory.Create();
    await window.OpenAsync(new Uri("about:blank"), new DesktopWindowHostOptions { Width = 640, Height = 480 });
    if (!window.IsOpen || window.NativeHandle == 0) throw new Exception("Native window missing");
    await window.CloseAsync();
    if (window.IsOpen) throw new Exception("Native window did not close");
    Console.WriteLine("PASS packaged GTK4 native window lifecycle");
    return 0;
});
'''
        (consumer / 'Program.cs').write_text(program)
        run(['dotnet', 'build', str(project), '-c', 'Release', '--nologo'])
        graph = json.loads((consumer / 'obj/project.assets.json').read_text())
        assert all(lib['type'] == 'package' for lib in graph['libraries'].values()), 'Source reference leaked into consumer'
        if package == 'Runic.Desktop':
            assert not any('GirCore' in name or 'Gtk4' in name for name in graph['libraries']), 'Optional GTK4 dependency leaked into base Desktop'
        executable = ['dotnet', str(consumer / 'bin/Release/net10.0/Consumer.dll')]
        if package.endswith('.Gtk4'):
            run(['dbus-run-session', '--', 'xvfb-run', '-a', *executable], dict(env, GDK_BACKEND='x11'))
            if args.missing_runtime:
                run([*executable, '--missing-runtime'], dict(env, LD_LIBRARY_PATH=''))
        else:
            run(executable)
print('PASS isolated Desktop/GTK4 package consumers and optional dependency graph')
