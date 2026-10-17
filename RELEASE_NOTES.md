# YouTube Offline 0.5.19

- Corrige a falha do Restart Manager ao tentar fechar outros programas que mantêm o yt-dlp.exe em uso durante a atualização.
- Restringe a detecção de aplicativos a fechar aos arquivos executáveis e bibliotecas do YouTube Offline, preservando ferramentas externas em execução.
- Elimina definitivamente a instalação automática na inicialização: o programa apenas verifica atualizações e não fecha sem solicitação.
- Adiciona verificações reais no Windows: abertura e permanência do processo, API local, métricas de CPU/memória e reinstalação com yt-dlp bloqueado.
- Bloqueia a publicação se o teste de reinstalação com ferramenta bloqueada falhar.
- Mantém o mecanismo de downloads e as demais funcionalidades da 0.5.18.
