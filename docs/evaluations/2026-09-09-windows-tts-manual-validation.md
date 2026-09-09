# Validación manual — salida hablada Windows

Estado: **ejecutada y superada** (2026-09-09). Este documento es el runbook reproducible
y el registro de la ejecución real; no sustituye a las pruebas automatizadas.

## Ejecución

- Equipo: Windows 11 Pro 10.0.26200, Windows Terminal + PowerShell 5.1.
- Voz SAPI: `Microsoft Helena Desktop` (es-ES, habilitada); `Microsoft Zira Desktop`
  (en-US) también presente. Proveedor: Ollama local para las respuestas.
- Instalación ya bootstrapada; el `private-client.json` estaba en el formato anterior y
  migró al esquema 2 en el primer turno (credencial y `LastConversationId` conservados).
- Recorridos los pasos 1–7. Voz solicitada/efectiva distinguidas; `/rate` y `/volume`
  sobrevivieron al reinicio; `/mute` omite la síntesis y `/unmute` no reproduce solo;
  `/repeat` re-sintetiza sin HTTP; `/stop` corta la reproducción al instante sin cerrar
  el cliente ni marcar error; texto español y pegado correctos; resize y transcript
  largo sin desbordes; `/exit`, EOF y `Ctrl+C` restauran cursor y terminal sin colgarse;
  emoji no verbalizados; **ningún archivo `.wav`** en el directorio local antes ni
  después; con las voces SAPI deshabilitadas el cliente degrada a texto y los comandos
  de voz informan de indisponibilidad sin bloquear el chat.
- Sin procesos `testhost`, cliente ni API residuales tras cerrar.

Conclusión: incremento 7 verificado manualmente. Se marca el punto 7 del `ROADMAP.md`.

## Entorno requerido

- Windows Terminal y PowerShell.
- Una voz SAPI española habilitada.
- API LocalAssistant local iniciada y una credencial de prueba.
- Directorio local del cliente anotado antes de comenzar, sin incluir su ruta personal
  ni datos de la credencial en la evidencia.

## Secuencia

1. Abrir el cliente normal, ejecutar `/voice` y comprobar que identifica voz solicitada
   y efectiva.
2. Seleccionar una voz exacta, ejecutar `/rate 2` y `/volume 70`, reiniciar el cliente
   y confirmar que los valores sobreviven.
3. Enviar una respuesta final, ejecutar `/stop` durante la reproducción, después
   `/unmute`, `/mute` y `/repeat`.
4. Comprobar texto pegado, caracteres españoles, resize y transcript largo en la TUI.
5. Comprobar `/exit`, EOF y `Ctrl+C`, incluida la restauración de cursor y terminal.
6. Comparar el directorio local anterior y posterior para confirmar que no existen WAV
   ni otros archivos de audio.
7. Repetir sin voces SAPI habilitadas y confirmar degradación al modo textual.

## Evidencia a registrar tras la ejecución

- Versión de Windows, Windows Terminal y PowerShell.
- Fecha, comandos ejecutados y resultado de cada paso.
- Ausencia de secretos, texto conversacional, nombres completos del inventario de voces
  y rutas personales.

## Hallazgos durante la validación (corregidos y re-verificados)

- **`speech_playback_failed` en la primera reproducción.** La síntesis seleccionaba la
  voz correctamente (`Speech: ready`), pero `SoundPlayer` no podía reproducir el stream
  porque `SetOutputToAudioStream` escribe PCM sin cabecera. Corregido usando
  `SpeechSynthesizer.SetOutputToWaveStream` (WAV/RIFF completo).
- **El motor leía los emoji por su nombre** («cara sonriente», etc.), lo que rompe una
  conversación natural. Añadido `SpokenText.ForSpeech`: quita emoji y símbolos no
  verbalizables antes de sintetizar; el transcript no cambia.
- **`/stop` no interrumpía la reproducción.** `SoundPlayer.PlaySync` (`SND_SYNC`) no se
  puede detener desde otro hilo. Cambiado a `SoundPlayer.Play` asíncrono con la duración
  calculada de la cabecera RIFF; `Stop` se llama desde el hilo que cancela.

Los tres se re-verificaron en la misma sesión y quedan cubiertos por pruebas
automáticas nuevas (`SpokenText`, `WaveAudio`, mapeo transcript/voz).
