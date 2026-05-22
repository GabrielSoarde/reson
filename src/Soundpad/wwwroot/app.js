(() => {
  const params = new URLSearchParams(location.search);
  const token = params.get('t');
  if (token) localStorage.setItem('soundpad.token', token);
  const tok = localStorage.getItem('soundpad.token') || '';
  const originId = crypto.randomUUID();

  const headers = () => ({ 'X-Auth-Token': tok, 'X-Origin-Id': originId, 'Content-Type': 'application/json' });

  async function api(path, opts = {}) {
    const r = await fetch(path, { ...opts, headers: { ...headers(), ...(opts.headers || {}) } });
    if (r.status === 401) { alert('Sessão expirada. Escaneie o QR code de novo.'); throw new Error('401'); }
    return r;
  }

  let state = null;
  let nowPlaying = null;

  function render() {
    if (!state) return;
    const grid = document.getElementById('grid');
    grid.style.setProperty('--cols', state.grid.cols);
    grid.innerHTML = '';
    const positioned = new Map();
    state.sounds.forEach(s => { if (s.position) positioned.set(`${s.position.col},${s.position.row}`, s); });
    for (let r = 0; r < state.grid.rows; r++) {
      for (let c = 0; c < state.grid.cols; c++) {
        const s = positioned.get(`${c},${r}`);
        const cell = document.createElement('div');
        cell.className = 'cell' + (s ? '' : ' empty') + (s && s.id === nowPlaying ? ' playing' : '') + (s && s.missing ? ' missing' : '');
        cell.textContent = s ? s.label : '+';
        if (s) cell.addEventListener('click', () => api(`/api/play/${s.id}`, { method: 'POST' }));
        grid.appendChild(cell);
      }
    }
    document.getElementById('monitor').checked = state.monitorEnabled;
    document.getElementById('volume').value = state.volume;
    document.getElementById('vol-value').textContent = state.volume + '%';
  }

  async function loadState() {
    const r = await api('/api/state');
    state = await r.json();
    nowPlaying = state.nowPlaying;
    render();
  }

  function connectWs() {
    const proto = location.protocol === 'https:' ? 'wss' : 'ws';
    const ws = new WebSocket(`${proto}://${location.host}/ws?t=${tok}`);
    ws.onmessage = e => {
      const { type, originId: oid, payload } = JSON.parse(e.data);
      if (oid && oid === originId && (type === 'volumeChanged' || type === 'monitorChanged')) return; // echo filter
      switch (type) {
        case 'playing': nowPlaying = payload.soundId; render(); break;
        case 'stopped': nowPlaying = null; render(); break;
        case 'monitorChanged': state.monitorEnabled = payload.enabled; render(); break;
        case 'volumeChanged': state.volume = payload.value; render(); break;
        case 'libraryChanged': loadState(); break;
      }
    };
    let backoff = 1000;
    ws.onclose = () => setTimeout(() => { backoff = Math.min(backoff * 2, 30000); connectWs(); }, backoff);
  }

  document.getElementById('stop').addEventListener('click', () => api('/api/stop', { method: 'POST' }));

  document.getElementById('monitor').addEventListener('change', e => {
    api('/api/monitor', { method: 'POST', body: JSON.stringify({ enabled: e.target.checked }) });
  });

  let volTimer;
  document.getElementById('volume').addEventListener('input', e => {
    clearTimeout(volTimer);
    document.getElementById('vol-value').textContent = e.target.value + '%';
    volTimer = setTimeout(() => api('/api/volume', { method: 'POST', body: JSON.stringify({ value: parseInt(e.target.value, 10) }) }), 100);
  });

  loadState().then(connectWs);
})();
