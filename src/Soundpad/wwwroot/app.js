(() => {
  const params = new URLSearchParams(location.search);
  const token = params.get('t');
  if (token) localStorage.setItem('soundpad.token', token);
  const tok = localStorage.getItem('soundpad.token') || '';
  // crypto.randomUUID() is only available in secure contexts (https/localhost).
  // Over plain http on a LAN IP we fall back to crypto.getRandomValues (always available).
  const originId = (crypto.randomUUID
    ? crypto.randomUUID()
    : (() => {
        const b = new Uint8Array(16);
        crypto.getRandomValues(b);
        b[6] = (b[6] & 0x0f) | 0x40; b[8] = (b[8] & 0x3f) | 0x80;
        const h = Array.from(b, x => x.toString(16).padStart(2, '0')).join('');
        return `${h.slice(0,8)}-${h.slice(8,12)}-${h.slice(12,16)}-${h.slice(16,20)}-${h.slice(20)}`;
      })());

  const headers = () => ({ 'X-Auth-Token': tok, 'X-Origin-Id': originId, 'Content-Type': 'application/json' });

  async function api(path, opts = {}) {
    const r = await fetch(path, { ...opts, headers: { ...headers(), ...(opts.headers || {}) } });
    if (r.status === 401) { alert('Sessão expirada. Escaneie o QR code de novo.'); throw new Error('401'); }
    return r;
  }

  let state = null;
  let nowPlaying = null;
  const recentTaps = new Set();
  const vibrate = pattern => { if (navigator.vibrate) { try { navigator.vibrate(pattern); } catch {} } };

  // ---------- Toast ----------
  let toastTimer = null;
  function toast(msg) {
    let el = document.getElementById('toast');
    if (!el) {
      el = document.createElement('div');
      el.id = 'toast';
      el.className = 'toast';
      document.body.appendChild(el);
    }
    el.textContent = msg;
    el.classList.add('show');
    clearTimeout(toastTimer);
    toastTimer = setTimeout(() => el.classList.remove('show'), 2000);
  }

  // ---------- Drag state machine ----------
  // States: idle -> pressing (long-press timer running) -> dragging -> idle
  const LONG_PRESS_MS = 350;
  const MOVE_CANCEL_PX = 10;
  const drag = {
    state: 'idle',          // idle | pressing | dragging
    cell: null,             // the source cell element
    sound: null,            // the source sound object
    pointerId: null,
    startX: 0, startY: 0,   // pointerdown coords
    curX: 0, curY: 0,       // current pointer coords
    timer: null,            // long-press timer id
    dropTarget: null,       // currently highlighted target cell
    deferredRender: false,  // libraryChanged arrived mid-drag
  };

  function resetDragSourceVisuals() {
    if (drag.cell) {
      drag.cell.classList.remove('dragging');
      drag.cell.style.transform = '';
      drag.cell.style.zIndex = '';
      drag.cell.style.pointerEvents = '';
    }
  }
  function clearDropTarget() {
    if (drag.dropTarget) {
      drag.dropTarget.classList.remove('drop-target');
      drag.dropTarget = null;
    }
  }
  function clearDropHints() {
    document.querySelectorAll('.cell.drop-hint').forEach(c => c.classList.remove('drop-hint'));
  }
  function endDrag() {
    if (drag.timer) { clearTimeout(drag.timer); drag.timer = null; }
    resetDragSourceVisuals();
    clearDropTarget();
    clearDropHints();
    const cell = drag.cell;
    if (cell && drag.pointerId != null) {
      try { cell.releasePointerCapture(drag.pointerId); } catch {}
    }
    const wasDeferred = drag.deferredRender;
    drag.state = 'idle';
    drag.cell = null;
    drag.sound = null;
    drag.pointerId = null;
    drag.deferredRender = false;
    if (wasDeferred) loadState();
  }

  function startDragMode() {
    if (drag.state !== 'pressing' || !drag.cell) return;
    drag.state = 'dragging';
    vibrate(40);
    drag.cell.classList.add('dragging');
    drag.cell.style.zIndex = '100';
    // Make the source cell transparent to pointer-hit-testing so elementFromPoint
    // returns the cell underneath the finger.
    drag.cell.style.pointerEvents = 'none';
    // Hint other cells as valid drop targets
    document.querySelectorAll('#grid .cell').forEach(c => {
      if (c !== drag.cell) c.classList.add('drop-hint');
    });
    updateDragVisual();
  }

  function updateDragVisual() {
    if (drag.state !== 'dragging' || !drag.cell) return;
    const dx = drag.curX - drag.startX;
    const dy = drag.curY - drag.startY;
    drag.cell.style.transform = `translate(${dx}px, ${dy}px) scale(1.05) rotate(1deg)`;
  }

  function cellAtPoint(x, y) {
    const el = document.elementFromPoint(x, y);
    if (!el) return null;
    const cell = el.closest('.cell');
    if (!cell) return null;
    if (!cell.parentElement || cell.parentElement.id !== 'grid') return null;
    if (cell === drag.cell) return null;
    return cell;
  }

  function updateDropTarget() {
    if (drag.state !== 'dragging') return;
    const target = cellAtPoint(drag.curX, drag.curY);
    if (target === drag.dropTarget) return;
    if (drag.dropTarget) drag.dropTarget.classList.remove('drop-target');
    drag.dropTarget = target;
    if (target) target.classList.add('drop-target');
  }

  function parsePos(cell) {
    if (!cell || !cell.dataset.pos) return null;
    const [c, r] = cell.dataset.pos.split(',').map(Number);
    return { col: c, row: r };
  }

  async function commitDrop(target) {
    const srcPos = drag.sound.position;
    const dstPos = parsePos(target);
    if (!srcPos || !dstPos) return;
    if (srcPos.col === dstPos.col && srcPos.row === dstPos.row) return; // no-op

    // Build placements: start from current positioned sounds, apply swap/move.
    const placements = state.sounds
      .filter(s => s.position)
      .map(s => ({ id: s.id, position: { col: s.position.col, row: s.position.row } }));
    const srcEntry = placements.find(p => p.id === drag.sound.id);
    const dstEntry = placements.find(p => p.position.col === dstPos.col && p.position.row === dstPos.row);
    if (!srcEntry) return;
    if (dstEntry) {
      // swap
      dstEntry.position = { col: srcPos.col, row: srcPos.row };
    }
    srcEntry.position = { col: dstPos.col, row: dstPos.row };

    // Optimistic local update so UI doesn't snap-back-flicker before WS broadcast.
    const srcSound = state.sounds.find(s => s.id === drag.sound.id);
    const dstSound = dstEntry ? state.sounds.find(s => s.id === dstEntry.id) : null;
    const snapshot = state.sounds.map(s => ({ id: s.id, position: s.position ? { ...s.position } : null }));
    if (srcSound) srcSound.position = { col: dstPos.col, row: dstPos.row };
    if (dstSound) dstSound.position = { col: srcPos.col, row: srcPos.row };
    render();

    try {
      const r = await api('/api/grid/layout', {
        method: 'POST',
        body: JSON.stringify({ placements }),
      });
      if (!r.ok) {
        // Revert
        snapshot.forEach(({ id, position }) => {
          const s = state.sounds.find(x => x.id === id);
          if (s) s.position = position;
        });
        render();
        let msg = 'Falha ao reordenar';
        try { const j = await r.json(); if (j && j.error) msg = `Falha: ${j.error}`; } catch {}
        toast(msg);
      }
    } catch (e) {
      // Network / 401 already handled — revert
      snapshot.forEach(({ id, position }) => {
        const s = state.sounds.find(x => x.id === id);
        if (s) s.position = position;
      });
      render();
      toast('Erro de rede ao reordenar');
    }
  }

  // ---------- Pointer handlers (attached per-cell in render) ----------
  function onCellPointerDown(ev, cell, s) {
    if (ev.button != null && ev.button !== 0) return; // ignore non-primary
    if (drag.state !== 'idle') return;                // ignore multi-touch
    cell.dataset.didDrag = '';
    drag.state = 'pressing';
    drag.cell = cell;
    drag.sound = s;
    drag.pointerId = ev.pointerId;
    drag.startX = drag.curX = ev.clientX;
    drag.startY = drag.curY = ev.clientY;
    try { cell.setPointerCapture(ev.pointerId); } catch {}
    // Ripple deferred to confirm it's a tap (not a long-press) — see below: we
    // spawn a ripple on pointerup if drag did not start.
    drag.timer = setTimeout(() => {
      drag.timer = null;
      if (drag.state === 'pressing') {
        cell.dataset.didDrag = '1';
        startDragMode();
      }
    }, LONG_PRESS_MS);
  }

  function onCellPointerMove(ev) {
    if (drag.state === 'idle' || ev.pointerId !== drag.pointerId) return;
    drag.curX = ev.clientX;
    drag.curY = ev.clientY;
    if (drag.state === 'pressing') {
      const dx = drag.curX - drag.startX;
      const dy = drag.curY - drag.startY;
      if (dx * dx + dy * dy > MOVE_CANCEL_PX * MOVE_CANCEL_PX) {
        // user scrolled — abort long-press, let the click happen naturally on release
        if (drag.timer) { clearTimeout(drag.timer); drag.timer = null; }
        if (drag.cell && drag.pointerId != null) {
          try { drag.cell.releasePointerCapture(drag.pointerId); } catch {}
        }
        drag.state = 'idle';
        drag.cell = null;
        drag.sound = null;
        drag.pointerId = null;
      }
    } else if (drag.state === 'dragging') {
      updateDragVisual();
      updateDropTarget();
      ev.preventDefault();
    }
  }

  async function onCellPointerUp(ev) {
    if (drag.state === 'idle' || ev.pointerId !== drag.pointerId) return;
    if (drag.state === 'pressing') {
      // Long-press never fired → treat as a normal tap. The click event will follow
      // and trigger play. Just reset drag state, leave didDrag unset.
      if (drag.timer) { clearTimeout(drag.timer); drag.timer = null; }
      if (drag.cell && drag.pointerId != null) {
        try { drag.cell.releasePointerCapture(drag.pointerId); } catch {}
      }
      // Late ripple so the user sees feedback on tap (not on hold)
      if (drag.cell) spawnRipple(drag.cell, ev);
      drag.state = 'idle';
      drag.cell = null;
      drag.sound = null;
      drag.pointerId = null;
      return;
    }
    // dragging
    const target = cellAtPoint(ev.clientX, ev.clientY);
    if (target) {
      // Snap visually back to a neutral position before commit (no big animation)
      drag.cell.style.transition = 'transform 0.12s';
      drag.cell.style.transform = '';
      setTimeout(() => { if (drag.cell) drag.cell.style.transition = ''; }, 130);
      await commitDrop(target);
    } else {
      // released outside grid → cancel with snap-back
      drag.cell.style.transition = 'transform 0.18s';
      drag.cell.style.transform = '';
      setTimeout(() => { if (drag.cell) drag.cell.style.transition = ''; }, 200);
    }
    endDrag();
  }

  function onCellPointerCancel(ev) {
    if (drag.state === 'idle' || ev.pointerId !== drag.pointerId) return;
    endDrag();
  }

  function spawnRipple(cell, ev) {
    const rect = cell.getBoundingClientRect();
    const x = (ev.clientX != null ? ev.clientX - rect.left : rect.width / 2);
    const y = (ev.clientY != null ? ev.clientY - rect.top : rect.height / 2);
    const size = Math.max(rect.width, rect.height) * 0.6;
    const ripple = document.createElement('span');
    ripple.className = 'ripple';
    ripple.style.width = ripple.style.height = size + 'px';
    ripple.style.left = (x - size / 2) + 'px';
    ripple.style.top = (y - size / 2) + 'px';
    ripple.addEventListener('animationend', () => ripple.remove());
    cell.appendChild(ripple);
  }

  function triggerPlay(s) {
    if (recentTaps.has(s.id)) return;
    recentTaps.add(s.id);
    setTimeout(() => recentTaps.delete(s.id), 120);
    vibrate(15);
    api(`/api/play/${s.id}`, { method: 'POST' });
  }

  function render() {
    if (!state) return;
    // Defer re-render while user is mid-drag — else cells jump under the finger.
    if (drag.state !== 'idle') { drag.deferredRender = true; return; }
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
        cell.dataset.pos = `${c},${r}`;
        if (s) {
          cell.style.backgroundColor = s.color || '#3b82f6';
          cell.dataset.soundId = s.id;
          cell.addEventListener('pointerdown', ev => onCellPointerDown(ev, cell, s));
          cell.addEventListener('pointermove', onCellPointerMove);
          cell.addEventListener('pointerup', onCellPointerUp);
          cell.addEventListener('pointercancel', onCellPointerCancel);
          cell.addEventListener('click', ev => {
            if (cell.dataset.didDrag === '1') {
              // suppress play after drag
              cell.dataset.didDrag = '';
              ev.preventDefault();
              ev.stopPropagation();
              return;
            }
            triggerPlay(s);
          });
        }
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
      if (oid && oid === originId && (type === 'volumeChanged' || type === 'monitorChanged' || type === 'monitorDeviceChanged')) return; // echo filter
      switch (type) {
        case 'playing': nowPlaying = payload.soundId; render(); break;
        case 'stopped': nowPlaying = null; render(); break;
        case 'monitorChanged': state.monitorEnabled = payload.enabled; render(); break;
        case 'volumeChanged': state.volume = payload.value; render(); break;
        case 'libraryChanged':
          if (drag.state !== 'idle') { drag.deferredRender = true; }
          else { loadState(); }
          break;
      }
    };
    let backoff = 1000;
    ws.onclose = () => setTimeout(() => { backoff = Math.min(backoff * 2, 30000); connectWs(); }, backoff);
  }

  document.getElementById('stop').addEventListener('click', () => {
    vibrate([30, 40, 30]);
    api('/api/stop', { method: 'POST' });
  });

  document.getElementById('monitor').addEventListener('change', e => {
    api('/api/monitor', { method: 'POST', body: JSON.stringify({ enabled: e.target.checked }) });
  });

  let volTimer;
  document.getElementById('volume').addEventListener('input', e => {
    clearTimeout(volTimer);
    document.getElementById('vol-value').textContent = e.target.value + '%';
    volTimer = setTimeout(() => api('/api/volume', { method: 'POST', body: JSON.stringify({ value: parseInt(e.target.value, 10) }) }), 100);
  });

  // ---------- Upload FAB ----------
  // The FAB forwards click → hidden <input type="file">. Selected files are
  // uploaded sequentially (not parallel) — keeps memory pressure low on
  // older phones and makes the progress text honest.
  const uploadFab = document.getElementById('upload-fab');
  const uploadInput = document.getElementById('upload-input');
  if (uploadFab && uploadInput) {
    uploadFab.addEventListener('click', () => { if (!uploadFab.disabled) uploadInput.click(); });
    uploadInput.addEventListener('change', async () => {
      const files = Array.from(uploadInput.files || []);
      // Reset the input value so picking the *same* file twice in a row still
      // fires `change` the second time (browsers dedupe identical values).
      uploadInput.value = '';
      if (files.length === 0) return;

      uploadFab.disabled = true;
      uploadFab.classList.add('uploading');
      const restoreFab = () => {
        uploadFab.disabled = false;
        uploadFab.classList.remove('uploading');
        uploadFab.textContent = '+';
      };

      let ok = 0;
      let failed = 0;
      for (let i = 0; i < files.length; i++) {
        const f = files[i];
        uploadFab.textContent = files.length > 1 ? `${i + 1}/${files.length}` : '…';
        try {
          // Multipart upload. Don't set Content-Type ourselves — the browser
          // computes the multipart boundary. We deliberately strip our default
          // 'Content-Type: application/json' from headers() by overriding it
          // with the FormData boundary (which fetch sets when body is FormData
          // *and* no explicit Content-Type is provided).
          const fd = new FormData();
          fd.append('file', f, f.name);
          const r = await fetch('/api/sounds/upload', {
            method: 'POST',
            headers: { 'X-Auth-Token': tok, 'X-Origin-Id': originId },
            body: fd,
          });
          if (r.ok) {
            ok++;
          } else {
            failed++;
            let msg = `Falha ao enviar ${f.name}`;
            try {
              const j = await r.json();
              if (j && j.error) msg = `${f.name}: ${j.error}`;
            } catch {}
            toast(msg);
          }
        } catch (e) {
          failed++;
          toast(`Erro de rede ao enviar ${f.name}`);
        }
      }
      restoreFab();
      if (ok > 0 && failed === 0) {
        toast(ok === 1 ? 'Som adicionado' : `${ok} sons adicionados`);
      } else if (ok > 0 && failed > 0) {
        toast(`${ok} ok, ${failed} falharam`);
      }
      // The grid refresh happens automatically via the libraryChanged WS
      // broadcast that /api/sounds/upload emits on success.
    });
  }

  // Cancel drag on orientation change — layout reflows and absolute pointer coords
  // no longer map to the source cell.
  window.addEventListener('orientationchange', () => { if (drag.state !== 'idle') endDrag(); });

  loadState().then(connectWs);
})();
