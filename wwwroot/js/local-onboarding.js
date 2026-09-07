export function createLocalOnboarding({ api, esc, onChange, onComplete }) {
  let busy = false;
  let status = null;
  let contextKey;

  function setContext(key) {
    if (contextKey !== undefined && contextKey !== key && !busy) status = null;
    contextKey = key;
  }

  function update(next) {
    status = next;
    onChange();
  }

  function render(registered = false) {
    const label = busy ? status?.kind === 'success' ? 'Refreshing card…' : 'Applying settings…' : status?.kind === 'error' ? 'Retry onboarding'
      : registered || status?.kind === 'success' ? 'Reapply NDI settings' : 'Onboard this server PC';
    const message = status ? `<div class="local-onboarding-status ${status.kind}" role="${status.kind === 'error' ? 'alert' : 'status'}"><strong>${esc(status.title)}</strong><span>${esc(status.message)}</span></div>` : '';
    return `<div class="local-onboarding-controls" data-local-onboarding-controls data-registered="${registered}" aria-busy="${busy}">${message}<div class="card-actions"><button type="button" class="local-onboarding-primary" data-local-onboarding-action="onboard" ${busy ? 'disabled' : ''}>${label}</button></div></div>`;
  }

  function sync(root) {
    root.querySelectorAll('[data-local-onboarding-controls]').forEach(element => {
      element.outerHTML = render(element.dataset.registered === 'true');
    });
    root.querySelectorAll('[data-local-onboarding-remove]').forEach(button => { button.disabled = busy; });
  }

  async function onboard() {
    if (busy) return;
    busy = true;
    update({ kind: 'pending', title: 'Checking PC Agent', message: 'Checking the installed companion before applying this job.' });
    try {
      const component = await api('/api/pc-onboarding/local');
      if (!component?.compatible) throw new Error(component?.error || 'Install or update the PC Agent component, then retry.');
      update({ kind: 'pending', title: 'Applying NDI settings', message: 'Configuring the preferred interface, job groups and Discovery Server. This can take up to three minutes.' });
      await api('/api/pc-onboarding/local', { method: 'POST', body: '{}' });
    } catch (error) {
      busy = false;
      update({ kind: 'error', title: 'Onboarding did not complete', message: error.message || 'The server could not complete onboarding. Check its connection and retry.' });
      return;
    }
    // A display refresh failure must not turn a successful configuration into a failed onboarding.
    update({ kind: 'success', title: 'Server PC onboarded', message: 'NDI settings verified and this PC registered with the job.' });
    try {
      await onComplete();
    } catch {
      update({ kind: 'success', title: 'Server PC onboarded', message: 'Settings were saved. The card could not refresh; reload the page to see the updated endpoint.' });
    } finally {
      busy = false;
      onChange();
    }
  }

  return { render, sync, onboard, setContext };
}
