# Diseño: proveedor neuronal local Kokoro para salida hablada

## Estado

Implementado y verificado (2026-09-12). Este incremento es independiente y posterior al cierre de la fase 5; no la renumera ni reabre, y no inicia la fase 6. El smoke offline y el runbook manual en Windows se ejecutaron y superaron (`docs/runbooks/kokoro-local-tts-validation.md`).

## Objetivo

Añadir Kokoro 0.9.4 como proveedor neuronal opcional de salida hablada local en CPU. Kokoro se ejecutará como un proceso Python independiente, escuchando exclusivamente en loopback. El cliente terminal seguirá enviando y recibiendo únicamente texto mediante la API conversacional existente.

SAPI permanece como proveedor soportado y fallback explícito. La falta, indisponibilidad o fallo de Kokoro nunca invalida una respuesta textual ya entregada ni impide usar el cliente sin audio.

## Límites de alcance

No se añaden audio ni endpoints de TTS a `LocalAssistant.Api`, al orquestador ni a los contratos de conversación. El cliente no inicia, instala, actualiza, reinicia ni mata el proceso Python; tampoco descarga modelos o busca instalaciones de Python.

Quedan fuera STT, micrófono, CUDA para Kokoro, clonación de voz, entrenamiento, streaming de tokens del LLM, transporte multimedia conversacional y acceso remoto al servicio.

## Decisión de servicio

El servicio será un paquete Python separado bajo el repositorio, con Python 3.11 y `aiohttp==3.14.3`. `aiohttp` se selecciona por su ciclo de vida asíncrono, límites de cuerpo, servidor HTTP mantenido y utilidades de prueba, sin añadir el conjunto de capas de un framework ASGI más servidor independiente.

Dependencias directas:

- `kokoro==0.9.4`, Apache-2.0.
- `aiohttp==3.14.3`, Apache-2.0.
- `torch==2.14.0+cpu` for CPython 3.11 and Windows x64, the CPU-only build measured in the successful evaluation.
- `numpy==2.4.6`, imported directly to emit PCM WAV and apply Kokoro gain.
- `misaki==0.9.4`, `espeakng-loader==0.2.4`, and `phonemizer-fork==3.3.2` as pinned operational phonemization dependencies.

All transitive dependencies will be pinned in a reproducible lockfile with hashes for Python 3.11. The evaluated environment did not include `aiohttp`; adding `aiohttp==3.14.3` therefore requires the abbreviated CPU/offline smoke validation defined below before it is accepted.

No se añade una dependencia Python para DPAPI. El servicio usa `ctypes` y las API nativas de Windows. La licencia y procedencia de Kokoro, pesos, voces y dependencias directas se documentarán en la guía operativa.

eSpeak NG and `phonemizer-fork` are GPL-3.0-or-later operational dependencies. They will not be added to this repository, committed as binaries, or bundled with the existing MIT terminal-client publication. The service remains an operator-installed local process. Any future redistribution of the service or its environment requires a dedicated licensing review and an updated notice file.

El runtime fuerza CPU antes de cargar Kokoro y activa modo offline. El directorio de
pesos configurado es la raíz de caché de Hugging Face que contiene el snapshot ya
preparado de `hexgrad/Kokoro-82M`; el servicio fija ese identificador de repositorio
y no acepta ninguno desde HTTP. `CpuThreads` se
valida entre 1 y 16 y no puede superar los procesadores lógicos detectados; si estos
no están disponibles se usa 1. El valor se aplica a PyTorch y a las bibliotecas
numéricas que expongan el límite equivalente. La ubicación de los pesos es
configuración local del operador; no procede del cliente. Si faltan pesos preparados,
el servicio queda degradado y no intenta descargarlos durante carga ni síntesis.

## Frontera HTTP local

El proceso escucha únicamente en `127.0.0.1`; IPv6 queda fuera de este incremento. No configura CORS, no usa `Host` ni `X-Forwarded-*` como frontera de confianza y no sigue redirecciones en el cliente.

Todos los endpoints requieren `Authorization: Bearer <kokoro-secret>`. El secreto es distinto de credenciales y bearer de LocalAssistant. Cada respuesta genera un `X-Kokoro-Correlation-Id` como UUID canónico aleatorio y seguro para diagnóstico; nunca incorpora texto, credenciales ni rutas locales. El servicio no acepta ni refleja un identificador de correlación proporcionado por el llamante.

The raw DPAPI secret is 32 bytes. Both sides encode it as a canonical 43-byte base64url bearer without padding. The service rejects every other length or character set before using `hmac.compare_digest` over fixed-length bytes. It never logs the header or distinguishes malformed and incorrect credentials beyond the same safe `401 unauthorized` response.

| Ruta | Respuesta | Semántica |
| --- | --- | --- |
| `GET /health` | JSON | Devuelve `loading`, `ready`, `busy` o `degraded`. Un estado no listo no es un error HTTP por sí mismo. |
| `GET /v1/voices` | JSON | Devuelve solo perfiles registrados `{ id, language }`. |
| `POST /v1/speech` | `audio/wav` | Sintetiza un único fragmento validado. |

`POST /v1/speech` exige `Content-Type: application/json`, rechaza campos desconocidos y acepta exclusivamente `text`, `voice`, `language`, `speed` y `volume`. El cuerpo se limita a 4 KiB antes de deserializar; el texto se limita a 320 caracteres Unicode no vacíos. Voz e idioma deben estar en allowlists, velocidad debe estar dentro del rango declarado por el servicio y volumen debe ser un entero de 0 a 100. No se aceptan modelos, repositorios, rutas, archivos de referencia, código ni parámetros adicionales.

Los errores son JSON seguro con `code` y `correlationId`. Una síntesis concurrente recibe exactamente `503 service_busy` y `Retry-After: 1`; el servicio no mantiene cola.

## Ciclo de vida y timeouts del servicio

Kokoro se carga una vez, en un `ThreadPoolExecutor(max_workers=1)` dedicado. Ninguna inferencia síncrona se ejecuta en el event loop de `aiohttp`. Antes de informar `ready`, el servicio valida que eSpeak NG esté disponible y tenga una versión compatible; `health` expone únicamente la versión del contrato del servicio, no rutas ni versiones de dependencias.

- Carga de modelo: 60 segundos desde el arranque. Al superar ese límite, el servicio pasa a `degraded/load_timeout` e ignora una terminación tardía hasta reinicio manual.
- Síntesis de un fragmento: 15 segundos. Si excede el límite, el request devuelve `504 synthesis_timeout`; el worker puede continuar, pero el estado permanece `busy` hasta que termine y el resultado se descarta.
- Health del cliente: 2 segundos.
- Síntesis HTTP del cliente: 18 segundos, incluyendo transporte.

Mientras se sintetiza, `GET /health` debe responder de inmediato con `busy`. Una segunda síntesis se rechaza antes de entrar al executor. Las pruebas demostrarán ambos hechos.

## Autorización y secreto compartido

El secreto se guarda en:

```text
%LOCALAPPDATA%\LocalAssistant\Kokoro\shared-secret.v1.dpapi
```

El formato binario es estable y compartido entre .NET y Python:

```text
Magic       8 bytes ASCII: LASKOK01
Version     UInt16 little-endian: 1
Scope       UInt8: 1 (CurrentUser)
BlobLength  UInt32 little-endian
Blob        resultado de DPAPI CryptProtectData / ProtectedData
```

El blob contiene exactamente 32 bytes generados por un generador criptográfico. Se protege con DPAPI `CurrentUser`; el directorio se crea con ACL de usuario propietario y sin permisos heredados para otros usuarios. El archivo se escribe a un temporal y se reemplaza atómicamente. El servicio solo lee el archivo.

Both implementations call `CryptProtectData` and `CryptUnprotectData` with `CRYPTPROTECT_UI_FORBIDDEN`. They validate the entire envelope before calling DPAPI: exact magic, supported version and scope, a non-zero bounded blob length, and no trailing bytes. After unprotection, the result must be exactly 32 bytes and is cleared from managed buffers as soon as possible. The directory DACL grants full control only to the current owner, `SYSTEM`, and Administrators; generic Users, Everyone, and inherited grants are removed.

La provisión es explícita mediante un modo local del cliente y nunca muestra el secreto. El proceso de servicio y el rotador comparten un mutex por usuario: el servicio lo mantiene durante toda su vida y el rotador debe adquirirlo en exclusión antes de crear el backup, sustituir el archivo o ejecutar rollback. Si el servicio sigue activo, la rotación falla sin modificar el archivo; una comprobación HTTP de `health` no sustituye ese bloqueo. Si el archivo es corrupto, tiene una cabecera inválida, no puede descifrarse o no conserva ACL válida, el servicio queda degradado y el cliente conserva texto, preferencias y fallback sin sobrescribir el archivo.

Este mecanismo evita exponer secretos por URL, argumentos o configuración legible. No pretende defender frente a malware que ya se ejecuta como el mismo usuario de Windows y puede invocar DPAPI; esa amenaza residual se documentará.

Habrá una verificación Windows opt-in e interoperable: .NET protegerá y Python desprotegerá un secreto, Python protegerá y .NET desprotegerá otro, y ambos comprobarán sustitución atómica y rechazo de corrupción. Todas las pruebas de corrupción, reemplazo y rollback usan exclusivamente directorios temporales por prueba y nunca acceden a la ruta operativa. No será requisito de la suite .NET común.

## Perfiles y preferencias

Kokoro expone perfiles estables, no voces internas:

| Perfil | Voz interna inicial | Idioma |
| --- | --- | --- |
| `jarvis-es` | `em_alex` | `es` |
| `jarvis-es-alt` | `em_santa` | `es` |
| `jarvis-es-female` | `ef_dora` | `es` |

La configuración validada del servicio contiene el mapeo y la allowlist. El cliente solo conoce perfil e idioma. Se representan explícitamente `es` e `en`, pero no se deduce idioma desde el texto.

El estado local evoluciona de esquema 2 a esquema 3 de forma idempotente y atómica. Las preferencias SAPI existentes se preservan. El nuevo estado contiene preferencias independientes para SAPI y Kokoro, proveedor solicitado, fallback y silencio global. No se persiste audio, texto repetible, bearer ni secreto en este estado.

`mute` is global. `volume` is provider-specific and always ranges from 0 to 100: SAPI passes it to `SpeechSynthesizer.Volume` while synthesizing; Kokoro applies the gain `volume / 100` to signed PCM samples before writing its WAV. Kokoro never amplifies samples, and clamps the result to the PCM range before encoding, so it cannot clip. `SoundPlayer` is not treated as a volume control.

El endpoint Kokoro es configuración externa no secreta y debe ser HTTP loopback. El secreto se obtiene exclusivamente del almacén DPAPI compartido.

## Selección y comandos

Se añaden:

```text
/speech-provider
/speech-provider sapi
/speech-provider kokoro
/speech-provider none
/speech-info
```

`/speech-provider kokoro` conserva la selección solicitada aunque la comprobación de health deje el proveedor efectivo en SAPI o texto. No modifica silenciosamente la preferencia persistida.

`/voice` siempre opera sobre el proveedor solicitado:

```text
/voice
/voice sapi
/voice kokoro
/voice sapi <voice-id>
/voice kokoro <profile-id>
```

La forma sin proveedor mantiene compatibilidad y usa el proveedor solicitado. Si Kokoro no está listo, no se sustituye su catálogo por el de SAPI: se informa de indisponibilidad y se conserva el perfil solicitado.

`/rate` conserva la semántica SAPI cuando SAPI es el proveedor solicitado. Cuando Kokoro es el solicitado, informa que su velocidad permanece fija en `1.0` en este incremento y no altera preferencias. `/volume` updates the selected provider's independent value; mute remains global. The actual gain is applied by synthesis, never by `SoundPlayer`.

`/speech-info` y el snapshot seguro distinguen proveedor solicitado, proveedor efectivo, fallback, voz solicitada, voz efectiva y código de aviso, sin exponer endpoint o secreto.

## Segmentación y pipeline

El cliente segmenta `SpokenText.ForSpeech()` antes de llamar a Kokoro. El segmentador es determinista, perezoso y conserva exactamente el texto de entrada al concatenar sus fragmentos.

- Primer objetivo: 160 caracteres.
- Objetivo posterior: 220 caracteres.
- Máximo absoluto: 320 caracteres.
- Preferencia de corte: `.`, `?`, `!`, `;`, `:`, y después espacios.
- No corta palabras salvo si una palabra aislada supera el máximo.
- Reequilibra un último fragmento demasiado pequeño cuando existe un corte seguro.

No implementa heurística lingüística compleja. Abreviaturas, números, URL, listas y saltos de línea se conservan literalmente y se prueban contra pérdida o duplicación.

`ISpeechSynthesizer` seguirá representando la síntesis de un fragmento. Un `IPreparedSpokenOutput` segmentado conservará el primer WAV preparado y, al reproducir, prefetch del siguiente fragmento. No se crean listas ilimitadas:

```text
texto público → segmentador perezoso → sintetizador → buffer de 1 → reproductor
```

Solo hay una síntesis, una reproducción y un WAV pendiente como máximo. El transcript mantiene una respuesta completa única y todos los streams se liberan ante éxito, fallo, cancelación o descarte.

The Kokoro HTTP client uses `ResponseHeadersRead`, rejects a content length above 3 MiB before reading, and performs a bounded stream copy that also rejects bodies whose actual length exceeds 3 MiB. It accepts only `audio/wav` and validates the complete RIFF/WAVE structure before playback: PCM format code 1, one channel, 24,000 Hz, 16 bits per sample, a declared data chunk within the received bytes, and a duration no greater than 45 seconds. Unknown or malformed chunks, overflow, unsupported format, trailing truncation, invalid media type, or an excessive duration become `speech_kokoro_invalid_wav`; the stream is disposed and never passed to `SoundPlayer`.

## Estado, cancelación y obsolescencia

`PlayingVoice` mantiene el significado de reproducción de audio activa: no promete que el dispositivo físico produzca sonido audible. Se añade `BufferingVoice` para la espera entre segmentos cuando el siguiente WAV no está listo.

```text
Ready/None → PlayingVoice
PlayingVoice → BufferingVoice
BufferingVoice → PlayingVoice
PlayingVoice | BufferingVoice → Ready/None
PlayingVoice | BufferingVoice → Closing/None → Closed/None
```

La TUI muestra la espera de forma segura, sin texto hablado. `/stop` trata ambos estados como salida activa.

Cada operación de salida tiene una generación. `/stop`, `/new`, selección de otra conversación, una respuesta posterior, cambio de proveedor, silencio, otra repetición y cierre cancelan la generación, liberan WAV pendientes e impiden reproducir resultados tardíos. La cancelación local no afirma que una inferencia síncrona remota haya sido interrumpida.

Timeout, disconnect, cancellation after dispatch, a 5xx response, or a malformed speech response are uncertain from the client's perspective. The client does not retry Kokoro automatically. A permitted SAPI fallback before any audible segment is a distinct local provider operation, not a retry of the uncertain Kokoro request.

Si falla la síntesis de N+1 mientras N todavía suena, N termina; el resto se descarta y se publica `speech_kokoro_partial_failure` como error recuperable. No se activa fallback, ni se repite texto, ni cambian proveedor, voz o preferencias.

## Fallback y códigos seguros

Si Kokoro falla antes de iniciar audio, se permite reproducir la respuesta completa con SAPI cuando sea el fallback configurado y esté disponible. Si SAPI tampoco está disponible, el resultado permanece textual. Tras haber comenzado audio no hay fallback automático para evitar duplicar contenido.

Los códigos seguros incluyen:

```text
speech_kokoro_not_configured
speech_kokoro_unavailable
speech_kokoro_unauthorized
speech_kokoro_busy
speech_kokoro_timeout
speech_kokoro_invalid_wav
speech_kokoro_voice_unavailable
speech_kokoro_synthesis_failed
speech_kokoro_partial_failure
```

## Pruebas y verificación

Las pruebas .NET serán deterministas y no requerirán Python, Kokoro, pesos, Ollama, GPU ni Internet. Cubrirán migración, selección, catálogo, health, autenticación, segmentación, WAV inválido, timeout, ocupado, pipeline, backpressure, cancelación, streams, fallback, fallo parcial, SAPI y degradación textual.

Las pruebas Python usarán un sintetizador falso y cubrirán estados de health, executor, respuesta inmediata `busy`, autorización, límites, allowlists, WAV, errores sanitizados y ausencia de texto completo en logs.

Before accepting the service environment, an abbreviated validation will run in `D:\IA\Kokoro` with the evaluated Python 3.11.9, `kokoro==0.9.4`, `torch==2.14.0+cpu`, and `numpy==2.4.6`, after adding the new pinned server and operational dependencies. It will prove offline startup from prepared weights, `torch.cuda.is_available() == false`, model device `cpu`, one valid synthesis with `em_alex`, and no network attempt. The smoke blocks outbound connections for the Python interpreter with a temporary Windows Firewall rule and removes the rule in `finally`; it fails if it cannot install the rule. If any evaluated runtime dependency changes, this validation must be repeated and its result recorded.

La verificación manual Windows comprobará Kokoro offline con pesos preparados, CPU, convivencia con Ollama en GPU, primera respuesta, respuesta larga, `/stop`, `/repeat`, mute, cambios Kokoro/SAPI, servicio detenido, reinicio manual, cierre y ausencia de WAV persistidos o secretos en consola.

## Documentación y trazabilidad

Se actualizarán `README.md`, `docs/ROADMAP.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `.gitignore` y la guía operativa. Se añadirá una evaluación resumida de Chatterbox y Kokoro y un nuevo ADR que selecciona Kokoro CPU. El ADR 0036 se marcará como sustituido únicamente respecto al candidato de motor, conservando sus decisiones de frontera textual, proceso aislado y fallback explícito.

El roadmap solo marcará esta evolución como implementada después de las verificaciones automáticas y manuales correspondientes. La fase 5 seguirá cerrada y la fase 6 conservará su estado real.
