"""Read archive metadata without extracting paths or running package scripts."""
import sys, json, tarfile, zipfile, hashlib, xml.etree.ElementTree as ET
kind, path = sys.argv[1:]
if kind == 'npm':
    with tarfile.open(path, 'r:gz') as archive:
        members = archive.getmembers()
        names = [m.name for m in members]
        if len(names) != len(set(names)): raise ValueError('Duplicate archive entries')
        p = json.load(archive.extractfile('package/package.json'))
        repo = p.get('repository', {})
        repo = repo if isinstance(repo, str) else repo.get('url', '')
        deps = {}
        for field in ['dependencies', 'optionalDependencies', 'peerDependencies']:
            for key, value in p.get(field, {}).items():
                if key in deps and deps[key] != value: raise ValueError('Conflicting dependency ranges')
                deps[key] = value
        if p.get('private'): raise ValueError('Private package')
        result = dict(name=p['name'], version=p['version'], repository=repo, source=p.get('gitHead', ''), dependencies=deps)
else:
    with zipfile.ZipFile(path) as archive:
        names = archive.namelist()
        if len(names) != len(set(names)): raise ValueError('Duplicate archive entries')
        specs = [n for n in names if n.endswith('.nuspec') and '/' not in n]
        if len(specs) != 1: raise ValueError('Expected one root nuspec')
        root = ET.fromstring(archive.read(specs[0]))
        for elem in root.iter(): elem.tag = elem.tag.split('}')[-1]
        m = root.find('metadata'); repo = m.find('repository')
        deps = {}
        for d in m.findall('.//dependency'):
            key, value = d.attrib['id'], d.attrib['version']
            if key in deps and deps[key] != value: raise ValueError('Conflicting framework dependency ranges')
            deps[key] = value
        # NuGet adds a repository signature; compare every other file exactly when resuming.
        content = {n: hashlib.sha256(archive.read(n)).hexdigest() for n in sorted(names) if n != '.signature.p7s'}
        result = dict(name=m.findtext('id'), version=m.findtext('version'), repository=repo.get('url', '') if repo is not None else '', source=repo.get('commit', '') if repo is not None else '', dependencies=deps, content=content)
print(json.dumps(result))
