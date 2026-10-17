(() => {
  const hiddenDownloadItems = new Set();
  const terminalStatuses = new Set(['completed', 'cancelled', 'skipped', 'failed']);

  const style = document.createElement('style');
  style.textContent = `
    .download-task {
      padding: 18px 20px;
      border: 1px solid #303030;
      border-radius: 14px;
      background: #181818;
      margin-bottom: 12px;
    }
    .download-task-head {
      display: flex;
      align-items: flex-start;
      gap: 14px;
    }
    .download-task-main {
      flex: 1;
      min-width: 0;
    }
    .download-task-title-row {
      display: flex;
      align-items: center;
      justify-content: space-between;
      gap: 12px;
    }
    .download-task-title {
      margin: 0;
      font-size: 16px;
      font-weight: 700;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .download-task-status {
      font-size: 13px;
      font-weight: 700;
      white-space: nowrap;
    }
    .download-task-status.downloading { color: #3ea6ff; }
    .download-task-status.preparing,
    .download-task-status.queued { color: #aaa; }
    .download-task-status.failed { color: #ff6b6b; }
    .download-task-status.completed { color: #46d17a; }
    .download-task-current {
      margin-top: 5px;
      color: #d8d8d8;
      font-size: 13px;
      white-space: nowrap;
      overflow: hidden;
      text-overflow: ellipsis;
    }
    .download-task-progress {
      margin: 13px 0 8px;
      overflow: hidden;
    }
    .download-task-progress.pending span {
      width: 34% !important;
      animation: download-pulse 1.15s ease-in-out infinite alternate;
    }
    @keyframes download-pulse {
      from { transform: translateX(-25%); opacity: .45; }
      to { transform: translateX(225%); opacity: 1; }
    }
    .download-task-meta {
      display: flex;
      gap: 8px 14px;
      flex-wrap: wrap;
      align-items: center;
      color: #aaa;
      font-size: 12px;
    }
    .download-task-meta strong {
      color: #f1f1f1;
      font-weight: 600;
    }
    .download-task-error {
      color: #ff8a8a;
    }
    .cancel-download.cancel-download-x {
      width: 32px;
      height: 32px;
      min-width: 32px;
      padding: 0;
      border-radius: 999px;
      display: inline-grid;
      place-items: center;
      font-size: 22px;
      line-height: 1;
      font-weight: 400;
      margin-top: 1px;
    }
  `;
  document.head.appendChild(style);

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

  function formatTransferBytes(bytes) {
    let value = Number(bytes || 0);
    if (!Number.isFinite(value) || value <= 0) return '';
    const units = ['B', 'KB', 'MB', 'GB', 'TB'];
    let unit = 0;
    while (value >= 1024 && unit < units.length - 1) {
      value /= 1024;
      unit++;
    }
    const digits = unit === 0 || value >= 100 ? 0 : 1;
    return `${value.toFixed(digits)} ${units[unit]}`;
  }

  function statusText(item, job) {
    switch (item.status) {
      case 'queued': return 'Na fila';
      case 'preparing': return 'Preparando download…';
      case 'downloading': return 'Baixando agora';
      case 'cancelling': return 'Cancelando…';
      case 'completed': return 'Concluído';
      case 'skipped': return 'Já baixado';
      case 'cancelled': return 'Cancelado';
      case 'failed': return 'Erro';
      default: return item.status || (job.status || 'Aguardando');
    }
  }

  function taskSubtitle(item, job) {
    if (item.error) return item.error;
    if (item.status === 'preparing') return 'Preparando o download…';
    if (item.status === 'queued') return 'Aguardando a tarefa anterior terminar.';
    if (item.status === 'downloading') {
      if (Number(item.progress || 0) <= 0 && !Number(item.downloadedBytes || 0)) return 'Conectando ao YouTube…';
      if (job.isPlaylist) {
        const total = Number(item.playlistTotal || 0);
        const index = Number(item.playlistIndex || 0);
        if (index > 0 && total > 0) {
          const remaining = Math.max(0, total - index);
          return `Vídeo ${index} de ${total} • ${remaining} ${remaining === 1 ? 'vídeo faltando' : 'vídeos faltando'}`;
        }
        if (total > 0) return `Playlist com ${total} vídeos`;
        return 'Playlist em andamento…';
      }
      return 'Download em andamento…';
    }
    return job.isPlaylist && Number(item.playlistTotal || 0) > 0
      ? `${item.playlistTotal} vídeos na playlist`
      : '';
  }

  const originalAnalyzeDownload = analyzeDownload;
  analyzeDownload = async function () {
    await hideFinishedQueueHistory();
    state.downloadCompletionSignature = '';
    return originalAnalyzeDownload();
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
      const visible = (jobs || [])
        .map(job => ({ ...job, item: (job.items || []).find(item => !hiddenDownloadItems.has(item.id)) }))
        .filter(job => job.item);

      if (!visible.length) {
        target.innerHTML = emptyState('Nenhum download na fila', 'Cole um link acima para adicionar vídeos ou playlists.');
        return;
      }

      target.innerHTML = visible.map(job => {
        const item = job.item;
        const progress = Math.max(0, Math.min(100, Number(item.progress ?? job.progress ?? 0)));
        const downloaded = formatTransferBytes(item.downloadedBytes);
        const total = formatTransferBytes(item.totalBytes);
        const sizeText = downloaded
          ? (total ? `${downloaded} / ${item.totalBytesEstimated ? '≈ ' : ''}${total}` : `${downloaded} baixados`)
          : (total ? `${item.totalBytesEstimated ? '≈ ' : ''}${total} total` : '');
        const subtitle = taskSubtitle(item, job);
        const currentTitle = job.isPlaylist && item.status === 'downloading' && item.title && item.title !== job.title
          ? item.title
          : '';
        const cancellable = ['queued', 'preparing', 'downloading', 'cancelling'].includes(item.status);
        const playlistTotal = Number(item.playlistTotal || 0);
        const playlistIndex = Number(item.playlistIndex || 0);
        const remaining = job.isPlaylist && playlistTotal > 0 && playlistIndex > 0
          ? Math.max(0, playlistTotal - playlistIndex)
          : null;
        const remainingText = remaining === null
          ? ''
          : `${remaining} ${remaining === 1 ? 'vídeo faltando' : 'vídeos faltando'}`;
        const meta = [
          `<strong>${Math.round(progress)}%</strong>`,
          sizeText,
          item.speed || '',
          remainingText
        ].filter(Boolean).join('<span>•</span>');

        return `<article class="download-task">
          <div class="download-task-head">
            <div class="download-task-main">
              <div class="download-task-title-row">
                <h3 class="download-task-title" title="${escapeHtml(job.title || 'Download')}">${escapeHtml(job.title || (job.isPlaylist ? 'Playlist' : 'Download'))}</h3>
                <span class="download-task-status ${escapeHtml(item.status)}">${escapeHtml(statusText(item, job))}</span>
              </div>
              ${currentTitle ? `<div class="download-task-current" title="${escapeHtml(currentTitle)}">${escapeHtml(currentTitle)}</div>` : ''}
              <div class="bar download-task-progress${(item.status === 'preparing' || (item.status === 'downloading' && progress <= 0)) ? ' pending' : ''}"><span style="width:${progress}%"></span></div>
              <div class="download-task-meta ${item.error ? 'download-task-error' : ''}">
                ${meta || '<span>0%</span>'}
                ${subtitle ? `<span>•</span><span>${escapeHtml(subtitle)}</span>` : ''}
              </div>
            </div>
            ${cancellable ? `<button class="cancel-download cancel-download-x" data-cancel-download="${item.id}" title="Cancelar" aria-label="Cancelar">×</button>` : ''}
          </div>
        </article>`;
      }).join('');

      $$('[data-cancel-download]', target).forEach(btn => btn.addEventListener('click', () => cancelDownloadItem(btn.dataset.cancelDownload)));

      const completionSignature = visible
        .filter(job => job.item.status === 'completed')
        .map(job => job.item.id)
        .sort()
        .join('|');

      if (completionSignature && completionSignature !== state.downloadCompletionSignature) {
        state.downloadCompletionSignature = completionSignature;
        await api('/api/media/rescan', { method: 'POST' });
        await refreshData();
      }
    } catch { }
  };

  const originalRenderDownloads0510 = renderDownloads;
  renderDownloads = function () {
    state.downloadCookieBrowser = 'none';
    localStorage.setItem('youtubeOfflineCookieBrowser', 'none');
    originalRenderDownloads0510();

    $('.download-auth-field')?.remove();

    const hint = $('.download-hint');
    if (hint) hint.textContent = 'Cole uma playlist e baixe. O app evita repetir vídeos que já foram concluídos.';

    if (state.downloadAnalysis?.isPlaylist) {
      const detected = $('.playlist-detected');
      if (detected) {
        const strong = $('strong', detected);
        const text = $('span', detected);
        if (strong) strong.textContent = 'Playlist detectada.';
        if (text) text.textContent = 'A playlist inteira será baixada como uma única tarefa.';
      }
      const start = $('#startDownload');
      if (start) start.textContent = 'Baixar playlist';
    }
  };

})();
