# Plan de implementación: incremento 7 de fase 5 — TTS local para Windows

> **Estado: finalizado** (2026-09-09). SAPI local en Windows detrás de los contratos
> existentes; comandos `/voice`, `/rate`, `/volume`, `/mute`, `/unmute`, `/stop`,
> `/repeat`; estado DPAPI versionado con migración; limpieza del texto hablado. `dotnet
> format` y `build -c Release` limpios, suite completa **655/655**, `dotnet list package
> --vulnerable` limpio. Verificación manual de audio real en Windows ejecutada y
> superada (`docs/evaluations/2026-09-09-windows-tts-manual-validation.md`), incluidos
> tres fallos encontrados y corregidos en la sesión (WAV vía `SetOutputToWaveStream`,
> `/stop` con `Play` asíncrono, `SpokenText.ForSpeech` para los emoji). `ROADMAP.md`
> punto 7 marcado.

## Alcance confirmado

Implementar el diseño aprobado en
`docs/specs/2026-09-09-phase5-windows-tts-design.md`. El cliente terminal utilizará
las voces SAPI instaladas en Windows para sintetizar WAV en memoria, reproducirá las
respuestas finales elegibles y ofrecerá configuración persistente y los comandos
`/voice`, `/rate`, `/volume`, `/mute`, `/unmute`, `/stop` y `/repeat`.

La API continuará siendo textual. No se añadirán endpoints, OpenAPI, audio remoto,
Piper, descarga de voces, archivos temporales, entrada de audio ni STT. Fuera de
Windows o sin voces habilitadas, la composición decidirá antes de conversar que la
salida hablada está `Unavailable` y el chat seguirá siendo textual.

## Suposiciones y decisiones de implementación

- Mantener `TargetFramework=net8.0` para que el CI Linux siga compilando y ejecutando
  la suite. Los adaptadores SAPI se aislarán y marcarán como específicos de Windows.
- Añadir referencias directas y fijadas a `System.Speech` 8.0.0 y
  `System.Windows.Extensions` 8.0.0, alineadas con .NET 8. No se añade un SDK TTS de
  terceros.
- Usar `SpeechSynthesizer` para generar WAV en memoria y `SoundPlayer` para reproducirlo.
  El reproductor cargará el WAV antes de iniciar `PlaySync` para que un stream inválido
  se traduzca en fallo y no produzca el sonido predeterminado de Windows.
- Mantener `PrivateClientCredential` como contrato de identidad. Las preferencias se
  expondrán a la aplicación mediante una interfaz local adicional implementada por el
  mismo almacén DPAPI; no se añadirán campos de voz al contrato HTTP ni a la credencial.
- La aplicación cargará las preferencias después de obtener el estado local válido y
  actualizará el coordinador antes del primer turno. Los dobles y el almacén manual
  usarán los valores iniciales sin afirmar persistencia.
- Implementar una sola lectura asíncrona pendiente durante reproducción. Si el audio
  termina primero, esa misma tarea será la siguiente entrada del bucle; nunca se
  abandonará una lectura que pueda consumir una línea futura.
- `/mute` afecta a reproducciones posteriores. `/stop` es el único comando ordinario
  que detiene el audio actual. `Ctrl+C` conserva la cancelación global.

## Incremento 1 — Modelo de preferencias y estado observable

**Archivos**

- Modificar `src/LocalAssistant.TerminalClient/SpokenOutput.cs`.
- Modificar `src/LocalAssistant.TerminalClient/TerminalClientState.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/SpokenOutputTests.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientStateTests.cs`.

**Implementación**

1. Ampliar `SpokenOutputPreferences` con `VoiceId`, `Rate`, `Volume` e `IsMuted`, con
   valores iniciales `null`, `0`, `100` y `false`.
2. Validar los rangos `Rate=-10..10` y `Volume=0..100` en una frontera única y no
   permitir identificadores vacíos o solo espacios.
3. Ampliar `TerminalClientSpokenOutputState` con voz efectiva, velocidad y volumen.
   El DTO seguirá sin texto, audio, rutas, motor, dispositivo o excepciones.
4. Extender `ISpokenOutputCoordinator` con operaciones internas para consultar voces,
   aplicar preferencias validadas y solicitar la detención local. Ninguna operación
   recibirá el coordinador de snapshots.
5. Mantener las invariantes de `PlayingVoice`: `Ready`, disponibilidad real y salida no
   silenciada. Los cambios de preferencia serán válidos en `Ready/None` y no crearán
   actividades ficticias.

**Pruebas obligatorias**

- Valores iniciales exactos y validación de todos los extremos válidos e inválidos.
- Igualdad inmutable y ausencia de modificación ante validación fallida.
- Snapshot seguro sin propiedades de texto, audio, ruta, credencial, bearer o desafío.
- Publicación sin duplicados al cambiar voz, rate, volumen y silencio.
- Rechazo de `PlayingVoice` si la salida está ausente o silenciada.
- Conservación del grafo existente para envío, confirmación, reproducción y cierre.

**Criterio de salida**

El modelo compila y las pruebas del coordinador de estado pasan sin adaptar todavía
SAPI ni activar audio real.

## Incremento 2 — Estado local DPAPI versionado y migración compatible

**Archivos**

- Modificar `src/LocalAssistant.TerminalClient/PrivateClientCredentialStore.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/DpapiPrivateClientCredentialStoreTests.cs`.
- Modificar dobles de almacén en
  `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`.

**Implementación**

1. Añadir un contrato interno de estado de preferencias, separado de
   `PrivateClientCredential`, que el almacén DPAPI y los dobles puedan cargar y guardar.
2. Introducir una versión explícita del formato. El formato nuevo contendrá como datos
   legibles únicamente versión, `ClientId` y `LastConversationId`; un payload DPAPI
   incluirá credencial y preferencias.
3. Mantener lectura del formato vigente con `ProtectedCredential`. Una carga válida
   devolverá la credencial original y preferencias iniciales; la siguiente escritura
   generará exclusivamente el formato nuevo.
4. Mantener la escritura temporal y el reemplazo atómico. Al guardar preferencias se
   reescribirá el estado completo usando la credencial ya validada, sin poder dejar un
   archivo que contenga preferencias nuevas y credencial ausente.
5. Extraer fronteras internas mínimas para protección DPAPI y reemplazo de archivo solo
   donde sean necesarias para probar Linux, corrupción y fallos de escritura de forma
   determinista. La implementación productiva seguirá usando `ProtectedData` y el
   filesystem real.
6. Limpiar los buffers UTF-8 de credencial, payload protegido y payload desprotegido en
   `finally`. No incluir valores protegidos en errores o salida.

**Pruebas obligatorias**

- Formato anterior real: carga `ClientId`, credencial y `LastConversationId`, aplica
  preferencias iniciales y migra en la siguiente escritura.
- Round-trip del formato nuevo con voz, rate, volumen y silencio.
- Migración idempotente y conservación exacta de identidad y conversación.
- El JSON legible no contiene credencial, preferencias, texto hablado ni bytes de audio.
- Payload Base64 o JSON corrupto, versión desconocida, DPAPI inválido y acceso denegado
  devuelven fallo seguro sin excepción publicada.
- Fallo antes y durante el reemplazo conserva byte por byte el archivo anterior y
  elimina solo el temporal creado por esa operación.
- Cancelación propaga `OperationCanceledException`, conserva el archivo anterior y no
  deja temporales.
- `SaveAsync` y borrado mantienen el comportamiento existente para credenciales.
- Tests condicionados a Windows ejercitan `Protect`/`Unprotect` reales; tests portables
  usan dobles de la frontera criptográfica y no esperan soporte DPAPI en Linux.

**Criterio de salida**

La persistencia puede desplegarse sobre un archivo anterior sin perder credenciales y
todos los fallos de migración son recuperables y atómicos.

## Incremento 3 — Adaptadores SAPI y reproducción WAV en memoria

**Archivos**

- Modificar `src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj`.
- Añadir `src/LocalAssistant.TerminalClient/WindowsSpokenOutput.cs`.
- Modificar `src/LocalAssistant.TerminalClient/SpokenOutput.cs` solo para integrar las
  capacidades aprobadas del coordinador.
- Añadir `tests/LocalAssistant.Tests/TerminalClient/WindowsSpokenOutputTests.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/SpokenOutputTests.cs`.

**Implementación**

1. Añadir los paquetes Microsoft fijados a 8.0.0 y comprobar licencias, dependencias
   transitivas y vulnerabilidades conocidas durante la verificación.
2. Crear fronteras internas estrechas para enumeración/síntesis SAPI y reproducción
   WAV. Los tests probarán la lógica mediante dobles sin construir SAPI o `SoundPlayer`.
3. Enumerar solo `InstalledVoice.Enabled`, ordenar de forma ordinal para una salida
   determinista y distinguir `VoiceId` guardado de voz efectiva.
4. Sintetizar con una instancia propia de `SpeechSynthesizer` por operación, seleccionar
   voz, rate y volumen antes de hablar y escribir a un stream WAV rebobinado.
5. Enlazar `SpeakCompleted` a una tarea cancelable, cancelar mediante
   `SpeakAsyncCancelAll`, desregistrar eventos y liberar sintetizador en todas las rutas.
6. Implementar un stream de audio sensible que limpie su buffer antes de liberarlo.
7. Cargar el WAV y ejecutar `SoundPlayer.PlaySync` fuera del hilo de aplicación. Registrar
   la cancelación para llamar `Stop`, esperar el fin real y liberar jugador y stream.
8. Actualizar el coordinador para aplicar preferencias entre operaciones, impedir
   solapamientos, hacer fallback a la voz predeterminada y exponer resultados tipados
   seguros. No cambiar preferencias durante un artefacto ya preparado.

**Pruebas obligatorias**

- Enumeración vacía produce `Unavailable`; voces deshabilitadas no aparecen.
- Enumeración se ordena y selecciona una voz predeterminada determinista del adaptador.
- Voz exacta, rate y volumen llegan una vez al sintetizador.
- Voz guardada ausente usa la predeterminada, conserva la solicitada y emite una única
  advertencia segura por sesión.
- Síntesis produce WAV rebobinado; reproducción carga antes de sonar.
- Stream inválido falla sin iniciar reproducción ni producir fallback sonoro.
- Éxito, fallo de síntesis, fallo de reproducción, cancelación local y cancelación global
  liberan exactamente una vez sintetizador, jugador, artefacto y exclusión.
- El buffer se sobrescribe tras éxito, fallo y ambas cancelaciones.
- Dos preparaciones/reproducciones concurrentes no se solapan; el segundo waiter puede
  cancelarse sin liberar el turno activo.
- Los resultados no contienen texto, audio, voz, dispositivo o excepción.
- La factoría fuera de Windows y ante excepción de inicialización devuelve el
  coordinador no disponible sin intentar reproducir.

**Criterio de salida**

Los adaptadores quedan aislados detrás de los contratos existentes, la suite portable
no requiere Windows y el único smoke test real pendiente es el manual de cierre.

## Incremento 4 — Lectura concurrente segura y control `/stop`

**Archivos**

- Modificar `src/LocalAssistant.TerminalClient/TerminalConsole.cs`.
- Modificar `src/LocalAssistant.TerminalClient/TerminalClientTui.cs`.
- Modificar `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`.

**Implementación**

1. Añadir una capacidad interna de lectura de línea asíncrona y cancelable a las dos
   consolas productivas. Mantener los métodos públicos síncronos para compatibilidad.
2. Hacer que el adaptador TUI reutilice su única solicitud/TCS existente, observe
   cancelación sin bloquear el hilo del host y no publique secretos en transcript.
3. En la consola plain usar `Console.In.ReadLineAsync(CancellationToken)` cuando esté
   disponible. No crear una lectura por reproducción ni abandonar tareas en background.
4. Sustituir únicamente la lectura principal del chat por una operación que reutilice
   una tarea prefetched; los prompts de pairing, confirmación y administración mantienen
   su semántica actual.
5. Al comenzar reproducción, crear un `CancellationTokenSource` local enlazado al token
   global y competir la reproducción con la única lectura principal pendiente.
6. Reconocer `/stop` mediante un helper compartido por el control concurrente y el
   manejador ordinario. La TUI no inspecciona la línea.
7. Conservar una línea distinta de `/stop` como entrada anticipada y procesarla una sola
   vez. Si audio termina primero, conservar la misma tarea pendiente para el siguiente
   ciclo. EOF detendrá audio y cerrará por la ruta normal.
8. Diferenciar el resultado `Stopped` de cancelación global y de fallo del reproductor.
   `/stop` vuelve a `Ready/None` sin error; `Ctrl+C` termina en `Closing -> Closed`.

**Pruebas obligatorias**

- `/stop` durante reproducción cancela solo el token local, espera al reproductor, no
  cancela HTTP y no publica error incierto.
- `/stop` cuando no hay audio no invoca al reproductor ni cambia el snapshot.
- Una línea normal y cada comando no sensible introducidos durante reproducción se
  procesan exactamente una vez después de terminar.
- Si reproducción termina primero, la lectura pendiente se reutiliza y no roba ni
  duplica la siguiente entrada.
- EOF durante reproducción detiene audio y cierra limpiamente.
- `Ctrl+C` durante síntesis, reproducción y lectura pendiente termina en
  `Closing -> Closed`, sin degradarse a `/stop`.
- Solo existe una lectura pendiente tanto en plain como TUI.
- Resize y publicaciones de snapshot siguen procesándose mientras la aplicación espera
  entrada o reproducción.
- La TUI no contiene lógica para reconocer `/stop` y una entrada secreta nunca participa
  en el mecanismo prefetched.

**Criterio de salida**

El usuario puede detener audio real sin perder una línea futura, bloquear la TUI ni
alterar el resultado del turno.

## Incremento 5 — Preferencias, comandos y `/repeat`

**Archivos**

- Modificar `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`.
- Modificar `src/LocalAssistant.TerminalClient/SpokenOutput.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`.
- Modificar dobles compartidos de salida y persistencia del cliente terminal.

**Implementación**

1. Cargar las preferencias locales una vez tras obtener una credencial válida y antes
   del primer turno. Aplicarlas al coordinador y publicar el snapshot efectivo.
2. Implementar `/voice` y `/voice <nombre exacto>` usando el resto completo de la línea,
   sin coincidencias parciales, fuzzy matching ni texto controlado por el servidor.
3. Implementar `/rate`, `/volume`, `/mute` y `/unmute`. Validar antes de persistir y
   aplicar en memoria únicamente tras una escritura correcta.
4. Ante fallo de escritura, conservar la preferencia y snapshot anteriores, publicar
   `speech_preferences_not_saved` y permitir que el chat continúe.
5. Retener solo el último `ConversationResponse.Content` final elegible ya mostrado.
   Actualizarlo aunque la salida esté silenciada o no disponible; no retener ninguna
   otra salida.
6. Implementar `/repeat` sin request HTTP. Reutilizar la misma preparación,
   reproducción, cancelación y tratamiento de errores que una respuesta nueva.
7. Actualizar `/help` y `/info` con comandos y configuración efectiva, sin mostrar
   contenido retenido ni valores protegidos.

**Pruebas obligatorias**

- Cada comando válido persiste el valor, publica exactamente un cambio y afecta a la
  siguiente síntesis.
- Todos los límites válidos y valores fuera de rango, no enteros, argumentos ausentes,
  sobrantes y nombres de voz no enumerados.
- Fallo de persistencia conserva byte por byte el estado anterior, la preferencia
  efectiva y credencial; el error no se limpia en la misma operación fallida.
- `/mute` omite síntesis posterior; `/unmute` no reproduce automáticamente y habilita la
  siguiente respuesta.
- `/repeat` usa exactamente el último texto elegible, sintetiza de nuevo una vez y no
  añade requests HTTP.
- `/repeat` conserva el texto tras fallo de síntesis/reproducción y después de
  `/new` o `/provider`.
- `/repeat` no retiene historial, listado, error, confirmación pendiente, contenido
  vacío, herramientas, ayuda, prompts o entradas secretas.
- `/repeat` sin valor y comandos con salida `Unavailable` son resultados normales.
- Fallos locales no cambian conversación, proveedor, bearer ni credencial y permiten
  un turno posterior.

**Criterio de salida**

Todas las preferencias y comandos del roadmap funcionan mediante el mismo controlador
en plain y TUI, sin segundo parser ni tráfico HTTP adicional.

## Incremento 6 — Presentación accesible y composición productiva

**Archivos**

- Modificar `src/LocalAssistant.TerminalClient/Program.cs`.
- Modificar `src/LocalAssistant.TerminalClient/TerminalClientStateTextSink.cs`.
- Modificar `src/LocalAssistant.TerminalClient/TerminalClientTui.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientProgramTests.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientStateTextSinkTests.cs`.
- Modificar `tests/LocalAssistant.Tests/TerminalClient/TerminalClientTuiTests.cs`.

**Implementación**

1. Crear una factoría productiva que seleccione SAPI o salida no disponible antes de
   construir `TerminalClientApplication`. Usar la misma decisión en plain y TUI.
2. No capturar excepciones de conversación ni cambiar de renderer como consecuencia de
   un fallo TTS; solo la inicialización local del adaptador puede degradar la capacidad.
3. Mostrar en texto y TUI disponibilidad, voz efectiva, rate, volumen, silencio y
   `PlayingVoice` mediante etiquetas estables sin color obligatorio.
4. Mostrar la voz guardada ausente solo como advertencia segura y no enumerar voces en
   snapshots, logs o métricas. `/voice` es el único listado solicitado por el usuario.
5. Mantener sanitización ANSI, presupuesto de transcript, resize, scroll, prioridad de
   confirmación, entrada secreta y restauración del terminal.

**Pruebas obligatorias**

- Factoría Windows con voces crea coordinador disponible; sin voces o con excepción
  crea el no disponible.
- Factoría no Windows no invoca ninguna frontera SAPI.
- Plain y TUI reciben la misma disponibilidad y preferencias efectivas.
- Etiquetas para unavailable, ready, muted y playing incluyen rate/volumen/voz sin
  depender del color ni duplicar respuestas.
- Un nombre de voz con caracteres de control se normaliza antes de presentación.
- Salida redirigida no contiene ESC, ANSI u OSC.
- Fallos del sink siguen aislados de síntesis, conversación y cierre.
- Ejecución productiva no crea archivos WAV ni procesos externos.

**Criterio de salida**

La composición real queda activa solo en Windows compatible y ambas presentaciones
reflejan el mismo estado sin romper las garantías de accesibilidad existentes.

## Incremento 7 — Documentación, seguridad y verificación manual

**Archivos**

- Modificar `README.md`.
- Modificar `docs/ARCHITECTURE.md`.
- Modificar `docs/SECURITY.md`.
- Modificar `docs/ROADMAP.md` al final, no antes.
- Añadir o ampliar una evidencia bajo `docs/evaluations/` para la prueba manual Windows.

**Implementación documental**

1. Documentar requisito Windows, dependencia en voces instaladas, valores iniciales,
   comandos y degradación textual.
2. Explicar que la API sigue siendo textual, SAPI es local, el WAV no se persiste y el
   texto de `/repeat` vive solo en memoria.
3. Documentar el formato DPAPI versionado, metadatos legibles y recuperación ante
   corrupción o voz ausente sin exponer datos reales.
4. Registrar versiones/licencias de los paquetes Microsoft y que Piper queda aplazado.
5. Documentar una prueba manual reproducible y después registrar su resultado real.
6. Marcar el punto 7 del roadmap solo cuando todas las pruebas automáticas, auditoría de
   dependencias y prueba manual hayan terminado correctamente.

**Escenarios manuales obligatorios**

- Windows Terminal y PowerShell con una voz española habilitada.
- `/voice`, selección exacta, `/rate`, `/volume` y persistencia tras reinicio.
- `/mute`, `/unmute`, `/stop` durante reproducción y `/repeat`.
- Texto con caracteres españoles, pegado, resize y transcript largo.
- `Ctrl+C`, EOF y `/exit`, comprobando restauración de cursor y ausencia de bloqueo.
- Verificación del directorio local antes y después para demostrar que no aparecen WAV.
- Ejecución con capacidad forzada a no disponible para comprobar degradación textual.

**Criterio de salida**

La evidencia no contiene conversaciones, voces personales completas, credenciales ni
rutas personales y el roadmap describe exclusivamente comportamiento comprobado.

## Verificación por incremento

Al terminar cada incremento de código ejecutar:

```powershell
dotnet format LocalAssistant.sln --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-build --filter "FullyQualifiedName~TerminalClient"
git diff --check
```

Después del incremento 3 y antes de cierre ejecutar también:

```powershell
dotnet list src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj package --include-transitive
dotnet list src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj package --vulnerable --include-transitive
```

Antes de solicitar commit o PR ejecutar la suite completa:

```powershell
dotnet test tests/LocalAssistant.Tests/LocalAssistant.Tests.csproj -c Release --no-build
```

Finalizar cada tanda comprobando que no quedan procesos `testhost`,
`LocalAssistant.TerminalClient` ni `LocalAssistant.Api` residuales. El pre-PR deberá
aplicar el checklist de revisión del repositorio al diff completo.

## Criterio de cierre del incremento 7

El punto 7 queda cerrado únicamente cuando:

- SAPI se selecciona solo en Windows con voces habilitadas;
- voz, rate, volumen y silencio sobreviven al reinicio sin perder credenciales;
- `/stop` detiene únicamente audio y ninguna línea pendiente se pierde o duplica;
- `/repeat` no persiste contenido ni llama a HTTP;
- audio y buffers se liberan y limpian tras todas las rutas;
- fallos locales degradan a texto y nunca hacen incierto un turno;
- plain, TUI y salida redirigida conservan sus garantías;
- CI Linux, suite completa, auditoría de dependencias y prueba manual Windows pasan;
- documentación y roadmap coinciden con lo realmente entregado.
