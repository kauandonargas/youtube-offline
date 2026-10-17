# Testes — YouTube Offline 0.5.0

## Atualização pelo GitHub

1. Execute `CONFIGURAR-GITHUB.cmd` uma única vez.
2. Confirme que foram criados `kauandonargas/youtube-offline` (privado) e `kauandonargas/youtube-offline-releases` (público).
3. Confirme no Actions do repositório privado que `Build e publicar atualização` concluiu.
4. No repositório público, confirme uma Release `v0.5.0` e o arquivo `update.json`.
5. Abra **Configurações > Atualizações** no app e use **Verificar agora**.
6. A 0.5.0 não deve oferecer atualização quando o feed também estiver em 0.5.0.
7. Publique uma versão maior com `PUBLICAR-ATUALIZACAO.cmd`.
8. Depois que o Actions terminar, abra o app antigo e confirme que a nova versão aparece.
9. Clique **Baixar e instalar**.
10. Confirme que o instalador é validado, o app fecha, atualiza e abre novamente.
11. Confirme que biblioteca, histórico, playlists e `download-archive.txt` continuam intactos.

## Playlist e duplicatas

1. Abra **Arquivos > Downloads**.
2. Adicione uma playlist que já tenha alguns vídeos concluídos.
3. Confirme que cada vídeo aparece como item separado.
4. Confirme que os já presentes no archive aparecem como **Já existe**.
5. Cancele alguns itens ainda em fila e o item atual.
6. Confirme que os cancelados não são baixados e a fila continua com os demais.

## Anti-bot

O app tenta primeiro sem cookies. Se o YouTube responder `Sign in to confirm you're not a bot`, a opção **Automático** tenta a sessão local do navegador configurado. Nenhuma senha do Google é solicitada ou salva.

## Instalador local

Execute `CRIAR-INSTALADOR.cmd` e confirme a geração de:

`dist\installer\YouTube-Offline-Setup-0.5.0-x64.exe`
