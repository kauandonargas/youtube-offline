# YouTube Offline 0.5.5

- Playlists voltam a ocupar apenas um lugar na fila de downloads.
- Cada playlist passa a ser processada por uma unica execucao do yt-dlp, evitando reabrir a playlist para cada video.
- Mantem a fila entre videos e playlists, mas remove a fila interna por video dentro de uma playlist.
- O modo automatico antigo deixa de acessar cookies de Chrome ou Edge; o comportamento padrao volta a ser anonimo.
- O historico local de downloads continua evitando baixar novamente videos ja concluidos.
