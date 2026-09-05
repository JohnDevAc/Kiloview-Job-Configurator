export function windowsCardState(endpoint) {
  const {
    lastSeenUtc, lastConnectivityCheckUtc, agentObservedUtc, observedUtc,
    lastDiscoveredUtc, agentUptimeSeconds, machineUptimeSeconds,
    physicalMemoryTotalBytes, physicalMemoryAvailableBytes,
    systemDriveTotalBytes, systemDriveFreeBytes, ...structure
  } = endpoint;
  if (structure.multicastConfiguration) {
    const { observedUtc, ...configuration } = structure.multicastConfiguration;
    structure.multicastConfiguration = configuration;
  }
  return structure;
}

export function createWindowsMonitor({ esc, compactDuration, compactBytes, product }) {
  const bytes = value => value == null ? 'N/A' : Number(value) === 0 ? '0 B' : compactBytes(value);

  function metricsValues(endpoint, agent) {
    return {
      agent: `AGENT UP ${compactDuration(endpoint.agentUptimeSeconds ?? agent?.agentUptimeSeconds)}`,
      machine: `PC UP ${compactDuration(endpoint.machineUptimeSeconds ?? agent?.machineUptimeSeconds)}`,
      memory: `MEM FREE ${bytes(endpoint.physicalMemoryAvailableBytes ?? agent?.physicalMemoryAvailableBytes)} / ${bytes(endpoint.physicalMemoryTotalBytes ?? agent?.physicalMemoryTotalBytes)}`,
      disk: `DISK FREE ${bytes(endpoint.systemDriveFreeBytes ?? agent?.systemDriveFreeBytes)} / ${bytes(endpoint.systemDriveTotalBytes ?? agent?.systemDriveTotalBytes)}`
    };
  }

  function windowsMetrics(endpoint, agent) {
    return `<div class="pc-agent-metrics">${Object.entries(metricsValues(endpoint, agent))
      .map(([key, value]) => `<span data-windows-metric="${key}">${esc(value)}</span>`).join('')}</div>`;
  }

  function windowsConnectivity(pc, agent) {
    const agentProduct = agent?.product || product;
    const status = agent?.status === 'online' ? 'online' : String(pc.connectivityStatus || 'unknown').toLowerCase();
    const online = status === 'online', offline = status === 'offline', stale = status === 'stale';
    const observed = agent?.observedUtc || pc.lastSeenUtc;
    const lastReply = observed ? new Date(observed).toLocaleString() : 'not yet recorded';
    const connectivityTitle = online ? `${agentProduct} reachable · last status ${lastReply}`
      : offline ? `${agentProduct} failed three consecutive status polls · last status ${lastReply}`
        : stale ? `${agentProduct} missed ${pc.consecutiveConnectivityFailures || 1} recent status poll${pc.consecutiveConnectivityFailures === 1 ? '' : 's'} · last status ${lastReply}`
          : `Waiting for the first ${agentProduct} status poll`;
    return {
      agentProduct, online, offline, stale, connectivityTitle,
      statusLabel: online ? 'ONLINE' : offline ? 'OFFLINE' : 'CHECKING',
      healthClass: online ? 'online' : offline ? 'error' : 'configuring',
      statusClass: online ? 'connectivity-online' : offline ? 'connectivity-offline' : stale ? 'connectivity-stale' : 'connectivity-unknown'
    };
  }

  function refreshWindowsMetrics(root, endpoints, agents) {
    const byId = new Map(endpoints.map(endpoint => [String(endpoint.endpointId).toLowerCase(), endpoint]));
    const agentById = new Map(agents.map(agent => [String(agent.endpointId).toLowerCase(), agent]));
    root?.querySelectorAll('[data-windows-endpoint]').forEach(card => {
      const id = card.dataset.windowsEndpoint.toLowerCase();
      const pc = byId.get(id), agent = agentById.get(id), endpoint = pc || agent;
      if (!endpoint) return;
      const values = metricsValues(endpoint, agent);
      card.querySelectorAll('[data-windows-metric]').forEach(metric => {
        metric.textContent = values[metric.dataset.windowsMetric];
      });
      if (pc) {
        const { connectivityTitle } = windowsConnectivity(pc, agent);
        card.querySelectorAll('[data-connectivity-title]').forEach(element => {
          element.title = connectivityTitle;
        });
      }
    });
  }

  return { windowsMetrics, windowsConnectivity, refreshWindowsMetrics };
}
