# YouTube Offline 0.5.9

- Volta a exibir cada vídeo ou playlist como uma única tarefa simples na fila, sem o bloco interno redundante.
- Mostra claramente se está na fila, preparando, baixando, cancelando, concluído ou com erro.
- Durante o download mostra porcentagem, MB/GB transferidos, tamanho total ou estimado, velocidade e tempo restante.
- Playlists continuam sendo baixadas por uma única execução do yt-dlp, como no fluxo antigo que funcionava.
- O instalador volta a incluir yt-dlp e FFmpeg para o download começar imediatamente após instalar/atualizar.
- O GitHub Actions passa a usar cache mensal dessas ferramentas para manter as próximas compilações rápidas.
