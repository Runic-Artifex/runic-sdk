// eng/ci/apt-install.sh against a stubbed sudo and apt-get (eng/ci/README.md, "Ubuntu packages"): a slow mirror that
// keeps delivering finishes, a stalled one is stopped and the next attempt prefers another official mirror, and the
// download budget bounds the whole phase. Nothing here runs the real apt-get or sudo.
import { test } from 'node:test';
import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { chmodSync, mkdirSync, mkdtempSync, readFileSync, rmSync, statSync, writeFileSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { resolve } from 'node:path';
import { root } from '../run.mjs';

const stubs = {
  sudo: '#!/usr/bin/env bash\nexec env "$@"\n',
  dpkg: '#!/usr/bin/env bash\nexit 0\n',
  // Signals only the stubbed apt-get of this test.
  pkill: `#!/usr/bin/env bash
signal=\${1#-}
if [ "$3" = apt-get ] && [ -f "$FAKE_STATE/apt.pid" ]; then kill "-$signal" "$(cat "$FAKE_STATE/apt.pid")" 2>/dev/null; fi
exit 0
`,
  // Serves one archive of FAKE_SIZE bytes from the mirror apt would try first, at that mirror's rate in FAKE_RATES
  // ("host=bytes-per-second" or "host=stall"), resuming a partial download.
  'apt-get': `#!/usr/bin/env bash
archives=
args=()
while [ $# -gt 0 ]; do
  case $1 in
    -o) if [[ $2 == Dir::Cache::Archives=* ]]; then archives=\${2#*=}; fi; shift 2 ;;
    *) args+=("$1"); shift ;;
  esac
done
archives=\${archives%/}
host() {
  if grep -qsF mirror+file: "$APT_ETC"/sources.list.d/*; then
    awk '{ p = 1e9; for (i = 2; i <= NF; i++) if ($i ~ /^priority:/) p = substr($i, 10) + 0; printf "%012d %06d %s\\n", p, NR, $1 }' "$APT_ETC/apt-mirrors.txt" | sort | head -n 1 | awk '{print $3}'
  else
    grep -hoE 'https?://[^/[:space:]]*ubuntu\\.com' "$APT_ETC"/sources.list.d/* | head -n 1
  fi | sed -E 's#^[a-z+]+://##; s#/.*##'
}
mirror=$(host)
echo "$mirror" >>"$FAKE_STATE/mirrors"
case " \${args[*]} " in
  *" update "*) echo "Hit:1 http://$mirror/ubuntu noble InRelease" ;;
  *" --print-uris "*) if [ ! -f "$archives/pkg.deb" ]; then echo "'http://$mirror/ubuntu/pool/main/p/pkg.deb' pkg.deb $FAKE_SIZE SHA256:00"; fi ;;
  *" --download-only "*)
    if [ -f "$archives/pkg.deb" ]; then exit 0; fi
    echo $$ >"$FAKE_STATE/apt.pid"
    mkdir -p "$archives/partial"
    echo "Get:1 http://$mirror/ubuntu noble/main amd64 pkg amd64 1.0 [$FAKE_SIZE B]"
    rate=
    for entry in $FAKE_RATES; do if [ "\${entry%%=*}" = "$mirror" ]; then rate=\${entry#*=}; fi; done
    if [ "$rate" = stall ]; then head -c 1000 /dev/zero >>"$archives/partial/pkg.deb"; exec sleep 1000; fi
    while [ "$(stat -c %s "$archives/partial/pkg.deb" 2>/dev/null || echo 0)" -lt "$FAKE_SIZE" ]; do
      have=$(stat -c %s "$archives/partial/pkg.deb" 2>/dev/null || echo 0)
      head -c $((rate / 4 < FAKE_SIZE - have ? rate / 4 : FAKE_SIZE - have)) /dev/zero >>"$archives/partial/pkg.deb"
      sleep 0.25 & wait $!
    done
    mv "$archives/partial/pkg.deb" "$archives/pkg.deb"
    ;;
esac
exit 0
`,
};

const run = (setup, env) => {
  const directory = mkdtempSync(resolve(tmpdir(), 'runic-apt-test-'));
  try {
    const bin = resolve(directory, 'bin');
    const etc = resolve(directory, 'etc/apt');
    const state = resolve(directory, 'state');
    for (const path of [bin, resolve(etc, 'apt.conf.d'), resolve(etc, 'sources.list.d'), state]) mkdirSync(path, { recursive: true });
    for (const [name, content] of Object.entries(stubs)) {
      writeFileSync(resolve(bin, name), content);
      chmodSync(resolve(bin, name), 0o755);
    }
    setup(etc);
    const result = spawnSync('bash', [resolve(root, 'eng/ci/apt-install.sh'), 'pkg'], {
      encoding: 'utf8',
      timeout: 120000,
      env: {
        ...process.env, PATH: `${bin}:${process.env.PATH}`, APT_ETC: etc, APT_ARCHIVES: resolve(directory, 'archives'),
        FAKE_STATE: state, APT_STALL_SECONDS: '3', APT_POLL_SECONDS: '1', ...env,
      },
    });
    const read = path => { try { return readFileSync(path, 'utf8'); } catch { return ''; } };
    const archive = resolve(directory, 'archives/pkg.deb');
    return {
      status: result.status, output: result.stdout + result.stderr,
      list: read(resolve(etc, 'apt-mirrors.txt')), sources: read(resolve(etc, 'sources.list.d/ubuntu.sources')),
      mirrors: read(resolve(state, 'mirrors')).trim().split('\n'),
      size: (() => { try { return statSync(archive).size; } catch { return 0; } })(),
    };
  } finally { rmSync(directory, { recursive: true, force: true }); }
};

// The x64 runners' layout: sources naming the mirror list, which lists an EC2 mirror, then the official hosts.
const mirrorList = etc => {
  writeFileSync(resolve(etc, 'apt-mirrors.txt'),
    'http://us-east-2.ec2.archive.ubuntu.com/ubuntu/\tpriority:1\nhttp://archive.ubuntu.com/ubuntu/\tpriority:2\nhttp://security.ubuntu.com/ubuntu/\tpriority:3\n');
  writeFileSync(resolve(etc, 'sources.list.d/ubuntu.sources'), `Types: deb\nURIs: mirror+file:${etc}/apt-mirrors.txt\nSuites: noble\n`);
};
const plainSources = etc => writeFileSync(resolve(etc, 'sources.list.d/ubuntu.sources'),
  'Types: deb\nURIs: http://us-east-2.ec2.archive.ubuntu.com/ubuntu/\nSuites: noble noble-updates\n\nTypes: deb\nURIs: http://security.ubuntu.com/ubuntu/\nSuites: noble-security\n');

test('apt-install keeps a slow mirror that makes progress beyond the stall window', { skip: process.platform === 'win32' }, () => {
  // 150 kB at 25 kB/s takes 6 seconds, twice the stall window.
  const result = run(mirrorList, { FAKE_SIZE: '150000', FAKE_RATES: 'us-east-2.ec2.archive.ubuntu.com=25000', APT_DOWNLOAD_SECONDS: '60' });
  assert.equal(result.status, 0, result.output);
  assert.equal(result.size, 150000);
  assert.match(result.output, /Attempt 1 of up to 5 via us-east-2\.ec2\.archive\.ubuntu\.com,archive\.ubuntu\.com,security\.ubuntu\.com: 0\.1 MB to fetch/);
  assert.match(result.output, /Downloaded 0\.1 MB in \d+s \(\d+ kB\/s\) via us-east-2/);
  assert.doesNotMatch(result.output, /::warning::/);
});

test('apt-install stops a stalled mirror and the next attempt prefers another official mirror', { skip: process.platform === 'win32' }, () => {
  const result = run(mirrorList, {
    FAKE_SIZE: '100000', FAKE_RATES: 'us-east-2.ec2.archive.ubuntu.com=stall archive.ubuntu.com=200000', APT_DOWNLOAD_SECONDS: '60',
  });
  assert.equal(result.status, 0, result.output);
  assert.equal(result.size, 100000);
  assert.match(result.output, /::warning::No download progress for 3s; stopping apt-get/);
  assert.match(result.output, /failed on attempt 1 via us-east-2[^:]*: fetched 0\.0 MB in \d+s \(\d+ kB\/s\), 0\.1 MB still to fetch/);
  assert.match(result.output, /Next attempt prefers archive\.ubuntu\.com: archive\.ubuntu\.com,security\.ubuntu\.com,us-east-2\.ec2\.archive\.ubuntu\.com/);
  assert.equal(result.list, 'http://archive.ubuntu.com/ubuntu/\tpriority:1\nhttp://security.ubuntu.com/ubuntu/\tpriority:2\nhttp://us-east-2.ec2.archive.ubuntu.com/ubuntu/\tpriority:3\n');
  // The second attempt resumed the 1000 bytes the stalled one left in partial/.
  assert.match(result.output, /Attempt 2 of up to 5 via archive\.ubuntu\.com[^:]*: 0\.1 MB to fetch/);
  assert.match(result.output, /Downloaded 0\.1 MB in \d+s \(\d+ kB\/s\) via archive\.ubuntu\.com/);
});

test('apt-install rotates the archive host of plain sources and keeps the security pocket', { skip: process.platform === 'win32' }, () => {
  const result = run(plainSources, {
    FAKE_SIZE: '50000', FAKE_RATES: 'us-east-2.ec2.archive.ubuntu.com=stall archive.ubuntu.com=stall security.ubuntu.com=200000', APT_DOWNLOAD_SECONDS: '60',
  });
  assert.equal(result.status, 0, result.output);
  assert.deepEqual([...new Set(result.mirrors)], ['us-east-2.ec2.archive.ubuntu.com', 'archive.ubuntu.com', 'security.ubuntu.com']);
  assert.equal(result.sources,
    'Types: deb\nURIs: http://security.ubuntu.com/ubuntu/\nSuites: noble noble-updates\n\nTypes: deb\nURIs: http://security.ubuntu.com/ubuntu/\nSuites: noble-security\n');
});

test('apt-install fails within the download budget and says how far it got', { skip: process.platform === 'win32' }, () => {
  // 10 kB/s never stalls, but 1 MB needs 100 seconds; the budget is 6.
  const result = run(mirrorList, { FAKE_SIZE: '1000000', FAKE_RATES: 'us-east-2.ec2.archive.ubuntu.com=10000', APT_DOWNLOAD_SECONDS: '6' });
  assert.equal(result.status, 1, result.output);
  assert.equal(result.size, 0);
  assert.match(result.output, /::warning::The download budget of 6s is used up; stopping apt-get/);
  assert.match(result.output, /::error title=Ubuntu packages could not be downloaded::pkg could not be downloaded within 6s \(0\.\d MB fetched\)\. attempt 1 via [^:]*: fetched 0\.\d MB in \d+s \(\d+ kB\/s\), 0\.\d MB still to fetch; download budget used up while fetching/);
  // The budget left less than a stall window, so no second attempt started.
  assert.doesNotMatch(result.output, /Attempt 2/);
});
