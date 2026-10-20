const $ = id => document.getElementById(id);
const english = Object.fromEntries([...document.querySelectorAll('[data-i18n]')].map(e => [e.dataset.i18n, e.innerHTML]));
const indonesian = {
  workbench:'WORKBENCH SPEKTRUM',local:'RUANG KERJA LOKAL',lab:'AKUISISI / ANALISIS',title:'Kenali spektrum Anda lebih dekat.',subtitle:'Hubungkan instrumen. Rekam sinyal. Telusuri perubahannya.',notConnected:'BELUM TERHUBUNG',instrument:'Instrumen',noDevice:'Belum ada instrumen',connectHint:'Hubungkan untuk memulai sesi.',connect:'Hubungkan instrumen',acquisition:'Akuisisi',integration:'Waktu integrasi',points:'Titik',averages:'Rata-rata',capture:'Rekam sampel',cancel:'Batalkan akuisisi',readyHint:'Hubungkan instrumen untuk merekam.',calibration:'Kalibrasi',calibrationHint:'Rekam sinyal dark dan referensi sebelum menghitung absorbansi.',dark:'Sinyal dark',reference:'Referensi',captureShort:'Rekam',signalView:'TAMPILAN SINYAL',spectrum:'Spektrum',noTrace:'Belum ada kurva',intensity:'INTENSITAS · A.U.',emptyTitle:'Penemuan Anda dimulai di sini.',emptyHint:'Hubungkan instrumen, lalu rekam sampel<br>untuk melihat sidik jari spektralnya.',wavelength:'PANJANG GELOMBANG · NM',cursor:'KURSOR',chartHint:'Arahkan kursor atau tekan ← → untuk melihat nilai',processing:'Pemrosesan sinyal',absorbance:'Absorbansi',smooth:'Perhalus',window:'Jendela penghalusan',session:'SESI INI',history:'Riwayat rekaman',measurement:'Pengukuran',time:'Waktu',source:'Sumber',emptyHistory:'Spektrum yang direkam muncul di sini. Pilih untuk melihat atau mengekspor.',offline:'Instrumen tidak terhubung',verification:'Transport perangkat keras memerlukan validasi perangkat.',footer:'Eksplorasi spektral yang dirancang dengan cermat.'
};
let language = 'en';
try { language = localStorage.getItem('siware-language') === 'id' ? 'id' : 'en'; } catch { /* Storage can be disabled. */ }
let status = {connected:false,busy:false,calibration:{dark:false,reference:false}}, selected = null, history = [], pending = false, cursorIndex = 0;
let historySignature = '';
const tr = (en,id) => language === 'id' ? id : en;
const kindName = kind => language === 'id' ? ({Sample:'Sampel',Dark:'Dark',Reference:'Referensi',Absorbance:'Absorbansi',Normalized:'Ternormalisasi'}[kind] || kind) : kind;
function notify(message, error = false) { $('notice').hidden = !message; $('notice').textContent = message; $('notice').classList.toggle('error',error); }
async function api(path, data, method) {
  const response = await fetch(path,{method:method || (data === undefined ? 'GET':'POST'),headers:data === undefined ? {} : {'Content-Type':'application/json'},body:data === undefined ? undefined:JSON.stringify(data)});
  const body = response.status === 204 ? null : await response.json().catch(() => null);
  if (!response.ok) throw new Error(body?.error || `${tr('Request failed','Permintaan gagal')} (${response.status})`);
  return body;
}
function setLanguage() {
  document.documentElement.lang = language;
  for (const element of document.querySelectorAll('[data-i18n]')) element.innerHTML = language === 'id' ? indonesian[element.dataset.i18n] || english[element.dataset.i18n] : english[element.dataset.i18n];
  $('language').innerHTML = language === 'id' ? 'ID <span>/ EN</span>' : 'EN <span>/ ID</span>';
  $('language').setAttribute('aria-label',language === 'id' ? 'Switch to English' : 'Ganti ke Bahasa Indonesia');
  $('chart').setAttribute('aria-label',tr('Spectrum chart. Use left and right arrow keys to inspect values.','Grafik spektrum. Gunakan tombol panah kiri dan kanan untuk memeriksa nilai.'));
  renderStatus(); renderHistory(); drawChart();
}
function renderStatus() {
  const locked = pending || status.busy;
  $('connect').textContent = status.connected ? tr('Disconnect instrument','Putuskan instrumen') : tr('Connect instrument','Hubungkan instrumen');
  $('connect').disabled = locked;
  $('deviceName').textContent = status.connected ? status.device?.name || tr('Connected instrument','Instrumen terhubung') : tr('No instrument connected','Belum ada instrumen');
  $('deviceDetail').textContent = status.connected ? `${status.device?.transport || '—'} · ${status.device?.isSimulated ? tr('Simulator session','Sesi simulator') : tr('Hardware unverified','Perangkat keras belum diverifikasi')}` : tr('Connect to start a session.','Hubungkan untuk memulai sesi.');
  $('modeBadge').textContent = selected?.isSimulated || (status.connected && status.device?.isSimulated) ? tr('SIMULATED DATA','DATA SIMULASI') : status.connected ? tr('HARDWARE · UNVERIFIED','PERANGKAT · BELUM DIVERIFIKASI') : tr('NOT CONNECTED','BELUM TERHUBUNG');
  $('connectionDot').classList.toggle('online',status.connected); $('sessionDot').classList.toggle('online',status.connected);
  $('sessionState').textContent = locked ? tr('Working…','Memproses…') : status.connected ? tr('Instrument connected · ready','Instrumen terhubung · siap') : tr('Instrument offline','Instrumen tidak terhubung');
  $('acquisitionState').textContent = locked ? tr('Acquiring or processing a spectrum…','Merekam atau memproses spektrum…') : status.connected ? tr('Ready to capture a spectrum.','Siap merekam spektrum.') : tr('Connect an instrument to capture.','Hubungkan instrumen untuk merekam.');
  for (const id of ['acquire','captureDark','captureReference']) $(id).disabled = !status.connected || locked;
  const darkSupported = status.supportsDark !== false;
  const absorbanceSupported = status.supportsAbsorbance !== false && darkSupported;
  $('captureDark').disabled ||= !darkSupported;
  const unsupported = tr('Dark acquisition is not verified for the vendor API. Absorbance requires a verified dark signal.','Akuisisi dark belum diverifikasi pada API vendor. Absorbansi memerlukan sinyal dark terverifikasi.');
  $('captureDark').title = darkSupported ? '' : unsupported;
  for (const id of ['integration','points','averages']) $(id).disabled = locked;
  $('cancel').hidden = !status.busy;
  $('darkDot').classList.toggle('done',!!status.calibration?.dark); $('referenceDot').classList.toggle('done',!!status.calibration?.reference);
  $('darkDot').title = status.calibration?.dark ? tr('Captured','Direkam') : tr('Not captured','Belum direkam');
  $('referenceDot').title = status.calibration?.reference ? tr('Captured','Direkam') : tr('Not captured','Belum direkam');
  $('absorbance').disabled = !selected || selected.kind !== 'Sample' || !status.calibration?.dark || !status.calibration?.reference || locked || !absorbanceSupported;
  $('absorbance').title = absorbanceSupported ? '' : unsupported;
  $('snv').disabled = $('smooth').disabled = !selected || locked;
  $('window').disabled = !selected || locked;
  $('engine').textContent = status.nativeAvailable ? tr('Rust native engine available','Mesin native Rust tersedia') : tr('Managed processing engine','Mesin pemrosesan managed');
  $('traceName').textContent = selected ? kindName(selected.kind) : tr('No trace','Belum ada kurva');
  for (const format of ['Csv','Json']) { const link = $(`export${format}`); link.classList.toggle('disabled',!selected); link.setAttribute('aria-disabled',String(!selected)); if(selected) link.href = `/api/export/${encodeURIComponent(selected.id)}?format=${format.toLowerCase()}`; else link.removeAttribute('href'); }
}
async function refresh() { const [nextStatus,nextHistory] = await Promise.all([api('/api/status'),api('/api/history')]); status = nextStatus; history = nextHistory; renderStatus(); renderHistory(); }
async function action(task) { if(pending) return; pending = true; notify(''); renderStatus(); try { await task(); await refresh(); } catch(error) { notify(error.message,true); try { await refresh(); } catch { /* Original failure stays visible. */ } } finally { pending = false; renderStatus(); } }
function selectSpectrum(spectrum) { selected = spectrum; cursorIndex = 0; renderStatus(); renderHistory(); drawChart(); }
function renderHistory() {
  const signature = `${language}|${selected?.id || ''}|${JSON.stringify(history)}`;
  if (signature === historySignature) return;
  historySignature = signature;
  $('historyCount').textContent = history.length;
  $('history').replaceChildren();
  if(!history.length) { const empty = document.createElement('p'); empty.className = 'empty-history'; empty.textContent = tr('Captured spectra will appear here. Select one to inspect or export.','Spektrum yang direkam muncul di sini. Pilih untuk melihat atau mengekspor.'); $('history').append(empty); return; }
  for(const item of history) {
    const row = document.createElement('button'); row.className = `history-row${selected?.id === item.id ? ' selected':''}`; row.setAttribute('aria-pressed',String(selected?.id === item.id));
    const time = new Date(item.capturedAt); const cells = [kindName(item.kind),time.toLocaleTimeString(language === 'id'?'id-ID':'en-GB',{hour:'2-digit',minute:'2-digit',second:'2-digit'}),String(item.points),item.isSimulated ? tr('SIMULATED','SIMULASI') : tr('HARDWARE','PERANGKAT')];
    for(const text of cells) { const cell = document.createElement('span'); cell.textContent = text; row.append(cell); }
    row.title = `${item.id} · ${time.toLocaleString()}`; row.addEventListener('click',() => action(async() => selectSpectrum(await api(`/api/spectra/${encodeURIComponent(item.id)}`)))); $('history').append(row);
  }
}
const bounds = {left:60,right:875,top:25,bottom:340};
let chartScale = null;
const svgElement = (name,attributes,text) => { const el=document.createElementNS('http://www.w3.org/2000/svg',name); for(const [key,value] of Object.entries(attributes)) el.setAttribute(key,value); if(text !== undefined) el.textContent=text; return el; };
function drawChart() {
  const xs = selected?.wavelengths || [], ys = selected?.values || [];
  const valid = xs.length > 1 && xs.length === ys.length && xs.every(Number.isFinite) && ys.every(Number.isFinite);
  $('emptyChart').hidden = valid; $('trace').setAttribute('d',''); $('area').setAttribute('d',''); $('cursor').setAttribute('visibility','hidden'); $('grid').replaceChildren(); chartScale = null;
  let minX=0,maxX=1,minY=0,maxY=1;
  if(valid) { minX=Math.min(...xs); maxX=Math.max(...xs); minY=Math.min(...ys); maxY=Math.max(...ys); const pad=(maxY-minY || Math.abs(maxY) || 1)*.1; minY-=pad; maxY+=pad; if(maxX===minX) maxX=minX+1; }
  const px=x=>bounds.left+(x-minX)/(maxX-minX)*(bounds.right-bounds.left), py=y=>bounds.bottom-(y-minY)/(maxY-minY)*(bounds.bottom-bounds.top);
  for(let i=0;i<=5;i++) { const y=bounds.top+i*(bounds.bottom-bounds.top)/5; $('grid').append(svgElement('line',{x1:bounds.left,y1:y,x2:bounds.right,y2:y})); if(valid) $('grid').append(svgElement('text',{x:bounds.left-10,y:y+4,'text-anchor':'end'},formatNumber(maxY-i*(maxY-minY)/5))); }
  for(let i=0;i<=6;i++) { const x=bounds.left+i*(bounds.right-bounds.left)/6; $('grid').append(svgElement('line',{x1:x,y1:bounds.top,x2:x,y2:bounds.bottom})); if(valid) $('grid').append(svgElement('text',{x,y:bounds.bottom+24,'text-anchor':'middle'},Math.round(minX+i*(maxX-minX)/6))); }
  $('cursorX').textContent='— nm'; $('cursorY').textContent='—';
  const kind = String(selected?.kind || '').toLowerCase();
  $('yLabel').textContent = kind.includes('absorbance') ? tr('ABSORBANCE','ABSORBANSI') : kind === 'normalized' ? 'SNV · σ' : tr('INTENSITY · A.U.','INTENSITAS · A.U.');
  if(!valid) return;
  const path = xs.map((x,i)=>`${i?'L':'M'}${px(x).toFixed(2)},${py(ys[i]).toFixed(2)}`).join(' ');
  $('trace').setAttribute('d',path); $('area').setAttribute('d',`${path} L${px(xs.at(-1))},${bounds.bottom} L${px(xs[0])},${bounds.bottom} Z`);
  chartScale={px,py,xs,ys};
  $('chart').setAttribute('aria-label',`${kindName(selected.kind)}: ${xs.length} ${tr('points. Use arrow keys to inspect.','titik. Gunakan tombol panah untuk memeriksa.')}`);
}
function formatNumber(value) { return Math.abs(value)>=10000 || (Math.abs(value)>0 && Math.abs(value)<.001) ? value.toExponential(2) : Number(value.toPrecision(5)).toString(); }
function inspect(index) { if(!chartScale) return; cursorIndex=Math.max(0,Math.min(chartScale.xs.length-1,index)); const {px,py,xs,ys}=chartScale; const x=px(xs[cursorIndex]),y=py(ys[cursorIndex]); const line=$('cursor').querySelector('line'),circle=$('cursor').querySelector('circle'); line.setAttribute('x1',x); line.setAttribute('x2',x); circle.setAttribute('cx',x); circle.setAttribute('cy',y); $('cursor').setAttribute('visibility','visible'); $('cursorX').textContent=`${xs[cursorIndex].toFixed(1)} nm`; $('cursorY').textContent=formatNumber(ys[cursorIndex]); }
$('chart').addEventListener('pointermove',event=>{if(!chartScale)return;const point=$('chart').createSVGPoint();point.x=event.clientX;point.y=event.clientY;const position=point.matrixTransform($('chart').getScreenCTM().inverse());let closest=0,distance=Infinity;chartScale.xs.forEach((x,i)=>{const delta=Math.abs(chartScale.px(x)-position.x);if(delta<distance){distance=delta;closest=i;}});inspect(closest);});
$('chart').addEventListener('keydown',event=>{if(['ArrowLeft','ArrowRight','Home','End'].includes(event.key)&&chartScale){event.preventDefault();inspect(event.key==='Home'?0:event.key==='End'?chartScale.xs.length-1:cursorIndex+(event.key==='ArrowRight'?1:-1));}});
$('chart').addEventListener('focus',()=>inspect(cursorIndex));
$('language').addEventListener('click',()=>{language=language==='en'?'id':'en';try{localStorage.setItem('siware-language',language);}catch{}setLanguage();});
$('connect').addEventListener('click',()=>action(async()=>{status=await api(`/api/${status.connected?'disconnect':'connect'}`,undefined,'POST');}));
async function acquire(kind) { if(!$('acquireForm').reportValidity())return; await action(async()=>{status.busy=true;renderStatus();try{selectSpectrum(await api('/api/acquire',{kind,points:Number($('points').value),integrationMilliseconds:Number($('integration').value),averages:Number($('averages').value)}));}finally{status.busy=false;}}); }
$('acquireForm').addEventListener('submit',event=>{event.preventDefault();acquire('Sample');});
$('captureDark').addEventListener('click',()=>acquire('Dark')); $('captureReference').addEventListener('click',()=>acquire('Reference'));
$('cancel').addEventListener('click',async()=>{try{await api('/api/cancel',undefined,'POST');notify(tr('Cancellation requested.','Pembatalan diminta.'));}catch(error){notify(error.message,true);}});
for(const operation of ['absorbance','snv','smooth']) $(operation).addEventListener('click',()=>action(async()=>{if(!selected)return;selectSpectrum(await api(`/api/process/${encodeURIComponent(selected.id)}`,{operation,window:Number($('window').value)}));}));
setLanguage(); refresh().catch(error=>notify(error.message,true));
setInterval(()=>{if(!pending) refresh().catch(()=>{});},5000);
