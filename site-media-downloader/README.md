# Site Media Downloader

Aplicativo **Windows independente** para baixar vídeos e áudios a partir da URL da **página de um site** (não é necessário enviar URL direta do arquivo).

A interface permite colar a página, escolher vídeo MP4 ou somente áudio (original/MP3/M4A), definir a qualidade, escolher a pasta, baixar, cancelar e abrir o destino.

O motor utiliza o yt-dlp e FFmpeg empacotados, com até 11 métodos para vídeo e 10 para áudio, inclusive o extrator genérico, compatibilidade HTTP, seleção de formatos, HLS e conexões conservadoras. Se o extrator não identificar a mídia, faz descoberta limitada de fontes HTML5, metadados OpenGraph/JSON-LD e iframes públicos.

O aplicativo só acessa os links fornecidos durante o uso. Os arquivos ficam no computador. Não importa cookies nem tenta contornar CAPTCHA, DRM, paywall ou requisitos de login. Não é possível prometer que *qualquer* site seja suportado; sites podem usar criptografia, autenticação e proteções que impedem o download.

## Instalação

Baixe o Setup EXE da versão `site-media-v0.1.0`. Não substitui nem modifica o **YouTube Offline**.

Os arquivos são salvos inicialmente em `Vídeos\Site Media Downloader`, mas você pode selecionar outra pasta.

## Desenvolvimento

O código fica em `site-media-downloader/`; a compilação Windows e o instalador são gerados no workflow `.github/workflows/site-media-downloader.yml`.
