# Diseño: modelo de estado del cliente terminal

## Objetivo

Entregar el incremento 4 de la fase 5: hacer explícito el estado observable del
cliente terminal .NET, sin construir una TUI ni activar salida de voz. El modelo
permitirá que la futura TUI reciba el snapshot completo vigente y se redibuje sin
reconstruir estado a partir de eventos.

## Decisiones

- `TerminalClientApplication` es la única autoridad que inicia transiciones.
- Un coordinador interno, propiedad exclusiva de la aplicación, conserva el snapshot
  vigente, valida las transiciones y lo publica mediante un sink inyectable.
- El sink recibe snapshots inmutables completos; no recibe un registro de eventos, no
  puede modificarlos y no puede solicitar transiciones.
- Un sink textual muestra cambios útiles. Las pruebas usan un sink recolector para
  comprobar secuencias e intentos de transición inválidos.
- Las excepciones del sink se ignoran de forma segura: nunca interrumpen autenticación,
  conversación, recuperación ni cierre.
- `PlayingVoice` forma parte del contrato para compatibilidad futura, pero este
  incremento no permite ninguna transición hacia él ni simula reproducción.

## Contrato interno

El snapshot separa tres dimensiones para evitar un enum plano ambiguo:

| Dimensión | Valores |
| --- | --- |
| Ciclo de vida | `Disconnected`, `Connecting`, `Authenticating`, `Ready`, `Closing`, `Closed`, `Blocked` |
| Actividad | `None`, `ResumingConversation`, `SelectingConversation`, `SendingTurn`, `AwaitingConfirmation`, `ResolvingConfirmation`, `CompletingConversation`, `PlayingVoice` |
| Error | Ninguno o error seguro `Recoverable`, `Uncertain` o `Blocking` |

El snapshot añade solo contexto no sensible: proveedor seleccionado, identificador de
la conversación activa y metadatos seguros de una confirmación pendiente. Los cambios
de proveedor, conversación o confirmación son cambios observables aunque el ciclo de
vida y la actividad no cambien. Nunca contiene credenciales, bearer, desafíos,
mensajes, prompts, argumentos ni resultados de herramientas.

El error usa un DTO seguro con `Category`, `Code`, `SafeMessage` y `Operation`. La
categoría es `Recoverable`, `Uncertain` o `Blocking`; la operación identifica la fase
segura, como health, autenticación, reanudación, turno, confirmación o completion. El
error se conserva en snapshots posteriores hasta que una operación posterior termina
correctamente. Un éxito publica entonces el snapshot actualizado sin error.

## Transiciones

| Disparador real | Ciclo de vida | Actividad | Error |
| --- | --- | --- | --- |
| Inicio de `RunAsync` | `Disconnected` | `None` | ninguno |
| Comprobación de health | `Connecting` | `None` | ninguno |
| Pairing o creación de sesión | `Authenticating` | `None` | ninguno |
| Sesión válida | `Ready` | `None` | ninguno |
| Reanudar o elegir conversación | `Ready` | `ResumingConversation` o `SelectingConversation` | ninguno |
| Enviar turno | `Ready` | `SendingTurn` | ninguno |
| Confirmación recibida | `Ready` | `AwaitingConfirmation` | ninguno |
| Resolver confirmación | `Ready` | `ResolvingConfirmation` | ninguno |
| Solicitar completion | `Ready` | `CompletingConversation` | ninguno |
| Resultado incierto de turno o transporte | `Ready` | `None` | `Uncertain` |
| Error recuperable | `Ready` | `None` | `Recoverable` |
| Health, autenticación o recuperación definitivamente fallidos | `Blocked` | `None` | `Blocking` |
| Salida o EOF | `Closing` y después `Closed` | `CompletingConversation` o `None` | último error seguro, si existe |

Una recuperación satisfactoria publica un snapshot `Ready` sin error tras el snapshot
del error. Un `404` concluyente durante la reanudación conserva esta regla y limpia la
preferencia local existente. Solo una operación que pudo alcanzar el servidor, como un
turno, una decisión de confirmación o completion tras enviar la solicitud, puede ser
`Uncertain`. Health, validación local, detalle, listado, errores HTTP concluyentes y
cancelaciones antes del envío no se clasifican como incertidumbre de turno.

## Grafo de transiciones permitidas

| Origen | Destino permitido | Condición y retorno |
| --- | --- | --- |
| `Disconnected` | `Connecting` | Inicio de una ejecución. |
| `Connecting` | `Authenticating` | Health correcto y credencial disponible. |
| `Connecting` | `Blocked` | Health falla de forma bloqueante. |
| `Authenticating` | `Ready/None` | Sesión o pairing válido. |
| `Authenticating` | `Blocked` | Autenticación definitivamente rechazada o cancelada. |
| `Ready/None` | `Ready/ResumingConversation` | Validar o cargar la última conversación. Retorna a `Ready/None`. |
| `Ready/None` | `Ready/SelectingConversation` | Listar, paginar o seleccionar conversación. Retorna a `Ready/None`. |
| `Ready/None` | `Ready/SendingTurn` | Enviar turno. Retorna a `Ready/None` o pasa a `Ready/AwaitingConfirmation`. |
| `Ready/AwaitingConfirmation` | `Ready/ResolvingConfirmation` | Usuario aprueba o rechaza. Retorna a `Ready/None` o a `Ready/AwaitingConfirmation` si sigue pendiente. |
| `Ready/*` | `Ready/CompletingConversation` | `/new`, cambio de proveedor, selección o `/exit`. Retorna a `Ready/None` antes de cualquier cierre. |
| `Ready/*` | `Ready/None` con error | Error recuperable o incierto; toda actividad termina explícitamente. |
| `Ready/*` | `Blocked/None` | Error bloqueante; toda actividad termina explícitamente. |
| `Ready/None` | `Closing/None` | EOF o salida sin completion pendiente. |
| `Ready/CompletingConversation` | `Ready/None` | Completion de `/exit`, antes de `Closing`. |
| `Closing/*` o `Blocked/*` | `Closed/None` | `finally` de `RunAsync`, para salida controlada, error capturado o cancelación capturada. |

No existe una transición entrante hacia `PlayingVoice` en este incremento. Toda
actividad alcanzable vuelve obligatoriamente a `Ready/None` antes de iniciar otra
actividad, bloquear o cerrar.

## Invariantes

1. Cada ejecución publica un snapshot inicial `Disconnected` y termina con uno final
   `Closed`, incluso si pasa por `Blocked`; `RunAsync` usa `finally` para garantizar
   `Closing → Closed` en toda salida controlada, error o cancelación capturada.
2. Dos snapshots iguales consecutivos no generan notificación.
3. Una transición no permitida no modifica el snapshot ni genera publicación.
4. Solo `TerminalClientApplication` solicita transiciones; los sinks son pasivos.
5. La notificación es síncrona y se ejecuta tras actualizar el snapshot, pero las
   excepciones del sink quedan aisladas.
6. `PlayingVoice` no puede ser el destino de ninguna transición del incremento 4.
7. El sink textual no repite snapshots ni vuelca contexto sensible o contenido
   conversacional. Solo muestra cambios operacionales que no estén ya descritos por
   `ITerminalConsole`; no reproduce respuestas, historial, herramientas, prompts ni
   compone un modelo de pantalla.

## Pruebas

- Secuencias de arranque, autenticación, reanudación, envío, confirmación, recuperación,
  resultado incierto, bloqueo y cierre.
- Snapshot inicial y final, orden determinista y supresión de duplicados.
- Transiciones inválidas rechazadas sin publicación.
- Sink que lanza excepción sin afectar la operación del cliente.
- Snapshot sin secretos ni contenido conversacional.
- Ausencia de transiciones a `PlayingVoice`.

## No objetivos

No se añaden dependencias de TUI, widgets, renderizado visual, audio, TTS, STT,
cancelación fiable de turnos, eventos persistidos, nuevos endpoints ni cambios en la
API HTTP.
