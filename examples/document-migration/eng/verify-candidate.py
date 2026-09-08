#!/usr/bin/env python3
"""Bounded package-only document acceptance, using the caller's locked development environment."""
import argparse
import hashlib
import importlib.util
import json
import os
import platform
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent
spec = importlib.util.spec_from_file_location('document_export', HERE / 'export-candidate.py')
exporter = importlib.util.module_from_spec(spec)
spec.loader.exec_module(exporter)


def sha(path):
    return hashlib.sha256(Path(path).read_bytes()).hexdigest()


def nuget_candidates(feed, version):
    candidates = {}
    for path in sorted(feed.glob('*.nupkg')):
        with zipfile.ZipFile(path) as archive:
            names = [name for name in archive.namelist() if name.endswith('.nuspec')]
            if len(names) != 1:
                raise ValueError(f'Invalid NuGet metadata: {path}')
            metadata = ET.fromstring(archive.read(names[0])).find('{*}metadata')
            name = metadata.findtext('{*}id')
            actual = metadata.findtext('{*}version')
        if not (name.startswith('Runic.') or name == 'dotnet-runic'):
            continue
        if actual != version or name in candidates:
            raise ValueError(f'Mixed or duplicate NuGet candidate: {name}@{actual}')
        candidates[name] = {'path': str(path.resolve()), 'sha256': sha(path)}
    if 'dotnet-runic' not in candidates:
        raise ValueError('Missing exact dotnet-runic candidate for package-only bridge inspection')
    return candidates


def assert_graph(assets, host, provider, version, candidates):
    libraries = assets['libraries']
    for identity, library in libraries.items():
        name, actual = identity.rsplit('/', 1)
        if name.startswith('Runic.'):
            if library['type'] != 'package' or actual != version or name not in candidates:
                raise AssertionError(f'Not an exact candidate SDK package: {identity}')
        elif library['type'] == 'project' and name not in {'DocumentDomain', 'DocumentApplication', 'DocumentMvvm'}:
            raise AssertionError(f'Unexpected project dependency: {identity}')
    providers = sorted(name for name in libraries if re_provider(name))
    expected = [] if provider == 'None' else [f'Runic.Platform.{provider}/{version}']
    if providers != expected:
        raise AssertionError(f'Static provider isolation failed: {providers}, expected {expected}')
    if host == 'cswebui':
        forbidden = ('Runic.Desktop/', 'Runic.Application.Desktop/', 'Runic.Application.Platform.Desktop/', 'Runic.Assets.Desktop/')
        if any(name.startswith(forbidden) for name in libraries):
            raise AssertionError('CS-WebUI resolved Desktop packages')
        references = set()
        for framework in assets['project']['frameworks'].values():
            references.update(framework.get('frameworkReferences', {}))
        for target in assets['targets'].values():
            for library in target.values():
                references.update(library.get('frameworkReferences', {}))
        if 'Microsoft.AspNetCore.App' in references:
            raise AssertionError('CS-WebUI resolved the ASP.NET Core framework')


def re_provider(name):
    return any(name.startswith(f'Runic.Platform.{provider}/') for provider in ('Windows', 'Linux', 'MacOS'))


def run_step(name, command, cwd, env, report, receipt, timeout=600):
    log = report / f'{name}.log'
    started = time.monotonic()
    print(f'{name}: running', flush=True)
    with log.open('w') as output:
        process = subprocess.Popen(command, cwd=cwd, env=env, stdout=output, stderr=subprocess.STDOUT,
                                   start_new_session=os.name != 'nt',
                                   creationflags=subprocess.CREATE_NEW_PROCESS_GROUP if os.name == 'nt' else 0)
        try:
            code = process.wait(timeout=timeout)
        except BaseException as error:
            if os.name == 'nt':
                subprocess.run(['taskkill', '/PID', str(process.pid), '/T', '/F'], capture_output=True, timeout=30)
            else:
                try:
                    os.killpg(process.pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
            process.wait(timeout=30)
            status = 'timeout' if isinstance(error, subprocess.TimeoutExpired) else 'interrupted'
            receipt['steps'].append({'name': name, 'status': status, 'log': log.name})
            if status == 'timeout':
                raise RuntimeError(f'{name} exceeded {timeout}s; task process tree terminated') from error
            raise
    receipt['steps'].append({'name': name, 'status': 'passed' if code == 0 else 'failed', 'exitCode': code,
                             'seconds': round(time.monotonic() - started, 3), 'log': log.name})
    if code:
        raise RuntimeError(f'{name} failed with exit {code}; see {log}')
    print(f'{name}: passed', flush=True)


def verify(args):
    report = args.report.resolve()
    if report.exists():
        raise ValueError('Report directory already exists; preserve immutable earlier receipts')
    nuget = nuget_candidates(args.nuget_feed.resolve(), args.version)
    report.mkdir(parents=True)
    root = Path(tempfile.mkdtemp(prefix='runic-document-packages-')).resolve()
    consumer = root / 'consumer'
    receipt = {'schema': 'runic.document-package-acceptance/1', 'version': args.version,
               'environment': {'os': platform.platform(), 'architecture': platform.machine(), 'configuration': 'Release',
                               'gdkBackend': os.environ.get('GDK_BACKEND'), 'display': os.environ.get('DISPLAY')},
               'nugetCandidates': nuget, 'steps': [], 'cases': [], 'status': 'running',
               'scope': 'package-only automated browser/managed acceptance; no native selected-file or accessibility certification'}
    try:
        revision = subprocess.run(['git', '-C', str(exporter.SOURCE.parents[1]), 'rev-parse', 'HEAD'], capture_output=True, text=True, check=True)
        receipt['sourceRevision'] = revision.stdout.strip()
        receipt['sourceDirty'] = bool(subprocess.run(['git', '-C', str(exporter.SOURCE.parents[1]), 'status', '--porcelain'], capture_output=True, text=True, check=True).stdout)
        exporter.export(consumer, args.version, args.nuget_feed.resolve(), args.npm_feed.resolve())
        inputs = json.loads((consumer / 'candidate-inputs.json').read_text())
        receipt['npmCandidates'] = inputs['npm']
        for project in consumer.rglob('*.csproj'):
            tree = ET.parse(project)
            for item in tree.getroot().iter('ProjectReference'):
                target = (project.parent / item.get('Include')).resolve()
                if not target.is_relative_to(consumer) or target.name.startswith('Runic.'):
                    raise AssertionError(f'Checkout SDK project reference escaped exporter: {target}')
        env = dict(os.environ)
        for key in ('RUNIC_BRIDGE_INSPECTOR', 'RunicHost', 'RUNIC_DOCUMENT_HOST_EXECUTABLE'):
            env.pop(key, None)
        env.update({'NUGET_PACKAGES': str(root / 'packages'), 'DOTNET_CLI_HOME': str(root / 'dotnet-home'),
                    'BUN_INSTALL_CACHE_DIR': str(root / 'bun-cache'), 'CONFIGURATION': 'Release'})
        frontend = consumer / 'Host/Frontend'
        run_step('npm-install', ['bun', 'install', '--ignore-scripts'], frontend, env, report, receipt)
        installed = {}
        for manifest in (frontend / 'node_modules').rglob('package.json'):
            if manifest.parent.parent.name != '@runic-artifex':
                continue
            data = json.loads(manifest.read_text())
            name = data.get('name', '')
            if not name.startswith(exporter.PREFIX):
                continue
            if data.get('version') != args.version or name not in inputs['npm'] or not manifest.resolve().is_relative_to(consumer):
                raise AssertionError(f'Noncandidate npm resolution: {name}')
            installed[name] = data['version']
        for group in ('dependencies', 'devDependencies'):
            manifest = json.loads((frontend / 'package.json').read_text())
            for name in manifest.get(group, {}):
                if name.startswith(exporter.PREFIX) and not (frontend / 'node_modules' / name).resolve().is_relative_to(consumer):
                    raise AssertionError(f'Workspace npm package leaked: {name}')
        if set(installed) != set(inputs['npm']):
            raise AssertionError(f'Missing npm dependency closure: {set(inputs["npm"]) - set(installed)}')
        receipt['installedNpm'] = installed
        shutil.copyfile(frontend / 'bun.lock', report / 'bun.lock')
        run_step('tool-restore', ['dotnet', 'tool', 'restore', '--configfile', str(consumer / 'NuGet.config')], consumer, env, report, receipt)
        run_step('managed', ['dotnet', 'run', '--project', 'Tests/DocumentMigration.Tests.csproj', '-c', 'Release'], consumer, env, report, receipt)
        native = {'Linux': 'Linux', 'Windows': 'Windows', 'Darwin': 'MacOS'}[platform.system()]
        for name, host, provider in [('desktop-omitted', 'desktop', 'None'), ('desktop-provider', 'desktop', native), ('cswebui', 'cswebui', 'None')]:
            run_step(name + '-build', ['dotnet', 'build', 'Host/DocumentDesktop.csproj', '-c', 'Release', f'-p:RunicHost={host}', f'-p:RunicNativeProvider={provider}'], consumer, env, report, receipt)
            assets_path = consumer / 'Host/obj/project.assets.json'
            assets = json.loads(assets_path.read_text())
            assert_graph(assets, host, provider, args.version, nuget)
            shutil.copyfile(assets_path, report / f'{name}.assets.json')
            run_step(name + '-browser', ['bun', 'browser.test.mjs'], frontend, env, report, receipt, timeout=120)
            receipt['cases'].append({'name': name, 'host': host, 'provider': provider, 'status': 'passed',
                                     'hostSha256': sha(consumer / 'Host/bin/Release/net10.0/DocumentDesktop.dll'),
                                     'contractSha256': sha(consumer / 'Host/Contract/bridge.ir.json')})
        # The feed is immutable: reject replacement of either registry's inputs during execution.
        for item in [*nuget.values(), *inputs['npm'].values()]:
            if sha(item['path']) != item['sha256']:
                raise AssertionError(f'Candidate bytes changed during verification: {item["path"]}')
        receipt['status'] = 'passed'
    except BaseException as error:
        receipt['status'] = 'failed'
        receipt['failure'] = str(error) or type(error).__name__
        raise
    finally:
        (report / 'receipt.json').write_text(json.dumps(receipt, indent=2) + '\n')
        shutil.rmtree(root)
    print(f'Document candidate acceptance passed: {report / "receipt.json"}', flush=True)


def interrupt(signum, frame):
    raise InterruptedError("Document candidate verification interrupted")


def main():
    signal.signal(signal.SIGTERM, interrupt)
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--version', default='0.2.0-preview.1')
    parser.add_argument('--nuget-feed', type=Path, required=True)
    parser.add_argument('--npm-feed', type=Path, required=True)
    parser.add_argument('--report', type=Path, required=True)
    args = parser.parse_args()
    try:
        verify(args)
    except Exception as error:
        print(f'Document candidate acceptance failed: {error}', file=sys.stderr)
        sys.exit(1)


if __name__ == '__main__':
    main()
