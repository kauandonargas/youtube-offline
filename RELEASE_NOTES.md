# YouTube Offline 0.5.21

- Motor de diagnóstico compartilhado com o Site Media Downloader: diferencia falha de formato, rede, HTTP 403, Cloudflare, CAPTCHA, autenticação, limite 429 e DRM.
- A análise e o download agora aceitam opcionalmente uma sessão autorizada do Edge, Chrome, Firefox ou Brave; o padrão continua sem cookies.
- Abre o link no navegador padrão para permitir verificações manuais legítimas.
- Não declara sucesso quando um arquivo novo não foi confirmado no disco; playlists preservam o controle dos itens concluídos.
- Mantém o sistema existente de recuperação, cancelamento, formatos e atualização.
- Não desbloqueia vídeos protegidos, conteúdo pago sem acesso ou desafios CAPTCHA de forma automática.
- Inclui novos testes automatizados das decisões de diagnóstico.
