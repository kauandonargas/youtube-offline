# YouTube Offline 0.5.12

- Restaura o motor de download da 0.5.4, a versão que baixava a playlist dividindo cada vídeo em uma tarefa própria.
- Volta a usar YtDlpService e remove o DownloadService novo que introduziu a regressão.
- Restaura a interface antiga da fila de downloads.
- Reativa o modo automático de sessão do navegador uma vez para instalações que foram forçadas para modo anônimo pela 0.5.10.
- Mantém as correções do atualizador e do processo de build fora do motor de download.
