# Plan de implementación: fase 5, incremento 4 — modelo de estado del cliente terminal

## Objetivo

Hacer explícito el estado operativo del cliente terminal .NET para que una futura TUI pueda renderizar el snapshot vigente sin reconstruirlo desde eventos. `TerminalClientApplication` seguirá siendo la única autoridad que inicia transiciones; los consumidores solo observarán snapshots inmutables.

El incremento no cambia contratos HTTP, persistencia ni autorización. Reutiliza el flujo textual, pairing, renovación de sesión, reanudación, selector, confirmaciones y completion ya entregados.

## Decisiones consolidadas

- El contrato será interno al ensamblado del cliente. El constructor público existente conservará su uso actual; una sobrecarga interna, visible al ensamblado de pruebas mediante `InternalsVisibleTo`, permitirá inyectar el sink. Así la futura TUI podrá reutilizarlo dentro del cliente sin declarar una API pública estable prematuramente.
- Cada publicación contiene el snapshot completo, no un delta ni un evento. El sink es síncrono, pasivo y sus excepciones se aíslan para que nunca afecten la operación.
- El snapshot separa ciclo de vida, actividad, error seguro y contexto seguro. No incluye bearer, credenciales, desafíos, mensajes, historial, prompts, argumentos ni resultados de herramientas.
- `PlayingVoice` se declara como actividad futura, pero ninguna transición de este incremento podrá tenerlo como destino.
- El error se conserva hasta que una operación posterior concluya correctamente. `Uncertain` queda limitado a envío de turno, resolución de confirmación y completion que realmente pudieron llegar al servidor; lecturas, validaciones y rechazos HTTP concluyentes son recuperables.
- `RunAsync` publicará siempre el estado inicial y garantizará `Closing → Closed` desde un `finally`, también ante cancelación capturada. El programa convertirá `Ctrl+C` en un `CancellationToken`; no afirmará que eso cancela de forma fiable un turno HTTP ya despachado.

## Paso 1: definir el contrato interno, las invariantes y el publicador

**Archivos:**

- Nuevo `src/LocalAssistant.TerminalClient/TerminalClientState.cs`.
- Nuevo o ajustado `src/LocalAssistant.TerminalClient/Properties/AssemblyInfo.cs`.
- `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`.

Crear records y enums internos inmutables para `TerminalClientLifecycle` (`Disconnected`, `Connecting`, `Authenticating`, `Ready`, `Closing`, `Closed`, `Blocked`); `TerminalClientActivity` (`None`, `ResumingConversation`, `SelectingConversation`, `SendingTurn`, `AwaitingConfirmation`, `ResolvingConfirmation`, `CompletingConversation`, `PlayingVoice`); `TerminalClientErrorCategory` (`Recoverable`, `Uncertain`, `Blocking`); `TerminalClientError` (categoría, código, mensaje seguro y operación); y `TerminalClientStateSnapshot` (las tres dimensiones, proveedor, `ConversationId` y metadatos seguros de confirmación: nombre de herramienta y caducidad, sin ID opaco ni argumentos).

Definir `ITerminalClientStateSink`, `NullTerminalClientStateSink` y un coordinador interno de estado propiedad de una sola instancia de `TerminalClientApplication`. El coordinador recibirá intenciones semánticas solo de la aplicación, verificará el grafo aprobado, actualizará antes de invocar el sink, suprimirá snapshots consecutivos iguales mediante igualdad de records y capturará cualquier excepción del observador. Una transición inválida no cambiará el snapshot ni publicará nada; devolverá un resultado interno para que las pruebas demuestren el rechazo sin crear una recuperación silenciosa.

Implementar expresamente este grafo:

| Origen | Destino | Regla |
| --- | --- | --- |
| `Disconnected/None` | `Connecting/None` | Una única vez al comenzar `RunAsync`. |
| `Connecting/None` | `Authenticating/None` | Health válido y comienzo de credencial/sesión. |
| `Connecting/None` | `Blocked/None` | Health bloqueante. |
| `Authenticating/None` | `Ready/None` | Sesión válida, también tras pairing o recuperación. |
| `Authenticating/None` | `Blocked/None` | Autenticación rechazada definitivamente o cancelada. |
| `Ready/None` | `Ready/ResumingConversation` o `Ready/SelectingConversation` | Reanudación, listado, detalle, historial o selección. |
| `Ready/None` | `Ready/SendingTurn` | Envío de mensaje. |
| `Ready/SendingTurn` | `Ready/AwaitingConfirmation` | Confirmación pendiente recibida. |
| `Ready/AwaitingConfirmation` | `Ready/ResolvingConfirmation` | El usuario aprueba o rechaza. |
| `Ready/ResolvingConfirmation` | `Ready/None` o `Ready/AwaitingConfirmation` | Decisión concluida o nueva confirmación. |
| `Ready/*` | `Ready/CompletingConversation` | `/new`, proveedor, selección o `/exit`. |
| `Ready/*` | `Ready/None` | Éxito, error recuperable o incierto; toda actividad vuelve aquí. |
| `Ready/*` | `Blocked/None` | Solo error que impide continuar. |
| `Ready/None` | `Closing/None` | EOF, salida o cancelación controlada sin completion activo. |
| `Ready/CompletingConversation` | `Ready/None` | Completion de `/exit`, antes del cierre. |
| `Closing/*` o `Blocked/*` | `Closed/None` | `finally` de `RunAsync`. |

No habrá transición entrante a `PlayingVoice`. El coordinador permitirá cambios de proveedor, conversación o confirmación dentro de `Ready/None` como publicaciones observables aunque ciclo de vida y actividad no cambien.

## Paso 2: integrar las transiciones en el flujo del cliente

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`.
- `src/LocalAssistant.TerminalClient/Program.cs`.
- `src/LocalAssistant.TerminalClient/TerminalConsole.cs`.

Reestructurar `RunAsync` alrededor de una única envoltura `try`/`catch`/`finally` que publique `Disconnected/None`, inicie conexión y termine siempre en `Closing/None` seguido de `Closed/None`. Conservar códigos de salida y mensajes seguros: health o autenticación bloqueantes producen `Blocked`; EOF, `/exit` y cancelación controlada cierran sin exponer una excepción. El `finally` no borrará el último error seguro, pero normalizará la actividad a `None`.

En `Program.cs`, crear un `CancellationTokenSource` de duración de aplicación y registrar `Console.CancelKeyPress` para solicitar cancelación sin terminar abruptamente. Desregistrar el handler y liberar el origen al terminar. El cliente solo clasificará como incierta una cancelación mientras turno, confirmación o completion pudieron despacharse; cancelar antes de enviar, health, sesión, listado, detalle e historial no se presentará como incertidumbre conversacional.

Introducir ayudantes privados para iniciar actividad, finalizarla con éxito y registrar un error seguro. El mapeo será consciente de la operación:

- Health, carga/guardado local, pairing, apertura o renovación de sesión usan `Connecting` o `Authenticating`; un fallo que finaliza el proceso es `Blocking`.
- Reanudación, listado, detalle e historial usan sus actividades de lectura y sus fallos son `Recoverable`, incluso si el adaptador HTTP marca un `5xx` genérico como incierto para otra clase de petición.
- `SendAsync`, `ResolveConfirmationAsync` y `CompleteAsync` conservan la evidencia del adaptador. Timeout, desconexión, cancelación posterior al envío o respuesta sin contrato que pudo dejar efecto son `Uncertain`; respuesta estructurada procesada o rechazo concluyente son `Recoverable`.
- Una respuesta válida actualiza `ConversationId` antes de publicar éxito. Una confirmación actualiza metadatos seguros y transita a `AwaitingConfirmation`; resolverla los limpia solo cuando termina realmente.
- `/new`, `/provider`, selección y `/exit` publican `CompletingConversation`. Para `/exit`, la secuencia será obligatoriamente `CompletingConversation → Ready/None → Closing/None → Closed/None`.
- Un éxito posterior elimina el error retenido. Un fallo posterior lo sustituye por el nuevo error seguro.

Mantener la aplicación como única autoridad: ni `PrivateApiClient`, ni almacén DPAPI, ni consola, ni sink cambiarán estado. No se añadirán reintentos, cambios de completion ni contratos HTTP.

## Paso 3: añadir el sink textual sin duplicar la consola existente

**Archivos:**

- Nuevo `src/LocalAssistant.TerminalClient/TerminalClientStateTextSink.cs`.
- `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`.
- `src/LocalAssistant.TerminalClient/Program.cs`.

Implementar un sink interno que use `ITerminalConsole` y reciba snapshots completos. Su proyección será mínima: anunciará solo cambios operacionales que no cubran ya los mensajes de la aplicación, como el comienzo de conexión o autenticación. No repetirá `WriteError`, prompts de pairing, confirmación, historial, respuestas, herramientas, iteraciones, contenido de conversación ni detalle de error. No será historial de UI ni intentará redibujar la consola.

El constructor público actual de `TerminalClientApplication` construirá este sink textual. Una sobrecarga interna aceptará un sink explícito para pruebas y composición futura dentro del mismo ensamblado. Ambas rutas usarán el mismo coordinador y aislamiento de excepciones.

## Paso 4: demostrar el modelo con pruebas deterministas

**Archivos:**

- Nuevo `tests/LocalAssistant.Tests/TerminalClient/TerminalClientStateTests.cs`.
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`.
- `tests/LocalAssistant.Tests/TerminalClient/PrivateApiClientTests.cs`, solo si hace falta programar explícitamente un fallo o cancelación ya representado por el adaptador.

Añadir un sink recolector interno que guarde snapshots e intentos de transición inválidos. No modificará estado ni usará reloj, red, procesos ni host HTTP real.

Las pruebas unitarias del coordinador comprobarán:

- snapshot inicial y final explícitos, orden determinista y supresión de duplicados;
- rechazo sin mutación ni publicación de transiciones inválidas relevantes;
- proveedor, conversación y confirmación como publicaciones distintas;
- retención del error hasta un éxito posterior y sustitución por error nuevo;
- ausencia de ruta hacia `PlayingVoice`;
- excepción del sink aislada, con estado actualizado para la publicación siguiente;
- contrato del snapshot sin campos que transporten secreto o contenido conversacional.

Las pruebas de flujo usarán `RecordingHttpMessageHandler`, `ScriptedTerminalConsole` y almacén de credenciales en memoria para verificar:

- arranque: `Disconnected`, `Connecting`, `Authenticating`, `Ready`, `Closing`, `Closed`;
- health o autenticación bloqueantes seguidos siempre de `Closing` y `Closed`;
- reanudación, selector e historial retornando a `Ready/None` y actualizando contexto solo tras respuesta válida;
- envío con confirmación, decisión approve/reject y limpieza o renovación de confirmación pendiente;
- fallo recuperable seguido de éxito que borra el error;
- timeout de turno, confirmación o completion como `Uncertain`, y timeout de lectura como `Recoverable`;
- `/new`, proveedor, selección y especialmente `/exit` con la secuencia de completion aprobada;
- cancelación controlada y sink que falla sin impedir autenticación, envío ni cierre;
- snapshots y salida capturada sin credencial, bearer, desafío ni mensaje de prueba secreto.

Las aserciones usarán snapshots completos y rutas/cabeceras observables, no variables privadas. No se añadirán esperas: los dobles controlarán la cancelación y todos los `HttpClient`, handlers y consolas se liberarán mediante `using` para no dejar recursos de test.

## Paso 5: actualizar documentación y cerrar el incremento

**Archivos:**

- `README.md`.
- `docs/SECURITY.md`.
- `docs/ROADMAP.md`.
- `docs/specs/2026-09-02-phase5-terminal-state-model-design.md`.

Actualizar README con la observabilidad operacional sin prometer una TUI. Precisar en `SECURITY.md` que snapshots y sink textual no contienen secretos ni contenido de conversación y que un resultado incierto solo expresa posible recepción por servidor, no cancelación fiable. Mantener el diseño como trazabilidad de las decisiones aprobadas y marcar el incremento 4 en el roadmap solo cuando código, pruebas y documentación estén integrados.

No se actualizará OpenAPI ni se añadirá ADR: no cambian contrato HTTP ni una decisión arquitectónica que trascienda el incremento interno ya documentado.

## Verificación

Desde la raíz, después de restaurar dependencias si fuera necesario:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test LocalAssistant.sln -c Release --no-restore
git diff --check
```

Durante el desarrollo se ejecutarán también las clases de terminal afectadas de forma focalizada. Antes de abrir la PR, se ejecutará la revisión de diff exigida por `AGENTS.md`, se corregirán hallazgos mecánicos y se repetirán las verificaciones afectadas. El incremento no estará terminado hasta que la suite pruebe transiciones y cierre, y roadmap y documentación reflejen exactamente el comportamiento entregado.
