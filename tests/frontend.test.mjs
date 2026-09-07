import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';

async function loadModule(path) {
  const source = await readFile(new URL(path, import.meta.url), 'utf8');
  return import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
}
const { windowsCardState, createWindowsMonitor } = await loadModule('../wwwroot/js/windows-monitor.js');
const { createPreviewController } = await loadModule('../wwwroot/js/previews.js');
const { createLocalOnboarding } = await loadModule('../wwwroot/js/local-onboarding.js');
const { createWindowsCards } = await loadModule('../wwwroot/js/windows-cards.js');
const windows = createWindowsMonitor({ esc: String, compactDuration: String, compactBytes: String, product: 'PC Agent' });
const appSource = await readFile(new URL('../wwwroot/app.js', import.meta.url), 'utf8');

test('Local onboarding failure stays on the card, permits retry and survives a redraw', async () => {
  let fail = true, completions = 0;
  const controller = createLocalOnboarding({ esc: value => String(value).replaceAll('<', '&lt;'), onChange: () => {},
    onComplete: async () => { completions++; }, api: async (url, options) => {
      if (!options) return { compatible: true };
      if (fail) throw new Error('Close <NDI client> before applying settings.');
      return { endpoint: {} };
    } });
  await controller.onboard();
  const failed = controller.render();
  assert.match(failed, /role="alert"/);
  assert.match(failed, /Close &lt;NDI client>/);
  assert.match(failed, /Retry onboarding/);
  assert.doesNotMatch(failed, /disabled|Applying settings/);
  const replacement = { dataset: { registered: 'false' }, outerHTML: '' };
  controller.sync({ querySelectorAll: selector => selector.includes('controls') ? [replacement] : [] });
  assert.equal(replacement.outerHTML, failed);
  fail = false;
  await controller.onboard();
  assert.equal(completions, 1);
  assert.match(controller.render(true), /Server PC onboarded/);
  assert.doesNotMatch(controller.render(true), /Retry onboarding|role="alert"/);
});

test('Repeated local clicks do not queue another configuration, including during refresh', async () => {
  let release, posted = 0;
  const pending = new Promise(resolve => { release = resolve; });
  const controller = createLocalOnboarding({ esc: String, onChange: () => {}, onComplete: () => pending,
    api: async (url, options) => { if (options) posted++; return { compatible: true }; } });
  const first = controller.onboard();
  await controller.onboard();
  await new Promise(resolve => setImmediate(resolve));
  assert.match(controller.render(), /disabled/);
  await controller.onboard();
  assert.equal(posted, 1);
  release();
  await first;
  assert.doesNotMatch(controller.render(true), /disabled/);
});

test('Display refresh failures cannot claim that a successful onboarding failed', async () => {
  const controller = createLocalOnboarding({ esc: String, onChange: () => {},
    api: async () => ({ compatible: true }), onComplete: async () => { throw Error('Display unavailable'); } });
  await controller.onboard();
  assert.match(controller.render(true), /Settings were saved/);
  assert.doesNotMatch(controller.render(true), /role="alert"|Retry onboarding|disabled/);
});

test('Missing companion prevents mutation and presents an actionable persistent error', async () => {
  let posted = 0;
  const controller = createLocalOnboarding({ esc: String, onChange: () => {}, onComplete: async () => {},
    api: async (url, options) => { if(options) posted++; return { compatible: false, error: 'Install the complete PC Agent package.' }; } });
  await controller.onboard();
  assert.equal(posted, 0);
  assert.match(controller.render(), /Install the complete PC Agent package/);
});
test('Remote onboarding requires attempt and durable outcome support', () => {
  const source = appSource.split(/\r?\n/).find(line => line.startsWith('function pcAgentSupportsRemoteOnboarding('));
  const supports = new Function(`${source};return pcAgentSupportsRemoteOnboarding`)();
  const capabilities = ['remote-onboarding-v2','network-config-v1','onboarding-attempt-v1','onboarding-outcome-v1'];
  assert.equal(supports({capabilities}), true);
  for(const missing of capabilities) assert.equal(supports({capabilities:capabilities.filter(value=>value!==missing)}), false);
  assert.equal(supports({}), false);
});
test('A management-only host can create a remote-PC job without a local companion', () => {
  const selectedFunction = appSource.split(/\r?\n/).find(line => line.startsWith('function updateSelected()'));
  const elements = { '#selectedCount': {}, '#buildPlan': {} };
  const state = { selected: new Set(), skipped: new Set(), includeServerPc: true, localPcComponent: null, networkReady: true };
  const update = new Function('state', '$', selectedFunction + ';updateSelected();');
  update(state, id => elements[id]);
  assert.equal(elements['#buildPlan'].disabled, false);
  assert.match(elements['#selectedCount'].textContent, /remote PCs/);
  state.networkReady = false;
  update(state, id => elements[id]);
  assert.equal(elements['#buildPlan'].disabled, true);
});
test('An immediately completed metadata-only job stops progress polling', async () => {
  const source = appSource.split(/\r?\n/).find(line => line.startsWith('async function pollProgress()'));
  let scheduled = 0, displayed = 0;
  const state = { poll: null };
  const run = new Function('state', 'api', 'renderProgress', 'loadDecoderOrMonitor', 'toast', 'clearInterval', 'setInterval', source + ';return pollProgress();');
  await run(state, async () => ({ status: 'completed' }), () => {}, async () => { displayed++; }, message => { throw Error(message); },
    () => {}, () => { scheduled++; });
  assert.equal(displayed, 1);
  assert.equal(scheduled, 0);
});
const signatureExpression = appSource.split(/\r?\n/).find(line => line.includes('cardWindowsPcs='));
const signature = new Function('app', 'devices', 'localPc', 'localAssignment', 'windowsPcs', 'pcAgents', 'windowsJobName', 'windowsCardState', `${signatureExpression}; return cardSignature;`);
const getSignature = (pcs, agents) => signature({}, [], null, null, pcs, agents, 'Job', windowsCardState);

test('Routine Windows telemetry keeps the monitor grid signature stable', () => {
  const pc = { endpointId: 'pc', connectivityStatus: 'online', agentUptimeSeconds: 10, physicalMemoryAvailableBytes: 200, agentObservedUtc: '2026-09-05T12:00:00Z' };
  const agent = { endpointId: 'agent', status: 'online', observedUtc: '2026-09-05T12:00:00Z', multicastConfiguration: { mode: 'multicast', observedUtc: 'old' } };
  assert.equal(getSignature([pc], [agent]), getSignature(
    [{ ...pc, agentUptimeSeconds: 25, physicalMemoryAvailableBytes: 100, agentObservedUtc: '2026-09-05T12:00:15Z' }],
    [{ ...agent, observedUtc: '2026-09-05T12:00:15Z', multicastConfiguration: { mode: 'multicast', observedUtc: 'new' } }]
  ));
  assert.notEqual(getSignature([pc], [agent]), getSignature([{ ...pc, connectivityStatus: 'offline' }], [agent]));
  assert.notEqual(getSignature([pc], [agent]), getSignature([pc], [{ ...agent, status: 'offline' }]));
  assert.notEqual(getSignature([pc], [agent]), getSignature([pc], [{ ...agent, multicastConfiguration: { mode: 'unicast' } }]));
});

test('Windows metrics and timestamps update existing elements, preserving zero readings', () => {
  const metrics = ['agent', 'machine', 'memory', 'disk'].map(name => ({ dataset: { windowsMetric: name }, textContent: '' }));
  const title = { title: '' };
  const card = { dataset: { windowsEndpoint: 'PC' }, querySelectorAll: selector => selector === '[data-windows-metric]' ? metrics : [title] };
  const root = { querySelectorAll: () => [card] };
  const pc = { endpointId: 'pc', connectivityStatus: 'online', agentUptimeSeconds: 0, machineUptimeSeconds: 20, physicalMemoryAvailableBytes: 0, physicalMemoryTotalBytes: 100, systemDriveFreeBytes: 0, systemDriveTotalBytes: 100, lastSeenUtc: '2026-09-05T12:00:00Z' };
  windows.refreshWindowsMetrics(root, [pc], [{ endpointId: 'pc', agentUptimeSeconds: 999 }]);
  assert.equal(metrics[0].textContent, 'AGENT UP 0');
  assert.equal(metrics[2].textContent, 'MEM FREE 0 B / 100');
  assert.equal(metrics[3].textContent, 'DISK FREE 0 B / 100');
  const previousTitle = title.title;
  windows.refreshWindowsMetrics(root, [{ ...pc, agentUptimeSeconds: 15, lastSeenUtc: '2026-09-05T12:00:15Z' }], []);
  assert.equal(metrics[0].textContent, 'AGENT UP 15');
  assert.notEqual(title.title, previousTitle);
  windows.refreshWindowsMetrics(root, [], [{ ...pc, agentUptimeSeconds: 30 }]);
  assert.equal(metrics[0].textContent, 'AGENT UP 30');
});

test('Both registered and available Windows cards expose metric update targets', () => {
  const remoteCard = appSource.slice(appSource.indexOf('  const remotePcCard='), appSource.indexOf('  const availablePcAgentCard='));
  const availableCard = appSource.split(/\r?\n/).find(line => line.includes('const availablePcAgentCard='));
  assert.match(remoteCard, /windowsEndpointCard\(pc/);
  assert.match(availableCard, /windowsEndpointCard\(agent/);
  assert.match(availableCard, /windowsMetrics\(agent\)/);
});

test('Windows expansion follows endpoint identity through a status redraw or registration', () => {
  const { captureWindowsExpansion, restoreWindowsExpansion } = createWindowsCards({ esc: String });
  const card = (id, open) => ({ dataset: { windowsEndpoint: id }, disclosure: { open }, querySelector() { return this.disclosure; } });
  const root = cards => ({ querySelectorAll: () => cards });
  const expanded = captureWindowsExpansion(root([card('SERVER', true), card('offline', false)]));
  const replacement = [card('server', false), card('OFFLINE', true), card('newly-discovered', false)];
  restoreWindowsExpansion(root(replacement), expanded);
  assert.deepEqual(replacement.map(c => c.disclosure.open), [true, false, false]);
  replacement[0].disclosure.open = false;
  replacement[1].disclosure.open = true;
  const next = [card('server', true), card('offline', false)];
  restoreWindowsExpansion(root(next), captureWindowsExpansion(root(replacement)));
  assert.deepEqual(next.map(c => c.disclosure.open), [false, true]);
});

test('Preview refresh skips hidden and offscreen images and prioritizes healthy captures', async () => {
  const originalFetch = globalThis.fetch, originalDocument = globalThis.document, originalWindow = globalThis.window;
  const calls = [];
  const image = (id, top, status = '') => ({
    dataset: { deviceId: id, previewStatus: status }, isConnected: true,
    getClientRects: () => [{}], getBoundingClientRect: () => ({ top, bottom: top + 20 }),
    closest: () => null
  });
  const images = [image('failed', 20), image('offscreen', 900), image('healthy', 30, 'live'), { ...image('hidden-view', 0), getClientRects: () => [] }];
  try {
    globalThis.document = { hidden: false };
    globalThis.window = { innerHeight: 600 };
    globalThis.fetch = async url => { calls.push(url); throw new Error('Synthetic failure'); };
    const controller = createPreviewController({ state: { previewWarnings: new Map() }, $: () => ({ classList: { contains: () => false } }), $$: () => images, esc: String, isTeleTool: () => false });
    controller.refreshEncoderPreviews();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(calls.length, 2);
    assert.match(calls[0], /healthy/);
    assert.match(calls[1], /failed/);
    document.hidden = true;
    controller.refreshEncoderPreviews();
    assert.equal(calls.length, 2);
  } finally {
    globalThis.fetch = originalFetch;
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
  }
});

test('A detached preview cannot create or retain an object URL', async () => {
  const originalFetch = globalThis.fetch, originalDocument = globalThis.document, originalWindow = globalThis.window, originalCreateUrl = URL.createObjectURL;
  let created = 0;
  const image = { dataset: { deviceId: 'removed' }, isConnected: true, getClientRects: () => [{}], getBoundingClientRect: () => ({ top: 0, bottom: 10 }), closest: () => null };
  try {
    globalThis.document = { hidden: false };
    globalThis.window = { innerHeight: 600 };
    URL.createObjectURL = () => { created++; return 'blob:test'; };
    globalThis.fetch = async () => ({ ok: true, headers: { get: () => 'live' }, blob: async () => { image.isConnected = false; return new Blob(); } });
    createPreviewController({ state: { previewWarnings: new Map() }, $: () => ({ classList: { contains: () => false } }), $$: () => [image], esc: String, isTeleTool: () => false }).refreshEncoderPreviews();
    await new Promise(resolve => setImmediate(resolve));
    assert.equal(created, 0);
  } finally {
    globalThis.fetch = originalFetch;
    globalThis.document = originalDocument;
    globalThis.window = originalWindow;
    URL.createObjectURL = originalCreateUrl;
  }
});
