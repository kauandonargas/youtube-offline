# YouTube Offline 0.5.18

- Corrige o erro do instalador `DeleteFile failed; código 5. Acesso negado` ao atualizar com `yt-dlp.exe`, Deno ou FFmpeg ainda em uso.
- As ferramentas externas passam a ser tratadas como seed de primeira instalação: em atualizações, o instalador não tenta sobrescrever executáveis já existentes em `tools`.
- O yt-dlp operacional continua sendo atualizado pelo próprio aplicativo no diretório de dados do usuário, sem depender da cópia empacotada.
- Mantém a correção da 0.5.17 que impede fechamento automático da janela durante a verificação de atualização.
- Mantém as correções de download HTTP 403, yt-dlp nightly e Deno.
