# YouTube Offline 0.5.15

- Corrige a análise de playlists e vídeos quando o yt-dlp retorna campos JSON nulos, como `duration` ou `height`.
- Evita a exceção `The requested operation requires an element of type 'Number', but the target element has type 'Null'.`
- Torna a leitura dos metadados mais tolerante a entradas indisponíveis ou metadados incompletos sem alterar o motor de download restaurado da v0.4.
