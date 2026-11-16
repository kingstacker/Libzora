import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { readFileSync, existsSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import vm from 'node:vm';
import test from 'node:test';

const source = readFileSync(new URL('../website/app.js', import.meta.url), 'utf8');
const html = readFileSync(new URL('../website/index.html', import.meta.url), 'utf8');
const snapshotPath = fileURLToPath(new URL('../website/_release.json', import.meta.url));
const snapshot = JSON.parse(readFileSync(snapshotPath, 'utf8'));
const releasePage = 'https://github.com/kingstacker/Libzora/releases/latest';

function element(attributes = {}) {
  const attrs = new Map(Object.entries(attributes));
  const classes = new Set();
  return {
    dataset: { downloadKey: attrs.get('data-download-key') },
    textContent: '',
    get href() { return attrs.get('href'); },
    set href(value) { attrs.set('href', value); },
    getAttribute: name => attrs.get(name) ?? null,
    setAttribute: (name, value) => attrs.set(name, value),
    removeAttribute: name => attrs.delete(name),
    classList: { add: name => classes.add(name), remove: name => classes.delete(name), contains: name => classes.has(name) },
  };
}

function harness() {
  const links = [...html.matchAll(/<a\b([^>]*)>/g)].map(([, tag]) => {
    const attributes = Object.fromEntries([...tag.matchAll(/([\w-]+)="([^"]*)"/g)].map(([, key, value]) => [key, value]));
    return attributes['data-download-key'] ? element(attributes) : null;
  }).filter(Boolean);
  const nodes = new Map(['latest-version', 'release-published', 'release-status'].map(id => [`#${id}`, element()]));
  for (const key of new Set(links.map(link => link.dataset.downloadKey))) {
    nodes.set(`[data-file-meta="${key}"]`, element());
  }
  const document = {
    addEventListener() {},
    querySelector: selector => nodes.get(selector) ?? null,
    querySelectorAll: selector => selector === '[data-download-key]' ? links : [],
  };
  const context = vm.createContext({ document, navigator: {}, window: { location: { hash: '' } }, snapshot });
  vm.runInContext(source, context);
  return { context, links, nodes, run: code => vm.runInContext(code, context) };
}

function nextRelease() {
  const next = structuredClone(snapshot);
  const version = snapshot.tag_name.replace(/^v/, '');
  next.tag_name = 'v9.0.0';
  next.published_at = '2099-01-01T00:00:00Z';
  next.html_url = 'https://github.com/kingstacker/Libzora/releases/tag/v9.0.0';
  for (const asset of next.assets) {
    asset.name = asset.name.replace(version, '9.0.0');
    asset.browser_download_url = `https://github.com/kingstacker/Libzora/releases/download/v9.0.0/${asset.name}`;
  }
  return next;
}

function mockFetch(h, code) {
  h.context.calls = [];
  h.run(`fetchRelease = async (url) => { calls.push(url); ${code} };`);
}

test('all release assets and both source archives have a direct GitHub entry', () => {
  const h = harness();
  h.context.release = snapshot;
  const keys = new Set(h.links.map(link => link.dataset.downloadKey));
  assert.equal(keys.size, 12);
  assert(!keys.has('mac-auto'), 'An unknown Mac architecture must not silently choose ARM64');
  const urls = new Set();
  for (const link of h.links) {
    h.context.key = link.dataset.downloadKey;
    const download = h.run('findReleaseDownload(key, release)');
    assert(download?.browser_download_url, link.dataset.downloadKey);
    assert.equal(link.href, download.browser_download_url, 'Static fallback must match the current snapshot');
    assert.equal(new URL(link.href).hostname, 'github.com');
    urls.add(link.href);
  }
  for (const asset of snapshot.assets) assert(urls.has(asset.browser_download_url), asset.name);
  for (const extension of ['zip', 'tar.gz']) {
    assert(urls.has(`https://github.com/kingstacker/Libzora/archive/refs/tags/${snapshot.tag_name}.${extension}`));
  }
});

test('macOS matches DMG files and never confuses Intel with Apple Silicon', () => {
  const h = harness();
  assert.match(h.run('findReleaseAsset("mac-arm64", snapshot.assets).name'), /-osx-arm64\.dmg$/);
  assert.match(h.run('findReleaseAsset("mac-x64", snapshot.assets).name'), /-osx-x64\.dmg$/);
  assert.match(html, /<details class="platform-download" data-platform="macos" id="download-macos">/);
  assert.doesNotMatch(html, /<section class="section downloads"/);
  assert.doesNotMatch(html, /href="manual\/index.html"/);
  assert.equal((html.match(/id="downloads"/g) || []).length, 1);
});

test('Linux labels distinguish amd64 from ARM64 in both languages', () => {
  const h = harness();
  for (const language of ['zh', 'en']) {
    assert.match(h.run(`translations.${language}.linuxDebX64`), /Intel \/ AMD.*x64/);
    assert.match(h.run(`translations.${language}.linuxDebArm64`), /ARM64/);
  }
  assert.match(h.run('findReleaseAsset("linux-deb-x64", snapshot.assets).name'), /_amd64\.deb$/);
  assert.match(h.run('findReleaseAsset("linux-deb-arm64", snapshot.assets).name'), /_arm64\.deb$/);
});

test('rendered links, version, date, and missing-file state follow the release', () => {
  const h = harness();
  h.run('renderRelease(snapshot)');
  assert.equal(h.nodes.get('#latest-version').textContent, snapshot.tag_name);
  assert.equal(h.nodes.get('#release-published').getAttribute('datetime'), snapshot.published_at);
  for (const link of h.links) {
    assert(link.href);
    assert.equal(link.getAttribute('aria-disabled'), null);
    assert(!link.classList.contains('disabled'));
  }
  const missing = structuredClone(snapshot);
  missing.assets = missing.assets.filter(asset => !/-osx-arm64\.dmg$/.test(asset.name));
  h.context.missing = missing;
  h.run('renderRelease(missing)');
  const mac = h.links.find(link => link.dataset.downloadKey === 'mac-arm64');
  assert.equal(mac.href, undefined);
  assert.equal(mac.getAttribute('aria-disabled'), 'true');
  h.run('renderRelease(snapshot)');
  assert(mac.href.endsWith('-osx-arm64.dmg'));
  assert.equal(mac.getAttribute('aria-disabled'), null);
});

test('a newer GitHub release replaces a stale bundled snapshot', async () => {
  const h = harness();
  h.context.next = nextRelease();
  mockFetch(h, 'return url === "_release.json" ? snapshot : next;');
  await h.run('loadLatestRelease()');
  assert.equal(h.context.calls.length, 2);
  assert.equal(h.nodes.get('#latest-version').textContent, 'v9.0.0');
  for (const link of h.links) assert(link.href.includes('v9.0.0'), link.href);
});

test('GitHub rate limiting keeps working snapshot links and discloses cached data', async () => {
  const h = harness();
  mockFetch(h, 'if (url === "_release.json") return snapshot; throw new Error("HTTP 403");');
  await h.run('loadLatestRelease()');
  assert.equal(h.nodes.get('#latest-version').textContent, snapshot.tag_name);
  assert.equal(h.nodes.get('#release-status').dataset.i18n, 'releaseCached');
  for (const link of h.links) assert.equal(new URL(link.href).hostname, 'github.com');
});

test('an older API response cannot roll back a fresher bundled snapshot', async () => {
  const h = harness();
  h.context.older = { ...snapshot, tag_name: 'v0.1.0', published_at: '2000-01-01T00:00:00Z' };
  mockFetch(h, 'return url === "_release.json" ? snapshot : older;');
  await h.run('loadLatestRelease()');
  assert.equal(h.nodes.get('#latest-version').textContent, snapshot.tag_name);
  assert.equal(h.nodes.get('#release-status').dataset.i18n, 'releaseCached');
});

test('a missing snapshot still loads GitHub; total failure offers the Release page', async () => {
  const h = harness();
  mockFetch(h, 'if (url === "_release.json") throw new Error("HTTP 404"); return snapshot;');
  await h.run('loadLatestRelease()');
  assert.equal(h.nodes.get('#latest-version').textContent, snapshot.tag_name);
  const failed = harness();
  mockFetch(failed, 'throw new Error("offline");');
  await failed.run('loadLatestRelease()');
  assert.equal(failed.nodes.get('#release-status').dataset.i18n, 'releaseFallback');
  for (const link of failed.links) assert.equal(link.href, releasePage);
});

test('source archives require a valid immutable release tag', () => {
  const h = harness();
  for (const tag of ['', undefined, '../master', 'v1.0.0?redirect=evil']) {
    h.context.invalid = { ...snapshot, tag_name: tag };
    assert.equal(h.run('findReleaseDownload("source-zip", invalid)'), undefined);
  }
});

test('the hero and first feature use independent macOS captures for both site languages', () => {
  const pictures = [...html.matchAll(/<picture>([\s\S]*?)<\/picture>/g)].slice(0, 2).map(match => match[1]);
  assert.equal(pictures.length, 2);
  for (const [index, name] of ['library', 'library-grid'].entries()) {
    assert(pictures[index].includes(`assets/${name}.png?v=20261116-platform1`));
    assert(pictures[index].includes(`assets/${name}.webp?v=20261116-platform1`));
    assert(pictures[index].includes(`data-src-zh="assets/${name}.png?v=20261116-platform1"`));
    assert(pictures[index].includes(`data-src-en="assets/${name}-en.png?v=20261116-platform1"`));
    assert(pictures[index].includes(`data-srcset-en="assets/${name}-en.webp?v=20261116-platform1"`));
    assert(!/Windows 布局|Windows-layout/.test(pictures[index]));
  }
  assert(!html.includes('desktopPreviewCaption'));
  assert.match(source, /function applyLocalizedImages\(\)/);
  assert.match(source, /element\.srcset = localizedSourceSet/);
});

test('retired user manual pages and images are absent', () => {
  assert.equal(existsSync(new URL('../website/manual/', import.meta.url)), false);
  assert.equal(existsSync(new URL('../docs/manual/', import.meta.url)), false);
  const sitemap = readFileSync(new URL('../website/sitemap.xml', import.meta.url), 'utf8');
  assert(!sitemap.includes('/manual/'));
});

test('the no-JavaScript build uses the same GitHub package URLs, never mirror URLs', () => {
  const script = fileURLToPath(new URL('./inject_website_noscript.py', import.meta.url));
  const result = spawnSync('python3', ['-I', '-c', [
    'import importlib.util,json,sys',
    'spec=importlib.util.spec_from_file_location("noscript",sys.argv[1])',
    'module=importlib.util.module_from_spec(spec)',
    'spec.loader.exec_module(module)',
    'print(module.build_block(json.load(open(sys.argv[2],encoding="utf-8"))))',
  ].join('\n'), script, snapshotPath], { encoding: 'utf8' });
  assert.equal(result.status, 0, result.stderr);
  const urls = [...result.stdout.matchAll(/<li><a href="([^"]*)"/g)].map(([, url]) => url);
  const packages = snapshot.assets.filter(asset => /\.(exe|zip|dmg|deb|tar\.gz)$/i.test(asset.name));
  assert.equal(urls.length, 8);
  assert.deepEqual(urls.sort(), packages.map(asset => asset.browser_download_url).sort());
  assert(!result.stdout.includes('libzora.stacker.beauty/dl/'));
});
