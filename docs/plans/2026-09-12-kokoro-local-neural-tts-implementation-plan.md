# Plan de implementación: Kokoro como proveedor neuronal local de TTS

## Estado

Finalizado (2026-09-12). Implementa docs/specs/2026-09-12-kokoro-local-neural-tts-design.md. No reabre la fase 5, no inicia la fase 6 y no modifica la API, el orquestador ni contratos de conversación. Los seis lotes están completos: el hash-locking de requirements-prod.lock/requirements-test.lock (lote 6, punto 7) se verificó instalando ambos con --require-hashes en un entorno limpio, y el smoke offline con firewall real más el runbook manual en Windows (lote 6, punto 6/8) se ejecutaron y superaron.

## Límites

- El servicio Python es manual e independiente; el cliente no lo inicia, instala, actualiza, reinicia ni detiene.
- SAPI se conserva como proveedor y fallback explícito.
- No se añaden WAV, pesos, entornos virtuales, cachés, secretos ni rutas personales al repositorio.
- La suite .NET común no requiere Python, Kokoro, pesos, Ollama, GPU ni Internet.
- eSpeak NG y phonemizer-fork son GPL-3.0-or-later: no se empaquetan con la publicación MIT del cliente sin revisión legal posterior.

## Dependencias evaluadas y bloqueo

La evaluación real de D:\IA\Kokoro utilizó Python 3.11.9, kokoro 0.9.4,
torch 2.14.0+cpu, numpy 2.4.6, misaki 0.9.4, espeakng-loader 0.2.4 y
phonemizer-fork 3.3.2. Se conservan esas versiones.

El servicio añade aiohttp 3.14.3, ausente de la evaluación. Tras instalarlo se realizará una validación abreviada: modo offline, pesos ya preparados, CPU confirmada, una síntesis em_alex válida y ausencia de intento de red. Si cambia una dependencia evaluada, se repite y registra esa validación.

Los requisitos directos de producción son aiohttp, kokoro, torch y numpy. Misaki,
espeakng-loader y phonemizer-fork se declaran como dependencias operativas. Un lockfile
con hashes fijará todas las transitivas Windows x64 para Python 3.11.

## Lote 1 — Servicio Python local, seguro y offline

Este lote define el proceso y sus contratos con un proveedor de secreto inyectable.
No constituye una composición operativa ni lee el almacén DPAPI de producción: las
pruebas usan un secreto temporal y una composición de desarrollo debe recibirlo de
una fuente explícitamente no productiva. El lector DPAPI real, la provisión y la
habilitación de la composición operativa pertenecen exclusivamente al lote 2.

### Archivos

- Añadir services/kokoro-tts/pyproject.toml, requirements-prod.lock y
  requirements-test.lock.
- Añadir services/kokoro-tts/src/localassistant_kokoro_tts/.
- Añadir services/kokoro-tts/tests/ y config.example.toml.
- Añadir THIRD-PARTY-NOTICES.md en el servicio.
- Modificar .gitignore.

### Implementación

1. Crear paquete y entry point manual con config.toml validado. El host es fijo
   127.0.0.1; son configurables solo puerto, perfiles, límites, pesos preparados e
   hilos CPU. CpuThreads se limita a 1..16 y nunca supera los procesadores lógicos
   detectados; si no se pueden detectar, se usa 1. El valor se aplica también a
   PyTorch y a las bibliotecas numéricas que expongan el límite equivalente.
2. Registrar jarvis-es, jarvis-es-alt y jarvis-es-female, resueltos internamente a
   em_alex, em_santa y ef_dora. HTTP nunca acepta rutas, modelos o archivos.
3. Cargar Kokoro una vez, offline y en CPU, con ThreadPoolExecutor(max_workers=1).
   El event loop no ejecuta inferencia síncrona. Antes de ready se comprueba una
   versión utilizable de eSpeak NG y se expone la versión de contrato del servicio
   en health; no se exponen rutas locales ni versiones de dependencias.
4. Implementar GET /health, GET /v1/voices y POST /v1/speech. Autorización obligatoria,
   correlación segura y sin logs de texto, audio o secretos. El identificador de
   correlación lo genera el servicio como UUID canónico; no acepta ni refleja una
   cabecera de correlación del llamante.
5. Mientras existe síntesis, rechazar una segunda inmediatamente con HTTP 503,
   code service_busy y Retry-After: 1. Health sigue respondiendo busy.
6. Limitar cuerpo a 4 KiB antes de JSON, texto a 320 caracteres, campos exactos,
   content type, perfiles, idiomas, velocidad y volumen entero permitido de 0 a 100.
7. Emitir WAV PCM mono, 24 kHz, 16 bits. Aplicar volume/100 antes de codificar,
   sin amplificación ni clipping.
8. Aplicar 60 s de carga y 15 s de síntesis. Tras timeout, devolver 504 seguro,
   descartar respuesta tardía y mantener busy hasta que acabe el worker.
9. Aislar el motor para usar un sintetizador falso en pruebas.
10. Declarar por separado las dependencias directas de producción y prueba. Los dos
    lockfiles incluyen hashes y transitivas para Windows x64/Python 3.11; el de prueba
    incluye pytest y los plugins estrictamente necesarios. La comprobación reproducible
    desde índices documentados se ejecuta en el lote 6 antes del cierre.

### Pruebas obligatorias

- Health loading, ready, busy, degraded y load_timeout.
- Health responde durante síntesis bloqueada.
- Segunda síntesis: 503, service_busy y Retry-After: 1.
- Una única inferencia entra al executor.
- Bearer ausente, inválido, longitud inválida y válido.
- Cuerpo, JSON, campos, texto, idioma, perfil y velocidad inválidos.
- Ganancia 0, intermedia y 100 sin amplificación ni clipping.
- Error de motor, timeout, correlación y logs sin texto completo.
- Arranque offline sin descarga mediante motor falso y smoke explícito con pesos preparados.
- Límite de hilos CPU, eSpeak NG ausente o con versión no válida, y versión de contrato.
- Correlación canónica generada por el servicio y sin influencia de una cabecera entrante.

## Lote 2 — Secreto DPAPI interoperable y configuración

### Archivos

- Añadir src/LocalAssistant.TerminalClient/KokoroSharedSecretStore.cs.
- Modificar Program.cs, TerminalClientConfiguration.cs y TerminalClientCommandLine.cs.
- Añadir pruebas .NET de secreto/configuración.
- Añadir scripts/Invoke-KokoroDpapiInteropTests.ps1.
- Añadir pruebas Python de interoperabilidad opt-in.

### Implementación

1. Implementar sobre LASKOK01: versión, scope CurrentUser, longitud exacta y blob DPAPI.
   Rechazar cabecera, longitud, bytes sobrantes, blob vacío o secreto distinto de 32 bytes.
2. Usar CryptProtectData/CryptUnprotectData con CRYPTPROTECT_UI_FORBIDDEN en .NET y
   Python; limpiar buffers.
3. Crear %LOCALAPPDATA%\LocalAssistant\Kokoro con DACL explícita para propietario,
   SYSTEM y Administrators. Rechazar ACL insegura.
4. Escribir temporal y reemplazar atómicamente. Servicio y rotador comparten un mutex
   de proceso por usuario durante toda la vida del servicio y durante la rotación o
   rollback. El rotador exige adquirirlo en exclusión antes de modificar nada: si el
   servicio está activo, falla sin tocar el secreto; si lo adquiere, lo mantiene hasta
   terminar sustitución y rollback. Health no se usa como prueba de que el proceso se
   haya detenido.
5. Añadir provisión y rotación explícitas sin mostrar secretos ni aceptarlos por
   argumentos, URL, appsettings o entorno.
6. Configurar endpoint Kokoro HTTP loopback y timeouts no secretos; desactivar
   redirecciones del HttpClient dedicado.

### Pruebas obligatorias

- Corrupción de todos los campos, DPAPI inválido, ACL incorrecta, sustitución fallida,
  rotación y rollback sin perder el estado anterior. Todas estas pruebas usan
  exclusivamente un almacén temporal por prueba; nunca leen ni modifican
  %LOCALAPPDATA%\LocalAssistant\Kokoro\shared-secret.v1.dpapi.
- El mutex retenido por el servicio impide la rotación; tras liberarlo, la sustitución
  y el rollback quedan protegidos por el mismo mutex.
- Ninguna salida, excepción o log contiene secreto.
- Endpoint no loopback y redirección rechazados.
- Script Windows: .NET protege/Python desprotege y viceversa, más reemplazo atómico y
  corrupción. No se ejecuta en la suite común.

## Lote 3 — Contratos de proveedor, preferencias y cliente HTTP

### Archivos

- Modificar SpokenOutput.cs, PrivateClientCredentialStore.cs y TerminalClientState.cs.
- Añadir KokoroSpokenOutput.cs.
- Modificar sinks textual y TUI.
- Añadir pruebas de spoken output, estado, persistencia y HTTP.

### Implementación

1. Introducir proveedores Sapi, Kokoro y None, con preferencias separadas. SAPI conserva
   voz/rate/volume; Kokoro guarda perfil/idioma/volume y velocidad fija 1.0; mute es global.
2. Migrar el estado DPAPI 2 a 3 de forma atómica e idempotente, conservando credencial,
   LastConversationId y preferencias SAPI.
3. Implementar KokoroSpeechClient detrás de ISpeechSynthesizer e ISpeechVoiceCatalog.
   Codifica bearer base64url canónico y el servicio compara con hmac.compare_digest de
   longitud fija.
4. Clasificar health, catálogo y errores. No reintentar síntesis tras timeout,
   desconexión, cancelación después de dispatch, 5xx o respuesta inválida.
5. Leer WAV con ResponseHeadersRead; rechazar Content-Length mayor de 3 MiB y aplicar
   el mismo límite durante copia real.
6. Validar audio/wav, RIFF/WAVE completo, PCM 1, mono, 24 kHz, 16 bits, data chunk
   íntegro y duración máxima de 45 s. Liberar stream en cualquier fallo.
7. Publicar proveedor/voz solicitados y efectivos, fallback, volumen y errores seguros,
   sin endpoint, secreto, texto ni audio.

### Pruebas obligatorias

- Migración 2→3, round-trip SAPI/Kokoro y fallo de persistencia sin perder estado previo.
- Health, catálogo, 401, 503, 504 y timeout de transporte.
- Bearer ausente de cabeceras diagnosticadas y logs.
- Content type, RIFF truncado, formato no PCM, estéreo, frecuencia/profundidad inválida,
  duración/bytes excesivos, data chunk inválido y stream sin Content-Length.
- Stream válido rebobinado y liberado una vez.
- SAPI conserva SpeechSynthesizer.Volume.

## Lote 4 — Segmentación, pipeline y cancelación

### Archivos

- Añadir SpokenTextSegmenter.cs.
- Modificar SpokenOutput.cs y TerminalClientState.cs.
- Modificar pruebas de spoken output, aplicación y estados.

### Implementación

1. Implementar segmentador perezoso: primer objetivo 160, posteriores 220, máximo 320.
   Conserva texto/puntuación/espacios, prioriza puntuación y espacios, evita residuales
   pequeños y corta una palabra solo si esa palabra supera el máximo.
2. Mantener ISpeechSynthesizer por fragmento. Preparar el primer WAV y hacer prefetch
   de uno siguiente; máximo una síntesis, una reproducción y un WAV pendiente.
3. Añadir BufferingVoice. PlayingVoice significa sonido efectivo; si termina N sin N+1,
   pasar a buffering y volver a playing al iniciar N+1.
4. Admitir cierres desde PlayingVoice y BufferingVoice: Closing/None y Closed/None.
5. Usar generación de salida: stop, cambio de proveedor, mute, nueva conversación,
   nueva respuesta, repeat posterior y cierre cancelan, liberan WAV y descartan tardíos.
6. Si N+1 falla mientras N suena, acabar N, descartar el resto, publicar
   speech_kokoro_partial_failure y no cambiar proveedor ni hacer fallback.
7. Permitir fallback SAPI solo antes del primer audio. Es otra operación local, no un
   reintento Kokoro.

### Pruebas obligatorias

- Segmentación de abreviaturas, números, URL, listas, saltos, sin puntuación y palabra
  mayor que máximo; concatenación exacta.
- Prefetch durante reproducción, orden, backpressure y máximos de concurrencia.
- Buffer underrun, retorno a playing y cierre desde ambos estados.
- Stop, cierre, mute, cambio de proveedor y respuesta nueva descartan audio tardío.
- Fallback previo una vez; fallo parcial sin duplicar audio ni invocar fallback.
- Liberación de streams en éxito, fallo, stop, cancelación y tardíos.

## Lote 5 — Comandos, presentación y composición

### Archivos

- Modificar TerminalClientApplication.cs, Program.cs, TerminalClientStateTextSink.cs y
  TerminalClientTui.cs.
- Modificar pruebas de comandos, programa, TUI y snapshots.

### Implementación

1. Añadir /speech-provider y /speech-info. Kokoro puede ser solicitado aunque el
   efectivo sea SAPI o texto; no se reescribe selección.
2. Hacer que /voice opere sobre proveedor solicitado y admita proveedor explícito.
   Un fallback SAPI no se presenta como catálogo Kokoro.
3. Mantener /rate para SAPI solicitado. Con Kokoro explica velocidad fija. /volume
   actualiza el valor independiente del proveedor solicitado y mute sigue global.
4. Mostrar estado útil: proveedores, fallback, voces, volumen, buffering y error seguro.
5. Tratar buffering como salida activa para stop, entrada, cierre y TUI, sin contenido
   conversacional ni secretos.

### Pruebas obligatorias

- Sintaxis, persistencia y snapshot de comandos.
- /voice Kokoro no consulta/modifica SAPI bajo fallback, y viceversa.
- /rate Kokoro no cambia SAPI; /volume cambia el proveedor solicitado; mute global.
- TUI y texto muestran buffering sin ANSI en salida redirigida.
- Cierre desde playing/buffering restaura terminal y no deja lectura o audio activo.

## Lote 6 — Documentación, validación y cierre

### Archivos

- Añadir docs/adr/0037-select-kokoro-cpu-local-neural-tts.md.
- Modificar ADR 0036, README.md, docs/ROADMAP.md, docs/ARCHITECTURE.md,
  docs/SECURITY.md y .gitignore.
- Añadir evaluación resumida y guía operativa bajo docs/.
- Añadir runbook de validación manual.

### Implementación

1. Registrar Kokoro CPU seleccionado, Chatterbox descartado en este hardware y SAPI
   retenido; ADR 0036 mantiene las decisiones generales.
2. Documentar preparación offline, eSpeak NG, licencias, secreto DPAPI, servicio manual,
   health, fallback y recuperación sin rutas/secreto/pesos.
3. Actualizar amenazas: texto a proceso local, secreto, loopback, WAV, CPU/cola,
   supply chain, eSpeak GPL, logs y servicio comprometido.
4. Actualizar roadmap solo al final: fase 5 cerrada y fase 6 intacta.
5. Ejecutar y registrar validación abreviada tras añadir aiohttp, sin datos sensibles.
6. Ejecutar runbook manual con Kokoro/SAPI y evidencia segura.
7. Separar y bloquear con hashes las dependencias de producción y de prueba:
   requirements-prod.lock y requirements-test.lock declaran todas las transitivas
   Windows x64/Python 3.11. El segundo incluye pytest y cualquier plugin necesario.
   Documentar los índices de paquetes y verificar desde un entorno limpio que las
   versiones del pip freeze evaluado, especialmente torch 2.14.0+cpu, se reconstruyen
   desde esos índices sin resolver versiones distintas.
8. El smoke offline bloquea realmente las conexiones salientes del intérprete Python
   mediante una regla temporal de Firewall de Windows, la elimina en finally y falla
   si no puede instalarla. No se aceptan mocks de red como sustituto de esta prueba.

## Verificación de cierre

~~~powershell
dotnet format LocalAssistant.sln --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TerminalClient"
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-build
git diff --check
& D:\IA\Kokoro\.venv\Scripts\python.exe -m pytest services\kokoro-tts\tests
scripts\Invoke-KokoroDpapiInteropTests.ps1
~~~

La prueba DPAPI es Windows opt-in y no entra en CI Linux. La validación manual usa
servicio instalado, pesos preparados y escucha humana; no se presentará como automática.
Antes de publicar se comprobará que no quedan procesos testhost, API, cliente o Kokoro.
