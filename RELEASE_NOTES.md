# YouTube Offline 0.5.6

- Corrige a atualização automática que falhava dizendo que o arquivo .download estava sendo usado por outro processo.
- O arquivo temporário agora é fechado antes da validação SHA-256 e da troca para o instalador final.
- Mantém a playlist como uma única tarefa na fila e preserva a proteção contra downloads repetidos.
