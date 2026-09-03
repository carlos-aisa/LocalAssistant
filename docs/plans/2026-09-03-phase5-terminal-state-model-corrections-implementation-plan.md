# Plan de implementación: correcciones del estado terminal

## Objetivo

Cerrar los gaps de coherencia, cancelación y errores observables del incremento 4 de
la fase 5, manteniendo `TerminalClientApplication` como única autoridad de estado.

## Paso 1: validar invariantes completas

**Archivos:** `TerminalClientState.cs`, `TerminalClientStateTests.cs`.

Incorporar `IsSnapshotValid(next)` antes del grafo. Exigir proveedor en `Ready`;
restringir la confirmación pendiente a `AwaitingConfirmation` y exigirla allí;
prohibir conversación y confirmación en conexión/autenticación; y exigir error
`Blocking` solo en `Blocked`. Corregir los tests que actualmente aceptan contexto
incoherente y añadir rechazos sin mutación ni publicación.

## Paso 2: conservar contexto y clasificar correctamente la cancelación

**Archivos:** `TerminalClientApplication.cs`, `TerminalClientApplicationTests.cs`.

Propagar el proveedor actual a través de `SelectConversationAsync`. Preservar
`Uncertain` si una cancelación capturada sucede tras completion, turno o confirmación
ya despachados. Etiquetar la cancelación de health como `health`. Verificar las
secuencias completas del selector, envío y confirmación.

## Paso 3: publicar todos los errores operativos seguros

**Archivos:** `TerminalClientApplication.cs`, `TerminalClientApplicationTests.cs`.

Traducir pairing, rotación, revocación, apertura de sesión posterior a rotación y
errores de escritura/eliminación local a `TerminalClientOperationError`, conservando
los mensajes existentes y sin incluir secretos. La categoría será recuperable o
bloqueante según la continuidad real del cliente.

## Paso 4: documentación y verificación

**Archivos:** esta especificación y plan, más `README.md`, `SECURITY.md` y
`ROADMAP.md` solo si cambia lo que describen.

Ejecutar:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test LocalAssistant.sln -c Release --no-restore
git diff --check
```

Antes de PR, ejecutar la revisión de diff exigida por `AGENTS.md`.

## No objetivos

Sin TUI, voz, endpoints, cambios de persistencia ni reintentos nuevos.
