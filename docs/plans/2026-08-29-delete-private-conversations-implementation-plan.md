# Plan de implementación: borrado selectivo de conversaciones privadas

## Alcance y decisiones aplicadas

Este plan implementa la especificación
`docs/specs/2026-08-29-delete-private-conversations-design.md`. Añade una
única operación HTTP para que un propietario autenticado elimine una conversación
privada persistida tras confirmar explícitamente el borrado.

La operación ya existe en `SqliteConversationStore`, por lo que se incorporará al
contrato `IConversationStore` existente en vez de crear otra abstracción de
persistencia. `AuthenticatedConversationStore` delegará el borrado únicamente al
almacén persistente: una conversación anónima nunca se eliminará por esta ruta.

## 1. Completar los contratos de conversación y confirmación

**Archivos:**
`src/LocalAssistant.Core/Conversations/ConversationContracts.cs`,
`src/LocalAssistant.Core/Conversations/InMemoryConversationStore.cs`,
`src/LocalAssistant.Infrastructure/Conversations/SqliteConversationStore.cs` y
`src/LocalAssistant.Core/Orchestration/ToolConfirmationStore.cs`.

- Añadir `DeleteOwnedAsync(Guid conversationId, string ownerPrincipalId,
  CancellationToken cancellationToken)` a `IConversationStore`.
- Mantener la implementación SQLite existente como borrado transaccional de mensajes y
  metadatos condicionado por identificador y propietario.
- Implementar el método en memoria con eliminación atómica condicionada por el mismo
  propietario. Esta implementación conserva la coherencia del contrato, aunque el
  endpoint nunca podrá alcanzarla cuando la persistencia esté desactivada.
- Implementar en `AuthenticatedConversationStore` una delegación exclusiva al almacén
  persistente. No consultar ni eliminar la conversación efímera.
- Añadir `RemoveAsync(Guid conversationId, CancellationToken cancellationToken)` a
  `IToolConfirmationStore` y a su implementación en memoria. Retira una confirmación
  pendiente sin ejecutarla y devuelve si existía.
- Conservar `CancellationToken` en todos los límites asíncronos. No añadir tablas,
  SQL nuevo, logs de contenido ni dependencias.

## 2. Exponer el borrado explícito en la API

**Archivo:** `src/LocalAssistant.Api/Endpoints/ConversationEndpoints.cs`.

- Declarar la cabecera `X-LocalAssistant-Confirm-Delete` como constante privada y
  mapear `DELETE /api/conversations/{conversationId:guid}` con un nombre y summary
  estables.
- Implementar el handler con las dependencias ya registradas: `HttpContext`,
  `IOptions<SqliteConversationStoreOptions>`, `IConversationStore`,
  `IConversationExecutionLock` e `IToolConfirmationStore`.
- Aplicar, por este orden, el límite de persistencia, la identidad autenticada y la
  cabecera. La cabecera debe tener exactamente un valor `true`; ausencia, repetición o
  cualquier otro valor devuelve un `ValidationProblem` de `400`.
- Obtener el propietario únicamente de `ClaimTypes.NameIdentifier`; una identidad
  ausente o sin ese claim devuelve `401`. No crear scopes, bypasses ni autorización
  procedente del cliente.
- Adquirir el bloqueo de conversación antes de borrar. Si `DeleteOwnedAsync` devuelve
  `false`, responder `404` sin retirar una confirmación pendiente. Si devuelve
  `true`, retirar la confirmación pendiente y responder `204`.
- No introducir una herramienta del modelo ni modificar el bucle de orquestación.

## 3. Demostrar contratos, propiedad y concurrencia observable

**Archivos:** actualizar
`tests/LocalAssistant.Tests/Infrastructure/SqliteConversationStoreTests.cs`,
`tests/LocalAssistant.Tests/Api/ConversationEndpointTests.cs` y las pruebas de
confirmaciones que correspondan al contrato actualizado.

- Mantener la prueba SQLite que demuestra que solo el propietario elimina los mensajes
  y ampliar la cobertura del adaptador compuesto para confirmar que el borrado no
  alcanza una conversación anónima.
- Añadir pruebas unitarias del almacén de confirmaciones que demuestren que retirar una
  confirmación la invalida sin devolverla como ejecutable.
- Añadir pruebas HTTP que creen una conversación persistida con identidad configurada
  y cubran: `204` con cabecera correcta; `400` con cabecera ausente, repetida o de
  valor incorrecto; `401` sin API key; `404` para propietario distinto, identificador
  inexistente y conversación anónima; y `503` sin crear el archivo SQLite cuando la
  persistencia está desactivada.
- Añadir un escenario que cree una confirmación pendiente, elimine su conversación y
  compruebe que resolver esa confirmación devuelve `404`. La prueba demuestra el
  efecto observable del bloqueo e invalidación sin acoplarse a detalles internos.

## 4. Actualizar contrato y documentación

**Archivos:** `docs/api/openapi.yaml`, `README.md`, `docs/ARCHITECTURE.md`,
`docs/SECURITY.md`, `docs/ROADMAP.md` y
`docs/adr/0025-define-private-storage-lifecycle.md`.

- Documentar en OpenAPI el nuevo `DELETE`, su parámetro UUID, la cabecera requerida,
  seguridad `LocalApiKey` y respuestas `204`, `400`, `401`, `404` y `503`.
- Explicar para operadores que la cabecera es una confirmación explícita, que el
  borrado incluye mensajes, que solo actúa sobre el propietario y que no borra backups
  ni otros recursos.
- Actualizar arquitectura y seguridad con el bloqueo por conversación, la retirada de
  confirmaciones pendientes y la respuesta `404` no reveladora.
- Marcar el slice de borrado HTTP como implementado en el roadmap solo tras superar
  las pruebas. Actualizar ADR 0025 para describir el mecanismo HTTP que concreta su
  decisión ya aceptada; no crear un ADR nuevo.

## 5. Verificación y revisión

- Ejecutar `dotnet format LocalAssistant.sln --verify-no-changes --no-restore`.
- Ejecutar `dotnet build LocalAssistant.sln --configuration Release --no-restore`.
- Ejecutar primero las pruebas de infraestructura y endpoint afectadas y después
  `dotnet test LocalAssistant.sln --configuration Release --no-build --no-restore`.
- Validar `docs/api/openapi.yaml` con las pruebas de contrato OpenAPI existentes.
- Revisar el diff frente a `origin/main`, comprobando SQL parametrizado, borrado
  condicionado por propietario, validación exacta de cabecera, ausencia de contenido
  sensible en logs y exclusión de conversaciones anónimas.

## No objetivos

- No hay borrado masivo, restauración, papelera, exportación, UI ni endpoint de
  administración.
- No se cambia la retención, el esquema SQLite, la política de backups ni la
  coordinación entre procesos.
- No se añaden scopes de conversación, se amplía `installation.owner`, ni se afectan
  notas personales, documentos, recordatorios, auditoría o herramientas del modelo.
