#!/usr/bin/env python3
"""Fast rejection/isolation regressions; no registry, .NET build, or browser required."""
import importlib.util
import io
import json
import sys
import tarfile
import tempfile
import unittest
import zipfile
from pathlib import Path

sys.dont_write_bytecode = True
HERE = Path(__file__).resolve().parent

def load(name, path):
    spec = importlib.util.spec_from_file_location(name, path)
    module = importlib.util.module_from_spec(spec)
    spec.loader.exec_module(module)
    return module

exporter = load('document_export_test', HERE / 'export-candidate.py')
verifier = load('document_verify_test', HERE / 'verify-candidate.py')
VERSION = '0.2.0-preview.1'
A, B = '@runic-artifex/a', '@runic-artifex/b'


def archive(feed, name, version=VERSION, dependencies=None, filename=None):
    data = json.dumps({'name': name, 'version': version, 'dependencies': dependencies or {}}).encode()
    path = feed / (filename or name.rsplit('/', 1)[1] + '.tgz')
    with tarfile.open(path, 'w:gz') as tar:
        member = tarfile.TarInfo('package/package.json')
        member.size = len(data)
        tar.addfile(member, io.BytesIO(data))
    return path


class CandidateTests(unittest.TestCase):
    def setUp(self):
        self.temporary = tempfile.TemporaryDirectory(prefix='runic-document-export-test-')
        self.root = Path(self.temporary.name)
        self.feed = self.root / 'npm'
        self.feed.mkdir()

    def tearDown(self):
        self.temporary.cleanup()

    def test_transitive_candidate_closure(self):
        archive(self.feed, A, dependencies={B: VERSION})
        archive(self.feed, B)
        values = exporter.npm_archives(self.feed, VERSION, {A})
        self.assertEqual(set(values), {A, B})
        self.assertTrue(all(len(item['sha256']) == 64 for item in values.values()))

    def test_missing_transitive_refuses_export(self):
        archive(self.feed, A, dependencies={B: VERSION})
        with self.assertRaisesRegex(ValueError, 'Missing exact npm'):
            exporter.npm_archives(self.feed, VERSION, {A})

    def test_wrong_version_and_duplicate_refused(self):
        item = archive(self.feed, A, version='0.1.0-preview.1')
        with self.assertRaisesRegex(ValueError, 'exact candidate version'):
            exporter.npm_archives(self.feed, VERSION, {A})
        item.unlink()
        archive(self.feed, A)
        archive(self.feed, A, filename='duplicate.tgz')
        with self.assertRaisesRegex(ValueError, 'Duplicate candidate'):
            exporter.npm_archives(self.feed, VERSION, {A})

    def test_workspace_or_ranged_dependency_refused(self):
        for constraint in ('workspace:*', '^' + VERSION, 'latest'):
            archive(self.feed, A, dependencies={B: constraint})
            archive(self.feed, B)
            with self.assertRaisesRegex(ValueError, 'noncandidate dependencies'):
                exporter.npm_archives(self.feed, VERSION, {A})

    def test_export_binds_direct_transitive_and_tool_versions(self):
        manifest = json.loads((exporter.SOURCE / 'Host/Frontend/package.json').read_text())
        required = {name for group in ('dependencies', 'devDependencies') for name in manifest[group] if name.startswith(exporter.PREFIX)}
        for name in required:
            archive(self.feed, name, dependencies={B: VERSION})
        archive(self.feed, B)
        consumer = exporter.export(self.root / 'consumer', VERSION, 'relative-candidate-nuget', self.feed)
        exported = json.loads((consumer / 'Host/Frontend/package.json').read_text())
        self.assertEqual(set(exported['overrides']), required | {B})
        self.assertIn(str(Path('relative-candidate-nuget').resolve()), (consumer / 'NuGet.config').read_text())
        self.assertTrue(all(value.startswith('file:') for value in exported['overrides'].values()))
        self.assertEqual(json.loads((consumer / '.config/dotnet-tools.json').read_text())['tools']['dotnet-runic']['version'], VERSION)
        for project in consumer.rglob('*.csproj'):
            self.assertNotIn('../../../', project.read_text())
            self.assertNotIn('.Generators.csproj', project.read_text())
        with self.assertRaisesRegex(ValueError, 'already|must not exist'):
            exporter.export(consumer, VERSION, '/candidate/nuget', self.feed)

    def test_reject_before_creating_consumer(self):
        output = self.root / 'consumer'
        with self.assertRaisesRegex(ValueError, 'Missing exact npm'):
            exporter.export(output, VERSION, '/candidate/nuget', self.feed)
        self.assertFalse(output.exists())

    def test_nuget_requires_exact_tool_and_library_candidates(self):
        feed = self.root / 'nuget'
        feed.mkdir()
        with self.assertRaisesRegex(ValueError, 'Missing exact dotnet-runic'):
            verifier.nuget_candidates(feed, VERSION)
        for name in ('dotnet-runic', 'Runic.Platform'):
            with zipfile.ZipFile(feed / (name + '.nupkg'), 'w') as package:
                package.writestr(name + '.nuspec', f'<package xmlns="urn:nuget"><metadata><id>{name}</id><version>{VERSION}</version></metadata></package>')
        self.assertEqual(set(verifier.nuget_candidates(feed, VERSION)), {'dotnet-runic', 'Runic.Platform'})
        with zipfile.ZipFile(feed / 'mixed.nupkg', 'w') as package:
            package.writestr('mixed.nuspec', '<package><metadata><id>Runic.Other</id><version>0.1.0-preview.1</version></metadata></package>')
        with self.assertRaisesRegex(ValueError, 'Mixed or duplicate NuGet'):
            verifier.nuget_candidates(feed, VERSION)

    def test_graph_rejects_mixed_packages_and_provider_leak(self):
        assets = {'libraries': {'Runic.Platform/' + VERSION: {'type': 'package'}}, 'project': {'frameworks': {'net10.0': {}}}, 'targets': {}}
        verifier.assert_graph(assets, 'cswebui', 'None', VERSION, {'Runic.Platform': {}})
        assets['libraries']['Runic.Platform.Windows/' + VERSION] = {'type': 'package'}
        with self.assertRaisesRegex(AssertionError, 'Static provider isolation'):
            verifier.assert_graph(assets, 'cswebui', 'None', VERSION, {'Runic.Platform': {}, 'Runic.Platform.Windows': {}})
        assets['libraries'] = {'Runic.Platform/' + VERSION: {'type': 'project'}}
        with self.assertRaisesRegex(AssertionError, 'exact candidate SDK package'):
            verifier.assert_graph(assets, 'desktop', 'None', VERSION, {'Runic.Platform': {}})

    def test_cswebui_rejects_desktop_and_aspnet(self):
        assets = {'libraries': {'Runic.Desktop/' + VERSION: {'type': 'package'}}, 'project': {'frameworks': {'net10.0': {}}}, 'targets': {}}
        with self.assertRaisesRegex(AssertionError, 'Desktop packages'):
            verifier.assert_graph(assets, 'cswebui', 'None', VERSION, {'Runic.Desktop': {}})
        assets['libraries'] = {}
        assets['project']['frameworks']['net10.0']['frameworkReferences'] = {'Microsoft.AspNetCore.App': {}}
        with self.assertRaisesRegex(AssertionError, 'ASP.NET Core'):
            verifier.assert_graph(assets, 'cswebui', 'None', VERSION, {})


if __name__ == '__main__':
    unittest.main()
