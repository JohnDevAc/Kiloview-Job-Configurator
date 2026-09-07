export function createWindowsCards({ esc }) {
  function windowsEndpointCard(endpoint, { className = '', statusLabel = 'ONLINE', statusClass = 'connectivity-online',
    statusTitle = '', context = '', needsAttention = false, isServerPc = endpoint.isServerPc, body = '' } = {}) {
    return `<article data-windows-endpoint="${esc(endpoint.endpointId)}" class="device-card windows-endpoint-card ${esc(className)}">
      <details data-windows-disclosure>
        <summary class="windows-endpoint-summary">
          <span class="windows-endpoint-heading">
            <span class="windows-endpoint-kind">Windows${isServerPc ? ' · Server PC' : ' PC'}</span>
            <span class="windows-endpoint-name" title="${esc(endpoint.hostname)}">${esc(endpoint.hostname)}</span>
            <span class="windows-endpoint-address">${esc(endpoint.address)}${endpoint.prefixLength == null ? '' : `/${esc(endpoint.prefixLength)}`}</span>
          </span>
          <span class="windows-endpoint-aside">
            <span data-connectivity-title class="pill ${esc(statusClass)}" title="${esc(statusTitle)}">${esc(statusLabel)}</span>
            <span class="windows-endpoint-toggle"><span class="windows-endpoint-show">Details</span><span class="windows-endpoint-hide">Less</span><svg viewBox="0 0 16 16" aria-hidden="true"><path d="m4 6 4 4 4-4"/></svg></span>
          </span>
          <span class="windows-endpoint-context"><span>${esc(endpoint.adapterName || 'Windows endpoint')}</span>${context ? `<span>${esc(context)}</span>` : ''}${needsAttention ? '<span class="windows-endpoint-attention">Needs attention</span>' : ''}</span>
        </summary>
        <div class="windows-endpoint-body">${body}</div>
      </details>
    </article>`;
  }

  function captureWindowsExpansion(container) {
    const expanded = new Set();
    container?.querySelectorAll('[data-windows-endpoint]').forEach(card => {
      if (card.querySelector('[data-windows-disclosure]')?.open)
        expanded.add(card.dataset.windowsEndpoint.toLowerCase());
    });
    return expanded;
  }

  function restoreWindowsExpansion(container, expanded) {
    container?.querySelectorAll('[data-windows-endpoint]').forEach(card => {
      const disclosure = card.querySelector('[data-windows-disclosure]');
      if (disclosure) disclosure.open = expanded.has(card.dataset.windowsEndpoint.toLowerCase());
    });
  }

  return { windowsEndpointCard, captureWindowsExpansion, restoreWindowsExpansion };
}
