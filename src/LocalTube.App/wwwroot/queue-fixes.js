(() => {
  const hiddenDownloadItems = new Set();
  const terminalStatuses = new Set(['completed', 'cancelled', 'skipped', 'failed']);

  // A v0.4.0 funcionava sem injetar cookies do navegador. A interface atual
  // continua aceitando o campo por compatibilidade, mas ele fica desativado/oculto.
  try {
    localStorage.setItem('youtubeOfflineCookieBrowser', 'none');
    if (typeof state !== 'undefined') state.downloadCookieBrowser = 'none';
  } catch { }

  const style = document.createElement('style');
  style.textContent = `
    .cancel-download.cancel-download-x {
      width: 30px;
      height: 30px;
      min-width: 30px;
      padding: 0;
      border-radius: 999px;
      display: inline-grid;
      place-items: center;
      font-size: 22px;
      line-height: 1;
      font-weight: 400;
    }
    .download-auth-field { display: none !important; }
  `;
  document.head.appendChild(style);

  function applyRestored040Ui() {
    const hint = $('.download-hint');
    if (hint) hint.textContent = 'Fluxo restaurado da v0.4.0: uma playlist inteira usa uma unica execucao do yt-dlp.';

    const detected = $('.playlist-detected span');
    if (detected) detected.textContent = 'A playlist sera baixada inteira em uma unica execucao, mantendo a ordem dos videos.';

    const start = $('#startDownload');
    if (start && state?.downloadAnalysis?.isPlaylist)
      start.textContent = `Baixar ${state.downloadAnalysis.itemCount || 'todos os'} videos`;
  }

  const observer = new MutationObserver(applyRestored040Ui);
  if (typeof content !== 'undefined' && content)
    observer.observe(content, { childList: true, subtree: true });

  async function hideFinishedQueueHistory() {
    try {
      const jobs = await api('/api/download/jobs');
      for (const job of jobs || []) {
        for (const item of job.items || []) {
          if (terminalStatuses.has(item.status)) hiddenDownloadItems.add(item.id);
        }
      }
    } catch { }
  }

  function visibleBatchSummary(job, items) {
    if (job.isPlaylist) {
      const item = items[0];
      if (item?.status === 'completed') return `${job.totalItems || item.playlistTotal || 0} videos concluidos`;
      if (item?.status === 'failed') return 'Playlist com erro';
      if (item?.status === 'cancelled') return 'Playlist cancelada';
      if (item?.playlistTotal > 0 && item?.playlistIndex > 0)
        return `Video ${item.playlistIndex} de ${item.playlistTotal}`;
      return 'Playlist em uma unica tarefa';
    }

    const completed = items.filter(x => x.status === 'completed').length;
    const queued = items.filter(x => x.status === 'queued').length;
    const skipped = items.filter(x => x.status === 'skipped').length;
    const cancelled = items.filter(x => x.status === 'cancelled').length;
    const failed = items.filter(x => x.status === 'failed').length;
    const parts = [];
    if (completed) parts.push(`${completed} concluido${completed === 1 ? '' : 's'}`);
    if (queued) parts.push(`${queued} na fila`);
    if (skipped) parts.push(`${skipped} ja existente${skipped === 1 ? '' : 's'}`);
    if (cancelled) parts.push(`${cancelled} cancelado${cancelled === 1 ? '' : 's'}`);
    if (failed) parts.push(`${failed} com erro`);
    return parts.join(' • ') || `${items.length} item(ns)`;
  }

  const originalAnalyzeDownload = analyzeDownload;
  analyzeDownload = async function () {
    await hideFinishedQueueHistory();
    state.downloadCompletionSignature = '';
    state.downloadCookieBrowser = 'none';
    const result = await originalAnalyzeDownload();
    applyRestored040Ui();
    return result;
  };

  cancelDownloadItem = async function (id) {
    hiddenDownloadItems.add(id);
    await renderJobs();
    try {
      await api(`/api/download/items/${id}/cancel`, { method: 'POST' });
    } catch (e) {
      hiddenDownloadItems.delete(id);
      await renderJobs();
      toast(e.message);
    }
  };

  renderJobs = async function () {
    const target = $('#downloadJobs');
    if (!target) return;

    try {
      const jobs = await api('/api/download/jobs');
      const visibleJobs = (jobs || [])
        .map(job => ({ ...job, visibleItems: (job.items || []).filter(item => !hiddenDownloadItems.has(item.id)) }))
        .filter(job => job.visibleItems.length > 0);

      if (!visibleJobs.length) {
        target.innerHTML = emptyState('Nenhum download na fila', 'Cole um link acima para adicionar videos.');
        return;
      }

      target.innerHTML = visibleJobs.map(job => {
        const items = job.visibleItems;
        const progress = Number(job.progress || 0);

        return `<article class="download-batch">
          <header class="download-batch-head">
            <div><h3>${escapeHtml(job.title || (job.isPlaylist ? 'Playlist' : 'Download'))}</h3><p>${escapeHtml(visibleBatchSummary(job, items))}</p></div>
            <div class="batch-progress-text">${Math.round(progress)}%</div>
          </header>
          <div class="bar batch-bar"><span style="width:${Math.max(0, Math.min(100, progress))}%"></span></div>
          <div class="download-items">
            ${items.map(item => {
              const cancellable = ['queued', 'preparing', 'downloading', 'cancelling'].includes(item.status);
              const index = item.playlistIndex > 0 ? `<span class="download-index">${String(item.playlistIndex).padStart(2, '0')}</span>` : '';
              const playlistProgress = job.isPlaylist && item.playlistTotal > 0 && item.playlistIndex > 0
                ? `Video ${item.playlistIndex} de ${item.playlistTotal}`
                : '';
              const detail = item.error || [playlistProgress, item.speed, item.eta ? `restam ${item.eta}` : ''].filter(Boolean).join(' • ');
              const itemProgress = job.isPlaylist ? progress : Number(item.progress || 0);
              return `<div class="download-item ${escapeHtml(item.status)}">
                ${index}
                <div class="download-item-main">
                  <div class="download-item-top"><strong title="${escapeHtml(item.title)}">${escapeHtml(item.title || (job.isPlaylist ? 'Playlist' : 'Video'))}</strong><span class="status-pill ${escapeHtml(item.status)}">${escapeHtml(downloadStatusLabel(item))}</span></div>
                  <div class="bar item-bar"><span style="width:${Math.max(0, Math.min(100, itemProgress))}%"></span></div>
                  <div class="download-item-bottom"><span>${escapeHtml(detail || (item.status === 'queued' ? 'Aguardando inicio' : ''))}</span>${cancellable ? `<button class="cancel-download cancel-download-x" data-cancel-download="${item.id}" title="Cancelar e remover" aria-label="Cancelar e remover">×</button>` : ''}</div>
                </div>
              </div>`;
            }).join('')}
          </div>
        </article>`;
      }).join('');

      $$('[data-cancel-download]', target).forEach(btn => btn.addEventListener('click', () => cancelDownloadItem(btn.dataset.cancelDownload)));

      const completionSignature = visibleJobs
        .flatMap(job => job.visibleItems.filter(x => x.status === 'completed').map(x => x.id))
        .sort()
        .join('|');

      if (completionSignature && completionSignature !== state.downloadCompletionSignature) {
        state.downloadCompletionSignature = completionSignature;
        await api('/api/media/rescan', { method: 'POST' });
        await refreshData();
      }
    } catch { }
  };

  applyRestored040Ui();
})();
