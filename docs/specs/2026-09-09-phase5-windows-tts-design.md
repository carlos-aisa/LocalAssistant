# Diseño del incremento 7 de la fase 5: TTS local para Windows

## Estado y propósito

Diseño **implementado y verificado** (incremento 7 de la fase 5, 2026-09-09). Este
incremento sustituye la
composición de salida hablada no disponible por una implementación real y local en
Windows, manteniendo la API HTTP estrictamente textual y la degradación a texto cuando
la capacidad de voz no pueda iniciarse.

El objetivo es reproducir las respuestas finales elegibles con voces instaladas en
Windows, permitir configurar voz, velocidad, volumen y silencio, y ofrecer los comandos
`/mute`, `/unmute`, `/stop` y `/repeat`. No incluye entrada de audio, STT, wake word,
audio remoto ni selección de dispositivo de salida.

## Alcance

El incremento incluye:

- un adaptador Windows para síntesis WAV en memoria y reproducción local;
- detección de disponibilidad y enumeración de voces habilitadas;
- preferencias persistentes de voz, velocidad, volumen y silencio;
- migración compatible y atómica del estado local protegido con DPAPI;
- comandos `/voice`, `/rate`, `/volume`, `/mute`, `/unmute`, `/stop` y `/repeat`;
- cancelación local de una reproducción sin cancelar el turno HTTP;
- retención exclusivamente en memoria del último texto final elegible para `/repeat`;
- entrada concurrente limitada durante reproducción, sin duplicar el parser en la TUI;
- limpieza del texto hablado: se quitan emoji y símbolos que el motor leería por su
  nombre, para no romper una conversación natural; el transcript conserva el original;
- estado observable seguro para modo textual y TUI;
- degradación explícita a texto fuera de Windows o ante falta de voces;
- pruebas deterministas y una comprobación manual de audio en Windows.

Quedan fuera:

- entrada de audio, micrófono, STT, wake word o reconocimiento de hablante;
- motores cloud, envío de texto o audio a terceros y cambios HTTP u OpenAPI;
- descarga automática de motores, voces o modelos;
- Piper, modelos neuronales y selección de dispositivo de reproducción;
- persistencia del último texto o de artefactos de audio;
- reproducción de historial, herramientas, errores, prompts o secretos;
- interrupción o cancelación fiable de turnos HTTP;
- SSML suministrado por el modelo, control de pronunciación, diccionarios de
  pronunciación o lectura de emoji por su nombre (solo se eliminan, no se traducen);
- atajos globales o aprobación de herramientas mediante una sola tecla.

## Evaluación acotada y elección

Se evaluaron tres alternativas para el objetivo Windows actual:

| Alternativa | Evidencia útil | Coste y riesgos | Decisión |
| --- | --- | --- | --- |
| `System.Speech.Synthesis` | Usa voces SAPI instaladas y ofrece enumeración, selección, velocidad y volumen. | API específica de Windows y calidad dependiente de las voces instaladas. | Elegida por su integración directa, operación offline y menor superficie. |
| `Windows.Media.SpeechSynthesis` | También usa voces instaladas y produce un stream de audio. | Requiere proyecciones WinRT y una composición de reproducción más compleja para este ejecutable. | Descartada para este incremento. |
| Piper | Síntesis neuronal local y catálogo amplio de voces. | Binario y modelos externos, instalación adicional, licencias por modelo y upstream actual GPL. | Aplazada para una futura evaluación de calidad. |

Referencias de la evaluación:

- [System.Speech.Synthesis](https://learn.microsoft.com/dotnet/api/system.speech.synthesis)
- [Windows.Media.SpeechSynthesis](https://learn.microsoft.com/uwp/api/windows.media.speechsynthesis)
- [Piper](https://github.com/OHF-Voice/piper1-gpl)

La elección se limita a este cliente Windows. No convierte SAPI en una dependencia del
servidor ni impide sustituir el adaptador detrás de los contratos internos existentes.

## Arquitectura

### Frontera local y composición

`TerminalClientApplication` continúa siendo la única autoridad del estado operacional,
de la elegibilidad del contenido y de los comandos. El coordinador de salida conserva
la exclusión de una única operación y no publica snapshots ni interpreta comandos.

La composición construye el adaptador real solo cuando se cumplen todas las condiciones:

1. el proceso se ejecuta en Windows;
2. el subsistema de síntesis puede inicializarse;
3. existe al menos una voz habilitada.

Si alguna condición falla, se inyecta la implementación no disponible antes de iniciar
la aplicación. No se cambia de coordinador a mitad de una operación ni se instala un
fallback externo. El chat textual sigue funcionando.

El proyecto mantiene `net8.0` para que la suite continúe ejecutándose en CI sobre Linux.
Las implementaciones SAPI se aíslan en tipos marcados como específicos de Windows y
solo se alcanzan tras una comprobación de plataforma.

### Síntesis y reproducción

El sintetizador Windows usa `SpeechSynthesizer.SetOutputToWaveStream` para escribir un
WAV completo (contenedor RIFF/WAVE) en un stream de memoria; no se usa
`SetOutputToAudioStream`, que produciría PCM sin cabecera que `SoundPlayer` no puede
reproducir.

El reproductor carga el WAV en `SoundPlayer` y usa `Play` (asíncrono) en lugar de
`PlaySync`: la reproducción síncrona (`SND_SYNC`) no se puede detener de forma fiable
desde otro hilo, así que `/stop` no funcionaría. La finalización natural se calcula a
partir de la duración leída de la cabecera RIFF (`WaveAudio.Duration`, con un pequeño
margen); `/stop` y la cancelación global llaman a `SoundPlayer.Stop` desde cualquier
hilo y terminan la espera. Si la cabecera no se puede interpretar, se cae a `PlaySync`
y el token corta la espera aunque el audio pueda no interrumpirse en todas las máquinas.

La reproducción observa dos cancelaciones distintas:

- el token de aplicación detiene el audio y continúa por `Closing -> Closed`;
- el token local de `/stop` detiene solo la reproducción y vuelve a `Ready/None`.

El artefacto de audio pertenece al objeto preparado. Tras éxito, fallo o cancelación se
limpia su buffer accesible y se libera exactamente una vez. Nunca se escribe un archivo
temporal. El adaptador no registra texto, bytes, formato interno, voz del sistema,
dispositivo ni excepciones concretas.

### Limpieza del texto hablado

Antes de sintetizar, la aplicación transforma la respuesta mostrada con
`SpokenText.ForSpeech`: elimina emoji, banderas, pictogramas, símbolos misceláneos y
dingbats, selectores de variación, ZWJ y controles C0/C1, y colapsa los espacios
resultantes conservando los saltos de línea. El objetivo es una conversación natural:
un motor SAPI leería `😀` como «cara sonriente». Solo se eliminan, no se traducen ni se
sustituyen por descripciones. El transcript técnico conserva la respuesta íntegra; el
filtro afecta únicamente al canal de voz. Si tras la limpieza no queda nada
pronunciable, no se sintetiza y es un resultado normal. `/repeat` vuelve a aplicar el
mismo filtro sobre el texto retenido, que se guarda sin transformar.

### Preferencias y estado local

Las preferencias tienen este significado:

```text
VoiceId: string?   // null selecciona la voz predeterminada de Windows
Rate: int          // -10..10; valor inicial 0
Volume: int        // 0..100; valor inicial 100
IsMuted: bool      // valor inicial false
```

El identificador de voz se compara de forma ordinal con un identificador enumerado por
el adaptador; no se aceptan coincidencias parciales o ambiguas. Los límites de velocidad
y volumen coinciden con los límites nativos de SAPI y se validan antes de modificar
estado.

El estado local evoluciona de forma compatible. El lector admite el formato vigente,
que contiene `ProtectedCredential`, y el nuevo formato versionado. Al escribir el nuevo
formato, un único payload DPAPI contiene la credencial y las preferencias de voz.
`ClientId` y `LastConversationId` continúan como metadatos locales no secretos para no
cambiar su semántica. La escritura conserva el patrón de archivo temporal y reemplazo
atómico.

La migración:

- conserva credencial, `ClientId` y `LastConversationId`;
- aplica los valores iniciales cuando no existen preferencias;
- es idempotente y solo reescribe después de una carga válida;
- no elimina el archivo anterior si protección, escritura o reemplazo fallan;
- no permite que un fallo guardando preferencias pierda la credencial.

Una preferencia se valida y se intenta persistir antes de convertirse en la preferencia
efectiva de la sesión. Si la escritura falla, la aplicación conserva la configuración
anterior y publica `speech_preferences_not_saved` como error local recuperable.

Un comando de preferencia solo se ejecuta cuando la aplicación no está en una operación
(el bucle no procesa comandos durante la reproducción; una línea escrita entonces queda
como entrada anticipada y se procesa después). Por eso aplicar la preferencia no falla
por «voz activa»: la operación en vuelo ya capturó su propia `SpeechSynthesisRequest`
inmutable y no se ve afectada. No hay un código de error para ese caso.

### Voz ausente o modificada

Si la voz guardada ya no está instalada o habilitada, el adaptador usa la voz
predeterminada durante la sesión, conserva la preferencia almacenada e informa una sola
vez mediante `speech_voice_unavailable`. `/voice` muestra tanto la selección guardada
no disponible como la voz efectiva. El usuario puede escoger otra voz para reemplazar
la preferencia.

Si no existe ninguna voz habilitada, la capacidad completa es `Unavailable`; no se
publica `PlayingVoice` y los comandos de configuración informan de la indisponibilidad
sin impedir conversar.

## Contrato de comandos

Los comandos siguen siendo líneas explícitas interpretadas solo por
`TerminalClientApplication`:

| Comando | Resultado |
| --- | --- |
| `/voice` | Lista las voces habilitadas, la selección guardada y la voz efectiva. |
| `/voice <nombre exacto>` | Valida, persiste y selecciona una voz enumerada. El resto de la línea forma el nombre. |
| `/rate <n>` | Valida y persiste un entero entre `-10` y `10`. |
| `/volume <n>` | Valida y persiste un entero entre `0` y `100`. |
| `/mute` | Persiste silencio para las reproducciones posteriores; no sustituye a `/stop`. |
| `/unmute` | Persiste la reactivación; no reproduce automáticamente. |
| `/stop` | Detiene únicamente la reproducción local actual. Fuera de reproducción muestra un aviso normal. |
| `/repeat` | Sintetiza otra vez el último texto final elegible retenido en memoria. |

Los argumentos inválidos muestran uso seguro, no cambian memoria, snapshot ni archivo y
no se tratan como errores del servidor. `/repeat` sin texto retenido y `/stop` sin audio
activo son resultados normales.

## Retención para `/repeat`

La aplicación conserva como máximo un string en memoria: el último contenido final que
cumplió la política de elegibilidad y fue mostrado. Se actualiza aunque la salida esté
silenciada o no disponible, para que una salida disponible posterior pueda repetirlo.
Un fallo de síntesis o reproducción no elimina el texto retenido.

No se retienen para repetir:

- contenido de usuario;
- historial o listados;
- respuestas con confirmación pendiente o error;
- contenido vacío;
- argumentos, resultados o resúmenes de herramientas;
- prompts, ayuda, errores, credenciales o desafíos.

El valor no entra en el snapshot, el estado DPAPI, el transcript técnico, métricas ni
logs. Se descarta al cerrar el proceso. `/new` y `/provider` no lo eliminan porque no
representa historial conversacional; `/repeat` repite la última respuesta audible de la
sesión, no una conversación concreta.

## Entrada concurrente y `/stop`

Durante reproducción, la aplicación mantiene una única solicitud de entrada pendiente y
espera simultáneamente la reproducción y esa entrada. La interfaz visual sigue enviando
líneas al mismo canal y no reconoce comandos por su cuenta.

- Si llega `/stop`, la aplicación cancela el token local, espera la liberación del
  reproductor y vuelve a `Ready/None` sin error.
- Si llega otra línea, se conserva como única entrada anticipada. La reproducción
  continúa y la línea pasa después por el flujo normal de comandos o mensajes.
- Si termina primero la reproducción, la solicitud de entrada pendiente se reutiliza
  como siguiente lectura; no se abandona una tarea que pueda consumir entrada futura.
- EOF cierra el canal y termina limpiamente.
- `Ctrl+C` cancela el token global, no se convierte en `/stop` y termina mediante
  `Closing -> Closed`.

Solo puede existir una lectura pendiente. No se añade un segundo parser, un comando
especial en la TUI ni lectura directa de controles desde el coordinador de voz.

La lectura principal de chat usa un contrato interno asíncrono y cancelable. En modo
texto delega en `Console.In.ReadLineAsync(CancellationToken)`; la TUI entrega la misma
línea mediante su solicitud pendiente y cancela esa solicitud cuando se cierra el canal
o se cancela el host. Los métodos síncronos existentes se mantienen para prompts de
pairing, confirmación y administración, que no forman parte de la entrada prefetched.

Nota de plataforma: en Windows, `Console.In.ReadLineAsync(CancellationToken)` puede no
interrumpir una lectura de stdin ya bloqueada; el cierre por `Ctrl+C` durante una
lectura pendiente termina la aplicación por la ruta de cancelación global de todos
modos, pero el hilo de lectura puede seguir bloqueado hasta que el usuario pulse Enter
o el proceso termine. La comprobación manual verifica que el cierre no se cuelga.

## Estado observable

El contexto seguro de salida se amplía con:

```text
SpokenOutput:
  Availability: Unavailable | Ready
  IsMuted: boolean
  VoiceId: string?
  Rate: integer
  Volume: integer
  WarningCode: "speech_voice_unavailable" | null
```

`VoiceId` procede exclusivamente de la enumeración local del sistema y se normaliza
antes de publicarse. `WarningCode` es un código seguro y acotado que refleja si la
sesión está usando la voz predeterminada por indisponibilidad de la voz guardada; el
coordinador de estado rechaza cualquier otro valor. No se publica el texto hablado. Cambios efectivos de preferencia,
fallback de voz, comienzo y fin de reproducción son transiciones observables. Los sinks
siguen recibiendo snapshots completos, pueden coalescer estados ordinarios y no pueden
iniciar transiciones.

`PlayingVoice` solo está permitido con disponibilidad `Ready` y `IsMuted = false`.
Puede iniciarse desde `Ready/None` exclusivamente para la reproducción local de
`/repeat`, además de los caminos que siguen un turno o una confirmación.
`/stop` produce `PlayingVoice -> None`; la cancelación global produce
`PlayingVoice -> Closing -> Closed`. Cambiar una preferencia cuando no se reproduce
permanece en `Ready/None` y publica un snapshot solo si el valor efectivo cambia.

## Errores y degradación

Los errores de TTS son locales, conocidos y recuperables. Nunca cambian
`ConversationId`, proveedor, bearer o credencial, no reintentan HTTP y no convierten el
turno en incierto.

Los códigos seguros incluyen:

- `speech_voice_unavailable`;
- `speech_synthesis_failed`;
- `speech_playback_failed`;
- `speech_preferences_not_saved`.

Los mensajes no incluyen texto, bytes, rutas, nombre de dispositivo ni mensajes de
excepción. Un éxito posterior limpia el error según la política general. `/stop` y las
omisiones normales no crean un error. Una excepción al liberar recursos no escapa de la
frontera de salida y no impide el cierre.

## Seguridad y privacidad

- La síntesis y reproducción permanecen dentro del equipo Windows.
- No existe tráfico de audio ni texto adicional respecto al contrato HTTP actual.
- El audio solo existe en memoria, se limpia y se libera después de una operación.
- El texto retenido para `/repeat` no se persiste ni se publica.
- Las preferencias están protegidas junto a la credencial mediante DPAPI.
- La enumeración de voces no se registra indiscriminadamente.
- Métricas futuras solo podrán registrar operación, resultado y duración, nunca
  contenido, audio, voz seleccionada o dispositivo.
- La TUI no interpreta ANSI procedente del motor ni recibe texto desde el plano de voz.
- Un comando de voz no aprueba herramientas ni altera autorización HTTP.

## Estrategia de pruebas obligatoria

### Persistencia y migración

- El formato anterior carga credencial y `LastConversationId` con preferencias iniciales.
- El nuevo formato hace round-trip de todas las preferencias.
- La migración es idempotente y conserva identidad y conversación.
- Corrupción, DPAPI inválido, acceso denegado y fallo de reemplazo devuelven un resultado
  seguro sin borrar el archivo anterior.
- Un fallo guardando preferencias conserva credencial y preferencias efectivas previas.
- Los tests Windows ejercitan protección y desprotección reales; los tests portables
  usan una frontera criptográfica inyectable sin fingir soporte de plataforma.

### Adaptadores y recursos

- La enumeración excluye voces deshabilitadas y selecciona la predeterminada.
- Una voz exacta, velocidad y volumen llegan al sintetizador.
- Una voz ausente aplica fallback una vez; ausencia total queda `Unavailable`.
- El WAV se sintetiza, rebobina, reproduce y libera exactamente una vez.
- El buffer se limpia tras éxito, fallo, `/stop` y cancelación global.
- Dos reproducciones no se solapan.
- Fallos y excepciones se traducen a resultados seguros sin detalles internos.
- Ningún test automatizado requiere altavoces, una voz real, red, reloj o terminal real.

### Comandos y aplicación

- `/voice`, `/rate`, `/volume`, `/mute` y `/unmute` validan, persisten y publican el
  snapshot exacto; entradas inválidas no cambian estado.
- `/mute` afecta a las reproducciones posteriores; `/stop` es el único comando que
  detiene audio activo y `/unmute` no reproduce por sí mismo.
- `/repeat` reproduce solo el último texto elegible y no llama a HTTP.
- `/repeat` sin texto y `/stop` sin reproducción son resultados normales.
- Una línea distinta de `/stop` durante reproducción se procesa una sola vez después.
- Si reproducción termina primero, la lectura pendiente se convierte en la siguiente
  entrada sin perderla ni duplicarla.
- `/stop` es cancelación local conocida; `Ctrl+C` termina en `Closing -> Closed`.
- Historial, listados, ayuda, errores, confirmaciones y contenido vacío no se retienen ni
  sintetizan.
- Fallos de TTS conservan texto, conversación, proveedor, bearer y credencial y permiten
  el siguiente turno.

### Presentación, plataforma y seguridad

- Modo textual y TUI muestran disponibilidad, voz efectiva, velocidad, volumen,
  silencio y reproducción sin depender del color.
- Los sinks no duplican la respuesta ni incluyen contenido hablado.
- Salida redirigida no contiene ANSI.
- Fuera de Windows y con inicialización fallida se selecciona texto antes de iniciar la
  aplicación.
- Snapshots, errores, salida técnica y datos persistidos legibles no contienen texto,
  audio, credencial, bearer o desafíos.

## Comprobación manual de Windows

Antes de cerrar el incremento se documentará una ejecución en Windows Terminal y
PowerShell que cubra:

1. enumeración y reproducción con una voz española instalada;
2. cambio de voz, velocidad y volumen y persistencia tras reinicio;
3. `/mute`, `/unmute`, `/stop` durante audio y `/repeat`;
4. pegado de texto, caracteres españoles, resize y transcript largo;
5. `Ctrl+C`, EOF y `/exit`, comprobando restauración de cursor y terminal;
6. ausencia de archivos de audio y degradación al deshabilitar la capacidad.

La evidencia registrará versión de Windows y comandos, pero no nombres personales,
credenciales, texto privado ni inventario completo de voces.

## Verificación y cierre

Antes de marcar el punto 7 del roadmap deberán pasar:

```powershell
dotnet format LocalAssistant.sln --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TerminalClient"
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-build
git diff --check
```

También se comprobará que no quedan procesos `testhost`, cliente o API residuales y se
actualizarán `README.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md` y
`docs/ROADMAP.md`. El punto solo se marcará después de completar la prueba manual de
audio real en Windows.

## Preparación para el incremento 8

La composición Windows quedará explícita y diagnosticable para que el cierre operativo
pueda publicar y configurar el cliente sin incrustar voces, rutas personales o secretos.
El incremento 8 deberá decidir el empaquetado del paquete Windows, validar la presencia
de componentes del sistema y conservar la degradación textual sin introducir un
instalador de voces automático.
