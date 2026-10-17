# YouTube Offline

Aplicativo desktop para organizar e assistir videos locais com interface inspirada no YouTube e downloader integrado.

## Atualizacoes

O repositorio e publico e usa GitHub Actions para compilar e publicar cada nova versao no proprio GitHub Releases. O aplicativo consulta `update.json` em `main`, baixa o instalador da nova versao, valida o SHA-256 e instala automaticamente. Nenhum login no GitHub e necessario para o usuario.

## Desenvolvimento

- Windows 10/11 x64
- .NET 8
- WebView2
- yt-dlp + FFmpeg para downloads
- Inno Setup para o instalador
