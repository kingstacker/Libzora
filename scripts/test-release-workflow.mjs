import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import test from 'node:test';

const workflow = readFileSync(new URL('../.github/workflows/release.yml', import.meta.url), 'utf8');
const developmentWorkflow = readFileSync(new URL('../.github/workflows/dev-build.yml', import.meta.url), 'utf8');
const AsyncFunction = Object.getPrototypeOf(async function () {}).constructor;
const sourceSha = 'a'.repeat(40);
const oldSha = 'b'.repeat(40);

function scriptFor(stepName) {
  const lines = workflow.split('\n');
  const step = lines.findIndex(line => line.trim() === `- name: ${stepName}`);
  assert.notEqual(step, -1, stepName);
  const start = lines.findIndex((line, index) => index > step && line.trim() === 'script: |');
  assert.notEqual(start, -1, stepName);
  const script = [];
  for (const line of lines.slice(start + 1)) {
    if (line.trim() && !line.startsWith('            ')) break;
    script.push(line.slice(12));
  }
  return script.join('\n');
}

const prepareScript = scriptFor('Resolve release tag');
const publishScript = scriptFor('Create or verify release tag after successful builds');

function harness({ tags = {}, requested = '', event = 'push', ref = 'refs/heads/master',
                   version = '1.1.6-dev.1', releases = [], raceSha } = {}) {
  const refs = new Map(Object.entries(tags));
  const calls = [];
  const outputs = {};
  const error = status => Object.assign(new Error(`HTTP ${status}`), { status });
  const github = {
    paginate: async () => releases,
    rest: {
      repos: {
        listReleases() {},
        getContent: async () => ({ data: { content: Buffer.from(`<Project><Version>${version}</Version></Project>`).toString('base64') } }),
        compareCommitsWithBasehead: async () => ({ data: { status: 'ahead' } }),
        getCommit: async ({ ref: revision }) => {
          calls.push(['getCommit', revision]);
          if (revision.startsWith('refs/tags/')) {
            const name = revision.slice('refs/tags/'.length);
            if (!refs.has(name)) throw error(404);
            return { data: { sha: refs.get(name) } };
          }
          assert.equal(revision, sourceSha);
          return { data: { sha: sourceSha } };
        },
      },
      git: {
        getRef: async ({ ref: name }) => {
          calls.push(['getRef', name]);
          if (!refs.has(name.slice('tags/'.length))) throw error(404);
          return { data: { object: { type: 'commit', sha: refs.get(name.slice('tags/'.length)) } } };
        },
        createRef: async ({ ref: name, sha }) => {
          calls.push(['createRef', name, sha]);
          if (raceSha) {
            refs.set(name.slice('refs/tags/'.length), raceSha);
            throw error(422);
          }
          assert(!refs.has(name.slice('refs/tags/'.length)), 'Never overwrite an existing tag');
          refs.set(name.slice('refs/tags/'.length), sha);
        },
        updateRef: async () => assert.fail('Tags must never be moved'),
      },
    },
  };
  const context = { eventName: event, ref, sha: sourceSha, repo: { owner: 'example', repo: 'app' } };
  const core = { setOutput: (name, value) => { outputs[name] = value; }, info() {} };
  // Inject only the three environment values the scripts read. No real API is used.
  const env = { REQUESTED_TAG: requested, RELEASE_TAG: 'v1.1.6', RELEASE_SOURCE_SHA: sourceSha };
  const run = script => new AsyncFunction('github', 'context', 'core', 'process', script)(github, context, core, { env });
  return { refs, calls, outputs, env, prepare: () => run(prepareScript), publish: () => run(publishScript) };
}

test('automatic preparation pins SHA without creating a tag', async () => {
  const h = harness();
  await h.prepare();
  assert.deepEqual(h.outputs, { tag: 'v1.1.6', source_sha: sourceSha, publish: 'true' });
  assert.equal(h.refs.size, 0);
  assert(!h.calls.some(c => c[0] === 'createRef'));
});

test('failed build can prepare the same unconsumed version again', async () => {
  const h = harness();
  await h.prepare();
  await h.prepare();
  assert.equal(h.outputs.tag, 'v1.1.6');
  assert.equal(h.refs.size, 0);
});

test('master push skips a previously existing stable tag', async () => {
  const h = harness({ tags: { 'v1.1.6': oldSha } });
  await h.prepare();
  assert.deepEqual(h.outputs, { publish: 'false' });
  assert.equal(h.refs.get('v1.1.6'), oldSha);
});

test('manual implicit rebuild refuses an existing tag', async () => {
  const h = harness({ tags: { 'v1.1.6': oldSha }, event: 'workflow_dispatch' });
  await assert.rejects(h.prepare(), /specify it explicitly/);
  assert.equal(h.refs.get('v1.1.6'), oldSha);
});

test('explicit rebuild checks out the tagged commit, not the workflow branch', async () => {
  const h = harness({ tags: { 'v1.1.5': oldSha }, requested: 'v1.1.5', event: 'workflow_dispatch' });
  await h.prepare();
  assert.deepEqual(h.outputs, { tag: 'v1.1.5', source_sha: oldSha, publish: 'true' });
  assert(!h.calls.some(c => c[0] === 'createRef'));
});

test('explicit missing tags fail before building', async () => {
  await assert.rejects(harness({ requested: 'v1.1.5', event: 'workflow_dispatch' }).prepare(), /HTTP 404/);
});

test('tag push pins its commit even if another requested tag was supplied', async () => {
  const h = harness({ tags: { 'v1.1.5': oldSha }, requested: 'v1.1.6', ref: 'refs/tags/v1.1.5' });
  await h.prepare();
  assert.equal(h.outputs.tag, 'v1.1.5');
  assert.equal(h.outputs.source_sha, oldSha);
});

test('invalid development version or requested tag is rejected', async () => {
  await assert.rejects(harness({ version: '1.1.6' }).prepare(), /X.Y.Z-dev.N/);
  await assert.rejects(harness({ requested: 'not-a-release', event: 'workflow_dispatch' }).prepare(), /Invalid release tag/);
});

test('reachable newer development releases retain version promotion behavior', async () => {
  const h = harness({ releases: [{ tag_name: 'v1.1.7-dev.2', draft: false }] });
  await h.prepare();
  assert.equal(h.outputs.tag, 'v1.1.7');
  assert.equal(h.outputs.source_sha, sourceSha);
  assert.equal(h.refs.size, 0);
});

test('successful publication creates the tag at exactly the built SHA', async () => {
  const h = harness();
  await h.publish();
  assert.deepEqual(h.calls.filter(c => c[0] === 'createRef'), [['createRef', 'refs/tags/v1.1.6', sourceSha]]);
  assert.equal(h.refs.get('v1.1.6'), sourceSha);
});

test('existing matching tags are accepted without mutation', async () => {
  const h = harness({ tags: { 'v1.1.6': sourceSha } });
  await h.publish();
  assert(!h.calls.some(c => c[0] === 'createRef'));
});

test('existing mismatching tags are refused without mutation', async () => {
  const h = harness({ tags: { 'v1.1.6': oldSha } });
  await assert.rejects(h.publish(), /Refusing to move/);
  assert.equal(h.refs.get('v1.1.6'), oldSha);
  assert(!h.calls.some(c => c[0] === 'createRef'));
});

test('concurrent matching creation is verified; mismatching creation is refused', async () => {
  await harness({ raceSha: sourceSha }).publish();
  const h = harness({ raceSha: oldSha });
  await assert.rejects(h.publish(), /Refusing to move/);
  assert.equal(h.refs.get('v1.1.6'), oldSha);
});

for (const [name, source] of [['Release', workflow], ['Development Build', developmentWorkflow]]) {
  test(`${name} keeps the native ARM asset runtime on macOS 26 and Intel on macOS 15`, () => {
    const macosJob = source.match(/\n  macos:\n[\s\S]*?(?=\n  [\w-]+:|$)/)?.[0];
    assert(macosJob, `${name} must retain its macOS build job`);
    assert.match(macosJob, /runs-on: \$\{\{ matrix\.runner \}\}/);
    assert.match(macosJob, /- arch: arm64\n\s+rid: osx-arm64\n\s+runner: macos-26\n/);
    assert.match(macosJob, /- arch: x64\n\s+rid: osx-x64\n\s+runner: macos-15-intel\n/);
    assert.match(macosJob, /DEVELOPER_DIR: \/Applications\/Xcode_26\.3\.app\/Contents\/Developer\n/);
    assert.match(macosJob, /xcrun assetutil -Z/);
    assert.match(macosJob, /iconutil --convert iconset/);
    assert.match(macosJob, /codesign --verify --deep --strict/);
  });
}

test('all platforms and changelog use the prepared SHA and tag creation stays behind the gate', () => {
  assert.equal((workflow.match(/ref: \$\{\{ needs\.prepare\.outputs\.source_sha \}\}/g) || []).length, 4);
  assert(!prepareScript.includes('createRef'));
  assert.match(workflow, /needs: \[prepare, windows, linux, macos\]/);
  const publishJob = workflow.slice(workflow.indexOf('\n  github-release:'));
  assert(publishJob.indexOf('Create update manifest') < publishJob.indexOf('Create or verify release tag'));
  assert(publishJob.indexOf('Create or verify release tag') < publishJob.indexOf('Create or update GitHub Release'));
});
