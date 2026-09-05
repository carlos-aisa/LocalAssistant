# Diseño: semántica de error del estado terminal

## Alcance

Esta enmienda completa el incremento 4 de la fase 5. Corrige la representación de
incertidumbre y gravedad, la persistencia de la última conversación y la adquisición
de credenciales. No incorpora TUI, endpoints, reintentos ni cambios de servidor.

## Decisiones

- `TerminalClientOperationError` separa `Severity` (`Recoverable` o `Blocking`) de
  `IsUncertain`. Un resultado puede ser bloqueante e incierto a la vez.
- Pairing, rotación y revocación son operaciones HTTP con efecto lateral. Timeout,
  desconexión, cancelación posterior al envío y una respuesta `2xx` cuyo contrato no
  pueda validarse se publican como inciertos. Una incertidumbre administrativa bloquea
  el cliente: continuar podría usar una credencial revocada o ya sustituida.
- `UpdateLastConversationAsync` devuelve el resultado de persistencia junto a la
  credencial. No muta el snapshot por sí misma. El flujo que la invoca conserva el
  error cuando la actualización local falla y solo lo limpia tras una operación
  posterior realmente satisfactoria.
- La obtención de credenciales distingue explícitamente credencial válida,
  cancelación del usuario y fallo de pairing. Solo `RunAsync` publica la transición
  terminal correspondiente. El fallo real devuelve código de salida `1`; la
  cancelación devuelve `2`.
- Una transición inválida solicitada por `TerminalClientApplication` lanza una
  excepción interna. Los sinks continúan aislados; el rechazo del coordinador sigue
  siendo observable y no muta el snapshot.
- El grafo vuelve a prohibir `Ready/None → Ready/AwaitingConfirmation`. Una
  confirmación solo nace después de `SendingTurn` o vuelve desde
  `ResolvingConfirmation`.

## Pruebas exigidas

Las pruebas demostrarán la incertidumbre de operaciones administrativas, la combinación
de bloqueo e incertidumbre, la retención del error de persistencia local, el resultado
de pairing fallido y la excepción ante una transición inválida iniciada por la
aplicación. También comprobarán que el coordinador rechaza la transición prohibida.

## No objetivos

No se añaden reintentos automáticos, cancelación fiable de servidor, nuevos contratos
HTTP ni recuperación automática de una rotación incierta.
