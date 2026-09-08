#!/usr/bin/env python3
"""Export a package-only document consumer; never publish or use checkout SDK references."""
import argparse
import hashlib
import json
import re
import shutil
import tarfile
import xml.etree.ElementTree as ET
from pathlib import Path

SOURCE = Path(__file__).resolve().parents[1]
PREFIX = '@runic-artifex/'


def npm_archives(feed, version, required):
    """Inspect metadata without extracting archives, then validate the SDK dependency closure."""
    archives = {}
    if not feed.is_dir():
        raise ValueError(f'npm feed is not a directory: {feed}')
    for path in sorted(feed.glob('*.tgz')):
        with tarfile.open(path, 'r:gz') as archive:
            entries = [item for item in archive.getmembers() if item.name == 'package/package.json']
            if len(entries) != 1 or not entries[0].isfile() or entries[0].size > 1024 * 1024:
                raise ValueError(f'Invalid npm package metadata: {path.name}')
            manifest = json.load(archive.extractfile(entries[0]))
        name = manifest.get('name', '')
        if not name.startswith(PREFIX):
            continue
        if manifest.get('version') != version:
            raise ValueError(f'{name} must have exact candidate version {version}: {path.name}')
        if name in archives:
            raise ValueError(f'Duplicate candidate identity: {name}')
        archives[name] = {'path': str(path.resolve()), 'sha256': hashlib.sha256(path.read_bytes()).hexdigest(), 'manifest': manifest}
    pending = list(required)
    seen = set()
    while pending:
        name = pending.pop()
        if name in seen:
            continue
        seen.add(name)
        if name not in archives:
            raise ValueError(f'Missing exact npm candidate: {name}@{version}')
        manifest = archives[name]['manifest']
        for group in ('dependencies', 'optionalDependencies', 'peerDependencies'):
            for dependency, constraint in manifest.get(group, {}).items():
                if not dependency.startswith(PREFIX):
                    continue
                if constraint != version:
                    raise ValueError(f'{name} has noncandidate {group}: {dependency}={constraint}')
                pending.append(dependency)
    return {name: archives[name] for name in sorted(seen)}


def export(destination, version, nuget_feed, npm_feed=None):
    destination = destination.resolve()
    if not re.match(r'^https?://', str(nuget_feed)):
        nuget_feed = Path(nuget_feed).resolve()
    if destination.is_relative_to(SOURCE.parents[1]):
        raise ValueError('Candidate consumption must be outside the SDK checkout.')
    if not re.fullmatch(r'[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?', version):
        raise ValueError('Use an exact semantic package version.')
    if destination.exists():
        raise ValueError('Destination must not exist; preserve previous acceptance outputs.')
    data = json.loads((SOURCE / 'Host/Frontend/package.json').read_text())
    required = {name for group in ('dependencies', 'devDependencies') for name in data.get(group, {}) if name.startswith(PREFIX)}
    candidates = npm_archives(npm_feed.resolve(), version, required) if npm_feed is not None else {}
    # Fail metadata/dependency validation before creating any consumer output.
    shutil.copytree(SOURCE, destination, ignore=shutil.ignore_patterns('bin', 'obj', 'node_modules', 'dist', 'packages.lock.json', '__pycache__'))
    for project in destination.rglob('*.csproj'):
        tree = ET.parse(project)
        for group in tree.getroot().findall('ItemGroup'):
            for item in list(group):
                include = item.get('Include', '')
                if item.tag != 'ProjectReference' or not include.startswith('../../../'):
                    continue
                group.remove(item)
                package = Path(include).stem
                if package.endswith('.Generators') or package == 'Runic.Assets.Packer':
                    continue  # Included in public package tooling; never extra identities.
                attributes = {'Include': package, 'Version': version}
                if item.get('Condition'):
                    attributes['Condition'] = item.get('Condition')
                ET.SubElement(group, 'PackageReference', attributes)
        for item in list(tree.getroot()):
            if item.tag == 'Import':
                tree.getroot().remove(item)  # NuGet buildTransitive imports take over.
        for item in tree.getroot().iter('PackageReference'):
            if item.get('Version') is None:
                versions = {'CommunityToolkit.Mvvm': '8.4.2', 'Microsoft.Extensions.DependencyInjection': '10.0.11'}
                item.set('Version', versions[item.get('Include')])
        ET.indent(tree)
        tree.write(project, encoding='unicode')
    (destination / 'Directory.Build.targets').write_text('<Project />\n')
    (destination / 'Directory.Build.props').write_text('<Project><PropertyGroup><ManagePackageVersionsCentrally>false</ManagePackageVersionsCentrally></PropertyGroup></Project>\n')
    config = ET.Element('configuration')
    feeds = ET.SubElement(config, 'packageSources')
    ET.SubElement(feeds, 'clear')
    ET.SubElement(feeds, 'add', {'key': 'candidate', 'value': str(nuget_feed)})
    ET.SubElement(feeds, 'add', {'key': 'nuget', 'value': 'https://api.nuget.org/v3/index.json'})
    mapping = ET.SubElement(config, 'packageSourceMapping')
    candidate = ET.SubElement(mapping, 'packageSource', {'key': 'candidate'})
    for pattern in ('Runic.*', 'dotnet-runic'):
        ET.SubElement(candidate, 'package', {'pattern': pattern})
    ET.SubElement(ET.SubElement(mapping, 'packageSource', {'key': 'nuget'}), 'package', {'pattern': '*'})
    ET.ElementTree(config).write(destination / 'NuGet.config', encoding='unicode')
    tools = destination / '.config'
    tools.mkdir(exist_ok=True)
    (tools / 'dotnet-tools.json').write_text(json.dumps({'version': 1, 'isRoot': True, 'tools': {'dotnet-runic': {'version': version, 'commands': ['dotnet-runic'], 'rollForward': False}}}, indent=2) + '\n')
    for group in ('dependencies', 'devDependencies'):
        for name in data.get(group, {}):
            if name.startswith(PREFIX):
                data[group][name] = 'file:' + Path(candidates[name]['path']).as_posix() if candidates else version
    if candidates:
        data['overrides'] = {name: 'file:' + Path(item['path']).as_posix() for name, item in candidates.items()}
    (destination / 'Host/Frontend/package.json').write_text(json.dumps(data, indent=2) + '\n')
    (destination / 'candidate-inputs.json').write_text(json.dumps({'version': version, 'nugetFeed': str(nuget_feed), 'npm': {name: {'path': item['path'], 'sha256': item['sha256']} for name, item in candidates.items()}}, indent=2) + '\n')
    return destination


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('destination', type=Path)
    parser.add_argument('--version', default='0.2.0-preview.1')
    parser.add_argument('--nuget-feed', required=True)
    parser.add_argument('--npm-feed', type=Path, help='Directory containing the verified immutable .tgz candidates')
    args = parser.parse_args()
    try:
        print(export(args.destination, args.version, args.nuget_feed, args.npm_feed))
    except (ValueError, OSError, tarfile.TarError) as error:
        parser.error(str(error))


if __name__ == '__main__':
    main()
