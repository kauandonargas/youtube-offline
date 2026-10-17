# YouTube Offline 0.5.13

- Restaura o fluxo de download da versão 0.4.0, recuperado do arquivo original `YouTube-Offline-v0.4.0.zip` criado antes das regressões posteriores.
- Playlist volta a ser entregue ao yt-dlp em uma única execução com `--yes-playlist`, `--ignore-errors` e `--continue`.
- Remove do motor restaurado a divisão da playlist em uma tarefa/processo por vídeo e as retentativas automáticas com cookies do navegador.
- A análise volta a usar primeiro `--dump-single-json --flat-playlist` e só faz inspeção completa para vídeo individual.
- Mantém apenas a camada atual de interface, instalador e atualização automática ao redor do motor antigo.
