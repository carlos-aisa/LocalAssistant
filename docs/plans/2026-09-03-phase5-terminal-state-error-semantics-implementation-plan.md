# Plan de implementación: semántica de error del estado terminal

## Paso 1: separar gravedad e incertidumbre

**Archivos:** `TerminalClientState.cs`, `PrivateApiClient.cs`, pruebas de estado y
adaptador.

Sustituir la categoría única por severidad e incertidumbre independientes. Marcar
pairing, rotación y revocación como operaciones mutables inciertas tras un fallo de
transporte. Actualizar invariantes de `Blocked` para exigir severidad bloqueante sin
perder la incertidumbre.

## Paso 2: hacer explícitos resultados y transiciones

**Archivos:** `TerminalClientApplication.cs`, pruebas del cliente terminal.

Introducir resultados internos para adquisición de credenciales y actualización de la
última conversación. Evitar que una escritura local fallida se borre con un `Ready`
inmediato. Centralizar la transición terminal de pairing y hacer que `MoveTo` falle de
forma explícita ante un error de la propia aplicación. Retirar la transición prohibida
del grafo y adaptar el flujo de confirmación para no necesitarla.

## Paso 3: demostrar casos de recuperación segura

**Archivos:** `PrivateApiClientTests.cs`, `TerminalClientStateTests.cs`,
`TerminalClientApplicationTests.cs`.

Cubrir timeout/desconexión de pairing, rotación y revocación; retención de error de
preferencia local; pairing fallido con salida `1`; y rechazo de la transición
`Ready/None → AwaitingConfirmation`.

## Verificación

Ejecutar formato, compilación Release, la suite completa de pruebas y `git diff --check`.

Antes de publicar se ejecutará la revisión exigida por `AGENTS.md`.
