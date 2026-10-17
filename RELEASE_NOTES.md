# YouTube Offline 0.5.10

- Corrige o motor de download e a interface ao mesmo tempo.
- O download nao faz uma segunda analise do link antes de iniciar; se a analise da tela ja existe, ela e reaproveitada.
- Playlists continuam como uma unica tarefa e uma unica execucao do yt-dlp.
- Desativa totalmente o acesso automatico a cookies de navegadores.
- A fila mostra claramente Preparando, Baixando agora, porcentagem, MB/GB, tamanho, velocidade e tempo restante.
- Enquanto ainda nao chegaram bytes, a barra fica animada e informa que o yt-dlp esta conectado e aguardando dados do YouTube.
- Em playlists, o titulo e o numero do video atual passam a ser atualizados pelo proprio progresso do yt-dlp.
