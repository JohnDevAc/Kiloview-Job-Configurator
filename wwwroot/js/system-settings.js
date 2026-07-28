export function createSystemSettingsController({ api, $, state, toast }) {
  function renderChannelHelp() {
    const development = $('#updateChannel').value === 'Development';
    $('#updateChannelHelp').textContent = development
      ? 'Development receives prerelease builds for testing and may be less stable.'
      : 'Main receives stable production releases.';
  }

  async function checkForSoftwareUpdate() {
    const button = $('#checkForUpdate');
    const install = $('#installUpdate');
    const status = $('#updateStatus');
    const latest = $('#latestVersion');
    const link = $('#releaseLink');
    try {
      button.disabled = true;
      button.textContent = 'Checking…';
      install.classList.add('hidden');
      latest.textContent = 'Checking GitHub…';
      status.textContent = 'Contacting the selected release feed.';
      state.updateInfo = await api('/api/system/update');
      latest.textContent = `Latest ${state.updateInfo.channel} version ${state.updateInfo.latestVersion}`;
      link.href = state.updateInfo.releaseUrl;
      link.classList.remove('hidden');
      install.querySelector('span').textContent = state.updateInfo.channelSwitch
        ? 'Switch channel'
        : 'Download & install';
      if (state.updateInfo.updateAvailable) {
        status.textContent = state.updateInfo.channelSwitch
          ? `Switch from ${state.updateInfo.currentChannel} ${state.updateInfo.currentVersion} to ${state.updateInfo.channel} ${state.updateInfo.latestVersion} (${(state.updateInfo.downloadSizeBytes / 1048576).toFixed(1)} MB).`
          : `Version ${state.updateInfo.latestVersion} is available (${(state.updateInfo.downloadSizeBytes / 1048576).toFixed(1)} MB).`;
        if (state.systemInfo?.isAdministrator) install.classList.remove('hidden');
        else status.textContent += ' Restart the application as administrator to install it.';
      } else {
        status.textContent = `${state.updateInfo.channel} ${state.updateInfo.currentVersion} is up to date.`;
      }
    } catch (error) {
      state.updateInfo = null;
      latest.textContent = 'Update check unavailable';
      status.textContent = error.message;
      link.classList.add('hidden');
    } finally {
      button.disabled = false;
      button.textContent = 'Check again';
    }
  }

  async function loadSystemSettings() {
    try {
      state.systemInfo = await api('/api/system/info');
      $('#settingsCurrentVersion').textContent = `Version ${state.systemInfo.currentVersion}`;
      $('#installedChannel').textContent = state.systemInfo.currentChannel;
      $('#updateChannel').value = state.systemInfo.selectedChannel;
      renderChannelHelp();
      const administrator = $('#administratorStatus');
      administrator.textContent = state.systemInfo.isAdministrator
        ? 'Administrator'
        : 'Not elevated';
      administrator.className = state.systemInfo.isAdministrator ? 'ok' : 'error-text';
    } catch (error) {
      $('#administratorStatus').textContent = 'Unavailable';
      $('#installedChannel').textContent = 'Unavailable';
      toast(error.message, true);
    }
    await checkForSoftwareUpdate();
  }

  async function changeChannel(event) {
    const select = event.currentTarget;
    try {
      select.disabled = true;
      const result = await api('/api/system/update/channel', {
        method: 'PUT',
        body: JSON.stringify({ channel: select.value })
      });
      state.systemInfo.selectedChannel = result.channel;
      renderChannelHelp();
      await checkForSoftwareUpdate();
      toast(`${result.channel} release channel selected`);
    } catch (error) {
      select.value = state.systemInfo?.selectedChannel || 'Main';
      renderChannelHelp();
      toast(error.message, true);
    } finally {
      select.disabled = false;
    }
  }

  async function installUpdate() {
    if (!state.updateInfo?.updateAvailable) return;
    const info = state.updateInfo;
    const development = info.channel === 'Development';
    const switchWarning = info.channelSwitch
      ? '\n\nThis switches release channels and may install a lower version.'
      : '';
    if (!confirm(
      `${info.channelSwitch ? 'Switch to' : 'Download and install'} ${info.channel} ${info.latestVersion}?`
      + switchWarning
      + (development
        ? '\n\nDevelopment releases are prerelease builds and are not intended for production.'
        : '')
      + '\n\nThe verified installer will open and require EULA acceptance.'
    )) return;

    const button = $('#installUpdate');
    const status = $('#updateStatus');
    try {
      button.disabled = true;
      button.querySelector('span').textContent = 'Downloading…';
      status.textContent = 'Downloading and verifying the selected GitHub release. Keep this page open.';
      const result = await api('/api/system/update/install', { method: 'POST', body: '{}' });
      status.textContent = `${result.channel} installer ${result.version} opened. Accept its EULA to complete the update; this page will briefly disconnect while the service restarts.`;
      toast(`Verified ${result.channel} release ${result.version} is ready to install`);
    } catch (error) {
      status.textContent = error.message;
      toast(error.message, true);
    } finally {
      button.disabled = false;
      button.querySelector('span').textContent = info.channelSwitch
        ? 'Switch channel'
        : 'Download & install';
    }
  }

  async function downloadDiagnostics() {
    const button = $('#downloadDiagnostics');
    try {
      button.disabled = true;
      button.textContent = 'Preparing diagnostics…';
      const response = await fetch('/api/system/diagnostics');
      if (!response.ok) {
        const error = await response.json().catch(() => null);
        throw new Error(error?.error || `Diagnostics download failed (${response.status})`);
      }
      const blob = await response.blob();
      const disposition = response.headers.get('Content-Disposition') || '';
      const match = disposition.match(/filename="?([^";]+)"?/i);
      const link = document.createElement('a');
      link.href = URL.createObjectURL(blob);
      link.download = match?.[1] || 'Kiloview-Job-Configurator-Diagnostics.zip';
      link.click();
      setTimeout(() => URL.revokeObjectURL(link.href), 1000);
      toast('Diagnostics package downloaded');
    } catch (error) {
      toast(error.message, true);
    } finally {
      button.disabled = false;
      button.textContent = 'Download diagnostics';
    }
  }

  $('#updateChannel').addEventListener('change', changeChannel);
  $('#checkForUpdate').addEventListener('click', checkForSoftwareUpdate);
  $('#installUpdate').addEventListener('click', installUpdate);
  $('#downloadDiagnostics').addEventListener('click', downloadDiagnostics);

  return { loadSystemSettings };
}
