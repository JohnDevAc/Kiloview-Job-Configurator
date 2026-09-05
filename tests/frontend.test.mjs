import assert from 'node:assert/strict';
import test from 'node:test';
import { readFile } from 'node:fs/promises';

async function loadModule(path) {
  const source = await readFile(new URL(path, import.meta.url), 'utf8');
  return import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
}
const { windowsCardState, createWindowsMonitor } = await loadModule('../wwwroot/js/windows-monitor.js');
const { createPreviewController } = await loadModule('../wwwroot/js/previews.js');
const windows = createWindowsMonitor({ esc: String, compactDuration: String, compactBytes: String, product: 'PC Agent' });
const appSource = await readFile(new URL('../wwwroot/app.js', import.meta.url), 'utf8');
const signatureExpression = appSource.split(/\r?\n/).find(line => line.includes('cardRemoteWindowsPcs='));
const signature = new Function('app', 'devices', 'localPc', 'localAssignment', 'remoteWindowsPcs', 'pcAgents', 'windowsJobName', 'windowsCardState', `${signatureExpression}; return cardSignature;`);
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
  assert.match(remoteCard, /data-windows-endpoint/);
  assert.match(availableCard, /data-windows-endpoint/);
  assert.match(availableCard, /windowsMetrics\(agent\)/);
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
