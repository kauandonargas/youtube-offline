# YouTube Offline 0.5.16

- Corrige falhas `HTTP Error 403: Forbidden` renovando o yt-dlp e reextraindo a URL de mídia antes de desistir do download.
- Passa a usar o canal nightly do yt-dlp e mantém uma cópia atualizável no diretório de dados do aplicativo.
- Inclui Deno e o fornece explicitamente ao yt-dlp para os desafios JavaScript atuais do YouTube.
- Evita empacotar uma cópia antiga do yt-dlp vinda do cache mensal do build.
- Adiciona retentativas de rede e isola as execuções de configurações globais externas do yt-dlp.
