# YouTube Offline 0.5.14

- Mantém o motor de playlists restaurado da v0.4: uma playlist inteira é processada por uma única execução do yt-dlp.
- Substitui a tela de downloads remendada por uma interface dedicada e coerente com o funcionamento real do backend.
- Remove o `queue-fixes.js` e as sobrescritas em runtime que dificultavam manutenção e diagnóstico.
- Mostra playlists como uma única tarefa com progresso geral, vídeo atual, velocidade e ETA quando disponíveis.
- Remove da interface o fluxo de cookies do navegador que não é usado pelo motor restaurado.
- Impede iniciar um download usando uma análise antiga depois que o link foi alterado.
- Alinha os modelos internos para não voltar ao desenho regressivo de um processo por vídeo da playlist.
