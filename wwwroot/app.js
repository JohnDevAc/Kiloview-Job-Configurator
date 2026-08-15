import { api, upload } from './js/api.js';
import { createPreviewController } from './js/previews.js';
import { createSystemSettingsController } from './js/system-settings.js';

const PC_AGENT_PRODUCT='NDI Configurator PC Agent';
const $=s=>document.querySelector(s), $$=s=>[...document.querySelectorAll(s)];
const state={settings:null,devices:[],discovery:null,selected:new Set(),skipped:new Set(),plan:null,multicastPlan:null,multicastConfigured:false,poll:null,afterFirmware:null,firmwarePreOnboarding:false,titleCardIds:new Set(),titleCardSources:new Map(),previewWarnings:new Map(),monitorCardSignature:null,returnView:'setup',systemInfo:null,updateInfo:null,infrastructureDetecting:false,infrastructureRerun:false,localInterfaces:[],selectedNetwork:null,networkReady:false,localPc:null,ndiPreflight:null,pcAgents:[],pcOnboardingPolls:new Map()};
const views=['setup','discover','progress','firmware','decoder','monitor','multicast','settings'];
function show(name){views.forEach(v=>$(`#${v}View`).classList.toggle('hidden',v!==name));scrollTo({top:0,behavior:'smooth'});if(name==='setup'){if(state.networkReady)setTimeout(detectInfrastructure,0);setTimeout(refreshNdiPreflight,0)}}
function toast(message,error=false){const el=$('#toast');el.textContent=message;el.className=error?'show error':'show';setTimeout(()=>el.className='',4200)}
function esc(v=''){return String(v).replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function roleClass(r){return String(r).toLowerCase()}
function isTeleTool(d){return d?.family==='TeleTool'||d?.family==='SimulatedTeleTool'||d?.model==='TeleTool'}
function deviceUrl(d){return `http://${d.ipAddress}${d.webPort&&d.webPort!==80?`:${d.webPort}`:''}`}
function windowsModel(version){
  const raw=String(version||'').replace(/^Microsoft\s+Windows\s*/i,'').replace(/^Windows\s*/i,'').replace(/^NT\s*/i,'').trim(),numeric=raw.match(/(\d+)\.(\d+)\.(\d+)/);
  let os=raw;
  if(numeric){const major=Number(numeric[1]),minor=Number(numeric[2]),build=Number(numeric[3]);os=major===10?`${build>=22000?'11':'10'} (${build})`:`${major}.${minor} (${build})`}
  else if(os.length>18)os=`${os.slice(0,17).trimEnd()}…`;
  return `Windows · ${esc(os||'unknown')}`;
}
function deviceTypeBadge(type,detail){return `<span class="model device-type-model"><strong>${esc(type)}</strong><span>${esc(detail)}</span></span>`}
function windowsBadge(version){return `<span class="model device-type-model"><strong>WINDOWS</strong><span>${windowsModel(version).replace(/^Windows\s*(?:Â·|·)\s*/,'')}</span></span>`}
function windowsEndpointPills({statusLabel,statusClass,statusTitle,preferred,adapterName,ndiToolsVersion,jobName,assignment,multicastApplied=true}){
  const title=statusTitle?` title="${esc(statusTitle)}"`:'';
  return `<span class="pill ${esc(statusClass)}"${title}>${esc(statusLabel)}</span><span class="pill ${preferred?'encoder':'standalone'}">${preferred?'PREFERRED NDI INTERFACE':'INTERFACE ATTENTION'}</span>${adapterName?`<span class="pill">${esc(adapterName)}</span>`:''}<span class="pill">NDI ${esc(ndiToolsVersion||'UNKNOWN')}</span>${jobName?`<span class="pill">${esc(jobName)}</span>`:''}${assignment?.netPrefix?`<span class="pill ${multicastApplied?'multicast-pill':'standalone'}">MC ${esc(assignment.netPrefix)}/${prefixLength(assignment.netmask)}</span><span class="pill">TTL ${esc(assignment.ttl)}</span>`:'<span class="pill">UNICAST READY</span>'}`;
}
function compactDuration(seconds){const value=Math.max(0,Number(seconds)||0),days=Math.floor(value/86400),hours=Math.floor(value%86400/3600),minutes=Math.floor(value%3600/60);return days?`${days}d ${hours}h`:hours?`${hours}h ${minutes}m`:`${minutes}m`}
function compactBytes(bytes){const value=Math.max(0,Number(bytes)||0);if(!value)return 'N/A';const units=['B','KB','MB','GB','TB'],index=Math.min(units.length-1,Math.floor(Math.log(value)/Math.log(1024)));return `${(value/1024**index).toFixed(index<3?0:1)} ${units[index]}`}
async function loadPcAgents(refresh=false){if(refresh)try{await api('/api/pc-agents/discover',{method:'POST',body:'{}'})}catch{};try{state.pcAgents=await api('/api/pc-agents')}catch{state.pcAgents=[]};return state.pcAgents}
function pcAgentSupportsRemoteOnboarding(agent){const capabilities=agent.capabilities||[];return capabilities.includes('remote-onboarding-v2')&&capabilities.includes('network-config-v1')}
function remoteOnboardingEditor(agent,registered=false){
  if(registered)return '';
  if(!pcAgentSupportsRemoteOnboarding(agent))return `<div class="local-pc-drift"><strong>${PC_AGENT_PRODUCT.toUpperCase()} UPDATE REQUIRED</strong><span>Install an agent that supports managed remote onboarding and guarded network configuration.</span></div>`;
  const network=agent.networkConfiguration||{},gateway=network.defaultGateways?.[0]||'',dns=(network.dnsServers||[]).join(', '),operation=agent.remoteOnboarding,status=operation?`<div class="remote-onboarding-state ${esc(operation.status)}"><strong>${esc(String(operation.status).replaceAll('-',' ').toUpperCase())}</strong><span>${esc(operation.message)}</span></div>`:'';
  return `<details class="remote-onboarding-editor"><summary>${registered?'Reconfigure this PC':'Remote onboarding'}</summary><form data-remote-onboarding-form data-endpoint-id="${esc(agent.endpointId)}" data-endpoint-name="${esc(agent.hostname)}"><label><span>Selected adapter</span><select name="adapterId"><option value="${esc(agent.adapterId)}">${esc(agent.adapterName)} · ${esc(agent.adapterId)}</option></select></label><label><span>Windows IPv4 mode</span><select name="mode" data-remote-network-mode><option value="unchanged">Keep current settings</option><option value="dhcp">Use DHCP</option><option value="static">Apply static settings</option></select></label><div class="remote-static-fields hidden" data-remote-static-fields><label><span>IPv4 address</span><input name="address" value="${esc(agent.address)}" inputmode="decimal"></label><label><span>Subnet mask</span><input name="subnetMask" value="${esc(subnetMaskFromPrefix(agent.prefixLength))}" inputmode="decimal" placeholder="255.255.255.0"></label><label><span>Default gateway <small>(optional)</small></span><input name="defaultGateway" value="${esc(gateway)}" inputmode="decimal"></label><label><span>DNS servers <small>(comma separated; blank clears)</small></span><input name="dnsServers" value="${esc(dns)}" inputmode="decimal"></label></div>${status}<div class="remote-onboarding-warning">The endpoint user must approve this request locally. Windows and NDI network settings may change after UAC.</div><button type="submit">Request local approval</button></form></details>`;
}
function captureRemoteOnboardingEditors(container){
  const drafts=new Map();
  container?.querySelectorAll('[data-remote-onboarding-form]').forEach(form=>{const values={};[...form.elements].filter(element=>element.name).forEach(element=>values[element.name]=element.value);drafts.set(form.dataset.endpointId,{open:form.closest('details')?.open===true,values})});
  return drafts;
}
function restoreRemoteOnboardingEditors(container,drafts){
  container?.querySelectorAll('[data-remote-onboarding-form]').forEach(form=>{const draft=drafts.get(form.dataset.endpointId);if(!draft)return;for(const[name,value]of Object.entries(draft.values))if(form.elements[name])form.elements[name].value=value;const details=form.closest('details');if(details)details.open=draft.open;updateRemoteOnboardingForm(form)});
}
const {clearPreviewWarning,encoderPreview,refreshEncoderPreviews,releasePreviewObjectUrls}=createPreviewController({state,$,$$,esc,isTeleTool});
const {loadSystemSettings}=createSystemSettingsController({api,$,state,toast});
const serviceConnections=$('#serviceConnections'),serviceConnectionsStateText=$('#serviceConnectionsStateText'),toggleServiceConnections=$('#toggleServiceConnections'),serviceChecks={kiloLinkFound:false,kiloLinkAuthenticated:false,ndiDiscoveryFound:false};
function setServiceConnectionsExpanded(expanded){
  serviceConnections.classList.toggle('is-collapsed',!expanded);
  toggleServiceConnections.setAttribute('aria-expanded',String(expanded));
  $('#serviceConnectionsBody').setAttribute('aria-hidden',String(!expanded));
  toggleServiceConnections.querySelector('span').textContent=expanded?'Minimize':'Show details';
}
function setServiceConnectionsStatus(mode,message){
  serviceConnections.classList.remove('is-checking','is-ready','needs-attention');
  serviceConnections.classList.add(mode);
  serviceConnectionsStateText.textContent=message;
}
function updateServiceConnectionsStatus(checking=false){
  if(checking){setServiceConnectionsStatus('is-checking','Checking services…');return}
  const ready=serviceChecks.kiloLinkFound&&serviceChecks.kiloLinkAuthenticated&&serviceChecks.ndiDiscoveryFound;
  if(ready){setServiceConnectionsStatus('is-ready','All services connected');setServiceConnectionsExpanded(false)}
  else{setServiceConnectionsStatus('needs-attention','Service attention required');setServiceConnectionsExpanded(true)}
}
toggleServiceConnections.onclick=()=>setServiceConnectionsExpanded(serviceConnections.classList.contains('is-collapsed'));
const onboardingNetworkAdapter=$('#onboardingNetworkAdapter'),onboardingNetworkMeta=$('#onboardingNetworkMeta'),onboardingLocalPcCard=$('#onboardingLocalPcCard');
function ipv4Number(value){const parts=String(value).split('.').map(Number);return parts.length===4&&parts.every(part=>Number.isInteger(part)&&part>=0&&part<=255)?parts.reduce((number,part)=>((number<<8)|part)>>>0,0):null}
function addressMatchesNetwork(address,network){
  const value=ipv4Number(address),local=ipv4Number(network.address),prefix=Math.max(0,Math.min(32,Number(network.prefixLength)));
  if(value===null||local===null)return false;
  const mask=prefix===0?0:(0xffffffff<<(32-prefix))>>>0;
  return (value&mask)===(local&mask);
}
function selectedNetworkOption(){
  const index=Number(onboardingNetworkAdapter.value);
  return onboardingNetworkAdapter.value!==''&&Number.isInteger(index)?state.localInterfaces[index]||null:null;
}
function renderOnboardingLocalPcCard(){
  const network=state.selectedNetwork,pc=state.localPc,preflight=state.ndiPreflight;
  const running=[...(preflight?.accessManagerRunning?['NDI Access Manager']:[]),...(preflight?.runningConfigurationApplications||[])];
  const preferred=pc?.preferredInterfaceConfigured===true,ready=preferred&&preflight?.ready===true;
  const hostname=pc?.hostname||'This Windows PC',address=network?.address||pc?.address,prefix=network?.prefixLength??pc?.prefixLength,interfaceName=network?.name||pc?.adapterName;
  const attention=!network
    ? 'Select the primary network adapter to onboard this PC.'
    : pc?.error
      ? pc.error
      : !preflight
        ? 'NDI application status could not be checked. Use Check NDI applications again before continuing.'
      : running.length
        ? `Close ${running.join(', ')} before continuing. Reopen client applications after onboarding so they load the new NDI settings.`
        : '';
  onboardingLocalPcCard.classList.toggle('configuration-drift',!ready);
  onboardingLocalPcCard.innerHTML=`<header>${windowsBadge(pc?.operatingSystemVersion)}<i class="health ${ready?'online':network?'configuring':'offline'}" title="${ready?'Ready for onboarding':'Needs attention'}"></i></header><h3>${esc(hostname)}</h3><div class="ip">${address?`${esc(address)}/${esc(prefix)}`:'NO ADAPTER SELECTED'}</div><div class="meta"><span class="pill ${preferred?'encoder':'standalone'}">${preferred?'PREFERRED NDI INTERFACE':'INTERFACE NOT APPLIED'}</span>${interfaceName?`<span class="pill">${esc(interfaceName)}</span>`:''}<span class="pill encoder">LOCAL PC</span></div>${ready?'':`<div class="local-pc-drift"><strong>LOCAL PC ATTENTION</strong><span>${esc(attention)}</span></div>`}<div class="card-actions"><button type="button" data-local-onboarding-action="refresh">Check readiness again</button></div>`;
}
function renderOnboardingPcAgents(){
  const container=$('#onboardingPcAgentCards');if(!container)return;
  const drafts=captureRemoteOnboardingEditors(container);
  const agents=state.pcAgents||[];
  $('#windowsEndpointDiscoveryCount').textContent=`${agents.length+1} ENDPOINT${agents.length?'S':''}`;
  container.innerHTML=agents.map(agent=>`<article class="device-card pc-agent-card"><header>${windowsBadge(agent.operatingSystemVersion)}<i class="health online" title="${esc(agent.product||PC_AGENT_PRODUCT)} online"></i></header><h3>${esc(agent.hostname)}</h3><div class="ip">${esc(agent.address)}/${esc(agent.prefixLength)}</div><div class="meta"><span class="pill agent-product">${esc(agent.product||PC_AGENT_PRODUCT)}</span><span class="pill connectivity-online">ONLINE</span><span class="pill">${esc(agent.adapterName)}</span><span class="pill">NDI ${esc(agent.ndiToolsVersion||'NOT INSTALLED')}</span><span class="pill">VERSION ${esc(agent.agentVersion)}</span><span class="pill">JOBS ${esc(agent.memberships?.length||0)}</span>${agent.registered?'<span class="pill encoder">ONBOARDED</span>':'<span class="pill standalone">AVAILABLE</span>'}</div>${remoteOnboardingEditor(agent,agent.registered)}</article>`).join('');
  restoreRemoteOnboardingEditors(container,drafts);
}
async function refreshNdiPreflight(){
  try{state.ndiPreflight=await api('/api/ndi/preflight')}
  catch{state.ndiPreflight=null}
  renderOnboardingLocalPcCard();
  return state.ndiPreflight;
}
function showSelectedNetwork(network){
  state.selectedNetwork=network;state.networkReady=!!network;
  const localStatus=state.localPc
    ? state.localPc.preferredInterfaceConfigured
      ? ' · local PC onboarded in NDI Access Manager'
      : ` · local PC needs attention: ${state.localPc.error||'preferred interface not applied'}`
    : '';
  onboardingNetworkMeta.textContent=network
    ? `${network.name} · ${network.address}/${network.prefixLength} · device scan ${network.scanCidr||'uses this subnet'}${localStatus}`
    : 'Choose the interface connected to the Kiloview, TeleTool, KiloLink, and NDI network.';
  renderOnboardingLocalPcCard();
}
async function persistNetworkSelection(network=selectedNetworkOption()){
  if(!network){showSelectedNetwork(null);throw new Error('Select the network adapter connected to the devices.')}
  const selected=await api('/api/network/selection',{method:'PUT',body:JSON.stringify({adapterId:network.id,address:network.address})});
  state.localPc=selected.localPc||null;
  state.ndiPreflight=selected.ndiPreflight||state.ndiPreflight;
  const merged={...network,...selected};showSelectedNetwork(merged);return merged;
}
async function loadNetworkSelection(app){
  state.localPc=app.localPc||null;
  await refreshNdiPreflight();
  state.localInterfaces=await api('/api/network/interfaces');
  onboardingNetworkAdapter.innerHTML='<option value="">Select an active network adapter</option>'+state.localInterfaces.map((candidate,index)=>`<option value="${index}">${esc(candidate.name)} · ${esc(candidate.address)}/${candidate.prefixLength} · ${esc(candidate.type)}</option>`).join('');
  let selected=state.localInterfaces.find(candidate=>candidate.id===app.selectedNetworkAdapterId&&candidate.address===app.selectedNetworkAddress)
    ||state.localInterfaces.find(candidate=>candidate.id===app.selectedNetworkAdapterId);
  const previousLocal=app.multicast?.assignments?.find(assignment=>assignment.endpointId==='local-pc')?.address;
  if(!selected&&previousLocal)selected=state.localInterfaces.find(candidate=>candidate.address===previousLocal);
  if(!selected){
    const devices=app.devices||[],ranked=state.localInterfaces.map(candidate=>({candidate,matches:devices.filter(device=>addressMatchesNetwork(device.ipAddress,candidate)).length})).sort((a,b)=>b.matches-a.matches);
    if(ranked.length&&ranked[0].matches>0&&(ranked.length===1||ranked[0].matches>ranked[1].matches))selected=ranked[0].candidate;
  }
  if(!selected&&state.localInterfaces.length===1)selected=state.localInterfaces[0];
  onboardingNetworkAdapter.value=selected?String(state.localInterfaces.indexOf(selected)):'';
  if(selected){
    await persistNetworkSelection(selected);
    app.localPc=state.localPc;
  }
  else{
    showSelectedNetwork(null);
    setServiceConnectionsStatus('needs-attention','Select network adapter');
    setServiceConnectionsExpanded(false);
  }
}
onboardingNetworkAdapter.onchange=async()=>{
  try{
    const selected=await persistNetworkSelection();
    if(!selected.localPc?.preferredInterfaceConfigured)
      toast(selected.localPc?.error||'The preferred NDI interface could not be applied.',true);
    Object.assign(serviceChecks,{kiloLinkFound:false,kiloLinkAuthenticated:false,ndiDiscoveryFound:false});
    if(state.infrastructureDetecting){state.infrastructureRerun=true;return}
    await detectInfrastructure();
  }catch(err){toast(err.message,true)}
};

async function boot(){
  try{const health=await api('/api/health'),development=String(health.channel).toLowerCase()==='development';$('#appVersion').textContent=`v${health.version}`;$('#headerVersion').textContent=`Version v${health.version}${development?' · DEV':''}`;$('#developmentBanner').classList.toggle('hidden',!development);document.body.classList.toggle('development-build',development);$('#serviceDot').className='online';const [app]=await Promise.all([api('/api/state'),loadPcAgents()]);state.devices=app.devices||[];await loadNetworkSelection(app);const localOnboarded=app.localPc||app.multicast?.assignments?.some(a=>a.endpointId==='local-pc'),remoteOnboarded=(app.remoteWindowsPcs||[]).length>0;if(app.lastJob&&(state.devices.some(d=>d.isOnboarded)||localOnboarded||remoteOnboarded)){renderMonitor(app);show('monitor')}else show('setup')}
  catch(e){toast(`Service unavailable: ${e.message}`,true)}
}

$('#setupForm').addEventListener('submit',async e=>{
  e.preventDefault();const f=new FormData(e.currentTarget);state.settings={kiloLinkServerIp:f.get('kiloLinkServerIp').trim(),kiloLinkOnboardingCode:'',kiloLinkUsername:f.get('kiloLinkUsername').trim(),kiloLinkPassword:f.get('kiloLinkPassword'),kiloLinkPort:+f.get('kiloLinkPort'),kiloLinkWebPort:+f.get('kiloLinkWebPort'),ndiDiscoveryServerIp:f.get('ndiDiscoveryServerIp').trim(),staticStart:f.get('staticStart').trim(),staticEnd:f.get('staticEnd').trim(),subnetMask:f.get('subnetMask').trim(),gateway:f.get('gateway').trim(),dns:f.get('dns').trim(),jobName:f.get('jobName').trim(),cleanOnboarding:f.get('cleanOnboarding')==='on',deviceIds:[]};
  const simulation=f.get('simulation')==='on';
  try{e.submitter.disabled=true;e.submitter.querySelector('span').textContent='Scanning…';const network=await persistNetworkSelection();if(!network.localPc?.preferredInterfaceConfigured)throw new Error(network.localPc?.error||'Apply the preferred NDI interface before continuing.');if(network.ndiPreflight?.ready!==true){if(!network.ndiPreflight)throw new Error('NDI application status could not be checked. Check NDI applications again before scanning.');const running=[...(network.ndiPreflight.accessManagerRunning?['NDI Access Manager']:[]),...(network.ndiPreflight.runningConfigurationApplications||[])];throw new Error(`Close ${running.join(', ')} before scanning. The NDI Discovery Server should remain running.`)}const [result]=await Promise.all([api('/api/discovery',{method:'POST',body:JSON.stringify({credentials:{username:f.get('username')||'admin',password:f.get('password')||'admin'},simulation,cleanOnboarding:f.get('cleanOnboarding')==='on'})}),loadPcAgents(true)]);state.devices=result.devices;state.discovery=result;state.skipped=new Set();state.selected=new Set(result.devices.filter(d=>d.canOnboard!==false&&!d.isOnboarded).map(d=>d.id));renderDiscovery(result);show('discover')}
  catch(err){toast(err.message,true)}finally{e.submitter.disabled=false;e.submitter.querySelector('span').textContent='Scan network'}
});
const kiloLinkIp=$('input[name="kiloLinkServerIp"]'),kiloLinkUser=$('input[name="kiloLinkUsername"]'),kiloLinkPassword=$('input[name="kiloLinkPassword"]'),credentialHint=$('#kiloLinkCredentialHint'),storedCredential=$('#kiloLinkStoredCredential'),storedUsername=$('#kiloLinkStoredUsername'),storedPassword=$('#kiloLinkStoredPassword'),revealStoredPassword=$('#showKiloLinkStoredPassword'),storedSecurityNote=$('#kiloLinkStoredSecurityNote');
function maskStoredKiloLinkPassword(){storedPassword.textContent='••••••••';storedPassword.dataset.revealed='false';revealStoredPassword.textContent='View'}
function hideStoredKiloLinkCredential(){storedCredential.classList.add('hidden');storedCredential.dataset.serverIp='';storedUsername.textContent='';maskStoredKiloLinkPassword()}
async function refreshKiloLinkCredentials(){
  const serverIp=kiloLinkIp.value.trim();
  if(!serverIp){hideStoredKiloLinkCredential();credentialHint.textContent='Stored locally in Windows Credential Manager. Leave blank when reusing a stored login.';return false}
  try{
    const status=await api(`/api/kilolink/credentials?serverIp=${encodeURIComponent(serverIp)}`);
    if(status.stored){
      const username=status.username||'Stored user';
      kiloLinkUser.value=username;kiloLinkPassword.value='';storedUsername.textContent=username;maskStoredKiloLinkPassword();revealStoredPassword.classList.toggle('hidden',status.canReveal!==true);storedSecurityNote.textContent=status.canReveal===true?'The password remains protected until View is selected. Leave the password field blank to use it.':'To view the password, open this page on the setup PC using localhost.';storedCredential.dataset.serverIp=serverIp;storedCredential.classList.remove('hidden');
      credentialHint.textContent=`Stored login found for ${username}. Leave the password blank to reuse it.`;
      return true;
    }else{
      hideStoredKiloLinkCredential();credentialHint.textContent='No stored login was found for this server. Enter a username and password.';
      return false;
    }
  }catch{hideStoredKiloLinkCredential();credentialHint.textContent='Enter a valid KiloLink server IP to check stored credentials.';return false}
}
revealStoredPassword.onclick=async()=>{
  if(storedPassword.dataset.revealed==='true'){maskStoredKiloLinkPassword();return}
  const serverIp=storedCredential.dataset.serverIp;
  if(!serverIp)return;
  try{
    revealStoredPassword.disabled=true;revealStoredPassword.textContent='Loading…';
    const credential=await api(`/api/kilolink/credentials/reveal?serverIp=${encodeURIComponent(serverIp)}`,{method:'POST'});
    if(storedCredential.dataset.serverIp!==serverIp)return;
    storedPassword.textContent=credential.password;storedPassword.dataset.revealed='true';revealStoredPassword.textContent='Hide';
  }catch(err){maskStoredKiloLinkPassword();toast(err.message,true)}
  finally{revealStoredPassword.disabled=false}
};
async function detectInfrastructure(){
  if(state.infrastructureDetecting)return;
  state.infrastructureDetecting=true;
  Object.assign(serviceChecks,{kiloLinkFound:false,kiloLinkAuthenticated:false,ndiDiscoveryFound:false});
  setServiceConnectionsExpanded(false);
  updateServiceConnectionsStatus(true);
  try{
    const kiloLinkFound=await detectKiloLink(true,true);
    if(kiloLinkFound)await testKiloLinkConnection(true,true);
    await detectNdiDiscovery(true,true);
  }finally{
    state.infrastructureDetecting=false;
    updateServiceConnectionsStatus();
    if(state.infrastructureRerun){state.infrastructureRerun=false;setTimeout(detectInfrastructure,0)}
  }
}
async function detectKiloLink(automatic=false,deferStatus=false){
  const button=$('#findKiloLink'),result=$('#kiloLinkDiscoveryResult'),port=+$('input[name="kiloLinkWebPort"]').value||80;
  if(button.disabled)return false;
  const initial=kiloLinkIp.value.trim();
  serviceChecks.kiloLinkFound=false;
  serviceChecks.kiloLinkAuthenticated=false;
  try{
    button.disabled=true;button.textContent='Searching…';result.className='';result.textContent='Scanning local networks…';
    const servers=await api(`/api/kilolink/discover?webPort=${port}`);
    if(!servers.length){result.className='error-text';result.textContent='No KiloLink Server found';return false}
    const server=servers.find(candidate=>candidate.serverIp===initial)||servers[0];
    if(kiloLinkIp.value.trim()===initial)kiloLinkIp.value=server.serverIp;
    serviceChecks.kiloLinkFound=true;
    result.className='ok';result.textContent=`Found ${server.serverIp} · ${server.version}`;
    await refreshKiloLinkCredentials();
    return true;
  }catch(err){
    result.className='error-text';result.textContent=automatic?'Automatic search failed':err.message;
    return false;
  }finally{
    button.disabled=false;button.textContent='Find KiloLink Server';
    if(!deferStatus)updateServiceConnectionsStatus();
  }
}
$('#findKiloLink').onclick=()=>detectKiloLink(false);
async function detectNdiDiscovery(automatic=false,deferStatus=false){
  const button=$('#findNdiDiscovery'),result=$('#ndiDiscoveryResult'),input=$('input[name="ndiDiscoveryServerIp"]');
  if(button.disabled)return false;
  const initial=input.value.trim();
  serviceChecks.ndiDiscoveryFound=false;
  try{
    button.disabled=true;button.textContent='Searching…';result.className='';result.textContent='Scanning TCP 5959…';
    const servers=await api('/api/ndi/discover');
    if(!servers.length){result.className='error-text';result.textContent='No NDI Discovery Server found';return false}
    const kilolink=kiloLinkIp.value.trim(),prefix=kilolink.split('.').slice(0,3).join('.')+'.',server=servers.find(candidate=>candidate.serverIp===initial)||servers.find(candidate=>candidate.serverIp===kilolink)||servers.find(candidate=>prefix!=='.'&&candidate.serverIp.startsWith(prefix))||servers[0];
    if(input.value.trim()===initial)input.value=server.serverIp;
    serviceChecks.ndiDiscoveryFound=true;
    result.className='ok';result.textContent=`Found ${server.serverIp}:${server.port}`;
    return true;
  }catch(err){
    result.className='error-text';result.textContent=automatic?'Automatic search failed':err.message;
    return false;
  }finally{
    button.disabled=false;button.textContent='Find NDI Discovery Server';
    if(!deferStatus)updateServiceConnectionsStatus();
  }
}
$('#findNdiDiscovery').onclick=()=>detectNdiDiscovery(false);
kiloLinkIp.addEventListener('input',()=>{serviceChecks.kiloLinkFound=false;serviceChecks.kiloLinkAuthenticated=false;if(storedCredential.dataset.serverIp!==kiloLinkIp.value.trim())hideStoredKiloLinkCredential();if(!state.infrastructureDetecting)updateServiceConnectionsStatus()});
[kiloLinkUser,kiloLinkPassword,$('input[name="kiloLinkWebPort"]')].forEach(input=>input.addEventListener('input',()=>{serviceChecks.kiloLinkAuthenticated=false;if(!state.infrastructureDetecting)updateServiceConnectionsStatus()}));
$('input[name="ndiDiscoveryServerIp"]').addEventListener('input',()=>{serviceChecks.ndiDiscoveryFound=false;if(!state.infrastructureDetecting)updateServiceConnectionsStatus()});
$('input[name="jobName"]').addEventListener('blur',()=>{if(serviceChecks.kiloLinkFound&&!serviceChecks.kiloLinkAuthenticated&&$('input[name="jobName"]').value.trim())testKiloLinkConnection(true)});
kiloLinkIp.addEventListener('blur',refreshKiloLinkCredentials);
async function testKiloLinkConnection(automatic=false,deferStatus=false){
  const button=$('#testKiloLink'),result=$('#kiloLinkTestResult'),f=new FormData($('#setupForm'));
  serviceChecks.kiloLinkAuthenticated=false;
  try{
    button.disabled=true;button.textContent='Testing…';result.className='';result.textContent=automatic?'Authenticating stored KiloLink login…':'Testing KiloLink login…';
    const status=await api('/api/kilolink/test',{method:'POST',body:JSON.stringify({serverIp:f.get('kiloLinkServerIp').trim(),webPort:+f.get('kiloLinkWebPort'),username:f.get('kiloLinkUsername').trim(),password:f.get('kiloLinkPassword'),jobName:f.get('jobName').trim()})});
    kiloLinkPassword.value='';
    serviceChecks.kiloLinkFound=true;
    serviceChecks.kiloLinkAuthenticated=true;
    result.className='ok';result.textContent=status.passwordChanged?`KiloLink onboarded · password set to Job Name · Server ${status.version}`:`Connected · Server ${status.version} · ${status.deviceCount} registered device${status.deviceCount===1?'':'s'}`;
    await refreshKiloLinkCredentials();
    return true;
  }catch(err){
    result.className='error-text';result.textContent=err.message;
    return false;
  }finally{
    button.disabled=false;button.textContent='Test KiloLink login';
    if(!deferStatus)updateServiceConnectionsStatus();
  }
}
$('#testKiloLink').onclick=()=>testKiloLinkConnection(false);
function ipn(ip){return ip.split('.').reduce((n,x)=>n*256+(+x),0)}function inRange(ip,a,b){return ipn(ip)>=ipn(a)&&ipn(ip)<=ipn(b)}
function renderDiscovery(result){
  const fresh=result.devices.filter(d=>d.canOnboard!==false&&!d.isOnboarded),teletools=result.devices.filter(isTeleTool),kiloviews=result.devices.length-teletools.length;
  $('#discoverySummary').innerHTML=`<div><strong>${kiloviews}</strong><small>Kiloviews found</small></div><div><strong>${teletools.length}</strong><small>TeleTools found</small></div><div><strong>${fresh.length}</strong><small>Ready to onboard</small></div><div><strong>${result.scannedCidrs.length}</strong><small>Networks scanned</small></div>`;
  $('#discoveredGrid').innerHTML=result.devices.length?result.devices.map(d=>{const retained=inRange(d.ipAddress,state.settings.staticStart,state.settings.staticEnd)&&d.isStatic&&!d.isOnboarded,blocked=d.canOnboard===false,disabled=d.isOnboarded||blocked,selected=state.selected.has(d.id),standalone=state.skipped.has(d.id),label=blocked?(d.managementMessage||'NOT AVAILABLE'):d.isOnboarded?'ALREADY ONBOARDED':retained?'STATIC · RETAIN ADDRESS':isTeleTool(d)?'ENCODER · DEV API':d.role;return `<article class="device-card ${selected?'selected':''} ${standalone?'skipped':''} ${blocked?'unavailable':''}" data-id="${esc(d.id)}"><header><span class="model">${esc(d.model)} · ${esc(d.family)}</span><input class="selectbox" type="checkbox" ${selected?'checked':''} ${disabled?'disabled':''} aria-label="Onboard ${esc(d.hostname)}"></header><h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}${d.webPort&&d.webPort!==80?`:${d.webPort}`:''}</div><div class="meta"><span class="pill">${esc(d.macAddress)}</span><span class="pill ${isTeleTool(d)?'teletool':roleClass(d.role)}">${esc(label)}</span>${standalone?'<span class="pill standalone">STANDALONE · NO CHANGES</span>':''}${d.firmwareVersion?`<span class="pill">${esc(d.firmwareVersion)}</span>`:''}</div>${blocked?`<div class="error-text">${esc(d.managementMessage||d.lastError||'This unit cannot be selected.')}</div>`:disabled?'':`<div class="card-actions discovery-decision"><button type="button" data-discovery-action="${selected?'skip':'include'}" data-device-id="${esc(d.id)}">${selected?'Leave standalone':'Include in onboarding'}</button></div>`}</article>`}).join(''):'<div class="empty">No Kiloview N6/N60 or TeleTool Dev units responded. Check the selected network adapter, cabling, and relevant device services.</div>';
  $$('#discoveredGrid .selectbox').forEach(box=>box.onchange=()=>setDiscoveryDecision(box.closest('.device-card').dataset.id,box.checked));
  $$('#discoveredGrid [data-discovery-action]').forEach(button=>button.onclick=()=>setDiscoveryDecision(button.dataset.deviceId,button.dataset.discoveryAction==='include'));
  renderOnboardingPcAgents();
  updateSelected()
}
function setDiscoveryDecision(id,onboard){if(onboard){state.selected.add(id);state.skipped.delete(id)}else{state.selected.delete(id);state.skipped.add(id)}renderDiscovery(state.discovery)}
function updateSelected(){$('#selectedCount').textContent=`${state.selected.size} onboard · ${state.skipped.size} standalone`;$('#buildPlan').disabled=!state.selected.size}
$('#buildPlan').onclick=async()=>{const button=$('#buildPlan'),label=button.querySelector('span');try{button.disabled=true;label.textContent='Preparing onboarding…';state.settings.deviceIds=[...state.selected];state.plan=await api('/api/onboarding/plan',{method:'POST',body:JSON.stringify(state.settings)});state.settings.kiloLinkPassword='';kiloLinkPassword.value='';credentialHint.textContent='KiloLink login stored locally for this server.';await continueWithPlan()}catch(e){toast(e.message,true);button.disabled=false}finally{label.textContent='Continue onboarding'}};
async function continueWithPlan(){const ids=new Set(state.plan.devices.filter(d=>['N6','N60'].includes(d.family)).map(d=>d.deviceId)),devices=(state.discovery?.devices||state.devices).filter(d=>ids.has(d.id));state.firmwarePreOnboarding=devices.length>0;if(state.firmwarePreOnboarding){renderFirmware({devices});show('firmware')}else await startOnboarding()}
async function startOnboarding(){try{await api('/api/onboarding/run',{method:'POST',body:JSON.stringify({planId:state.plan.planId})});show('progress');pollProgress()}catch(e){toast(e.message,true);throw e}}
async function pollProgress(){clearInterval(state.poll);const tick=async()=>{try{const p=await api('/api/onboarding/progress');renderProgress(p);if(!['idle','running'].includes(p.status)){clearInterval(state.poll);state.poll=null;await loadDecoderOrMonitor(p.status)}}catch(e){toast(e.message,true)}};await tick();state.poll=setInterval(tick,1200)}
function renderProgress(p){const pct=p.total?Math.round(p.completed/p.total*100):0;$('#progressBar').style.width=`${pct}%`;$('#progressTitle').textContent=p.status==='running'?'Applying configuration':p.status.replaceAll('-',' ');$('#progressMessage').textContent=p.status==='running'?`${p.completed} of ${p.total} stages complete · mode changes can take one minute`:'Onboarding pass finished.';$('#progressSteps').innerHTML=p.steps.length?p.steps.slice().reverse().map(s=>`<div class="timeline-row"><i class="health ${esc(s.status==='ok'?'online':s.status)}"></i><code>${esc(s.ipAddress)}</code><strong>${esc(s.step)}</strong><span class="${esc(s.status)}">${esc(s.message||s.status)}</span></div>`).join(''):'<div class="empty">Preparing the first device…</div>'}
function currentRunDevices(devices){const ids=new Set((state.plan?.devices||[]).map(device=>device.deviceId));return ids.size?devices.filter(device=>ids.has(device.id)):devices}
async function loadDecoderOrMonitor(status){const app=await api('/api/state');state.devices=app.devices||[];const onboarded=state.devices.filter(d=>d.isOnboarded),runDevices=currentRunDevices(onboarded),kiloviews=runDevices.filter(d=>!isTeleTool(d)),decoders=kiloviews.filter(d=>d.role==='Decoder'),encoders=kiloviews.filter(d=>d.role==='Encoder'),unknowns=kiloviews.filter(d=>d.role==='Unknown');if(kiloviews.length){renderDecoders(decoders,encoders,unknowns);show('decoder');if(decoders.length)await prepareIdentityCards();if(status==='failed'||status==='completed-with-errors')toast('Onboarding finished with errors; successfully onboarded devices can still be assigned and named.',true);else if(unknowns.length)toast(`${unknowns.length} Kiloview role${unknowns.length===1?'':'s'} need confirmation`)}else{renderMonitor(app);show('monitor')}}
function renderFirmware(app){const devices=(app.devices||[]).filter(d=>!isTeleTool(d)),n6=devices.filter(d=>d.model.toUpperCase().startsWith('N6')&&!d.model.toUpperCase().startsWith('N60')).length,n60=devices.filter(d=>d.model.toUpperCase().startsWith('N60')).length;$('#n6FirmwareLabel').classList.toggle('hidden',n6===0);$('#n60FirmwareLabel').classList.toggle('hidden',n60===0);$('input[name="n6Firmware"]').required=n6>0;$('input[name="n60Firmware"]').required=n60>0;$('#firmwareCoverage').dataset.models=[...(n6?['N6']:[]),...(n60?['N60']:[])].join(',');$('#firmwareCoverage').innerHTML=`<div><strong>${n6}</strong><small>N6 units</small></div><div><strong>${n60}</strong><small>N60 units</small></div><div><strong>${devices.length}</strong><small>Kiloview fleet</small></div>`;$('#firmwareStatus').innerHTML='';$('#skipFirmware').classList.toggle('hidden',state.firmwarePreOnboarding);$('#stageFirmware').type='submit';$('#stageFirmware').onclick=null;$('#stageFirmware').querySelector('span').textContent=state.firmwarePreOnboarding?'Stage & start onboarding':'Stage firmware'}
$('#firmwareForm').addEventListener('submit',async e=>{e.preventDefault();const button=$('#stageFirmware'),models=$('#firmwareCoverage').dataset.models;try{button.disabled=true;button.querySelector('span').textContent='Staging…';const staged=await upload(`/api/firmware/stage?models=${encodeURIComponent(models)}`,new FormData(e.currentTarget));$('#firmwareStatus').innerHTML=staged.packages.map(p=>`<div class="firmware-package"><strong>${esc(p.model)}</strong><span>${esc(p.fileName)} · <code>${esc(p.sha256.slice(0,12))}</code></span></div>`).join('');if(state.firmwarePreOnboarding){toast('Firmware staged; starting onboarding');await startOnboarding()}else{button.querySelector('span').textContent='Start fleet update';button.type='button';button.onclick=startFleetUpdate;button.disabled=false}}catch(err){toast(err.message,true);button.disabled=false;button.querySelector('span').textContent=state.firmwarePreOnboarding?'Stage & start onboarding':'Stage firmware'}});
async function startFleetUpdate(){const button=$('#stageFirmware');try{button.disabled=true;button.querySelector('span').textContent='Uploading & dispatching…';const result=await api('/api/firmware/start',{method:'POST',body:'{}'});if(result.completed){toast('Fleet firmware update completed');state.afterFirmware?.()}else if(result.started){$('#firmwareStatus').innerHTML=`<div class="note">${esc(result.message)}${result.managementUrl?` <a href="${esc(result.managementUrl)}" target="_blank" rel="noreferrer">Monitor in KiloLink Server ↗</a>`:''}</div>`;$('#skipFirmware').textContent='Continue to decoder setup';toast('KiloLink fleet update dispatched')}else{$('#firmwareStatus').innerHTML=`<div class="warning">${esc(result.message)}</div>`}}catch(err){$('#firmwareStatus').innerHTML=`<div class="warning">${esc(err.message)}</div>`;toast(err.message,true)}finally{button.disabled=false;button.querySelector('span').textContent='Start fleet update'}}
$('#skipFirmware').onclick=()=>state.afterFirmware?.();
async function prepareIdentityCards(){try{const result=await api('/api/onboarding/identify',{method:'POST',body:'{}'});state.titleCardIds=new Set(result.active||[]);state.titleCardSources=new Map((result.cards||[]).map(card=>[card.id,card]));const app=await api('/api/state');state.devices=app.devices||[];const kiloviews=currentRunDevices(state.devices.filter(d=>d.isOnboarded)).filter(d=>!isTeleTool(d));renderDecoders(kiloviews.filter(d=>d.role==='Decoder'),kiloviews.filter(d=>d.role==='Encoder'),kiloviews.filter(d=>d.role==='Unknown'));if(result.errors?.length)toast(`${result.errors.length} display identity card${result.errors.length===1?'':'s'} could not be shown`,true);else toast('Every NDI identity source now matches its decoder card')}catch(err){toast(`Display identity cards: ${err.message}`,true)}}
async function openDisplayNaming(){try{const app=await api('/api/state');state.devices=app.devices||[];state.plan=null;const kiloviews=state.devices.filter(d=>d.isOnboarded&&!isTeleTool(d)),decoders=kiloviews.filter(d=>d.role==='Decoder'),encoders=kiloviews.filter(d=>d.role==='Encoder'),unknowns=kiloviews.filter(d=>d.role==='Unknown');if(!decoders.length&&!unknowns.length){toast('No onboarded Kiloview decoders or unassigned devices are available.',true);return}renderDecoders(decoders,encoders,unknowns);show('decoder');if(decoders.length)await prepareIdentityCards()}catch(err){toast(`Display naming: ${err.message}`,true)}}
function deviceGroup(label,devices,cards){return devices.length?`<section class="device-group"><div class="device-group-title"><h2>${esc(label)}</h2><span>${devices.length} DEVICE${devices.length===1?'':'S'}</span></div><div class="device-grid">${cards}</div></section>`:''}
function deviceGroupBand(kind,groups){
  const visible=groups.filter(group=>group.devices.length);
  if(!visible.length)return'';
  if(visible.length===1){const group=visible[0];return deviceGroup(group.label,group.devices,group.cards)}
  const counts=visible.map(group=>group.devices.length),total=counts.reduce((sum,count)=>sum+count,0),largest=Math.max(...counts),pairStandard=total<=5,pairWide=total<=8&&largest<=4;
  return `<div class="device-group-band ${esc(kind)}${pairStandard?' pair-standard':''}${pairWide?' pair-wide':''}">${visible.map(group=>deviceGroup(group.label,group.devices,group.cards)).join('')}</div>`;
}
function ndiGroupConfirmation(devices){if(!devices.length)return'';const groups=[...new Set(devices.map(d=>d.ndiGroup).filter(Boolean))],group=groups.length===1?groups[0]:groups.join(', '),simulation=devices.every(d=>d.family==='Simulated');return `<section class="ndi-group-confirmation"><span>JOB NAME → NDI GROUP</span><strong>${esc(group)}</strong><p>Applied to all ${devices.length} ${simulation?'simulated ':''}Kiloviews. Every decoder identity card shows this same group.${simulation?' Simulation card sources are also advertised in the public group so Studio Monitor can discover them without an Access Manager change; the simulated devices remain assigned to the Job Name group.':''}</p></section>`}
function renderDecoders(decoders,encoders=[],unknowns=[]){const identityForm=d=>`<form class="identity-form" data-id="${esc(d.id)}"><label><span>Hostname</span><input name="hostname" value="${esc(d.hostname)}" required></label><label><span>NDI Channel Name</span><input name="ndiChannelName" value="${esc(d.ndiChannelName)}" required></label><button>Apply names</button></form>`;const decoderCards=decoders.map(d=>{const source=state.titleCardSources.get(d.id);return `<article class="device-card"><header><span class="model">DECODER · ${esc(d.model)}</span><i class="health ${roleClass(d.health)}"></i></header><h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}</div><div class="meta"><span class="pill decoder">${esc(d.ndiGroup)}</span><span class="pill">${esc(d.ndiChannelName)}</span><span class="pill">HDMI ${esc(d.hdmiOutputResolution||'OUTPUT')}</span><span class="pill ${state.titleCardIds.has(d.id)?'identity-active':''}">${state.titleCardIds.has(d.id)?'IDENTITY CARD ACTIVE':'STARTING IDENTITY CARD'}</span>${source?`<span class="pill identity-source">SOURCE ${esc(source.name)}</span>`:''}</div>${identityForm(d)}</article>`}).join('');const encoderCards=encoders.map(d=>`<article class="device-card"><header><span class="model">ENCODER · ${esc(d.model)}</span><i class="health ${roleClass(d.health)}"></i></header>${encoderPreview(d)}<h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}</div><div class="meta"><span class="pill encoder">${esc(d.ndiGroup)}</span><span class="pill">${esc(d.ndiChannelName)}</span></div>${identityForm(d)}</article>`).join('');const unknownCards=unknowns.map(d=>`<article class="device-card role-required"><header><span class="model">ROLE REQUIRED · ${esc(d.model)}</span><i class="health ${roleClass(d.health)}"></i></header><h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}</div><div class="warning">No live HDMI input was detected. A disconnected device cannot be classified safely from its configured resolution.</div><div class="card-actions"><button type="button" data-role-device="${esc(d.id)}" data-role="Encoder">Set as encoder</button><button type="button" data-role-device="${esc(d.id)}" data-role="Decoder">Set as decoder / display</button></div></article>`).join('');const devices=[...decoders,...encoders,...unknowns],grid=$('#decoderGrid');releasePreviewObjectUrls(grid);grid.innerHTML=ndiGroupConfirmation(devices)+deviceGroup('Role confirmation required',unknowns,unknownCards)+deviceGroup('Decoders · Name the displays',decoders,decoderCards)+deviceGroup('Encoders · Identify & name HDMI inputs',encoders,encoderCards);$$('.identity-form').forEach(form=>form.onsubmit=saveIdentity);$$('[data-role-device]').forEach(button=>button.onclick=()=>assignKiloviewRole(button));$('#completeSetup').disabled=unknowns.length>0}
async function assignKiloviewRole(button){const id=button.dataset.roleDevice,role=button.dataset.role;try{$(`[data-role-device="${CSS.escape(id)}"]`)?.closest('.device-card')?.querySelectorAll('button').forEach(item=>item.disabled=true);button.textContent=`Switching to ${role.toLowerCase()}…`;const device=await api(`/api/devices/${encodeURIComponent(id)}/role`,{method:'POST',body:JSON.stringify({role})});state.devices=state.devices.map(item=>item.id===device.id?device:item);const kiloviews=currentRunDevices(state.devices.filter(item=>item.isOnboarded&&!isTeleTool(item))),decoders=kiloviews.filter(item=>item.role==='Decoder'),encoders=kiloviews.filter(item=>item.role==='Encoder'),unknowns=kiloviews.filter(item=>item.role==='Unknown');renderDecoders(decoders,encoders,unknowns);toast(`${device.ipAddress} set to ${role}`);if(role==='Decoder')await prepareIdentityCards()}catch(err){toast(`Role change: ${err.message}`,true);const app=await api('/api/state');state.devices=app.devices||[];const kiloviews=currentRunDevices(state.devices.filter(item=>item.isOnboarded&&!isTeleTool(item)));renderDecoders(kiloviews.filter(item=>item.role==='Decoder'),kiloviews.filter(item=>item.role==='Encoder'),kiloviews.filter(item=>item.role==='Unknown'))}}
async function saveIdentity(e){e.preventDefault();const form=e.currentTarget,button=form.querySelector('button'),f=new FormData(form);try{button.disabled=true;button.textContent='Applying…';const device=await api(`/api/devices/${encodeURIComponent(form.dataset.id)}/identity`,{method:'POST',body:JSON.stringify({hostname:f.get('hostname'),ndiChannelName:f.get('ndiChannelName')})});state.devices=state.devices.map(d=>d.id===device.id?device:d);await prepareIdentityCards();toast(`${device.role} ${device.ipAddress} hostname and NDI channel updated`)}catch(err){toast(err.message,true);button.textContent='Try again'}finally{button.disabled=false}}
$('#completeSetup').onclick=async()=>{try{$('#completeSetup').disabled=true;const result=await api('/api/onboarding/complete',{method:'POST',body:'{}'});if(!result.completed)toast(`${result.errors.length} decoder(s) could not be blanked`,true);const app=await api('/api/state');renderMonitor(app);show('monitor')}catch(e){toast(e.message,true)}finally{$('#completeSetup').disabled=false}};
function danteAudioBadge(d){
  const active=d.danteAudioActive===true,status=d.danteAudioStatus||'Unknown',device=d.danteAudioDeviceLabel?` on ${d.danteAudioDeviceLabel}`:'',details=d.danteAudioDetails||`Dante audio status is ${status.toLowerCase()}.`;
  return `<span class="dante-status ${active?'active':'inactive'}" role="status" aria-label="${esc(`Dante audio ${status}${device}`)}" title="${esc(details)}"><strong>DANTE</strong><small>${esc(status.toUpperCase())}</small></span>`;
}
function temperaturePresentation(value){
  const temperature=Number(value);
  if(!Number.isFinite(temperature))return{kind:'unavailable',label:'TEMP N/A',title:'TeleTool system temperature is unavailable.'};
  const formatted=temperature.toFixed(1),kind=temperature>=80?'hot':temperature>=70?'warm':'normal',stateLabel=kind==='hot'?'hot':kind==='warm'?'warm':'normal';
  return{kind,label:`TEMP ${formatted}°C`,title:`System temperature ${formatted}°C (${stateLabel}). Warning begins at 70°C; hot begins at 80°C.`};
}
function temperatureBadge(d){
  const temperature=temperaturePresentation(d.systemTemperatureC);
  return `<span class="pill temperature-status temp-${temperature.kind}" data-teletool-temperature="${esc(d.id)}" role="status" aria-label="${esc(temperature.title)}" title="${esc(temperature.title)}">${esc(temperature.label)}</span>`;
}
function refreshTeleToolTemperatures(devices){
  const temperatures=new Map(devices.map(device=>[String(device.id),temperaturePresentation(device.systemTemperatureC)]));
  $$('[data-teletool-temperature]').forEach(badge=>{
    const temperature=temperatures.get(badge.dataset.teletoolTemperature);if(!temperature)return;
    badge.className=`pill temperature-status temp-${temperature.kind}`;badge.textContent=temperature.label;badge.title=temperature.title;badge.setAttribute('aria-label',temperature.title);
  });
}
function multicastIcon(endpoint){
  const reserved=endpoint.status==='reserved',configured=endpoint.multicastConfigured===true||endpoint.status==='applied'||reserved,active=endpoint.multicastInUse===true||endpoint.inUse===true,prefix=endpoint.multicastNetPrefix||endpoint.netPrefix,title=reserved?`Multicast range reserved for manual setup${prefix?` · ${prefix}`:''}`:active?`Multicast active${prefix?` · ${prefix}`:''}`:configured?`Multicast configured but not currently active${prefix?` · ${prefix}`:''}`:'Multicast is not configured';
  return `<span class="multicast-status ${active?'active':configured?'configured':'inactive'}" role="img" aria-label="${esc(title)}" title="${esc(title)}">MULTICAST</span>`;
}
function renderMonitor(app){
  state.devices=app.devices||[];
  state.localPc=app.localPc||state.localPc;
  state.multicastConfigured=app.multicast!=null;
  const revert=$('#revertMulticast'),revertSummary=$('#multicastRevertSummary');
  if(revert)revert.disabled=!state.multicastConfigured;
  if(revertSummary)revertSummary.textContent=state.multicastConfigured
    ? 'This disables multicast transport on every onboarded device and this Windows PC without changing names, groups, or discovery settings.'
    : 'No applied multicast configuration is currently stored for this job.';
  const devices=state.devices.filter(d=>d.isOnboarded),teletools=devices.filter(isTeleTool),kiloviews=devices.filter(d=>!isTeleTool(d)),decoderDevices=kiloviews.filter(d=>d.role==='Decoder'),encoderDevices=kiloviews.filter(d=>d.role==='Encoder'),unknownDevices=kiloviews.filter(d=>d.role==='Unknown'),remoteWindowsPcs=app.remoteWindowsPcs||[],registeredEndpointIds=new Set(remoteWindowsPcs.map(pc=>String(pc.endpointId).toLowerCase())),pcAgents=state.pcAgents||[],agentById=new Map(pcAgents.map(agent=>[String(agent.endpointId).toLowerCase(),agent])),availablePcAgents=pcAgents.filter(agent=>!registeredEndpointIds.has(String(agent.endpointId).toLowerCase())),healthyRemotePcs=remoteWindowsPcs.filter(pc=>pc.preferredInterfaceConfigured&&pc.status==='onboarded'&&!pc.error&&(agentById.get(String(pc.endpointId).toLowerCase())?.status==='online'||pc.connectivityStatus==='online')),localAssignment=app.multicast?.assignments?.find(a=>a.endpointId==='local-pc'),localPc=app.localPc||(localAssignment?{...localAssignment,preferredInterfaceConfigured:true,adapterName:'Previously selected adapter'}:null),preferredApplied=localPc?.preferredInterfaceConfigured===true,localMulticastApplied=!localAssignment||localAssignment.status==='applied',localHealthy=preferredApplied&&localMulticastApplied,online=devices.filter(d=>d.health==='Online').length+(localHealthy?1:0)+healthyRemotePcs.length,running=teletools.filter(d=>d.streamRunning).length,onboarded=devices.length+(localPc?1:0)+remoteWindowsPcs.length,benignManagementStates=new Set(['','managed','available','mode-starting','mode-fallback']),deviceAttention=devices.filter(d=>d.health!=='Online'||(!isTeleTool(d)&&d.role==='Unknown')||!!d.lastError||!!d.multicastLastError||!benignManagementStates.has(String(d.managementState||'').toLowerCase())).length,windowsAttention=(localPc&&!localHealthy?1:0)+(remoteWindowsPcs.length-healthyRemotePcs.length),needsAttention=deviceAttention+windowsAttention;
  $('#nameDisplays').classList.toggle('hidden',!decoderDevices.length&&!kiloviews.some(d=>d.role==='Unknown'));
  const activeTeleToolIds=new Set(teletools.filter(d=>d.streamRunning).map(d=>d.id));[...state.previewWarnings.keys()].filter(id=>!activeTeleToolIds.has(id)).forEach(clearPreviewWarning);
  $('#monitorTitle').textContent=app.lastJob?.jobName||'Device status';
  $('#monitorMeta').textContent=app.lastJob?`${app.lastJob.staticStart} – ${app.lastJob.staticEnd} · NDI discovery ${app.lastJob.ndiDiscoveryServerIp}`:'Health and stream state refresh every 15 seconds.';
  $('#monitorStats').innerHTML=`<div class="stat"><strong>${onboarded}</strong><small>Onboarded</small></div><div class="stat"><strong>${online}</strong><small>Online</small></div><div class="stat attention-stat${needsAttention?' active':''}"><strong>${needsAttention}</strong><small>Needs attention</small></div><div class="stat"><strong>${running}/${teletools.length}</strong><small>TeleTool streams live</small></div>`;
  const multicastMeta=d=>d.multicastNetPrefix?`<span class="pill multicast-pill">MC ${esc(d.multicastNetPrefix)}/${prefixLength(d.multicastNetmask)}</span>`:'';
  const kiloviewModel=d=>{const model=String(d.model||'').toUpperCase();return model.includes('N60')?'N60':model.includes('N6')?'N6':model||'KILOVIEW'};
  const kiloviewCard=d=>{
    const managementIssue=d.role==='Unknown'||(!['','managed','available','mode-starting','mode-fallback'].includes(String(d.managementState||'').toLowerCase())),decoderSource=d.role==='Decoder'?`<span class="pill decoder-source-pill" title="${esc(d.tunedNdiChannelName?`Tuned to ${d.tunedNdiChannelName}`:'No NDI source selected')}">TUNED · ${esc(d.tunedNdiChannelName||'NO SOURCE')}</span>`:'',body=`<h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}</div><div class="meta"><span class="pill ${roleClass(d.role)}">${esc(d.role)}</span><span class="pill">${esc(d.ndiGroup)}</span>${decoderSource}${multicastMeta(d)}${d.firmwareVersion?`<span class="pill">FW ${esc(d.firmwareVersion)}</span>`:''}${d.hdmiOutputResolution?`<span class="pill">${esc(d.hdmiOutputResolution)}</span>`:''}</div>${managementIssue?`<div class="device-management-warning"><strong>DEVICE ATTENTION</strong><span>${esc(d.managementMessage||'The device role could not be confirmed. It remains onboarded and will be checked again automatically.')}</span></div>`:''}${d.multicastLastError?`<div class="error-text">Multicast: ${esc(d.multicastLastError)}</div>`:''}${d.lastError?`<div class="error-text">${esc(d.lastError)}</div>`:''}`,actions=`<div class="card-actions encoder-card-actions"><a href="/api/devices/${encodeURIComponent(d.id)}/ui" target="_blank" rel="noreferrer">Device UI ↗</a><button class="remove-kiloview" data-kiloview-action="remove" data-device-id="${esc(d.id)}" data-device-name="${esc(d.hostname)}">Remove</button></div>`;
    return `<article class="device-card${d.role==='Decoder'?' decoder-card':''}${managementIssue?' device-attention':''}"><header>${deviceTypeBadge(kiloviewModel(d),d.role)}<span class="card-indicators">${multicastIcon(d)}<i class="health ${managementIssue?'error':roleClass(d.health)}" title="${esc(managementIssue?'Needs attention':d.health)}"></i></span></header>${d.role==='Encoder'?encoderPreview(d)+`<div class="encoder-card-body">${body}</div>${actions}`:body+actions}</article>`;
  };
  const teleToolCard=d=>{
    const channel=[d.activeChannelNumber,d.activeChannelName].filter(Boolean).join(' ')||'No active TV channel',startDisabled=d.health!=='Online'||d.streamRunning||!d.teleToolControlReady,stopDisabled=d.health!=='Online'||!d.streamRunning,previewError=d.streamRunning&&state.previewWarnings.has(d.id);
    const actions=`<div class="card-actions encoder-card-actions"><a href="${esc(deviceUrl(d))}" target="_blank" rel="noreferrer">Device UI ↗</a><button data-teletool-action="start" data-device-id="${esc(d.id)}" ${startDisabled?'disabled':''}>Start NDI</button><button data-teletool-action="stop" data-device-id="${esc(d.id)}" ${stopDisabled?'disabled':''}>Stop NDI</button><button class="remove-teletool" data-teletool-action="remove" data-device-id="${esc(d.id)}" data-device-name="${esc(d.hostname)}">Remove</button></div>`;
    return `<article class="device-card teletool-card${previewError?' preview-error':''}"><header>${deviceTypeBadge('TELETOOL','ENCODER')}<span class="card-indicators">${multicastIcon(d)}<i class="health ${roleClass(d.health)}" title="${esc(d.health)}"></i></span></header>${encoderPreview(d)}<div class="encoder-card-body"><div class="teletool-heading"><h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}:${d.webPort||8000}</div></div><div class="meta teletool-meta"><span class="pill teletool">TV ${esc(channel)}</span><span class="pill">${esc(d.ndiGroup)}</span>${multicastMeta(d)}${d.firmwareVersion?`<span class="pill">FW ${esc(d.firmwareVersion)}</span>`:''}</div><div class="teletool-status-row">${danteAudioBadge(d)}<span class="pill rf-${esc(d.rfSignalKind||'bad')}">RF ${esc(d.rfSignal||'N/A')}</span>${temperatureBadge(d)}</div>${d.multicastLastError?`<div class="error-text">Multicast: ${esc(d.multicastLastError)}</div>`:''}${d.lastError?`<div class="error-text">${esc(d.lastError)}</div>`:''}</div>${actions}</article>`;
  };
  const windowsJobName=app.lastJob?.jobName||app.multicast?.jobName,localError=localPc?.error||localAssignment?.error,localStatusLabel=localHealthy?'ONLINE':'ATTENTION',localStatusClass=localHealthy?'connectivity-online':'connectivity-offline',localPills=localPc?windowsEndpointPills({statusLabel:localStatusLabel,statusClass:localStatusClass,statusTitle:localHealthy?'Local endpoint ready':'Local endpoint needs attention',preferred:preferredApplied,adapterName:localPc.adapterName,ndiToolsVersion:localPc.ndiToolsVersion,jobName:windowsJobName,assignment:localAssignment,multicastApplied:localMulticastApplied}):'',localCard=localPc?`<article class="device-card local-pc-card ${localHealthy?'':'configuration-drift'}"><header>${windowsBadge(localPc.operatingSystemVersion)}<span class="card-indicators">${multicastIcon(localAssignment||{})}<i class="health ${localHealthy?'online':'error'}" title="${localHealthy?'Applied':'Configuration changed'}"></i></span></header><h3>${esc(localPc.hostname)}</h3><div class="ip">${esc(localPc.address)}/${esc(localPc.prefixLength)}</div><div class="meta">${localPills}</div>${localError?`<div class="local-pc-drift"><strong>NDI Access Manager needs attention</strong><span>${esc(localError)}</span></div>`:`<div class="local-pc-note">${localAssignment?'Preferred interface and multicast settings match NDI Access Manager.':'Preferred interface is applied. Multicast setup will update this same endpoint card.'}</div>`}${!preferredApplied?'<div class="card-actions"><button type="button" data-local-pc-action="reapply">Reapply preferred interface</button></div>':''}</article>`:'';
  const remotePcCard=pc=>{
    const agent=agentById.get(String(pc.endpointId).toLowerCase());
    const agentProduct=agent?.product||PC_AGENT_PRODUCT;
    const configured=pc.preferredInterfaceConfigured&&pc.status==='onboarded'&&!pc.error;
    const status=agent?.status==='online'?'online':String(pc.connectivityStatus||'unknown').toLowerCase();
    const online=status==='online',offline=status==='offline',stale=status==='stale';
    const statusLabel=online?'ONLINE':offline?'OFFLINE':'CHECKING';
    const healthClass=online?'online':offline?'error':'configuring';
    const statusClass=online?'connectivity-online':offline?'connectivity-offline':stale?'connectivity-stale':'connectivity-unknown';
    const lastReply=agent?.observedUtc?new Date(agent.observedUtc).toLocaleString():pc.lastSeenUtc?new Date(pc.lastSeenUtc).toLocaleString():'not yet recorded';
    const connectivityTitle=online?`${agentProduct} reachable · last status ${lastReply}`:offline?`${agentProduct} failed three consecutive status polls · last status ${lastReply}`:stale?`${agentProduct} missed ${pc.consecutiveConnectivityFailures||1} recent status poll${pc.consecutiveConnectivityFailures===1?'':'s'} · last status ${lastReply}`:`Waiting for the first ${agentProduct} status poll`;
    const assignment=app.multicast?.assignments?.find(item=>String(item.endpointId).toLowerCase()===String(pc.endpointId).toLowerCase());
    const manualMulticast=assignment?.status==='reserved'&&assignment?.netPrefix?`<div class="remote-multicast-setup"><strong>${PC_AGENT_PRODUCT} update required for automatic multicast</strong><span>Until the agent supports remote multicast, configure ${esc(pc.hostname)} manually:</span><dl><div><dt>NET PREFIX</dt><dd>${esc(assignment.netPrefix)}</dd></div><div><dt>NETMASK</dt><dd>${esc(assignment.netmask)}</dd></div><div><dt>TTL</dt><dd>${esc(assignment.ttl)}</dd></div></dl><small>Enable Multicast Send and Receive in NDI Access Manager, or update the ${PC_AGENT_PRODUCT} and reapply multicast setup.</small></div>`:'';
    const attention=pc.error?`<div class="local-pc-drift"><strong>Windows endpoint needs attention</strong><span>${esc(pc.error)}</span></div>`:offline?`<div class="local-pc-drift"><strong>Windows endpoint offline</strong><span>The ${agentProduct} did not answer three consecutive authenticated-contract status checks on TCP 8094.</span></div>`:stale?`<div class="remote-pc-checking"><strong>Confirming agent connectivity</strong><span>A recent ${agentProduct} status poll failed. The endpoint is marked offline only after three consecutive failures.</span></div>`:`<div class="local-pc-note">${agentProduct} ${esc(pc.agentVersion||agent?.agentVersion||'unknown')} is reporting live Windows and NDI health.</div>`;
    const metrics=`<div class="pc-agent-metrics"><span>AGENT UP ${compactDuration(pc.agentUptimeSeconds||agent?.agentUptimeSeconds)}</span><span>PC UP ${compactDuration(pc.machineUptimeSeconds||agent?.machineUptimeSeconds)}</span><span>MEM FREE ${compactBytes(pc.physicalMemoryAvailableBytes||agent?.physicalMemoryAvailableBytes)} / ${compactBytes(pc.physicalMemoryTotalBytes||agent?.physicalMemoryTotalBytes)}</span><span>DISK FREE ${compactBytes(pc.systemDriveFreeBytes||agent?.systemDriveFreeBytes)} / ${compactBytes(pc.systemDriveTotalBytes||agent?.systemDriveTotalBytes)}</span></div>`;
    const pills=windowsEndpointPills({statusLabel,statusClass,statusTitle:connectivityTitle,preferred:pc.preferredInterfaceConfigured,adapterName:pc.adapterName,ndiToolsVersion:pc.ndiToolsVersion,jobName:windowsJobName,assignment,multicastApplied:true});
    return `<article class="device-card remote-pc-card ${configured&&online?'':'configuration-drift'}"><header>${windowsBadge(pc.operatingSystemVersion)}<span class="card-indicators">${multicastIcon(assignment?{...assignment,multicastConfigured:true}:{})}<i class="health ${healthClass}" title="${esc(connectivityTitle)}"></i></span></header><h3>${esc(pc.hostname)}</h3><div class="ip">${esc(pc.address)}/${esc(pc.prefixLength)}</div><div class="meta">${pills}${agent?`<span class="pill agent-product">${esc(agentProduct)}</span><span class="pill">VERSION ${esc(agent.agentVersion)}</span><span class="pill">JOBS ${esc(agent.memberships?.length||0)}</span>`:''}</div>${metrics}${attention}${manualMulticast}<div class="card-actions"><button type="button" class="remove-remote-pc" data-remote-pc-action="remove" data-endpoint-id="${esc(pc.endpointId)}" data-endpoint-name="${esc(pc.hostname)}">Remove</button></div>${agent?remoteOnboardingEditor(agent,true):''}</article>`;
  };
  const availablePcAgentCard=agent=>`<article class="device-card pc-agent-card"><header>${windowsBadge(agent.operatingSystemVersion)}<i class="health online" title="${esc(agent.product||PC_AGENT_PRODUCT)} online"></i></header><h3>${esc(agent.hostname)}</h3><div class="ip">${esc(agent.address)}/${esc(agent.prefixLength)}</div><div class="meta"><span class="pill agent-product">${esc(agent.product||PC_AGENT_PRODUCT)}</span><span class="pill connectivity-online">ONLINE</span><span class="pill">${esc(agent.adapterName)}</span><span class="pill">NDI ${esc(agent.ndiToolsVersion||'NOT INSTALLED')}</span><span class="pill">VERSION ${esc(agent.agentVersion)}</span><span class="pill">JOBS ${esc(agent.memberships?.length||0)}</span><span class="pill standalone">NOT ONBOARDED</span></div><div class="pc-agent-metrics"><span>AGENT UP ${compactDuration(agent.agentUptimeSeconds)}</span><span>PC UP ${compactDuration(agent.machineUptimeSeconds)}</span><span>MEM FREE ${compactBytes(agent.physicalMemoryAvailableBytes)} / ${compactBytes(agent.physicalMemoryTotalBytes)}</span><span>DISK FREE ${compactBytes(agent.systemDriveFreeBytes)} / ${compactBytes(agent.systemDriveTotalBytes)}</span></div><div class="local-pc-note">${esc(agent.product||PC_AGENT_PRODUCT)} discovered read-only on the selected subnet. Onboarding opens only after this PC's user approves the request locally.</div>${remoteOnboardingEditor(agent,false)}</article>`;
  const remoteEndpointIds=new Set(remoteWindowsPcs.map(pc=>String(pc.endpointId).toLowerCase())),remoteMulticastAssignments=(app.multicast?.assignments||[]).filter(assignment=>remoteEndpointIds.has(String(assignment.endpointId).toLowerCase())),cardDevices=devices.map(({lastSeenUtc,systemTemperatureC,...device})=>device),cardRemoteWindowsPcs=remoteWindowsPcs.map(({lastSeenUtc,lastConnectivityCheckUtc,...endpoint})=>endpoint),cardSignature=JSON.stringify({devices:cardDevices,localPc,localAssignment,remoteWindowsPcs:cardRemoteWindowsPcs,availablePcAgents,remoteMulticastAssignments,jobName:app.multicast?.jobName});
  if(cardSignature!==state.monitorCardSignature){
    const windowsEndpoints=[...(localPc?[localPc]:[]),...remoteWindowsPcs,...availablePcAgents],windowsCards=localCard+remoteWindowsPcs.map(remotePcCard).join('')+availablePcAgents.map(availablePcAgentCard).join(''),monitorGrid=$('#monitorGrid'),remoteEditorDrafts=captureRemoteOnboardingEditors(monitorGrid);releasePreviewObjectUrls(monitorGrid);monitorGrid.innerHTML=onboarded||availablePcAgents.length
      ? deviceGroupBand('encoder-band',[
          {label:'Kiloview Encoders',devices:encoderDevices,cards:encoderDevices.map(kiloviewCard).join('')},
          {label:'TeleTool Encoders',devices:teletools,cards:teletools.map(teleToolCard).join('')}
        ])+deviceGroupBand('receiver-band',[
          {label:'Kiloview Decoders',devices:decoderDevices,cards:decoderDevices.map(kiloviewCard).join('')},
          {label:'Windows NDI Endpoints',devices:windowsEndpoints,cards:windowsCards}
        ])+deviceGroup('Kiloview Needs Attention',unknownDevices,unknownDevices.map(kiloviewCard).join(''))
      : '<div class="empty">No onboarded devices yet.</div>';
    restoreRemoteOnboardingEditors(monitorGrid,remoteEditorDrafts);
    state.monitorCardSignature=cardSignature;
    const reapplyLocalPc=monitorGrid.querySelector('[data-local-pc-action="reapply"]');
    if(reapplyLocalPc)reapplyLocalPc.onclick=()=>reapplyPreferredInterface(reapplyLocalPc);
    monitorGrid.querySelectorAll('[data-remote-pc-action="remove"]').forEach(button=>button.onclick=()=>removeRemotePc(button));
    setTimeout(refreshEncoderPreviews,0);
  }
  refreshTeleToolTemperatures(teletools);
}
function validUnicastIpv4(value){const number=ipv4Number(value),first=number===null?null:number>>>24;return number!==null&&number!==0&&number!==0xffffffff&&first>0&&first!==127&&first<224}
function usableIpv4Host(value,prefix){const number=ipv4Number(value),length=Number(prefix);if(number===null||!Number.isInteger(length)||length<1||length>30)return false;const mask=(0xffffffff<<(32-length))>>>0,network=(number&mask)>>>0,broadcast=(network|(~mask>>>0))>>>0;return number!==network&&number!==broadcast}
function subnetMaskFromPrefix(prefix){const length=Number(prefix);if(!Number.isInteger(length)||length<0||length>32)return'';const mask=length===0?0:(0xffffffff<<(32-length))>>>0;return[24,16,8,0].map(shift=>(mask>>>shift)&255).join('.')}
function prefixFromSubnetMask(mask){const value=ipv4Number(mask);if(value===null)return null;const inverse=(~value)>>>0;if((inverse&(inverse+1))!==0)return null;let bits=0,remaining=value;while(remaining){bits+=remaining&1;remaining>>>=1}return bits}
function remoteNetworkPayload(form){
  const mode=form.elements.mode.value;
  const adapterId=form.elements.adapterId.value;
  if(mode==='unchanged'||mode==='dhcp')return{network:{mode,adapterId}};
  const address=form.elements.address.value.trim(),prefix=prefixFromSubnetMask(form.elements.subnetMask.value.trim()),gateway=form.elements.defaultGateway.value.trim(),server=state.selectedNetwork?.address;
  if(prefix===null||prefix<1||prefix>30)throw new Error('Enter a contiguous subnet mask between 128.0.0.0 and 255.255.255.252.');
  if(!usableIpv4Host(address,prefix)||!validUnicastIpv4(address))throw new Error('Enter a usable unicast static IPv4 address.');
  if(!server||!addressMatchesNetwork(address,state.selectedNetwork))throw new Error("The static address must remain in the Job Configurator's selected subnet.");
  if(!addressMatchesNetwork(server,{address,prefixLength:prefix}))throw new Error('The selected static prefix would exclude the Job Configurator.');
  if(address===server)throw new Error("The static address cannot be the Job Configurator's own address.");
  if(gateway&&(!usableIpv4Host(gateway,prefix)||!validUnicastIpv4(gateway)||gateway===address||!addressMatchesNetwork(gateway,{address,prefixLength:prefix})))throw new Error('The default gateway must be a different usable address in the configured static subnet.');
  const dnsServers=form.elements.dnsServers.value.split(',').map(value=>value.trim()).filter(Boolean);
  if(dnsServers.length>4||dnsServers.some(value=>!validUnicastIpv4(value)))throw new Error('Enter up to four usable unicast IPv4 DNS servers, separated by commas. Leave blank to clear static DNS.');
  return{network:{mode,adapterId,address,prefixLength:prefix,defaultGateway:gateway||null,dnsServers}};
}
function updateRemoteOnboardingForm(form){form.querySelector('[data-remote-static-fields]').classList.toggle('hidden',form.elements.mode.value!=='static')}
async function pollRemoteOnboarding(endpointId,endpointName){
  if(state.pcOnboardingPolls.has(endpointId))return;
  const poll=async()=>{
    try{
      const result=await api(`/api/pc-agents/${encodeURIComponent(endpointId)}/onboarding/status`);
      if(result.status==='completed'){
        clearInterval(state.pcOnboardingPolls.get(endpointId));state.pcOnboardingPolls.delete(endpointId);state.monitorCardSignature=null;await loadPcAgents();const app=await api('/api/state');if(!$('#monitorView').classList.contains('hidden'))renderMonitor(app);else renderOnboardingPcAgents();toast(`${endpointName} onboarded successfully at ${result.registeredAddress}`);return;
      }
      if(result.status==='failed'||result.status==='expired'){
        clearInterval(state.pcOnboardingPolls.get(endpointId));state.pcOnboardingPolls.delete(endpointId);toast(result.message,true);
      }
    }catch{}
  };
  state.pcOnboardingPolls.set(endpointId,setInterval(poll,3000));await poll();
}
async function openPcAgentOnboarding(form){
  const endpointId=form.dataset.endpointId,endpointName=form.dataset.endpointName||'the Windows PC',button=form.querySelector('button[type="submit"]');
  try{
    button.disabled=true;button.textContent='Waiting for local approval…';
    const result=await api(`/api/pc-agents/${encodeURIComponent(endpointId)}/onboarding/open`,{method:'POST',body:JSON.stringify(remoteNetworkPayload(form))});
    toast(`${endpointName} approved the request; waiting for elevated registration`);pollRemoteOnboarding(endpointId,endpointName);return result;
  }catch(err){toast(err.message,true)}
  finally{button.disabled=false;button.textContent='Request local approval'}
}
async function removeRemotePc(button){
  const endpointId=button.dataset.endpointId,endpointName=button.dataset.endpointName||'this Windows PC';
  if(!confirm(`Remove ${endpointName} from this job?\n\nThis removes its Job Configurator registration only. It does not change NDI settings on the remote PC.`))return;
  try{
    button.disabled=true;button.textContent='Removing…';
    await api(`/api/pc-onboarding/${encodeURIComponent(endpointId)}`,{method:'DELETE'});
    state.monitorCardSignature=null;
    renderMonitor(await api('/api/state'));
    toast(`${endpointName} removed from the job`);
  }catch(err){
    button.disabled=false;button.textContent='Remove';
    toast(err.message,true);
  }
}
async function reapplyPreferredInterface(button){
  if(!state.selectedNetwork){toast('Return to New onboarding and select an active network adapter.',true);return}
  try{
    button.disabled=true;button.textContent='Applying…';
    const selected=await persistNetworkSelection(state.selectedNetwork);
    if(!selected.localPc?.preferredInterfaceConfigured)throw new Error(selected.localPc?.error||'NDI Access Manager did not retain the preferred interface.');
    renderMonitor(await api('/api/state'));
    toast('Preferred NDI interface reapplied');
  }catch(err){
    button.disabled=false;button.textContent='Reapply preferred interface';
    toast(err.message,true);
    try{renderMonitor(await api('/api/state'))}catch{}
  }
}
function prefixLength(mask){
  if(!mask)return'';
  return mask.split('.').reduce((total,octet)=>total+(Number(octet)>>>0).toString(2).replace(/0/g,'').length,0);
}
function renderMulticastPlan(plan){
  state.multicastPlan=plan;
  const assignments=plan.assignments||[],senders=assignments.filter(a=>a.sender),receivers=assignments.filter(a=>a.receiver),applied=assignments.filter(a=>a.status==='applied').length,errors=assignments.filter(a=>a.status==='error').length;
  $('#multicastPool').textContent=`${plan.poolPrefix}/${prefixLength(plan.poolNetmask)}`;
  $('#multicastPoolMeta').textContent=`${plan.poolPrefix} – ${plan.poolLastAddress} · organization-local scope · TTL ${plan.ttl}`;
  $('#multicastAllocationMask').textContent=`/${prefixLength(plan.allocationNetmask)} per sender`;
  const access=$('#accessManagerStatus');
  access.className=`access-manager-status ${plan.includeLocalPc?(plan.accessManagerRunning?'warning-state':plan.accessManagerDetected?'ready':'warning-state'):'disabled-state'}`;
  access.innerHTML=plan.includeLocalPc
    ? plan.accessManagerRunning
      ? '<strong>Close NDI Access Manager before applying</strong><span>Access Manager caches its settings and can overwrite this job when it exits. Close it, then apply this plan.</span>'
      : plan.accessManagerDetected
      ? '<strong>NDI Access Manager detected</strong><span>This PC will be configured as a sender and receiver. The in-app card preview receiver will reload automatically.</span>'
      : '<strong>NDI configuration will be created</strong><span>Access Manager was not detected. The shared NDI configuration will still be written; install NDI Tools if the runtime is missing.</span>'
    : '<strong>Local PC excluded</strong><span>No Access Manager changes will be made.</span>';
  $('#multicastSummary').innerHTML=`<div><strong>${assignments.length}</strong><span>ENDPOINTS</span></div><div><strong>${senders.length}</strong><span>SENDERS</span></div><div><strong>${receivers.length}</strong><span>RECEIVERS</span></div><div><strong>${applied}</strong><span>APPLIED</span></div>`;
  $('#multicastAssignments').innerHTML=`<div class="multicast-list-head"><div><p class="eyebrow">ENDPOINT PLAN</p><h2>${esc(plan.jobName)}</h2></div><span>${esc(plan.status.toUpperCase())}</span></div><div class="multicast-list">${assignments.map(a=>{
    const stateClass=a.status==='error'?'error':a.status==='applied'?'applied':'planned',role=[a.sender?'SEND':'',a.receiver?'RECEIVE':''].filter(Boolean).join(' + ');
    return `<article class="multicast-assignment ${stateClass}"><div class="multicast-assignment-icon">${multicastIcon(a)}</div><div class="multicast-assignment-name"><strong>${esc(a.hostname)}</strong><span>${esc(a.family)} · ${esc(a.address)}</span></div><div><small>ROLE</small><strong>${esc(role||a.role)}</strong></div><div><small>ALLOCATION</small><strong>${a.netPrefix?`${esc(a.netPrefix)}/${prefixLength(a.netmask)}`:'Uses sender ranges'}</strong></div><div><small>STATUS</small><strong>${esc(a.status.toUpperCase())}</strong>${a.error?`<span class="assignment-error">${esc(a.error)}</span>`:''}</div></article>`;
  }).join('')}</div>`;
  $('#multicastApplySummary').textContent=errors?`${errors} endpoint${errors===1?'':'s'} need attention`:`${senders.length} sender range${senders.length===1?'':'s'} · ${receivers.length} receiver${receivers.length===1?'':'s'}`;
  const apply=$('#applyMulticast');
  apply.disabled=plan.status==='running'||plan.status==='completed';
  apply.querySelector('span').textContent=plan.status==='completed'?'Multicast configured':plan.status==='partial'?'Retry multicast setup':'Apply multicast setup';
}
async function loadMulticastSetup(regenerate=false){
  const ttl=Math.max(1,Math.min(255,Number($('#multicastTtl').value)||1)),includeLocalPc=$('#includeLocalPc').checked,apply=$('#applyMulticast');
  const networkStatus=$('#multicastNetworkAdapter');
  const preferredApplied=state.localPc?.preferredInterfaceConfigured===true;
  networkStatus.className=`access-manager-status ${includeLocalPc&&state.selectedNetwork&&preferredApplied?'ready':includeLocalPc?'warning-state':'disabled-state'}`;
  networkStatus.innerHTML=includeLocalPc&&state.selectedNetwork&&preferredApplied
    ? `<strong>Preferred NDI interface</strong><span>${esc(state.selectedNetwork.name)} · ${esc(state.selectedNetwork.address)}/${state.selectedNetwork.prefixLength} · local PC already onboarded</span>`
    : includeLocalPc&&state.selectedNetwork
      ? `<strong>Preferred NDI interface needs attention</strong><span>${esc(state.localPc?.error||'Return to New onboarding and reapply the selected adapter.')}</span>`
    : includeLocalPc
      ? '<strong>Network adapter required</strong><span>Return to New onboarding and select an active adapter.</span>'
      : '<strong>Local PC excluded</strong><span>No local network adapter is required.</span>';
  $('#multicastPool').textContent='Generating…';$('#multicastPoolMeta').textContent='Checking onboarded endpoints and selecting conflict-free ranges.';apply.disabled=true;
  try{
    const plan=await api('/api/multicast/plan',{method:'POST',body:JSON.stringify({includeLocalPc,ttl,regenerate})});
    renderMulticastPlan(plan);
  }catch(err){
    state.multicastPlan=null;$('#multicastPool').textContent='Plan unavailable';$('#multicastPoolMeta').textContent=err.message;$('#multicastAssignments').innerHTML=`<div class="empty">${esc(err.message)}</div>`;toast(err.message,true);
  }
}
async function applyMulticastSetup(){
  if(!state.multicastPlan)return;
  const button=$('#applyMulticast');
  if(!confirm(`Apply multicast setup to ${state.multicastPlan.assignments.length} endpoint${state.multicastPlan.assignments.length===1?'':'s'}?\n\nActive NDI senders may restart briefly. The local in-app preview receiver will reload automatically; other running NDI applications on this PC must be restarted.`))return;
  try{
    button.disabled=true;button.querySelector('span').textContent='Applying…';
    const result=await api('/api/multicast/apply',{method:'POST',body:JSON.stringify(state.multicastPlan)});
    renderMulticastPlan(result.configuration);
    const app=await api('/api/state');renderMonitor(app);
    if(!result.failed)show('monitor');
    const reserved=result.configuration.assignments.filter(assignment=>assignment.status==='reserved').length,reservedText=reserved?`; ${reserved} remote Windows endpoint${reserved===1?' needs':'s need'} an agent update or manual setup`:'';
    toast(result.failed?`Multicast applied to ${result.applied}${reservedText}; ${result.failed} endpoint${result.failed===1?'':'s'} need attention`:`Multicast applied to ${result.applied} endpoint${result.applied===1?'':'s'}${reservedText}`,result.failed>0);
  }catch(err){toast(err.message,true);button.disabled=false;button.querySelector('span').textContent='Retry multicast setup'}
}
async function revertMulticastSetup(){
  if(!state.multicastConfigured)return;
  const button=$('#revertMulticast');
  if(!confirm('Revert every onboarded endpoint to unicast?\n\nMulticast send and receive will be disabled on all devices and this Windows PC. Active TeleTool streams will restart briefly. Device names, NDI groups, and discovery-server settings will be preserved.'))return;
  try{
    button.disabled=true;button.querySelector('span').textContent='Reverting…';
    const result=await api('/api/multicast/revert',{method:'POST',body:'{}'});
    const app=await api('/api/state');renderMonitor(app);
    if(!result.failed){
      show('monitor');
      toast(`All ${result.reverted} endpoints reverted to unicast`);
      return;
    }
    if(result.configuration)renderMulticastPlan(result.configuration);
    toast(`${result.reverted} endpoints reverted; ${result.failed} need attention${result.errors?.length?`: ${result.errors.join(' · ')}`:''}`,true);
  }catch(err){
    toast(err.message,true);
  }finally{
    button.querySelector('span').textContent='Revert all to unicast';
    button.disabled=!state.multicastConfigured;
  }
}
$('#regenerateMulticast').onclick=()=>loadMulticastSetup(true);
$('#multicastTtl').onchange=()=>loadMulticastSetup(false);
$('#includeLocalPc').onchange=()=>loadMulticastSetup(false);
$('#applyMulticast').onclick=applyMulticastSetup;
$('#revertMulticast').onclick=revertMulticastSetup;
async function refreshMonitor(){if($('#monitorView').classList.contains('hidden'))return;try{const [app]=await Promise.all([api('/api/state'),loadPcAgents()]);renderMonitor(app)}catch{}}
async function removeKiloview(button){
  const id=button.dataset.deviceId,name=button.dataset.deviceName||'this Kiloview';
  if(!confirm(`Remove ${name} from this job?\n\nThis deletes its KiloLink registration and Job Configurator card. The device's network, login, and NDI settings are retained.`))return;
  try{
    button.disabled=true;button.textContent='Removing…';
    const result=await api(`/api/devices/${encodeURIComponent(id)}`,{method:'DELETE'});
    state.titleCardIds.delete(id);state.titleCardSources.delete(id);state.monitorCardSignature=null;
    renderMonitor(await api('/api/state'));
    toast(`${name} removed · ${result.managedKiloviewCount} Kiloview${result.managedKiloviewCount===1?'':'s'} remaining`);
  }catch(err){
    toast(err.message,true);
    await refreshMonitor();
  }
}
async function controlTeleTool(button){
  const action=button.dataset.teletoolAction,id=button.dataset.deviceId;
  if(action==='remove'){
    const name=button.dataset.deviceName||'this TeleTool';
    if(!confirm(`Remove ${name} from this job?\n\nThis releases its Fleet Manager adoption and removes its card. Network and NDI settings are retained, and an active stream will keep running.`))return;
    try{
      button.disabled=true;
      button.textContent='Removing…';
      const result=await api(`/api/teletools/${encodeURIComponent(id)}`,{method:'DELETE'});
      renderMonitor(await api('/api/state'));
      toast(`${name} removed from the TeleTool fleet · ${result.managedTeleToolCount} remaining`);
    }catch(err){
      toast(err.message,true);
      await refreshMonitor();
    }
    return;
  }
  try{
    button.disabled=true;
    button.textContent=action==='start'?'Starting…':'Stopping…';
    await api(`/api/teletools/${encodeURIComponent(id)}/${action}`,{method:'POST',body:'{}'});
    renderMonitor(await api('/api/state'));
    toast(`TeleTool NDI ${action==='start'?'started':'stopped'}`);
  }catch(err){
    toast(err.message,true);
    await refreshMonitor();
  }
}
setInterval(refreshMonitor,15000);
setInterval(refreshEncoderPreviews,5000);
document.addEventListener('click',e=>{const button=e.target.closest('[data-teletool-action]');if(button){e.preventDefault();controlTeleTool(button)}});
document.addEventListener('click',e=>{const button=e.target.closest('[data-kiloview-action]');if(button){e.preventDefault();removeKiloview(button)}});
document.addEventListener('change',e=>{const form=e.target.closest('[data-remote-onboarding-form]');if(form&&e.target.matches('[data-remote-network-mode]'))updateRemoteOnboardingForm(form)});
document.addEventListener('submit',e=>{const form=e.target.closest('[data-remote-onboarding-form]');if(form){e.preventDefault();openPcAgentOnboarding(form)}});
document.addEventListener('click',e=>{const a=e.target.closest('[data-action]');if(!a)return;e.preventDefault();if(a.dataset.action==='back-setup'||a.dataset.action==='new-job')show('setup');if(a.dataset.action==='monitor'||a.dataset.action==='back-multicast')api('/api/state').then(x=>{renderMonitor(x);show('monitor')});if(a.dataset.action==='name-displays')openDisplayNaming();if(a.dataset.action==='multicast'){show('multicast');loadMulticastSetup(false)}if(a.dataset.action==='settings'){state.returnView=views.find(v=>!$(`#${v}View`).classList.contains('hidden'))||'setup';show('settings');loadSystemSettings()}if(a.dataset.action==='back-settings')show(state.returnView||'setup')});
document.addEventListener('click',async e=>{
  const button=e.target.closest('[data-local-onboarding-action="refresh"]');
  if(!button)return;
  try{
    button.disabled=true;button.textContent='Checking…';
    await refreshNdiPreflight();
    if(state.selectedNetwork)await persistNetworkSelection(state.selectedNetwork);
    toast(state.ndiPreflight?.ready?'NDI application preflight passed':'Close the listed NDI client applications before continuing',state.ndiPreflight?.ready===false);
  }catch(err){toast(err.message,true);renderOnboardingLocalPcCard()}
});
boot();
