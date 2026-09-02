# Diseño: correcciones de coherencia del modelo de estado terminal

## Alcance

Esta enmienda completa el incremento 4 de la fase 5. Sustituye únicamente las reglas
del diseño de estado que resultaron demasiado permisivas durante la revisión; no añade
una TUI, contratos HTTP, persistencia nueva ni reproducción de voz.

## Decisiones

- El proveedor observable procede siempre de la sesión actual. El selector recibe y
  devuelve ese proveedor; no lee la configuración inicial después de `/provider`.
- El coordinador valida dos cosas antes de publicar: el grafo de transición y la
  coherencia completa del snapshot. Un snapshot inválido no modifica el estado ni
  notifica al sink.
- `Ready` requiere proveedor. Solo `AwaitingConfirmation` contiene una confirmación
  pendiente y la exige. `Connecting` y `Authenticating` no contienen conversación ni
  confirmación. `Blocked` exige un error `Blocking`; ningún otro ciclo admite esa
  categoría.
- La cancelación no puede degradar un `Uncertain` ya registrado por un turno,
  confirmación o completion que pudo alcanzar el servidor. La cancelación de health
  identifica la operación como `health`.
- Todo fallo operativo visible en consola se refleja en el snapshot con un error
  seguro: pairing, rotación, revocación, apertura de sesión después de rotar y cambios
  en el almacén local de credenciales o de la última conversación.

## Pruebas exigidas

Las pruebas cubrirán las invariantes completas, cambio de proveedor seguido de
selector, las secuencias `SelectingConversation`, `SendingTurn`,
`AwaitingConfirmation → ResolvingConfirmation`, cancelación durante completion y las
rutas operativas de error anteriores. Los asserts observarán snapshots y salida
pública, sin inspeccionar secretos ni estado privado.

## No objetivos

No se activa `PlayingVoice`, no se implementa la TUI y no se cambian los contratos de
red ni la semántica de autorización.
