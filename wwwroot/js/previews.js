export function createPreviewController({ state, $, $$, esc, isTeleTool }) {
  function clearPreviewWarning(id) {
    const warning = state.previewWarnings.get(id);
    if (warning?.objectUrl) URL.revokeObjectURL(warning.objectUrl);
    state.previewWarnings.delete(id);
  }

  function encoderPreview(device) {
    const teleTool = isTeleTool(device);
    const label = teleTool ? 'NDI PREVIEW' : 'HDMI INPUT PREVIEW';
    const latched = teleTool && device.streamRunning ? state.previewWarnings.get(device.id) : null;
    const source = latched?.objectUrl
      || `/api/devices/${encodeURIComponent(device.id)}/thumbnail?t=${Date.now()}`;
    const objectUrl = latched?.objectUrl
      ? ` data-object-url="${esc(latched.objectUrl)}"`
      : '';
    const alert = teleTool
      ? `<div class="preview-alert ${latched ? '' : 'hidden'}" data-preview-alert><strong>NDI PREVIEW WARNING</strong><span>No video after two attempts · Check NDI output, group and Discovery Server.</span></div>`
      : '';
    return `<div class="encoder-preview${latched ? ' warning' : ''}" data-teletool="${teleTool}" data-stream-active="${teleTool && device.streamRunning}" data-failures="${latched?.failures || 0}"><img loading="lazy" data-device-id="${esc(device.id)}" src="${esc(source)}"${objectUrl} alt="${label.toLowerCase()} for ${esc(device.hostname)}"><span>${label} · 5s</span>${alert}</div>`;
  }

  function setPreviewStatus(image, status, failures = 0) {
    const preview = image.closest('.encoder-preview');
    const streamActive = preview?.dataset.teletool === 'true'
      && preview.dataset.streamActive === 'true';
    const warning = streamActive
      && status !== 'live'
      && (status === 'warning' || state.previewWarnings.has(image.dataset.deviceId));
    const card = preview?.closest('.teletool-card');
    const alert = card?.querySelector('[data-preview-alert]');
    preview?.classList.toggle('warning', warning);
    card?.classList.toggle('preview-error', warning);
    alert?.classList.toggle('hidden', !warning);
    if (preview) preview.dataset.failures = String(failures);
    image.dataset.previewStatus = status;
  }

  function releasePreviewObjectUrls(root) {
    const latchedUrls = new Set(
      [...state.previewWarnings.values()].map(warning => warning.objectUrl)
    );
    root?.querySelectorAll('.encoder-preview img').forEach(image => {
      if (image.dataset.objectUrl && !latchedUrls.has(image.dataset.objectUrl)) {
        URL.revokeObjectURL(image.dataset.objectUrl);
      }
    });
  }

  async function refreshEncoderPreview(image) {
    if (image.dataset.refreshing === '1') return;
    image.dataset.refreshing = '1';
    const id = image.dataset.deviceId;
    const preview = image.closest('.encoder-preview');
    try {
      const response = await fetch(
        `/api/devices/${encodeURIComponent(id)}/thumbnail?t=${Date.now()}`,
        { cache: 'no-store' }
      );
      if (!response.ok) throw new Error(`Preview request failed (${response.status})`);
      const status = response.headers.get('X-Kiloview-Preview') || 'unavailable';
      const failures = Number(response.headers.get('X-Kiloview-Preview-Failures') || 0);
      const blob = await response.blob();
      if (!image.isConnected) return;
      const latched = state.previewWarnings.get(id);
      const streamActive = preview?.dataset.streamActive === 'true';
      if (status === 'live') {
        const next = URL.createObjectURL(blob);
        const previous = image.dataset.objectUrl;
        image.src = next;
        image.dataset.objectUrl = next;
        if (previous && previous !== latched?.objectUrl) URL.revokeObjectURL(previous);
        clearPreviewWarning(id);
      } else if (status === 'warning' && streamActive) {
        const next = URL.createObjectURL(blob);
        const previous = image.dataset.objectUrl;
        const obsolete = new Set([previous, latched?.objectUrl]);
        image.src = next;
        image.dataset.objectUrl = next;
        state.previewWarnings.set(id, { objectUrl: next, failures });
        obsolete.delete(next);
        obsolete.forEach(url => {
          if (url) URL.revokeObjectURL(url);
        });
      } else if (!latched || !streamActive) {
        const next = URL.createObjectURL(blob);
        const previous = image.dataset.objectUrl;
        image.src = next;
        image.dataset.objectUrl = next;
        if (previous) URL.revokeObjectURL(previous);
        if (!streamActive) clearPreviewWarning(id);
      }
      setPreviewStatus(image, status, failures);
    } catch {
      const failures = Number(preview?.dataset.failures || 0) + 1;
      setPreviewStatus(image, 'unavailable', failures);
    } finally {
      image.dataset.refreshing = '0';
    }
  }

  function refreshEncoderPreviews() {
    if (document.hidden || ($('#decoderView').classList.contains('hidden')
        && $('#monitorView').classList.contains('hidden'))) return;
    $$('.encoder-preview img')
      .filter(image => {
        if (!image.isConnected || !image.getClientRects().length) return false;
        const bounds = image.getBoundingClientRect();
        return bounds.bottom > 0 && bounds.top < window.innerHeight;
      })
      .sort((left, right) => Number(right.dataset.previewStatus === 'live') - Number(left.dataset.previewStatus === 'live'))
      .forEach(refreshEncoderPreview);
  }

  return {
    clearPreviewWarning,
    encoderPreview,
    refreshEncoderPreviews,
    releasePreviewObjectUrls
  };
}
