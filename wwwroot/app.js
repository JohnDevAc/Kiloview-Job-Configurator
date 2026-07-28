const $=s=>document.querySelector(s), $$=s=>[...document.querySelectorAll(s)];
const state={settings:null,devices:[],discovery:null,selected:new Set(),skipped:new Set(),plan:null,multicastPlan:null,poll:null,afterFirmware:null,titleCardIds:new Set(),titleCardSources:new Map(),previewWarnings:new Map(),returnView:'setup',systemInfo:null,updateInfo:null};
const views=['setup','discover','plan','progress','firmware','decoder','monitor','multicast','settings'];
function show(name){views.forEach(v=>$(`#${v}View`).classList.toggle('hidden',v!==name));scrollTo({top:0,behavior:'smooth'});if(name==='setup')setTimeout(detectInfrastructure,0)}
function toast(message,error=false){const el=$('#toast');el.textContent=message;el.className=error?'show error':'show';setTimeout(()=>el.className='',4200)}
async function api(url,options={}){const response=await fetch(url,{headers:{'Content-Type':'application/json'},...options});const text=await response.text();let data;try{data=text?JSON.parse(text):null}catch{data={error:text}}if(!response.ok)throw new Error(data?.error||data?.detail||`Request failed (${response.status})`);return data}
function esc(v=''){return String(v).replace(/[&<>'"]/g,c=>({'&':'&amp;','<':'&lt;','>':'&gt;',"'":'&#39;','"':'&quot;'}[c]))}
function roleClass(r){return String(r).toLowerCase()}
function isTeleTool(d){return d?.family==='TeleTool'||d?.family==='SimulatedTeleTool'||d?.model==='TeleTool'}
function deviceUrl(d){return `http://${d.ipAddress}${d.webPort&&d.webPort!==80?`:${d.webPort}`:''}`}

async function boot(){
  try{const health=await api('/api/health'),development=String(health.channel).toLowerCase()==='development';$('#appVersion').textContent=`v${health.version}`;$('#headerVersion').textContent=`Version v${health.version}${development?' · DEV':''}`;$('#developmentBanner').classList.toggle('hidden',!development);document.body.classList.toggle('development-build',development);$('#serviceDot').className='online';const cidrs=await api('/api/network/subnets');$('#scanCidrs').value=cidrs.join('\n');const app=await api('/api/state');state.devices=app.devices||[];const localOnboarded=app.multicast?.assignments?.some(a=>a.endpointId==='local-pc');if(app.lastJob&&(state.devices.some(d=>d.isOnboarded)||localOnboarded)){renderMonitor(app);show('monitor')}else show('setup')}
  catch(e){toast(`Service unavailable: ${e.message}`,true)}
}

$('#setupForm').addEventListener('submit',async e=>{
  e.preventDefault();const f=new FormData(e.currentTarget);state.settings={kiloLinkServerIp:f.get('kiloLinkServerIp').trim(),kiloLinkOnboardingCode:'',kiloLinkUsername:f.get('kiloLinkUsername').trim(),kiloLinkPassword:f.get('kiloLinkPassword'),kiloLinkPort:+f.get('kiloLinkPort'),kiloLinkWebPort:+f.get('kiloLinkWebPort'),ndiDiscoveryServerIp:f.get('ndiDiscoveryServerIp').trim(),staticStart:f.get('staticStart').trim(),staticEnd:f.get('staticEnd').trim(),subnetMask:f.get('subnetMask').trim(),gateway:f.get('gateway').trim(),jobName:f.get('jobName').trim(),deviceIds:[]};
  const scanCidrs=f.get('scanCidrs').split(/[\n,]+/).map(x=>x.trim()).filter(Boolean);const simulation=f.get('simulation')==='on';
  try{e.submitter.disabled=true;e.submitter.querySelector('span').textContent='Scanning…';const result=await api('/api/discovery',{method:'POST',body:JSON.stringify({scanCidrs,credentials:{username:f.get('username')||'admin',password:f.get('password')||'admin'},simulation})});state.devices=result.devices;state.discovery=result;state.skipped=new Set();state.selected=new Set(result.devices.filter(d=>d.canOnboard!==false&&!inRange(d.ipAddress,state.settings.staticStart,state.settings.staticEnd)).map(d=>d.id));renderDiscovery(result);show('discover')}
  catch(err){toast(err.message,true)}finally{e.submitter.disabled=false;e.submitter.querySelector('span').textContent='Scan network'}
});
const kiloLinkIp=$('input[name="kiloLinkServerIp"]'),kiloLinkUser=$('input[name="kiloLinkUsername"]'),kiloLinkPassword=$('input[name="kiloLinkPassword"]'),credentialHint=$('#kiloLinkCredentialHint'),storedCredential=$('#kiloLinkStoredCredential'),storedUsername=$('#kiloLinkStoredUsername'),storedPassword=$('#kiloLinkStoredPassword'),revealStoredPassword=$('#showKiloLinkStoredPassword'),storedSecurityNote=$('#kiloLinkStoredSecurityNote');
function maskStoredKiloLinkPassword(){storedPassword.textContent='••••••••';storedPassword.dataset.revealed='false';revealStoredPassword.textContent='View'}
function hideStoredKiloLinkCredential(){storedCredential.classList.add('hidden');storedCredential.dataset.serverIp='';storedUsername.textContent='';maskStoredKiloLinkPassword()}
async function refreshKiloLinkCredentials(){
  const serverIp=kiloLinkIp.value.trim();
  if(!serverIp){hideStoredKiloLinkCredential();credentialHint.textContent='Stored locally in Windows Credential Manager. Leave blank when reusing a stored login.';return}
  try{
    const status=await api(`/api/kilolink/credentials?serverIp=${encodeURIComponent(serverIp)}`);
    if(status.stored){
      const username=status.username||'Stored user';
      kiloLinkUser.value=username;kiloLinkPassword.value='';storedUsername.textContent=username;maskStoredKiloLinkPassword();revealStoredPassword.classList.toggle('hidden',status.canReveal!==true);storedSecurityNote.textContent=status.canReveal===true?'The password remains protected until View is selected. Leave the password field blank to use it.':'To view the password, open this page on the setup PC using localhost.';storedCredential.dataset.serverIp=serverIp;storedCredential.classList.remove('hidden');
      credentialHint.textContent=`Stored login found for ${username}. Leave the password blank to reuse it.`;
    }else{
      hideStoredKiloLinkCredential();credentialHint.textContent='No stored login was found for this server. Enter a username and password.';
    }
  }catch{hideStoredKiloLinkCredential();credentialHint.textContent='Enter a valid KiloLink server IP to check stored credentials.'}
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
async function detectInfrastructure(){await detectKiloLink(true);await detectNdiDiscovery(true)}
async function detectKiloLink(automatic=false){const button=$('#findKiloLink'),result=$('#kiloLinkDiscoveryResult'),port=+$('input[name="kiloLinkWebPort"]').value||80;if(button.disabled)return;const initial=kiloLinkIp.value.trim();try{button.disabled=true;button.textContent='Searching…';result.className='';result.textContent='Scanning local networks…';const servers=await api(`/api/kilolink/discover?webPort=${port}`);if(!servers.length){result.className='error-text';result.textContent='No KiloLink Server found';return}const server=servers.find(candidate=>candidate.serverIp===initial)||servers[0];if(kiloLinkIp.value.trim()===initial)kiloLinkIp.value=server.serverIp;result.className='ok';result.textContent=`Found ${server.serverIp} · ${server.version}`;await refreshKiloLinkCredentials()}catch(err){result.className='error-text';result.textContent=automatic?'Automatic search failed':err.message}finally{button.disabled=false;button.textContent='Find KiloLink Server'}}
$('#findKiloLink').onclick=()=>detectKiloLink(false);
async function detectNdiDiscovery(automatic=false){const button=$('#findNdiDiscovery'),result=$('#ndiDiscoveryResult'),input=$('input[name="ndiDiscoveryServerIp"]');if(button.disabled)return;const initial=input.value.trim();try{button.disabled=true;button.textContent='Searching…';result.className='';result.textContent='Scanning TCP 5959…';const servers=await api('/api/ndi/discover');if(!servers.length){result.className='error-text';result.textContent='No NDI Discovery Server found';return}const kilolink=kiloLinkIp.value.trim(),prefix=kilolink.split('.').slice(0,3).join('.')+'.',server=servers.find(candidate=>candidate.serverIp===initial)||servers.find(candidate=>candidate.serverIp===kilolink)||servers.find(candidate=>prefix!=='.'&&candidate.serverIp.startsWith(prefix))||servers[0];if(input.value.trim()===initial)input.value=server.serverIp;result.className='ok';result.textContent=`Found ${server.serverIp}:${server.port}`}catch(err){result.className='error-text';result.textContent=automatic?'Automatic search failed':err.message}finally{button.disabled=false;button.textContent='Find NDI Discovery Server'}}
$('#findNdiDiscovery').onclick=()=>detectNdiDiscovery(false);
kiloLinkIp.addEventListener('input',()=>{if(storedCredential.dataset.serverIp!==kiloLinkIp.value.trim())hideStoredKiloLinkCredential()});
kiloLinkIp.addEventListener('blur',refreshKiloLinkCredentials);
$('#testKiloLink').onclick=async()=>{const button=$('#testKiloLink'),result=$('#kiloLinkTestResult'),f=new FormData($('#setupForm'));try{button.disabled=true;button.textContent='Testing…';const status=await api('/api/kilolink/test',{method:'POST',body:JSON.stringify({serverIp:f.get('kiloLinkServerIp').trim(),webPort:+f.get('kiloLinkWebPort'),username:f.get('kiloLinkUsername').trim(),password:f.get('kiloLinkPassword')})});kiloLinkPassword.value='';result.className='ok';result.textContent=`Connected · Server ${status.version} · ${status.deviceCount} registered device${status.deviceCount===1?'':'s'}`;await refreshKiloLinkCredentials()}catch(err){result.className='error-text';result.textContent=err.message}finally{button.disabled=false;button.textContent='Test KiloLink login'}};
function ipn(ip){return ip.split('.').reduce((n,x)=>n*256+(+x),0)}function inRange(ip,a,b){return ipn(ip)>=ipn(a)&&ipn(ip)<=ipn(b)}
function renderDiscovery(result){
  const fresh=result.devices.filter(d=>d.canOnboard!==false&&!inRange(d.ipAddress,state.settings.staticStart,state.settings.staticEnd)),teletools=result.devices.filter(isTeleTool),kiloviews=result.devices.length-teletools.length;
  $('#discoverySummary').innerHTML=`<div><strong>${kiloviews}</strong><small>Kiloviews found</small></div><div><strong>${teletools.length}</strong><small>TeleTools found</small></div><div><strong>${fresh.length}</strong><small>Ready to onboard</small></div><div><strong>${result.scannedCidrs.length}</strong><small>Networks scanned</small></div>`;
  $('#discoveredGrid').innerHTML=result.devices.length?result.devices.map(d=>{const locked=inRange(d.ipAddress,state.settings.staticStart,state.settings.staticEnd),blocked=d.canOnboard===false,disabled=locked||blocked,selected=state.selected.has(d.id),standalone=state.skipped.has(d.id),label=blocked?(d.managementMessage||'NOT AVAILABLE'):locked?'IN STATIC RANGE':isTeleTool(d)?'ENCODER · DEV API':d.role;return `<article class="device-card ${selected?'selected':''} ${standalone?'skipped':''} ${blocked?'unavailable':''}" data-id="${esc(d.id)}"><header><span class="model">${esc(d.model)} · ${esc(d.family)}</span><input class="selectbox" type="checkbox" ${selected?'checked':''} ${disabled?'disabled':''} aria-label="Onboard ${esc(d.hostname)}"></header><h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}${d.webPort&&d.webPort!==80?`:${d.webPort}`:''}</div><div class="meta"><span class="pill">${esc(d.macAddress)}</span><span class="pill ${isTeleTool(d)?'teletool':roleClass(d.role)}">${esc(label)}</span>${standalone?'<span class="pill standalone">STANDALONE · NO CHANGES</span>':''}${d.firmwareVersion?`<span class="pill">${esc(d.firmwareVersion)}</span>`:''}</div>${blocked?`<div class="error-text">${esc(d.managementMessage||d.lastError||'This unit cannot be selected.')}</div>`:disabled?'':`<div class="card-actions discovery-decision"><button type="button" data-discovery-action="${selected?'skip':'include'}" data-device-id="${esc(d.id)}">${selected?'Leave standalone':'Include in onboarding'}</button></div>`}</article>`}).join(''):'<div class="empty">No Kiloview N6/N60 or TeleTool Dev units responded. Check the scan CIDR, cabling, and relevant device services.</div>';
  $$('#discoveredGrid .selectbox').forEach(box=>box.onchange=()=>setDiscoveryDecision(box.closest('.device-card').dataset.id,box.checked));
  $$('#discoveredGrid [data-discovery-action]').forEach(button=>button.onclick=()=>setDiscoveryDecision(button.dataset.deviceId,button.dataset.discoveryAction==='include'));
  updateSelected()
}
function setDiscoveryDecision(id,onboard){if(onboard){state.selected.add(id);state.skipped.delete(id)}else{state.selected.delete(id);state.skipped.add(id)}renderDiscovery(state.discovery)}
function updateSelected(){$('#selectedCount').textContent=`${state.selected.size} onboard · ${state.skipped.size} standalone`;$('#buildPlan').disabled=!state.selected.size}
$('#buildPlan').onclick=async()=>{try{state.settings.deviceIds=[...state.selected];state.plan=await api('/api/onboarding/plan',{method:'POST',body:JSON.stringify(state.settings)});if(state.skipped.size)state.plan={...state.plan,warnings:[...(state.plan.warnings||[]),`${state.skipped.size} discovered device${state.skipped.size===1?' is':'s are'} being left standalone with no configuration changes.`]};state.settings.kiloLinkPassword='';kiloLinkPassword.value='';credentialHint.textContent='KiloLink login stored locally for this server.';renderPlan();show('plan')}catch(e){toast(e.message,true)}};
function renderPlan(){const hasKiloview=state.plan.devices.some(d=>!['TeleTool','SimulatedTeleTool'].includes(d.family)),hasTeleTool=state.plan.devices.some(d=>['TeleTool','SimulatedTeleTool'].includes(d.family));$('#planRows').innerHTML=state.plan.devices.map(d=>`<div class="plan-row"><div><small>${['TeleTool','SimulatedTeleTool'].includes(d.family)?'TELETOOL':'KILOVIEW'} · FOUND ADDRESS</small><span class="from">${esc(d.currentIp)}</span></div><div class="arrow">→</div><div><small>STATIC ADDRESS</small><span class="to">${esc(d.targetIp)}</span></div><div><small>INITIAL HOSTNAME</small><strong>${esc(d.hostname)}</strong></div></div>`).join('');$('#planWarnings').innerHTML=state.plan.warnings.map(w=>`<div class="warning">⚠ ${esc(w)}</div>`).join('');$('#planCount').textContent=`${state.plan.devices.length} device${state.plan.devices.length===1?'':'s'} will be changed`;$('#planConfirmation').textContent=hasKiloview?'Confirmation accepts the EULA only on selected Kiloview units; TeleTools use their Dev management API.':'TeleTools will be configured through their Dev management API; no Kiloview EULA action is required.';$('#runPlan').querySelector('span').textContent=hasKiloview?hasTeleTool?'Accept Kiloview EULA & onboard all':'Accept EULA & onboard':'Onboard TeleTools'}
$('#runPlan').onclick=async()=>{try{$('#runPlan').disabled=true;await api('/api/onboarding/run',{method:'POST',body:JSON.stringify(state.plan)});show('progress');pollProgress()}catch(e){toast(e.message,true);$('#runPlan').disabled=false}};
async function pollProgress(){clearInterval(state.poll);const tick=async()=>{try{const p=await api('/api/onboarding/progress');renderProgress(p);if(!['idle','running'].includes(p.status)){clearInterval(state.poll);state.poll=null;await loadDecoderOrMonitor(p.status)}}catch(e){toast(e.message,true)}};await tick();state.poll=setInterval(tick,1200)}
function renderProgress(p){const pct=p.total?Math.round(p.completed/p.total*100):0;$('#progressBar').style.width=`${pct}%`;$('#progressTitle').textContent=p.status==='running'?'Applying configuration':p.status.replaceAll('-',' ');$('#progressMessage').textContent=p.status==='running'?`${p.completed} of ${p.total} stages complete · mode changes can take one minute`:'Onboarding pass finished.';$('#progressSteps').innerHTML=p.steps.length?p.steps.slice().reverse().map(s=>`<div class="timeline-row"><i class="health ${esc(s.status==='ok'?'online':s.status)}"></i><code>${esc(s.ipAddress)}</code><strong>${esc(s.step)}</strong><span class="${esc(s.status)}">${esc(s.message||s.status)}</span></div>`).join(''):'<div class="empty">Preparing the first device…</div>'}
function currentRunDevices(devices){const ids=new Set((state.plan?.devices||[]).map(device=>device.deviceId));return ids.size?devices.filter(device=>ids.has(device.id)):devices}
async function loadDecoderOrMonitor(status){const app=await api('/api/state');state.devices=app.devices||[];const onboarded=state.devices.filter(d=>d.isOnboarded),runDevices=currentRunDevices(onboarded),kiloviews=runDevices.filter(d=>!isTeleTool(d)),decoders=kiloviews.filter(d=>d.role==='Decoder'),encoders=kiloviews.filter(d=>d.role==='Encoder');state.afterFirmware=async()=>{if(kiloviews.length&&status!=='failed'){renderDecoders(decoders,encoders);show('decoder');if(decoders.length)await prepareIdentityCards()}else{renderMonitor(app);show('monitor')}};if(status!=='failed'&&kiloviews.length){renderFirmware({...app,devices:kiloviews});show('firmware')}else state.afterFirmware()}
function renderFirmware(app){const devices=(app.devices||[]).filter(d=>d.isOnboarded&&!isTeleTool(d)),n6=devices.filter(d=>d.model.toUpperCase().startsWith('N6')&&!d.model.toUpperCase().startsWith('N60')).length,n60=devices.filter(d=>d.model.toUpperCase().startsWith('N60')).length;$('#n6FirmwareLabel').classList.toggle('hidden',n6===0);$('#n60FirmwareLabel').classList.toggle('hidden',n60===0);$('input[name="n6Firmware"]').required=n6>0;$('input[name="n60Firmware"]').required=n60>0;$('#firmwareCoverage').innerHTML=`<div><strong>${n6}</strong><small>N6 units</small></div><div><strong>${n60}</strong><small>N60 units</small></div><div><strong>${devices.length}</strong><small>Kiloview fleet</small></div>`;$('#firmwareStatus').innerHTML=''}
$('#firmwareForm').addEventListener('submit',async e=>{e.preventDefault();const button=$('#stageFirmware');try{button.disabled=true;button.querySelector('span').textContent='Staging…';const staged=await upload('/api/firmware/stage',new FormData(e.currentTarget));$('#firmwareStatus').innerHTML=staged.packages.map(p=>`<div class="firmware-package"><strong>${esc(p.model)}</strong><span>${esc(p.fileName)} · <code>${esc(p.sha256.slice(0,12))}</code></span></div>`).join('');button.querySelector('span').textContent='Start fleet update';button.type='button';button.onclick=startFleetUpdate;button.disabled=false}catch(err){toast(err.message,true);button.disabled=false;button.querySelector('span').textContent='Stage firmware'}});
async function upload(url,body){const response=await fetch(url,{method:'POST',body});const text=await response.text();let data;try{data=text?JSON.parse(text):null}catch{data={error:text}}if(!response.ok)throw new Error(data?.error||data?.detail||`Request failed (${response.status})`);return data}
async function startFleetUpdate(){const button=$('#stageFirmware');try{button.disabled=true;button.querySelector('span').textContent='Uploading & dispatching…';const result=await api('/api/firmware/start',{method:'POST',body:'{}'});if(result.completed){toast('Fleet firmware update completed');state.afterFirmware?.()}else if(result.started){$('#firmwareStatus').innerHTML=`<div class="note">${esc(result.message)}${result.managementUrl?` <a href="${esc(result.managementUrl)}" target="_blank" rel="noreferrer">Monitor in KiloLink Server ↗</a>`:''}</div>`;$('#skipFirmware').textContent='Continue to decoder setup';toast('KiloLink fleet update dispatched')}else{$('#firmwareStatus').innerHTML=`<div class="warning">${esc(result.message)}</div>`}}catch(err){$('#firmwareStatus').innerHTML=`<div class="warning">${esc(err.message)}</div>`;toast(err.message,true)}finally{button.disabled=false;button.querySelector('span').textContent='Start fleet update'}}
$('#skipFirmware').onclick=()=>state.afterFirmware?.();
async function prepareIdentityCards(){try{const result=await api('/api/onboarding/identify',{method:'POST',body:'{}'});state.titleCardIds=new Set(result.active||[]);state.titleCardSources=new Map((result.cards||[]).map(card=>[card.id,card]));const app=await api('/api/state');state.devices=app.devices||[];const kiloviews=currentRunDevices(state.devices.filter(d=>d.isOnboarded)).filter(d=>!isTeleTool(d));renderDecoders(kiloviews.filter(d=>d.role==='Decoder'),kiloviews.filter(d=>d.role==='Encoder'));if(result.errors?.length)toast(`${result.errors.length} display identity card${result.errors.length===1?'':'s'} could not be shown`,true);else toast('Every NDI identity source now matches its decoder card')}catch(err){toast(`Display identity cards: ${err.message}`,true)}}
function deviceGroup(label,devices,cards){return devices.length?`<section class="device-group"><div class="device-group-title"><h2>${esc(label)}</h2><span>${devices.length} DEVICE${devices.length===1?'':'S'}</span></div><div class="device-grid">${cards}</div></section>`:''}
function clearPreviewWarning(id){const warning=state.previewWarnings.get(id);if(warning?.objectUrl)URL.revokeObjectURL(warning.objectUrl);state.previewWarnings.delete(id)}
function encoderPreview(d){const teletool=isTeleTool(d),label=teletool?'NDI PREVIEW':'HDMI INPUT PREVIEW',latched=teletool&&d.streamRunning?state.previewWarnings.get(d.id):null,source=latched?.objectUrl||`/api/devices/${encodeURIComponent(d.id)}/thumbnail?t=${Date.now()}`,objectUrl=latched?.objectUrl?` data-object-url="${esc(latched.objectUrl)}"`:'',alert=teletool?`<div class="preview-alert ${latched?'':'hidden'}" data-preview-alert><strong>NDI PREVIEW WARNING</strong><span>No video after two attempts · Check NDI output, group and Discovery Server.</span></div>`:'';return `<div class="encoder-preview${latched?' warning':''}" data-teletool="${teletool}" data-stream-active="${teletool&&d.streamRunning}" data-failures="${latched?.failures||0}"><img data-device-id="${esc(d.id)}" src="${esc(source)}"${objectUrl} alt="${label.toLowerCase()} for ${esc(d.hostname)}"><span>${label} · 5s</span>${alert}</div>`}
function setPreviewStatus(img,status,failures=0){const preview=img.closest('.encoder-preview'),streamActive=preview?.dataset.teletool==='true'&&preview.dataset.streamActive==='true',warning=streamActive&&status!=='live'&&(status==='warning'||state.previewWarnings.has(img.dataset.deviceId)),card=preview?.closest('.teletool-card'),alert=card?.querySelector('[data-preview-alert]');preview?.classList.toggle('warning',warning);card?.classList.toggle('preview-error',warning);alert?.classList.toggle('hidden',!warning);if(preview)preview.dataset.failures=String(failures)}
function releasePreviewObjectUrls(root){const latchedUrls=new Set([...state.previewWarnings.values()].map(warning=>warning.objectUrl));root?.querySelectorAll('.encoder-preview img').forEach(img=>{if(img.dataset.objectUrl&&!latchedUrls.has(img.dataset.objectUrl))URL.revokeObjectURL(img.dataset.objectUrl)})}
async function refreshEncoderPreview(img){
  if(img.dataset.refreshing==='1')return;
  img.dataset.refreshing='1';
  const id=img.dataset.deviceId,preview=img.closest('.encoder-preview');
  try{
    const response=await fetch(`/api/devices/${encodeURIComponent(id)}/thumbnail?t=${Date.now()}`,{cache:'no-store'});
    if(!response.ok)throw new Error(`Preview request failed (${response.status})`);
    const status=response.headers.get('X-Kiloview-Preview')||'unavailable',failures=Number(response.headers.get('X-Kiloview-Preview-Failures')||0),blob=await response.blob(),latched=state.previewWarnings.get(id),streamActive=preview?.dataset.streamActive==='true';
    if(status==='live'){
      const next=URL.createObjectURL(blob),previous=img.dataset.objectUrl;
      img.src=next;img.dataset.objectUrl=next;
      if(previous&&previous!==latched?.objectUrl)URL.revokeObjectURL(previous);
      clearPreviewWarning(id);
    }else if(status==='warning'&&streamActive){
      const next=URL.createObjectURL(blob),previous=img.dataset.objectUrl,obsolete=new Set([previous,latched?.objectUrl]);
      img.src=next;img.dataset.objectUrl=next;state.previewWarnings.set(id,{objectUrl:next,failures});
      obsolete.delete(next);obsolete.forEach(url=>{if(url)URL.revokeObjectURL(url)});
    }else if(!latched||!streamActive){
      const next=URL.createObjectURL(blob),previous=img.dataset.objectUrl;
      img.src=next;img.dataset.objectUrl=next;if(previous)URL.revokeObjectURL(previous);
      if(!streamActive)clearPreviewWarning(id);
    }
    setPreviewStatus(img,status,failures);
  }catch{
    const failures=Number(preview?.dataset.failures||0)+1;
    setPreviewStatus(img,'unavailable',failures);
  }finally{img.dataset.refreshing='0'}
}
function refreshEncoderPreviews(){if($('#decoderView').classList.contains('hidden')&&$('#monitorView').classList.contains('hidden'))return;$$('.encoder-preview img').forEach(refreshEncoderPreview)}
function ndiGroupConfirmation(devices){if(!devices.length)return'';const groups=[...new Set(devices.map(d=>d.ndiGroup).filter(Boolean))],group=groups.length===1?groups[0]:groups.join(', '),simulation=devices.every(d=>d.family==='Simulated');return `<section class="ndi-group-confirmation"><span>JOB NAME → NDI GROUP</span><strong>${esc(group)}</strong><p>Applied to all ${devices.length} ${simulation?'simulated ':''}Kiloviews. Every decoder identity card shows this same group.${simulation?' Simulation card sources are also advertised in the public group so Studio Monitor can discover them without an Access Manager change; the simulated devices remain assigned to the Job Name group.':''}</p></section>`}
function renderDecoders(decoders,encoders=[]){const identityForm=d=>`<form class="identity-form" data-id="${esc(d.id)}"><label><span>Hostname</span><input name="hostname" value="${esc(d.hostname)}" required></label><label><span>NDI Channel Name</span><input name="ndiChannelName" value="${esc(d.ndiChannelName)}" required></label><button>Apply names</button></form>`;const decoderCards=decoders.map(d=>{const source=state.titleCardSources.get(d.id);return `<article class="device-card"><header><span class="model">DECODER · ${esc(d.model)}</span><i class="health ${roleClass(d.health)}"></i></header><h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}</div><div class="meta"><span class="pill decoder">${esc(d.ndiGroup)}</span><span class="pill">${esc(d.ndiChannelName)}</span><span class="pill">HDMI ${esc(d.hdmiOutputResolution||'NEGOTIATED')}</span><span class="pill ${state.titleCardIds.has(d.id)?'identity-active':''}">${state.titleCardIds.has(d.id)?'IDENTITY CARD ACTIVE':'STARTING IDENTITY CARD'}</span>${source?`<span class="pill identity-source">SOURCE ${esc(source.name)}</span>`:''}</div>${identityForm(d)}</article>`}).join('');const encoderCards=encoders.map(d=>`<article class="device-card"><header><span class="model">ENCODER · ${esc(d.model)}</span><i class="health ${roleClass(d.health)}"></i></header>${encoderPreview(d)}<h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}</div><div class="meta"><span class="pill encoder">${esc(d.ndiGroup)}</span><span class="pill">${esc(d.ndiChannelName)}</span></div>${identityForm(d)}</article>`).join('');const devices=[...decoders,...encoders],grid=$('#decoderGrid');releasePreviewObjectUrls(grid);grid.innerHTML=ndiGroupConfirmation(devices)+deviceGroup('Decoders · Name the displays',decoders,decoderCards)+deviceGroup('Encoders · Identify & name HDMI inputs',encoders,encoderCards);$$('.identity-form').forEach(form=>form.onsubmit=saveIdentity)}
async function saveIdentity(e){e.preventDefault();const form=e.currentTarget,button=form.querySelector('button'),f=new FormData(form);try{button.disabled=true;button.textContent='Applying…';const device=await api(`/api/devices/${encodeURIComponent(form.dataset.id)}/identity`,{method:'POST',body:JSON.stringify({hostname:f.get('hostname'),ndiChannelName:f.get('ndiChannelName')})});state.devices=state.devices.map(d=>d.id===device.id?device:d);await prepareIdentityCards();toast(`${device.role} ${device.ipAddress} hostname and NDI channel updated`)}catch(err){toast(err.message,true);button.textContent='Try again'}finally{button.disabled=false}}
$('#completeSetup').onclick=async()=>{try{$('#completeSetup').disabled=true;const result=await api('/api/onboarding/complete',{method:'POST',body:'{}'});if(!result.completed)toast(`${result.errors.length} decoder(s) could not be blanked`,true);const app=await api('/api/state');renderMonitor(app);show('monitor')}catch(e){toast(e.message,true)}finally{$('#completeSetup').disabled=false}};
function danteAudioBadge(d){
  const active=d.danteAudioActive===true,status=d.danteAudioStatus||'Unknown',device=d.danteAudioDeviceLabel?` on ${d.danteAudioDeviceLabel}`:'',details=d.danteAudioDetails||`Dante audio status is ${status.toLowerCase()}.`;
  return `<span class="dante-status ${active?'active':'inactive'}" role="status" aria-label="${esc(`Dante audio ${status}${device}`)}" title="${esc(details)}"><strong>DANTE</strong><small>${esc(status.toUpperCase())}</small></span>`;
}
function multicastIcon(endpoint){
  const configured=endpoint.multicastConfigured===true||endpoint.status==='applied',active=endpoint.multicastInUse===true||endpoint.inUse===true,prefix=endpoint.multicastNetPrefix||endpoint.netPrefix,title=active?`Multicast active${prefix?` · ${prefix}`:''}`:configured?`Multicast configured but not currently active${prefix?` · ${prefix}`:''}`:'Multicast is not configured';
  return `<span class="multicast-status ${active?'active':configured?'configured':'inactive'}" role="img" aria-label="${esc(title)}" title="${esc(title)}"><svg viewBox="0 0 28 28" aria-hidden="true"><circle cx="14" cy="14" r="2.5"/><path d="M9.7 9.7a6.1 6.1 0 0 0 0 8.6M18.3 9.7a6.1 6.1 0 0 1 0 8.6M6.2 6.2a11 11 0 0 0 0 15.6M21.8 6.2a11 11 0 0 1 0 15.6"/></svg></span>`;
}
function renderMonitor(app){
  state.devices=app.devices||[];
  const devices=state.devices.filter(d=>d.isOnboarded),teletools=devices.filter(isTeleTool),kiloviews=devices.filter(d=>!isTeleTool(d)),decoderDevices=kiloviews.filter(d=>d.role==='Decoder'),encoderDevices=kiloviews.filter(d=>d.role==='Encoder'),localPc=app.multicast?.assignments?.find(a=>a.endpointId==='local-pc'),localApplied=localPc?.status==='applied',online=devices.filter(d=>d.health==='Online').length+(localApplied?1:0),running=teletools.filter(d=>d.streamRunning).length,onboarded=devices.length+(localPc?1:0);
  const activeTeleToolIds=new Set(teletools.filter(d=>d.streamRunning).map(d=>d.id));[...state.previewWarnings.keys()].filter(id=>!activeTeleToolIds.has(id)).forEach(clearPreviewWarning);
  $('#monitorTitle').textContent=app.lastJob?.jobName||'Device status';
  $('#monitorMeta').textContent=app.lastJob?`${app.lastJob.staticStart} – ${app.lastJob.staticEnd} · NDI discovery ${app.lastJob.ndiDiscoveryServerIp}`:'Health and stream state refresh every 15 seconds.';
  $('#monitorStats').innerHTML=`<div class="stat"><strong>${onboarded}</strong><small>Onboarded</small></div><div class="stat"><strong>${online}</strong><small>Online</small></div><div class="stat"><strong>${onboarded-online}</strong><small>Needs attention</small></div><div class="stat"><strong>${running}/${teletools.length}</strong><small>TeleTool streams live</small></div>`;
  const multicastMeta=d=>d.multicastNetPrefix?`<span class="pill multicast-pill">MC ${esc(d.multicastNetPrefix)}/${d.multicastNetmask==='255.255.255.240'?'28':esc(d.multicastNetmask||'')}</span>`:'';
  const kiloviewCard=d=>`<article class="device-card"><header><span class="model">${esc(d.model)} · ${esc(d.role)}</span><span class="card-indicators">${multicastIcon(d)}<i class="health ${roleClass(d.health)}" title="${esc(d.health)}"></i></span></header>${d.role==='Encoder'?encoderPreview(d):''}<h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}</div><div class="meta"><span class="pill ${roleClass(d.role)}">${esc(d.role)}</span><span class="pill">${esc(d.ndiGroup)}</span>${multicastMeta(d)}${d.firmwareVersion?`<span class="pill">FW ${esc(d.firmwareVersion)}</span>`:''}${d.hdmiOutputResolution?`<span class="pill">${esc(d.hdmiOutputResolution)}</span>`:''}</div>${d.multicastLastError?`<div class="error-text">Multicast: ${esc(d.multicastLastError)}</div>`:''}${d.lastError?`<div class="error-text">${esc(d.lastError)}</div>`:''}<div class="card-actions"><a href="${esc(deviceUrl(d))}" target="_blank" rel="noreferrer">Device UI ↗</a></div></article>`;
  const teleToolCard=d=>{
    const channel=[d.activeChannelNumber,d.activeChannelName].filter(Boolean).join(' ')||'No active TV channel',startDisabled=d.health!=='Online'||d.streamRunning||!d.teleToolControlReady,stopDisabled=d.health!=='Online'||!d.streamRunning,release=[d.firmwareVersion,d.teleToolReleaseBranch].filter(Boolean).join(' ')||'Unknown',previewError=d.streamRunning&&state.previewWarnings.has(d.id);
    return `<article class="device-card teletool-card${previewError?' preview-error':''}"><header><span class="model">TELETOOL · ENCODER</span><span class="card-indicators">${multicastIcon(d)}<i class="health ${roleClass(d.health)}" title="${esc(d.health)}"></i></span></header>${encoderPreview(d)}<div class="teletool-heading"><h3>${esc(d.hostname)}</h3><div class="ip">${esc(d.ipAddress)}:${d.webPort||8000}</div></div><div class="teletool-status-row">${danteAudioBadge(d)}<span class="pill rf-${esc(d.rfSignalKind||'bad')}">RF ${esc(d.rfSignal||'N/A')}</span></div><dl class="teletool-details"><div><dt>TV CHANNEL</dt><dd>${esc(channel)}</dd></div><div><dt>NDI SOURCE</dt><dd>${esc(d.ndiChannelName)}</dd></div><div><dt>NDI GROUP</dt><dd>${esc(d.ndiGroup)}</dd></div><div><dt>PIPELINE</dt><dd>${esc(d.pipelineStatus||'Unknown')}</dd></div><div class="teletool-release"><dt>RELEASE</dt><dd>${esc(release)}</dd></div></dl>${d.multicastLastError?`<div class="error-text">Multicast: ${esc(d.multicastLastError)}</div>`:''}${d.lastError?`<div class="error-text">${esc(d.lastError)}</div>`:''}<div class="card-actions"><a href="${esc(deviceUrl(d))}" target="_blank" rel="noreferrer">TeleTool UI ↗</a><button data-teletool-action="start" data-device-id="${esc(d.id)}" ${startDisabled?'disabled':''}>Start NDI</button><button data-teletool-action="stop" data-device-id="${esc(d.id)}" ${stopDisabled?'disabled':''}>Stop NDI</button><button class="remove-teletool" data-teletool-action="remove" data-device-id="${esc(d.id)}" data-device-name="${esc(d.hostname)}">Remove from job</button></div></article>`;
  };
  const localCard=localPc?`<article class="device-card local-pc-card ${localApplied?'':'configuration-drift'}"><header><span class="model">WINDOWS PC · NDI ACCESS MANAGER</span><span class="card-indicators">${multicastIcon(localPc)}<i class="health ${localApplied?'online':'error'}" title="${localApplied?'Applied':'Configuration changed'}"></i></span></header><h3>${esc(localPc.hostname)}</h3><div class="ip">${esc(localPc.address)}</div><div class="meta"><span class="pill ${localApplied?'encoder':'standalone'}">${localApplied?'APPLIED':'CONFIGURATION CHANGED'}</span><span class="pill">${esc(app.multicast.jobName)}</span><span class="pill ${localApplied?'multicast-pill':'standalone'}">MC ${esc(localPc.netPrefix)}/28</span><span class="pill">TTL ${esc(localPc.ttl)}</span></div>${localPc.error?`<div class="local-pc-drift"><strong>Access Manager no longer matches this job</strong><span>${esc(localPc.error)}</span></div>`:`<div class="local-pc-note">Live settings match NDI Access Manager and are checked every 15 seconds.</div>`}</article>`:'';
  const monitorGrid=$('#monitorGrid');releasePreviewObjectUrls(monitorGrid);monitorGrid.innerHTML=onboarded?deviceGroup('Kiloview encoders',encoderDevices,encoderDevices.map(kiloviewCard).join(''))+deviceGroup('TeleTool Encoders',teletools,teletools.map(teleToolCard).join(''))+deviceGroup('Kiloview decoders',decoderDevices,decoderDevices.map(kiloviewCard).join(''))+deviceGroup('Local NDI endpoint',localPc?[localPc]:[],localCard):'<div class="empty">No onboarded devices yet.</div>';
  setTimeout(refreshEncoderPreviews,0);
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
    toast(result.failed?`Multicast applied to ${result.applied}; ${result.failed} endpoint${result.failed===1?'':'s'} need attention`:`Multicast configured on all ${result.applied} endpoints`,result.failed>0);
  }catch(err){toast(err.message,true);button.disabled=false;button.querySelector('span').textContent='Retry multicast setup'}
}
$('#regenerateMulticast').onclick=()=>loadMulticastSetup(true);
$('#multicastTtl').onchange=()=>loadMulticastSetup(false);
$('#includeLocalPc').onchange=()=>loadMulticastSetup(false);
$('#applyMulticast').onclick=applyMulticastSetup;
async function refreshMonitor(){if($('#monitorView').classList.contains('hidden'))return;try{renderMonitor(await api('/api/state'))}catch{}}
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
async function loadSystemSettings(){
  try{state.systemInfo=await api('/api/system/info');$('#settingsCurrentVersion').textContent=`Version ${state.systemInfo.currentVersion}`;$('#installedChannel').textContent=state.systemInfo.currentChannel;$('#updateChannel').value=state.systemInfo.selectedChannel;renderChannelHelp();const admin=$('#administratorStatus');admin.textContent=state.systemInfo.isAdministrator?'Administrator':'Not elevated';admin.className=state.systemInfo.isAdministrator?'ok':'error-text'}catch(err){$('#administratorStatus').textContent='Unavailable';$('#installedChannel').textContent='Unavailable';toast(err.message,true)}
  await checkForSoftwareUpdate()
}
function renderChannelHelp(){const development=$('#updateChannel').value==='Development';$('#updateChannelHelp').textContent=development?'Development receives prerelease builds for testing and may be less stable.':'Main receives stable production releases.'}
async function checkForSoftwareUpdate(){const button=$('#checkForUpdate'),install=$('#installUpdate'),status=$('#updateStatus'),latest=$('#latestVersion'),link=$('#releaseLink');try{button.disabled=true;button.textContent='Checking…';install.classList.add('hidden');latest.textContent='Checking GitHub…';status.textContent='Contacting the selected release feed.';state.updateInfo=await api('/api/system/update');latest.textContent=`Latest ${state.updateInfo.channel} version ${state.updateInfo.latestVersion}`;link.href=state.updateInfo.releaseUrl;link.classList.remove('hidden');install.querySelector('span').textContent=state.updateInfo.channelSwitch?'Switch channel':'Download & install';if(state.updateInfo.updateAvailable){status.textContent=state.updateInfo.channelSwitch?`Switch from ${state.updateInfo.currentChannel} ${state.updateInfo.currentVersion} to ${state.updateInfo.channel} ${state.updateInfo.latestVersion} (${(state.updateInfo.downloadSizeBytes/1048576).toFixed(1)} MB).`:`Version ${state.updateInfo.latestVersion} is available (${(state.updateInfo.downloadSizeBytes/1048576).toFixed(1)} MB).`;if(state.systemInfo?.isAdministrator)install.classList.remove('hidden');else status.textContent+=' Restart the application as administrator to install it.'}else status.textContent=`${state.updateInfo.channel} ${state.updateInfo.currentVersion} is up to date.`}catch(err){state.updateInfo=null;latest.textContent='Update check unavailable';status.textContent=err.message;link.classList.add('hidden')}finally{button.disabled=false;button.textContent='Check again'}}
$('#updateChannel').onchange=async e=>{const select=e.currentTarget;try{select.disabled=true;const result=await api('/api/system/update/channel',{method:'PUT',body:JSON.stringify({channel:select.value})});state.systemInfo.selectedChannel=result.channel;renderChannelHelp();await checkForSoftwareUpdate();toast(`${result.channel} release channel selected`)}catch(err){select.value=state.systemInfo?.selectedChannel||'Main';renderChannelHelp();toast(err.message,true)}finally{select.disabled=false}};
$('#checkForUpdate').onclick=checkForSoftwareUpdate;
$('#installUpdate').onclick=async()=>{if(!state.updateInfo?.updateAvailable)return;const info=state.updateInfo,development=info.channel==='Development',switchWarning=info.channelSwitch?'\n\nThis switches release channels and may install a lower version.':'';if(!confirm(`${info.channelSwitch?'Switch to':'Download and install'} ${info.channel} ${info.latestVersion}?${switchWarning}${development?'\n\nDevelopment releases are prerelease builds and are not intended for production.':''}\n\nThe verified installer will open and require EULA acceptance.`))return;const button=$('#installUpdate'),status=$('#updateStatus');try{button.disabled=true;button.querySelector('span').textContent='Downloading…';status.textContent='Downloading and verifying the selected GitHub release. Keep this page open.';const result=await api('/api/system/update/install',{method:'POST',body:'{}'});status.textContent=`${result.channel} installer ${result.version} opened. Accept its EULA to complete the update; this page will briefly disconnect while the service restarts.`;toast(`Verified ${result.channel} release ${result.version} is ready to install`)}catch(err){status.textContent=err.message;toast(err.message,true)}finally{button.disabled=false;button.querySelector('span').textContent=info.channelSwitch?'Switch channel':'Download & install'}};
setInterval(refreshMonitor,15000);
setInterval(refreshEncoderPreviews,5000);
document.addEventListener('click',e=>{const button=e.target.closest('[data-teletool-action]');if(button){e.preventDefault();controlTeleTool(button)}});
document.addEventListener('click',e=>{const a=e.target.closest('[data-action]');if(!a)return;e.preventDefault();if(a.dataset.action==='back-setup'||a.dataset.action==='new-job')show('setup');if(a.dataset.action==='back-devices')show('discover');if(a.dataset.action==='monitor'||a.dataset.action==='back-multicast')api('/api/state').then(x=>{renderMonitor(x);show('monitor')});if(a.dataset.action==='multicast'){show('multicast');loadMulticastSetup(false)}if(a.dataset.action==='settings'){state.returnView=views.find(v=>!$(`#${v}View`).classList.contains('hidden'))||'setup';show('settings');loadSystemSettings()}if(a.dataset.action==='back-settings')show(state.returnView||'setup')});
boot();
