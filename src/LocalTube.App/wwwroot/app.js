const state = {
  media: [],
  playlists: [],
  settings: null,
  view: 'home',
  search: '',
  currentMedia: null,
  downloadAnalysis: null,
  downloadPoll: null,
  downloadCompletionSignature: '',
  downloadCookieBrowser: localStorage.getItem('youtubeOfflineCookieBrowser') || 'auto',
};

const $ = (q, root = document) => root.querySelector(q);
const $$ = (q, root = document) => [...root.querySelectorAll(q)];
const content = $('#content');

async function api(url, options = {}) {
  const init = { ...options };
  if (init.body && typeof init.body !== 'string') {
    init.headers = { 'Content-Type': 'application/json', ...(init.headers || {}) };
    init.body = JSON.stringify(init.body);
  }
  const res = await fetch(url, init);
  if (!res.ok) {
    let msg = `Erro ${res.status}`;
    try { const data = await res.json(); msg = data.error || msg; } catch { }
    throw new Error(msg);
  }
  const text = await res.text();
  return text ? JSON.parse(text) : null;
}

function escapeHtml(value = '') {
  return String(value).replace(/[&<>'"]/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', "'":'&#39;', '"':'&quot;' }[c]));
}

function fmtDuration(seconds) {
  if (!seconds || !Number.isFinite(seconds)) return '';
  const s = Math.max(0, Math.floor(seconds));
  const h = Math.floor(s / 3600);
  const m = Math.floor((s % 3600) / 60);
  const sec = s % 60;
  return h ? `${h}:${String(m).padStart(2,'0')}:${String(sec).padStart(2,'0')}` : `${m}:${String(sec).padStart(2,'0')}`;
}

function fmtSize(bytes) {
  if (!bytes) return '';
  const units = ['B','KB','MB','GB','TB'];
  let n = bytes, i = 0;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return `${n >= 10 || i === 0 ? n.toFixed(0) : n.toFixed(1)} ${units[i]}`;
}

function fmtViews(n) {
  if (n == null) return null;
  try { return `${new Intl.NumberFormat('pt-BR', { notation: 'compact', maximumFractionDigits: 1 }).format(n)} visualizações`; }
  catch { return `${n} visualizações`; }
}

function relativeDate(value) {
  if (!value) return '';
  const date = new Date(value);
  const delta = Date.now() - date.getTime();
  const days = Math.floor(delta / 86400000);
  if (days < 1) return 'hoje';
  if (days === 1) return 'há 1 dia';
  if (days < 30) return `há ${days} dias`;
  const months = Math.floor(days / 30);
  if (months < 12) return months === 1 ? 'há 1 mês' : `há ${months} meses`;
  const years = Math.floor(months / 12);
  return years === 1 ? 'há 1 ano' : `há ${years} anos`;
}

function longDate(value) {
  if (!value) return '';
  try { return new Intl.DateTimeFormat('pt-BR', { day:'2-digit', month:'short', year:'numeric' }).format(new Date(value)); }
  catch { return ''; }
}

function initials(text) {
  return (text || 'L').trim().split(/\s+/).slice(0,2).map(x => x[0]?.toUpperCase()).join('') || 'L';
}

function metaLine(item) {
  const parts = [];
  const views = fmtViews(item.viewCount);
  if (views) parts.push(views);
  parts.push(relativeDate(item.publishedAt || item.modifiedAt));
  return parts.filter(Boolean).join(' • ');
}

function thumbHtml(item, extraClass = '') {
  const progressPct = item.durationSeconds > 0 ? Math.min(100, Math.max(0, (item.progressSeconds / item.durationSeconds) * 100)) : 0;
  return `<div class="thumb ${extraClass}" data-watch="${item.id}">
    ${item.hasThumbnail ? `<img src="/api/media/${item.id}/thumbnail" alt="" loading="lazy" onerror="this.style.display='none';this.nextElementSibling.style.display='grid'" /><div class="thumb-fallback" style="display:none"><svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg></div>` : `<div class="thumb-fallback"><svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg></div>`}
    ${item.durationSeconds ? `<span class="duration">${fmtDuration(item.durationSeconds)}</span>` : ''}
    ${progressPct > 0 ? `<span class="progress"><span style="width:${progressPct}%"></span></span>` : ''}
  </div>`;
}

function mediaCard(item) {
  return `<article class="media-card">
    ${thumbHtml(item)}
    <div class="card-info">
      <div class="avatar">${escapeHtml(initials(item.channel))}</div>
      <div class="card-text">
        <h3 class="card-title" data-watch="${item.id}">${escapeHtml(item.title)}</h3>
        <div class="card-meta">${escapeHtml(item.channel || 'Local')}</div>
        <div class="card-meta">${escapeHtml(metaLine(item))}</div>
      </div>
      <button class="card-more" data-card-menu="${item.id}" aria-label="Mais opções">⋮</button>
    </div>
  </article>`;
}

function listCard(item) {
  return `<article class="list-card">
    ${thumbHtml(item)}
    <div>
      <h3 data-watch="${item.id}">${escapeHtml(item.title)}</h3>
      <p>${escapeHtml(item.channel || 'Local')}</p>
      <p>${escapeHtml(metaLine(item))}</p>
    </div>
  </article>`;
}

function section(title, items, opts = {}) {
  if (!items.length) return '';
  const cards = items.slice(0, opts.limit || 12).map(mediaCard).join('');
  return `<section class="section">
    <div class="section-head"><h2>${escapeHtml(title)}</h2>${opts.view ? `<button data-view="${opts.view}">Ver tudo</button>` : ''}</div>
    <div class="media-grid">${cards}</div>
  </section>`;
}

function emptyState(title, text, action = '') {
  return `<div class="empty"><h2>${escapeHtml(title)}</h2><p>${escapeHtml(text)}</p>${action}</div>`;
}

async function refreshData() {
  const [media, playlists, settings] = await Promise.all([
    api('/api/media'),
    api('/api/playlists'),
    api('/api/settings')
  ]);
  state.media = media || [];
  state.playlists = playlists || [];
  state.settings = settings;
}

function updateNav(view) {
  $$('.nav-item').forEach(btn => btn.classList.toggle('active', btn.dataset.view === view || btn.dataset.action === view));
}

function folderTags() {
  const counts = new Map();
  for (const item of state.media) {
    const first = String(item.relativePath || '').split(/[\\/]/)[0];
    if (!first || first === item.fileName) continue;
    counts.set(first, (counts.get(first) || 0) + 1);
  }
  return [...counts.entries()].sort((a,b) => b[1]-a[1]).slice(0,8).map(([name]) => name);
}

function renderHome() {
  updateNav('home');
  state.view = 'home';
  const all = state.media.filter(x => x.mediaType === 'video');
  if (!all.length) {
    content.innerHTML = `<div class="page-head"><div><h1>Início</h1><p>Sua biblioteca local</p></div></div>` +
      emptyState('Sua biblioteca está vazia', 'Escolha uma pasta com vídeos para começar.', `<button class="primary" id="emptyChooseFolder">Escolher pasta</button>`);
    $('#emptyChooseFolder')?.addEventListener('click', chooseFolder);
    return;
  }

  const tags = folderTags();
  const watched = all.filter(x => x.progressSeconds > 10).sort((a,b) => new Date(b.lastWatchedAt || 0) - new Date(a.lastWatchedAt || 0));
  const recent = [...all].sort((a,b) => new Date(b.modifiedAt) - new Date(a.modifiedAt));
  const liked = all.filter(x => x.liked).slice(0,12);
  const watchLater = all.filter(x => x.watchLater).slice(0,12);
  const recommended = [...all].sort((a,b) => {
    const av = (a.liked ? 2 : 0) + (a.lastWatchedAt ? 1 : 0);
    const bv = (b.liked ? 2 : 0) + (b.lastWatchedAt ? 1 : 0);
    return bv - av || new Date(b.modifiedAt) - new Date(a.modifiedAt);
  });

  content.innerHTML = `
    <div class="chip-row" id="homeChips">
      <button class="chip active" data-folder="">Tudo</button>
      <button class="chip" data-special="video">Vídeos</button>
      <button class="chip" data-special="audio">Música</button>
      ${tags.map(t => `<button class="chip" data-folder="${escapeHtml(t)}">${escapeHtml(t)}</button>`).join('')}
    </div>
    <div id="homeSections">
      ${section('Recomendados', recommended, { limit: 10 })}
      ${section('Continuar assistindo', watched, { limit: 10, view: 'history' })}
      ${section('Adicionados recentemente', recent, { limit: 10, view: 'library' })}
      ${section('Assistir mais tarde', watchLater, { limit: 10, view: 'watchlater' })}
      ${section('Vídeos curtidos', liked, { limit: 10, view: 'liked' })}
    </div>`;

  $('#homeChips')?.addEventListener('click', e => {
    const chip = e.target.closest('.chip');
    if (!chip) return;
    $$('.chip', $('#homeChips')).forEach(x => x.classList.remove('active'));
    chip.classList.add('active');
    let items = state.media;
    if (chip.dataset.special === 'video') items = state.media.filter(x => x.mediaType === 'video');
    else if (chip.dataset.special === 'audio') items = state.media.filter(x => x.mediaType === 'audio');
    else if (chip.dataset.folder) items = state.media.filter(x => String(x.relativePath).split(/[\\/]/)[0] === chip.dataset.folder);
    $('#homeSections').innerHTML = section(chip.textContent.trim(), items, { limit: 40 }) || emptyState('Nada aqui', 'Nenhum item nesta categoria.');
    bindDynamicEvents();
  });
  bindDynamicEvents();
}

function renderLibrary(items = state.media, title = 'Biblioteca') {
  updateNav(title === 'Biblioteca' ? 'library' : '');
  state.view = 'library';
  content.innerHTML = `
    <div class="page-head">
      <div><h1>${escapeHtml(title)}</h1><p>${items.length} ${items.length === 1 ? 'item' : 'itens'} na sua biblioteca</p></div>
      <div class="page-actions"><button class="secondary" id="rescanBtn">Atualizar biblioteca</button></div>
    </div>
    ${items.length ? `<div class="media-grid">${items.map(mediaCard).join('')}</div>` : emptyState('Nenhum resultado', 'Não encontrei vídeos ou áudios com esse filtro.')}`;
  $('#rescanBtn')?.addEventListener('click', async () => {
    try { await api('/api/media/rescan', { method:'POST' }); await refreshData(); renderLibrary(); toast('Biblioteca atualizada.'); }
    catch (e) { toast(e.message); }
  });
  bindDynamicEvents();
}

function renderHistory() {
  updateNav('history');
  state.view = 'history';
  const items = state.media.filter(x => x.lastWatchedAt).sort((a,b) => new Date(b.lastWatchedAt) - new Date(a.lastWatchedAt));
  content.innerHTML = `<div class="page-head"><div><h1>Histórico</h1><p>O que você assistiu neste PC</p></div></div>` +
    (items.length ? `<div class="media-grid">${items.map(mediaCard).join('')}</div>` : emptyState('Nenhum histórico ainda', 'Os vídeos reproduzidos aparecerão aqui.'));
  bindDynamicEvents();
}

function renderLiked() {
  updateNav('liked');
  state.view = 'liked';
  const items = state.media.filter(x => x.liked);
  content.innerHTML = `<div class="page-head"><div><h1>Vídeos curtidos</h1><p>${items.length} ${items.length === 1 ? 'item' : 'itens'}</p></div></div>` +
    (items.length ? `<div class="media-grid">${items.map(mediaCard).join('')}</div>` : emptyState('Nenhum vídeo curtido', 'Use o botão Curtir na página do vídeo.'));
  bindDynamicEvents();
}

function renderWatchLater() {
  updateNav('watchlater');
  state.view = 'watchlater';
  const items = state.media.filter(x => x.watchLater);
  content.innerHTML = `<div class="page-head"><div><h1>Assistir mais tarde</h1><p>${items.length} ${items.length === 1 ? 'vídeo salvo' : 'vídeos salvos'}</p></div></div>` +
    (items.length ? `<div class="media-grid">${items.map(mediaCard).join('')}</div>` : emptyState('Nenhum vídeo salvo', 'Na página de um vídeo, use “Assistir mais tarde”.'));
  bindDynamicEvents();
}

function renderAudio() {
  const items = state.media.filter(x => x.mediaType === 'audio');
  renderLibrary(items, 'Áudios');
  state.view = 'audio';
  updateNav('audio');
}

function renderPlaylists() {
  updateNav('playlists');
  state.view = 'playlists';
  content.innerHTML = `
    <div class="page-head">
      <div><h1>Playlists</h1><p>Organize sua biblioteca sem mover os arquivos.</p></div>
      <div class="page-actions"><button class="primary" id="newPlaylistBtn">Nova playlist</button></div>
    </div>
    ${state.playlists.length ? `<div class="playlist-grid">${state.playlists.map(p => {
      const count = p.itemIds?.length || 0;
      return `<article class="playlist-card" data-playlist="${p.id}">
        <div class="playlist-cover">▶</div>
        <div class="playlist-card-body"><h3>${escapeHtml(p.name)}</h3><p>${count} ${count === 1 ? 'vídeo' : 'vídeos'}</p></div>
      </article>`;
    }).join('')}</div>` : emptyState('Nenhuma playlist', 'Crie uma playlist para agrupar seus vídeos.')}`;

  $('#newPlaylistBtn')?.addEventListener('click', async () => {
    const name = prompt('Nome da playlist:');
    if (!name?.trim()) return;
    try { await api('/api/playlists', { method:'POST', body:{ name:name.trim() } }); await refreshData(); renderPlaylists(); }
    catch (e) { toast(e.message); }
  });
  $$('[data-playlist]').forEach(el => el.addEventListener('click', () => renderPlaylist(el.dataset.playlist)));
}

function renderPlaylist(id) {
  const p = state.playlists.find(x => x.id === id);
  if (!p) return renderPlaylists();
  const items = (p.itemIds || []).map(id => state.media.find(x => x.id === id)).filter(Boolean);
  content.innerHTML = `
    <div class="page-head">
      <div><h1>${escapeHtml(p.name)}</h1><p>${items.length} ${items.length === 1 ? 'item' : 'itens'}</p></div>
      <div class="page-actions"><button class="secondary" id="backPlaylists">Voltar</button><button class="secondary" id="deletePlaylist">Excluir playlist</button></div>
    </div>
    ${items.length ? `<div class="media-grid">${items.map(mediaCard).join('')}</div>` : emptyState('Playlist vazia', 'Adicione vídeos usando o botão Salvar na página de reprodução.')}`;
  $('#backPlaylists')?.addEventListener('click', renderPlaylists);
  $('#deletePlaylist')?.addEventListener('click', async () => {
    if (!confirm(`Excluir a playlist "${p.name}"? Os arquivos não serão apagados.`)) return;
    await api(`/api/playlists/${p.id}`, { method:'DELETE' });
    await refreshData();
    renderPlaylists();
  });
  bindDynamicEvents();
}

function renderSettings() {
  updateNav('settings');
  state.view = 'settings';
  const tools = state.settings?.toolStatus || {};
  const update = state.settings?.update || {};
  const toolText = tools.ytDlp?.available && tools.ffmpeg?.available
    ? 'Downloader pronto para vídeos, áudio e playlists.'
    : 'As ferramentas de download serão preparadas automaticamente no primeiro uso.';
  const updateText = update.available
    ? `Versão ${escapeHtml(update.latestVersion || '')} disponível.`
    : update.error
      ? escapeHtml(update.error)
      : `Você está na versão ${escapeHtml(update.currentVersion || '')}.`;
  const notes = Array.isArray(update.notes) && update.notes.length
    ? `<ul class="update-notes">${update.notes.map(x => `<li>${escapeHtml(x)}</li>`).join('')}</ul>`
    : '';
  content.innerHTML = `
    <div class="page-head"><div><h1>Configurações</h1><p>Biblioteca, ferramentas e atualizações</p></div></div>
    <div class="panel form-grid" style="max-width:900px">
      <div class="field">
        <label>Pasta da biblioteca</label>
        <input id="libraryPath" value="${escapeHtml(state.settings?.libraryFolder || '')}" />
      </div>
      <div class="row">
        <button class="primary" id="chooseFolderSettings">Escolher pasta</button>
        <button class="secondary" id="saveFolder">Usar este caminho</button>
        <button class="secondary" id="openFolder">Abrir no Explorador</button>
      </div>
      <hr style="border:0;border-top:1px solid #333;width:100%" />
      <div>
        <h3 style="margin:0 0 8px">Ferramentas de download</h3>
        <p class="muted" style="margin:0">${toolText}</p>
        <p class="muted" style="margin:6px 0 0">yt-dlp: ${tools.ytDlp?.available ? 'pronto' : 'será baixado automaticamente'} • FFmpeg: ${tools.ffmpeg?.available ? 'pronto' : 'será baixado automaticamente'}</p>
      </div>
      <div class="row"><button class="secondary" id="prepareTools">Preparar agora</button></div>
      <hr style="border:0;border-top:1px solid #333;width:100%" />
      <div class="update-box ${update.available ? 'available' : ''}">
        <div>
          <h3 style="margin:0 0 8px">Atualizações</h3>
          <p class="muted" id="updateStatusText" style="margin:0">${updateText}</p>
          ${notes}
          ${update.downloading ? `<p class="muted" style="margin:8px 0 0">Baixando atualização: ${Number(update.downloadPercent || 0)}%</p>` : ''}
        </div>
        <div class="row">
          <button class="secondary" id="checkUpdate">${update.checking ? 'Verificando…' : 'Verificar agora'}</button>
          ${update.available ? `<button class="primary" id="installUpdate">${update.downloading ? `Baixando ${Number(update.downloadPercent || 0)}%` : 'Baixar e instalar'}</button>` : ''}
        </div>
      </div>
    </div>`;
  $('#chooseFolderSettings')?.addEventListener('click', chooseFolder);
  $('#saveFolder')?.addEventListener('click', () => saveFolder($('#libraryPath').value));
  $('#openFolder')?.addEventListener('click', async () => { try { await api('/api/settings/open-folder', { method:'POST' }); } catch (e) { toast(e.message); } });
  $('#prepareTools')?.addEventListener('click', async e => {
    const btn = e.currentTarget;
    btn.disabled = true;
    btn.textContent = 'Preparando…';
    try {
      await api('/api/tools/prepare', { method:'POST' });
      await refreshData();
      toast('Downloader preparado.');
      renderSettings();
    } catch (err) {
      toast(err.message);
      btn.disabled = false;
      btn.textContent = 'Tentar novamente';
    }
  });
  $('#checkUpdate')?.addEventListener('click', async e => {
    const btn = e.currentTarget;
    btn.disabled = true;
    btn.textContent = 'Verificando…';
    try {
      const result = await api('/api/update/check', { method:'POST' });
      state.settings.update = result;
      if (result.available) toast(`Atualização ${result.latestVersion} disponível.`);
      else if (result.error) toast(result.error);
      else toast('Você já está na versão mais recente.');
      renderSettings();
    } catch (err) {
      toast(err.message);
      btn.disabled = false;
      btn.textContent = 'Tentar novamente';
    }
  });
  $('#installUpdate')?.addEventListener('click', async e => {
    const btn = e.currentTarget;
    btn.disabled = true;
    btn.textContent = 'Baixando atualização…';
    try {
      const poll = setInterval(async () => {
        try {
          const st = await api('/api/update/status');
          if (state.settings) state.settings.update = st;
          if (state.view === 'settings' && st.downloading) {
            btn.textContent = `Baixando ${Number(st.downloadPercent || 0)}%`;
          }
        } catch { }
      }, 700);
      try {
        await api('/api/update/install', { method:'POST' });
        btn.textContent = 'Instalando…';
        toast('Atualização pronta. O YouTube Offline será reiniciado.');
      } finally {
        clearInterval(poll);
      }
    } catch (err) {
      toast(err.message);
      btn.disabled = false;
      btn.textContent = 'Tentar novamente';
    }
  });
}

function renderWatch(id, pushHash = true) {
  const item = state.media.find(x => x.id === id);
  if (!item) return renderHome();
  state.currentMedia = item;
  updateNav('');
  state.view = 'watch';
  if (pushHash && location.hash !== `#/watch/${id}`) history.pushState({}, '', `#/watch/${id}`);

  const suggestions = state.media.filter(x => x.id !== item.id && x.mediaType === item.mediaType).slice(0,18);
  const description = item.description || `Arquivo local: ${item.relativePath}`;
  const facts = [fmtViews(item.viewCount), longDate(item.publishedAt || item.modifiedAt), fmtSize(item.sizeBytes)].filter(Boolean).join(' • ');

  content.innerHTML = `<div class="watch-layout">
    <section class="watch-main">
      <div class="watch-player">
        ${item.mediaType === 'audio' ? `
          <div class="audio-player-stage"><div>
            ${item.hasThumbnail ? `<img src="/api/media/${item.id}/thumbnail" alt="" />` : `<div class="thumb-fallback" style="aspect-ratio:1;border-radius:18px"><svg viewBox="0 0 24 24"><path d="M9 18V6l10-2v12"/><circle cx="6" cy="18" r="3"/><circle cx="16" cy="16" r="3"/></svg></div>`}
            <audio id="mainPlayer" controls autoplay src="/api/media/${item.id}/stream"></audio>
          </div></div>` : `<video id="mainPlayer" controls autoplay playsinline src="/api/media/${item.id}/stream"></video>`}
      </div>
      <h1 class="watch-title">${escapeHtml(item.title)}</h1>
      <div class="watch-bar">
        <div class="channel-block">
          <div class="avatar">${escapeHtml(initials(item.channel))}</div>
          <div><div class="channel-name">${escapeHtml(item.channel || 'Local')}</div><div class="channel-sub">${escapeHtml(item.extractor || 'Biblioteca local')}</div></div>
        </div>
        <div class="watch-actions">
          <button class="pill-btn ${item.liked ? 'active' : ''}" id="likeBtn">
            <svg viewBox="0 0 24 24"><path d="M8 21H4V10h4zM8 10l4-7c2 0 2 1.5 1.5 4L13 9h6c1.4 0 2 1 1.7 2.3l-1.5 7C19 20 18 21 16.5 21H8z"/></svg>${item.liked ? 'Curtido' : 'Curtir'}
          </button>
          <button class="pill-btn ${item.watchLater ? 'active' : ''}" id="watchLaterBtn"><svg viewBox="0 0 24 24"><circle cx="12" cy="12" r="8"/><path d="M12 8v5l3 2"/></svg>${item.watchLater ? 'Salvo' : 'Assistir mais tarde'}</button>
          <button class="pill-btn" id="saveBtn"><svg viewBox="0 0 24 24"><path d="M5 6h10M5 11h10M5 16h6"/><path d="M18 14v7M14.5 17.5h7"/></svg>Salvar</button>
          ${item.sourceUrl ? `<button class="pill-btn" id="sourceBtn"><svg viewBox="0 0 24 24"><path d="M10 14 21 3M14 3h7v7"/><path d="M19 13v7H4V5h7"/></svg>Original</button>` : ''}
        </div>
      </div>
      <div class="description-box">
        <div class="facts">${escapeHtml(facts)}</div>
        ${escapeHtml(description)}
        ${item.sourceUrl ? `<div style="margin-top:10px"><a class="source-link" href="${escapeHtml(item.sourceUrl)}" target="_blank" rel="noreferrer">${escapeHtml(item.sourceUrl)}</a></div>` : ''}
      </div>
    </section>
    <aside class="up-next"><h2>A seguir</h2>${suggestions.map(listCard).join('')}</aside>
  </div>`;

  bindDynamicEvents();
  const player = $('#mainPlayer');
  if (player) {
    let lastSaved = 0;
    player.addEventListener('loadedmetadata', () => {
      if (item.progressSeconds > 2 && item.progressSeconds < (player.duration || Infinity) - 10) player.currentTime = item.progressSeconds;
    });
    player.addEventListener('timeupdate', () => {
      if (Math.abs(player.currentTime - lastSaved) >= 5) {
        lastSaved = player.currentTime;
        saveProgress(item.id, player.currentTime, player.duration);
      }
    });
    player.addEventListener('pause', () => saveProgress(item.id, player.currentTime, player.duration));
    player.addEventListener('ended', () => saveProgress(item.id, player.duration, player.duration));
  }

  $('#likeBtn')?.addEventListener('click', async () => {
    const next = !item.liked;
    try {
      await api(`/api/media/${item.id}/like`, { method:'POST', body:{ liked:next } });
      item.liked = next;
      renderWatch(item.id, false);
    } catch (e) { toast(e.message); }
  });
  $('#watchLaterBtn')?.addEventListener('click', async () => {
    const next = !item.watchLater;
    try {
      await api(`/api/media/${item.id}/watch-later`, { method:'POST', body:{ watchLater:next } });
      item.watchLater = next;
      renderWatch(item.id, false);
    } catch (e) { toast(e.message); }
  });
  $('#saveBtn')?.addEventListener('click', () => choosePlaylistFor(item));
  $('#sourceBtn')?.addEventListener('click', () => { if (item.sourceUrl) window.open(item.sourceUrl, '_blank'); });
}

async function saveProgress(id, progressSeconds, durationSeconds) {
  if (!Number.isFinite(progressSeconds)) return;
  const item = state.media.find(x => x.id === id);
  if (item) {
    item.progressSeconds = progressSeconds;
    if (Number.isFinite(durationSeconds)) item.durationSeconds = durationSeconds;
    item.lastWatchedAt = new Date().toISOString();
  }
  try { await api(`/api/media/${id}/progress`, { method:'POST', body:{ progressSeconds, durationSeconds:Number.isFinite(durationSeconds) ? durationSeconds : 0 } }); }
  catch { }
}

async function choosePlaylistFor(item) {
  if (!state.playlists.length) {
    const name = prompt('Você ainda não tem playlists. Nome da nova playlist:');
    if (!name?.trim()) return;
    const p = await api('/api/playlists', { method:'POST', body:{ name:name.trim() } });
    state.playlists.push(p);
  }
  const options = state.playlists.map((p,i) => `${i + 1}. ${p.name}`).join('\n');
  const answer = prompt(`Salvar em qual playlist?\n\n${options}\n\nDigite o número:`);
  const index = Number(answer) - 1;
  if (!Number.isInteger(index) || !state.playlists[index]) return;
  const p = state.playlists[index];
  await api(`/api/playlists/${p.id}/items/${item.id}`, { method:'POST' });
  if (!p.itemIds.includes(item.id)) p.itemIds.push(item.id);
  toast(`Adicionado a “${p.name}”.`);
}

function bindDynamicEvents() {
  $$('[data-watch]').forEach(el => {
    el.onclick = e => {
      const nested = e.target.closest('[data-card-menu]');
      if (nested) return;
      const id = el.dataset.watch;
      if (id) renderWatch(id);
    };
  });
  $$('[data-view]').forEach(el => el.onclick = () => navigate(el.dataset.view));
  $$('[data-card-menu]').forEach(btn => btn.onclick = e => {
    e.stopPropagation();
    const item = state.media.find(x => x.id === btn.dataset.cardMenu);
    if (item) quickMenu(item);
  });
}

function quickMenu(item) {
  const action = prompt(`Opções para “${item.title}”\n\n1. Reproduzir\n2. ${item.watchLater ? 'Remover de Assistir mais tarde' : 'Assistir mais tarde'}\n3. ${item.liked ? 'Remover dos curtidos' : 'Curtir'}\n4. Salvar em playlist\n\nDigite o número:`);
  if (action === '1') renderWatch(item.id);
  if (action === '2') api(`/api/media/${item.id}/watch-later`, { method:'POST', body:{ watchLater:!item.watchLater } }).then(async () => { await refreshData(); navigate(state.view); });
  if (action === '3') api(`/api/media/${item.id}/like`, { method:'POST', body:{ liked:!item.liked } }).then(async () => { await refreshData(); navigate(state.view); });
  if (action === '4') choosePlaylistFor(item);
}

function navigate(view) {
  if (view !== 'watch' && location.hash.startsWith('#/watch/')) history.pushState({}, '', '#/');
  switch (view) {
    case 'home': renderHome(); break;
    case 'library': renderLibrary(); break;
    case 'history': renderHistory(); break;
    case 'watchlater': renderWatchLater(); break;
    case 'liked': renderLiked(); break;
    case 'playlists': renderPlaylists(); break;
    case 'audio': renderAudio(); break;
    case 'downloads': renderDownloads(); break;
    case 'settings': renderSettings(); break;
    default: renderHome(); break;
  }
}

async function chooseFolder() {
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage({ type:'select-folder' });
  } else {
    const current = state.settings?.libraryFolder || '';
    const path = prompt('Caminho completo da pasta:', current);
    if (path) saveFolder(path);
  }
}

window.localTubeNativeFolderSelected = async path => {
  $('#libraryPath') && ($('#libraryPath').value = path);
  await saveFolder(path);
};

async function saveFolder(path) {
  if (!path?.trim()) return;
  try {
    const result = await api('/api/settings/library', { method:'POST', body:{ path:path.trim() } });
    await refreshData();
    toast(`Biblioteca alterada. ${result.count} itens encontrados.`);
    renderHome();
  } catch (e) { toast(e.message); }
}

function renderDownloads() {
  updateNav('downloads');
  state.view = 'downloads';
  const a = state.downloadAnalysis;
  const playlistInfo = a?.isPlaylist ? `Playlist • ${a.itemCount || 0} vídeos` : '';
  const previewEntries = a?.isPlaylist && a.entries?.length
    ? `<div class="playlist-preview-list">${a.entries.slice(0,8).map((x,i) => `<div><span>${x.index || i + 1}</span><strong>${escapeHtml(x.title || 'Vídeo')}</strong>${x.duration ? `<small>${fmtDuration(x.duration)}</small>` : ''}</div>`).join('')}${a.itemCount > 8 ? `<p>+ ${a.itemCount - 8} vídeos</p>` : ''}</div>`
    : '';

  content.innerHTML = `
    <div class="page-head"><div><h1>Downloads</h1><p>Vídeos, áudios e playlists para a sua biblioteca.</p></div></div>
    <section class="download-page-panel">
      <div class="form-grid">
        <div class="field"><label>Link do vídeo ou playlist</label><div class="row" style="flex-wrap:nowrap"><input id="downloadUrl" style="flex:1" placeholder="https://youtube.com/watch?v=... ou /playlist?list=..." /><button class="secondary" id="analyzeBtn">Analisar</button></div></div>
        <p class="muted download-hint">O YouTube Offline evita downloads duplicados usando o ID original do vídeo e um histórico local de downloads.</p>
        <div class="field download-auth-field" style="max-width:520px"><label>Se o YouTube pedir verificação anti-bot</label><select id="downloadCookieBrowser"><option value="auto">Automático — tentar minha sessão local do navegador</option><option value="none">Não usar sessão do navegador</option><option value="edge">Microsoft Edge</option><option value="chrome">Google Chrome</option><option value="firefox">Firefox</option><option value="brave">Brave</option></select><small class="muted">A sessão só é tentada se o download anônimo receber a verificação do YouTube. O app não pede sua senha.</small></div>
        ${a ? `<div class="download-preview">
          ${a.thumbnail ? `<img src="${escapeHtml(a.thumbnail)}" alt="" />` : `<div class="thumb-fallback" style="aspect-ratio:16/9;border-radius:8px"><svg viewBox="0 0 24 24"><path d="M8 5v14l11-7z"/></svg></div>`}
          <div><h3>${escapeHtml(a.title)}</h3><p>${escapeHtml(a.channel || '')}</p><p>${a.isPlaylist ? escapeHtml(playlistInfo) : fmtDuration(a.duration)}</p></div>
        </div>
        ${previewEntries}
        <div class="row">
          <div class="field" style="flex:1;min-width:180px"><label>Tipo</label><select id="downloadMode"><option value="video">Vídeo</option><option value="audio">Somente áudio</option></select></div>
          <div class="field" style="flex:1;min-width:180px"><label>Qualidade</label><select id="downloadQuality"><option value="best">Melhor disponível</option>${(a.qualities || []).map(q => `<option value="${q}">${q >= 2160 ? `${q}p${q === 2160 ? ' / 4K' : q === 4320 ? ' / 8K' : ''}` : `${q}p`}</option>`).join('')}</select></div>
          <div class="field hidden" id="audioFormatWrap" style="flex:1;min-width:180px"><label>Formato de áudio</label><select id="audioFormat"><option value="original">Original</option><option value="mp3">MP3</option><option value="m4a">M4A</option><option value="opus">Opus</option></select></div>
        </div>
        ${a.isPlaylist ? `<div class="playlist-detected"><strong>Playlist detectada.</strong><span>Cada vídeo entra separadamente na fila. Você pode cancelar qualquer item antes ou durante o download.</span></div>` : `<div class="row"><label style="display:flex;gap:8px;align-items:center"><input type="checkbox" id="downloadPlaylist" /> Tratar este link como playlist</label></div>`}
        <div class="row"><button class="primary" id="startDownload">${a.isPlaylist ? `Adicionar ${a.itemCount || 'todos os'} vídeos à fila` : 'Baixar para a biblioteca'}</button></div>` : ''}
      </div>
    </section>
    <section class="download-activity">
      <div class="section-head"><h2>Fila e atividade</h2></div>
      <div id="downloadJobs"></div>
    </section>`;

  const cookieSelect = $('#downloadCookieBrowser');
  if (cookieSelect) {
    cookieSelect.value = state.downloadCookieBrowser || 'auto';
    cookieSelect.addEventListener('change', e => {
      state.downloadCookieBrowser = e.target.value;
      localStorage.setItem('youtubeOfflineCookieBrowser', state.downloadCookieBrowser);
    });
  }
  $('#analyzeBtn')?.addEventListener('click', analyzeDownload);
  $('#downloadUrl')?.addEventListener('keydown', e => { if (e.key === 'Enter') analyzeDownload(); });
  $('#downloadMode')?.addEventListener('change', e => {
    $('#audioFormatWrap')?.classList.toggle('hidden', e.target.value !== 'audio');
    $('#downloadQuality')?.closest('.field')?.classList.toggle('hidden', e.target.value === 'audio');
  });
  $('#startDownload')?.addEventListener('click', startDownload);
  renderJobs();
  pollJobs();
}

async function analyzeDownload() {
  const url = $('#downloadUrl')?.value?.trim();
  if (!url) return;
  const btn = $('#analyzeBtn');
  if (btn) { btn.disabled = true; btn.textContent = 'Preparando e analisando…'; }
  try {
    state.downloadAnalysis = await api('/api/download/analyze', { method:'POST', body:{ url, cookieBrowser: state.downloadCookieBrowser || 'auto' } });
    renderDownloads();
    $('#downloadUrl').value = url;
  } catch (e) {
    toast(e.message);
    if (btn) { btn.disabled = false; btn.textContent = 'Analisar'; }
  }
}

async function startDownload() {
  const url = $('#downloadUrl')?.value?.trim();
  if (!url) return;
  const isPlaylist = !!state.downloadAnalysis?.isPlaylist || !!$('#downloadPlaylist')?.checked;
  const btn = $('#startDownload');
  if (btn) { btn.disabled = true; btn.textContent = 'Montando fila…'; }
  try {
    const result = await api('/api/download/start', { method:'POST', body:{
      url,
      mode: $('#downloadMode')?.value || 'video',
      quality: $('#downloadQuality')?.value || 'best',
      audioFormat: $('#audioFormat')?.value || 'original',
      playlist: isPlaylist,
      cookieBrowser: state.downloadCookieBrowser || 'auto'
    }});
    const duplicates = result?.skippedItems || 0;
    toast(isPlaylist
      ? `Playlist adicionada à fila.${duplicates ? ` ${duplicates} vídeo(s) já existiam e foram ignorados.` : ''}`
      : (duplicates ? 'Esse vídeo já existe na biblioteca ou já foi baixado.' : 'Download adicionado à fila.'));
    state.downloadAnalysis = null;
    renderDownloads();
  } catch (e) {
    toast(e.message);
    if (btn) { btn.disabled = false; btn.textContent = 'Tentar novamente'; }
  }
}

function downloadStatusLabel(item) {
  switch (item.status) {
    case 'queued': return 'Na fila';
    case 'preparing': return 'Preparando';
    case 'downloading': return `${Math.round(item.progress || 0)}%`;
    case 'cancelling': return 'Cancelando…';
    case 'completed': return 'Concluído';
    case 'cancelled': return 'Cancelado';
    case 'skipped': return 'Já existe';
    case 'failed': return 'Falhou';
    default: return item.status || '';
  }
}

function batchSummary(job) {
  const parts = [];
  if (job.completedItems) parts.push(`${job.completedItems} concluído${job.completedItems === 1 ? '' : 's'}`);
  const queued = (job.items || []).filter(x => x.status === 'queued').length;
  if (queued) parts.push(`${queued} na fila`);
  if (job.skippedItems) parts.push(`${job.skippedItems} já existente${job.skippedItems === 1 ? '' : 's'}`);
  if (job.cancelledItems) parts.push(`${job.cancelledItems} cancelado${job.cancelledItems === 1 ? '' : 's'}`);
  if (job.failedItems) parts.push(`${job.failedItems} com erro`);
  return parts.join(' • ') || `${job.totalItems || 0} item(ns)`;
}

async function cancelDownloadItem(id) {
  try {
    await api(`/api/download/items/${id}/cancel`, { method:'POST' });
    toast('Cancelamento solicitado.');
    await renderJobs();
  } catch (e) { toast(e.message); }
}

async function renderJobs() {
  const target = $('#downloadJobs');
  if (!target) return;
  try {
    const jobs = await api('/api/download/jobs');
    if (!jobs?.length) {
      target.innerHTML = emptyState('Nenhum download ainda', 'Cole um link acima para adicionar vídeos à fila.');
      return;
    }

    target.innerHTML = jobs.map(job => {
      const items = job.items || [];
      return `<article class="download-batch">
        <header class="download-batch-head">
          <div><h3>${escapeHtml(job.title || (job.isPlaylist ? 'Playlist' : 'Download'))}</h3><p>${escapeHtml(batchSummary(job))}</p></div>
          <div class="batch-progress-text">${Math.round(job.progress || 0)}%</div>
        </header>
        <div class="bar batch-bar"><span style="width:${Math.max(0,Math.min(100,job.progress || 0))}%"></span></div>
        <div class="download-items">
          ${items.map(item => {
            const cancellable = ['queued','preparing','downloading','cancelling'].includes(item.status);
            const index = item.playlistIndex > 0 ? `<span class="download-index">${String(item.playlistIndex).padStart(2,'0')}</span>` : '';
            const detail = item.error || [item.speed, item.eta ? `restam ${item.eta}` : ''].filter(Boolean).join(' • ');
            return `<div class="download-item ${escapeHtml(item.status)}">
              ${index}
              <div class="download-item-main">
                <div class="download-item-top"><strong title="${escapeHtml(item.title)}">${escapeHtml(item.title || 'Vídeo')}</strong><span class="status-pill ${escapeHtml(item.status)}">${escapeHtml(downloadStatusLabel(item))}</span></div>
                <div class="bar item-bar"><span style="width:${Math.max(0,Math.min(100,item.progress || 0))}%"></span></div>
                <div class="download-item-bottom"><span>${escapeHtml(detail || (item.status === 'queued' ? 'Aguardando os vídeos anteriores' : ''))}</span>${cancellable && item.status !== 'cancelling' ? `<button class="cancel-download" data-cancel-download="${item.id}">Cancelar</button>` : ''}</div>
              </div>
            </div>`;
          }).join('')}
        </div>
      </article>`;
    }).join('');

    $$('[data-cancel-download]', target).forEach(btn => btn.addEventListener('click', () => cancelDownloadItem(btn.dataset.cancelDownload)));

    const completionSignature = jobs.flatMap(j => (j.items || []).filter(x => x.status === 'completed').map(x => x.id)).sort().join('|');
    if (completionSignature && completionSignature !== state.downloadCompletionSignature) {
      state.downloadCompletionSignature = completionSignature;
      await api('/api/media/rescan', { method:'POST' });
      await refreshData();
    }
  } catch { }
}

function pollJobs() {
  renderJobs();
  if (state.downloadPoll) return;
  state.downloadPoll = setInterval(() => {
    if (state.view === 'downloads') renderJobs();
    else { clearInterval(state.downloadPoll); state.downloadPoll = null; }
  }, 900);
}

function toast(message) {
  const el = $('#toast');
  el.textContent = message;
  el.classList.remove('hidden');
  clearTimeout(toast.timer);
  toast.timer = setTimeout(() => el.classList.add('hidden'), 3500);
}

async function checkUpdateQuietly() {
  try {
    const result = await api('/api/update/status');
    if (state.settings) state.settings.update = result;
    if (result.available) {
      toast(`Atualização ${result.latestVersion} disponível em Configurações.`);
      return;
    }
    if (result.checking) {
      setTimeout(checkUpdateQuietly, 2500);
    }
  } catch { }
}


function routeFromHash() {
  const m = location.hash.match(/^#\/watch\/([^/]+)/);
  if (m) renderWatch(m[1], false);
  else renderHome();
}

$('#menuBtn').addEventListener('click', () => {
  if (innerWidth <= 900) $('#sidebar').classList.toggle('mobile-open');
  else document.body.classList.toggle('sidebar-collapsed');
});
$('#brandBtn').addEventListener('click', () => navigate('home'));
$('#downloadsNav').addEventListener('click', () => navigate('downloads'));
$$('.nav-item[data-view]').forEach(btn => btn.addEventListener('click', () => { $('#sidebar').classList.remove('mobile-open'); navigate(btn.dataset.view); }));
$('#searchForm').addEventListener('submit', e => {
  e.preventDefault();
  const q = $('#searchInput').value.trim().toLocaleLowerCase('pt-BR');
  if (!q) return renderLibrary();
  const items = state.media.filter(x => `${x.title} ${x.channel} ${x.description || ''} ${x.relativePath}`.toLocaleLowerCase('pt-BR').includes(q));
  renderLibrary(items, `Resultados para “${$('#searchInput').value.trim()}”`);
});
window.addEventListener('hashchange', routeFromHash);

(async function init() {
  try {
    await refreshData();
    routeFromHash();
    setTimeout(checkUpdateQuietly, 3000);
  } catch (e) {
    content.innerHTML = emptyState('Não foi possível carregar o YouTube Offline', e.message);
  }
})();
