(() => {
  state.downloadAnalysisUrl = state.downloadAnalysisUrl || '';
  state.downloadUiMeta = state.downloadUiMeta || {};
  state.downloadCookieBrowser = 'none';

  const activeStatuses = new Set(['queued', 'preparing', 'downloading', 'cancelling']);

  function statusText(status, progress = 0) {
    switch (status) {
      case 'queued': return 'Na fila';
      case 'preparing': return 'Preparando';
      case 'downloading': return `${Math.round(progress || 0)}%`;
      case 'partial': return 'Parcial';
      case 'cancelling': return 'Cancelando…';
      case 'completed': return 'Concluído';
      case 'cancelled': return 'Cancelado';
      case 'failed': return 'Falhou';
      case 'skipped': return 'Ignorado';
      default: return status || '';
    }
  }

  function renderAnalysis(a) {
    if (!a) {
      return `<div class="download-idle">
        <div class="download-idle-icon" aria-hidden="true">↓</div>
        <div><strong>Cole um link para começar</strong><span>Vídeo, áudio ou playlist do YouTube.</span></div>
      </div>`;
    }

    const entries = a.isPlaylist && a.entries?.length
      ? `<div class="download-preview-list">
          ${a.entries.slice(0, 5).map((item, index) => `<div class="download-preview-row">
            <span>${String(item.index || index + 1).padStart(2, '0')}</span>
            <strong>${escapeHtml(item.title || 'Vídeo')}</strong>
            <small>${item.duration ? fmtDuration(item.duration) : ''}</small>
          </div>`).join('')}
          ${a.itemCount > 5 ? `<div class="download-preview-more">+ ${a.itemCount - 5} vídeos</div>` : ''}
        </div>`
      : '';

    return `<div class="download-analysis">
      <div class="download-analysis-main">
        <div class="download-analysis-thumb">
          ${a.thumbnail ? `<img src="${escapeHtml(a.thumbnail)}" alt="" />` : '<div class="download-analysis-fallback">▶</div>'}
        </div>
        <div class="download-analysis-copy">
          <div class="download-type-badge">${a.isPlaylist ? 'PLAYLIST' : 'VÍDEO'}</div>
          <h3>${escapeHtml(a.title || (a.isPlaylist ? 'Playlist' : 'Vídeo'))}</h3>
          <p>${escapeHtml(a.channel || 'YouTube')}</p>
          <div class="download-analysis-meta">
            ${a.isPlaylist ? `<span>${a.itemCount || 0} vídeos</span><span>1 tarefa contínua</span><span>ordem preservada</span>` : `<span>${a.duration ? fmtDuration(a.duration) : 'Duração não informada'}</span>`}
          </div>
        </div>
      </div>
      ${entries}
    </div>`;
  }

  renderDownloads = function () {
    updateNav('downloads');
    state.view = 'downloads';
    const a = state.downloadAnalysis;

    content.innerHTML = `
      <div class="page-head downloads-head">
        <div><h1>Downloads</h1><p>Adicione conteúdo à biblioteca usando o fluxo estável da v0.4.</p></div>
      </div>
      <div class="downloads-layout">
        <section class="download-create-card">
          <div class="download-card-head">
            <div><span class="download-kicker">NOVO DOWNLOAD</span><h2>Adicionar à biblioteca</h2></div>
          </div>

          <div class="download-url-block">
            <label for="downloadUrl">Link do YouTube</label>
            <div class="download-url-row">
              <input id="downloadUrl" type="url" inputmode="url" autocomplete="off" placeholder="https://youtube.com/watch?v=... ou /playlist?list=..." />
              <button class="secondary" id="analyzeBtn">Analisar</button>
            </div>
            <small>Primeiro identificamos se o link é um vídeo ou uma playlist. Playlists são entregues ao yt-dlp em uma única execução.</small>
          </div>

          ${renderAnalysis(a)}

          ${a ? `<div class="download-options">
            <div class="field"><label>Formato</label><select id="downloadMode"><option value="video">Vídeo</option><option value="audio">Somente áudio</option></select></div>
            <div class="field" id="downloadQualityWrap"><label>Qualidade</label><select id="downloadQuality"><option value="best">Melhor disponível</option>${(a.qualities || []).map(q => `<option value="${q}">${q >= 2160 ? `${q}p${q === 2160 ? ' / 4K' : q === 4320 ? ' / 8K' : ''}` : `${q}p`}</option>`).join('')}</select></div>
            <div class="field hidden" id="audioFormatWrap"><label>Formato de áudio</label><select id="audioFormat"><option value="original">Original</option><option value="mp3">MP3</option><option value="m4a">M4A</option><option value="opus">Opus</option></select></div>
          </div>
          ${a.isPlaylist ? `<div class="download-playlist-note"><strong>Playlist detectada</strong><span>Os ${a.itemCount || 'vários'} vídeos serão processados pela mesma execução do yt-dlp e salvos em uma pasta própria, mantendo o índice original.</span></div>` : ''}
          <button class="primary download-start" id="startDownload">${a.isPlaylist ? `Baixar playlist (${a.itemCount || 'todos'})` : 'Baixar para a biblioteca'}</button>` : ''}
        </section>

        <section class="download-activity-card">
          <div class="download-card-head">
            <div><span class="download-kicker">ATIVIDADE</span><h2>Downloads desta sessão</h2></div>
            <span class="download-activity-hint">Uma linha por ação</span>
          </div>
          <div id="downloadJobs"></div>
        </section>
      </div>`;

    const input = $('#downloadUrl');
    if (input) input.value = state.downloadAnalysisUrl || '';

    $('#analyzeBtn')?.addEventListener('click', analyzeDownload);
    input?.addEventListener('keydown', event => {
      if (event.key === 'Enter') {
        event.preventDefault();
        analyzeDownload();
      }
    });
    $('#downloadMode')?.addEventListener('change', event => {
      const audio = event.target.value === 'audio';
      $('#audioFormatWrap')?.classList.toggle('hidden', !audio);
      $('#downloadQualityWrap')?.classList.toggle('hidden', audio);
    });
    $('#startDownload')?.addEventListener('click', startDownload);

    renderJobs();
    pollJobs();
  };

  analyzeDownload = async function () {
    const input = $('#downloadUrl');
    const url = input?.value?.trim();
    if (!url) return;

    const btn = $('#analyzeBtn');
    if (btn) {
      btn.disabled = true;
      btn.textContent = 'Analisando…';
    }

    try {
      const analysis = await api('/api/download/analyze', {
        method: 'POST',
        body: { url, cookieBrowser: 'none' }
      });
      state.downloadAnalysis = analysis;
      state.downloadAnalysisUrl = url;
      renderDownloads();
    } catch (error) {
      toast(error.message);
      if (btn) {
        btn.disabled = false;
        btn.textContent = 'Analisar';
      }
    }
  };

  startDownload = async function () {
    const url = $('#downloadUrl')?.value?.trim();
    const analysis = state.downloadAnalysis;
    if (!url || !analysis) return;

    if (url !== state.downloadAnalysisUrl) {
      toast('O link mudou. Analise novamente antes de iniciar o download.');
      return;
    }

    const btn = $('#startDownload');
    if (btn) {
      btn.disabled = true;
      btn.textContent = analysis.isPlaylist ? 'Iniciando playlist…' : 'Iniciando…';
    }

    try {
      const job = await api('/api/download/start', {
        method: 'POST',
        body: {
          url,
          mode: $('#downloadMode')?.value || 'video',
          quality: $('#downloadQuality')?.value || 'best',
          audioFormat: $('#audioFormat')?.value || 'original',
          playlist: !!analysis.isPlaylist,
          cookieBrowser: 'none'
        }
      });

      if (job?.id) {
        state.downloadUiMeta[job.id] = {
          title: analysis.title || (analysis.isPlaylist ? 'Playlist' : 'Vídeo'),
          channel: analysis.channel || '',
          isPlaylist: !!analysis.isPlaylist,
          itemCount: analysis.itemCount || 1
        };
      }

      toast(analysis.isPlaylist ? 'Playlist adicionada aos downloads.' : 'Download iniciado.');
      state.downloadAnalysis = null;
      state.downloadAnalysisUrl = '';
      renderDownloads();
    } catch (error) {
      toast(error.message);
      if (btn) {
        btn.disabled = false;
        btn.textContent = 'Tentar novamente';
      }
    }
  };

  cancelDownloadItem = async function (id) {
    try {
      await api(`/api/download/items/${id}/cancel`, { method: 'POST' });
      toast('Cancelamento solicitado.');
      await renderJobs();
    } catch (error) {
      toast(error.message);
    }
  };

  renderJobs = async function () {
    const target = $('#downloadJobs');
    if (!target) return;

    try {
      const jobs = await api('/api/download/jobs');
      if (!jobs?.length) {
        target.innerHTML = `<div class="download-activity-empty"><strong>Nenhum download nesta sessão</strong><span>Quando você iniciar um download, o progresso aparecerá aqui.</span></div>`;
        return;
      }

      target.innerHTML = jobs.slice(0, 8).map(job => {
        const item = (job.items || [])[0] || {};
        const meta = state.downloadUiMeta[job.id] || {};
        const progress = Math.max(0, Math.min(100, Number(job.progress || item.progress || 0)));
        const status = job.status || item.status || 'queued';
        const title = meta.title || (job.isPlaylist ? 'Playlist' : item.title || job.title || 'Download');
        const position = job.isPlaylist && item.playlistTotal > 0 && item.playlistIndex > 0
          ? `Vídeo ${item.playlistIndex} de ${item.playlistTotal}`
          : '';
        const currentTitle = job.isPlaylist && item.title && item.title !== 'Playlist' && item.title !== job.url
          ? item.title
          : '';
        const strategy = item.strategy && activeStatuses.has(status) ? `Método ${item.attempt || 1}/${item.attemptTotal || 1}: ${item.strategy}` : '';
        const detail = [strategy, item.error || [position, item.speed, item.eta ? `restam ${item.eta}` : ''].filter(Boolean).join(' • ')].filter(Boolean).join(' • ');
        const cancellable = activeStatuses.has(item.status || status);

        return `<article class="download-job-card ${escapeHtml(status)}">
          <div class="download-job-main">
            <div class="download-job-icon" aria-hidden="true">${job.isPlaylist ? '▤' : '↓'}</div>
            <div class="download-job-copy">
              <div class="download-job-title-row">
                <strong title="${escapeHtml(title)}">${escapeHtml(title)}</strong>
                <span class="download-job-status ${escapeHtml(status)}">${escapeHtml(statusText(status, progress))}</span>
              </div>
              <div class="download-job-subtitle">${escapeHtml(currentTitle || detail || (status === 'queued' ? 'Aguardando início' : job.isPlaylist ? 'Processando playlist em uma única tarefa' : 'Processando download'))}</div>
              ${currentTitle && detail ? `<div class="download-job-detail">${escapeHtml(detail)}</div>` : ''}
              <div class="download-job-progress"><span style="width:${progress}%"></span></div>
            </div>
            ${cancellable && item.id && item.status !== 'cancelling' ? `<button class="download-cancel-btn" data-cancel-download="${item.id}" title="Cancelar download" aria-label="Cancelar download">×</button>` : ''}
          </div>
        </article>`;
      }).join('');

      $$('[data-cancel-download]', target).forEach(button => {
        button.addEventListener('click', () => cancelDownloadItem(button.dataset.cancelDownload));
      });

      const completionSignature = jobs
        .flatMap(job => (job.items || []).filter(item => item.status === 'completed').map(item => item.id))
        .sort()
        .join('|');

      if (completionSignature && completionSignature !== state.downloadCompletionSignature) {
        state.downloadCompletionSignature = completionSignature;
        await api('/api/media/rescan', { method: 'POST' });
        await refreshData();
      }
    } catch { }
  };
})();
