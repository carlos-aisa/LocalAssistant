# Plan de implementación: incertidumbre de operaciones mutables del cliente terminal

## Alcance

Completar el incremento 4 de la fase 5 para que pairing, rotación y revocación no
se presenten como recuperables cuando el cliente no puede demostrar su resultado.
No añade reintentos, endpoints, persistencia ni cambios en el protocolo del servidor.

## Paso 1: validar y clasificar contratos mutables

**Archivos:** `src/LocalAssistant.TerminalClient/PrivateApiClient.cs`,
`tests/LocalAssistant.Tests/TerminalClient/PrivateApiClientTests.cs`.

- Aplicar `canBeUncertainAfterDispatch` a respuestas 2xx cuyo JSON sea inválido o
  cuyo DTO no pueda deserializarse o validarse.
- Añadir validadores explícitos para pairing, rotación y revocación. Pairing y
  rotación requieren `ClientId` y credencial no vacíos; revocación requiere
  `ClientId` no vacío. Rotación y revocación validan además que el identificador
  devuelto coincide con el solicitado.
- Mantener los secretos fuera de errores, snapshots y pruebas de salida.
- Probar `HttpRequestException`, cancelación, JSON malformado, DTO incompleto e
  identificador diferente. Los casos ambiguos deben ser `IsUncertain = true`.

## Paso 2: conservar la incertidumbre de pairing y bloquear operaciones administrativas ambiguas

**Archivos:** `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`,
`tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`.

- No lanzar la cancelación del token antes de consumir el resultado ya clasificado
  de `CompletePairingAsync`; así una cancelación después del envío conserva el
  error `pairing` bloqueante e incierto.
- Comprobar que un contrato mutable ambiguo, incluido un `ClientId` distinto, termina
  en `Blocked` con `Severity = Blocking` e `IsUncertain = true`, sin borrar estado
  local ni continuar el bucle conversacional.
- Añadir una prueba que cancele el mismo `CancellationToken` entregado al request de
  pairing y otra que pruebe la transición inválida solicitada desde la aplicación.

## Paso 3: alinear documentación de seguridad y operación

**Archivos:** `README.md`, `docs/SECURITY.md`, y la especificación de semántica de
errores de fase 5.

- Declarar que pairing, rotación y revocación también pueden ser inciertos tras el
  envío, incluso con un 2xx que no puede validarse.
- Aclarar que el cliente no reintenta automáticamente ni borra credenciales locales
  en un resultado administrativo ambiguo.

## Verificación

Ejecutar `dotnet format LocalAssistant.sln --verify-no-changes --no-restore`,
`dotnet build LocalAssistant.sln --configuration Release --no-restore`, la suite
completa de `LocalAssistant.Tests` y `git diff --check`. Antes de publicar, revisar
el diff conforme a `AGENTS.md`.

## No objetivos

No se implementan reintentos idempotentes, recuperación automática de una rotación
ambigua, cancelación fiable del servidor ni nuevos privilegios administrativos.
