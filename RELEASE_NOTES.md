# YouTube Offline 0.5.17

- Impede que o aplicativo instale atualizações silenciosamente poucos segundos depois de abrir e feche a própria janela sem ação do usuário.
- A inicialização agora apenas verifica se existe atualização; a instalação continua disponível quando solicitada explicitamente.
- Define explicitamente a versão do assembly como 0.5.17.0 para evitar divergências entre a versão exibida pelo executável e a versão publicada no feed.
- Mantém as correções de download HTTP 403, yt-dlp nightly e Deno introduzidas na 0.5.16.
