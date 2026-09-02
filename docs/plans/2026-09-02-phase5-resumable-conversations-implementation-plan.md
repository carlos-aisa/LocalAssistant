# Plan de implementación: conversaciones reanudables del cliente terminal

## Objetivo

Entregar el incremento 3 de la fase 5: contratos HTTP bearer para consultar conversaciones propias, preferencia local compatible para el último identificador y reanudación asistida en el cliente terminal. La implementación no introducirá el modelo explícito de estados del incremento 4.

## Decisiones confirmadas

- Las conversaciones pertenecen al principal humano autenticado. El `ClientId` solo identifica el cliente técnico y conserva localmente una preferencia.
- `completion` solicita indexación; no marca una conversación como terminada.
- Listado e historial se paginan mediante cursores opacos. El servidor aplica orden estable y límites máximos.
- Un ID inexistente y uno de otro principal responden de forma indistinguible con `404`.
- La lectura requiere `PrivateBearer`, loopback y `conversations.read`.
- Si la persistencia está deshabilitada, listado, detalle e historial devuelven `503`; el terminal inicia una conversación nueva y conserva el ID local.
- La selección de una conversación distinta completa primero la conversación activa; el mero listado no la completa.

## Paso 1: ampliar los contratos de conversaciones y la lectura propietaria

**Archivos:**

- `src/LocalAssistant.Core/Conversations/ConversationContracts.cs`
- `src/LocalAssistant.Infrastructure/Conversations/SqliteConversationStore.cs`
- `src/LocalAssistant.Core/Conversations/InMemoryConversationStore.cs`
- `tests/LocalAssistant.Tests/Infrastructure/SqliteConversationStoreTests.cs`

Añadir las operaciones de lectura al contrato `IConversationStore` y sus implementaciones SQLite, efímera y `AuthenticatedConversationStore` (esta última está definida en `SqliteConversationStore.cs`). Definir contratos independientes del historial interno: resumen, detalle, página de historial y entrada pública. Extender el almacén SQLite con consultas parametrizadas por `OwnerPrincipalId`, orden determinista por actividad e ID, y cursores opacos validados. Aplicar límites máximos definidos por el servidor aunque el cliente pida valores mayores.

Convertir los mensajes persistidos a entradas públicas permitidas. Excluir roles de sistema, contexto interno, argumentos, resultados de herramientas y detalle interno de errores. Calcular el título sin LLM desde el primer mensaje visible del usuario, con normalización, truncado y fallback estable. Exponer `IndexingRequestedAtUtc` solamente como metadato de solicitud de indexación.

**Pruebas:** paginación estable, cursor inválido, límite impuesto, título y fallback deterministas, historial saneado, aislamiento por propietario e inexistencia/propiedad indistinguibles. Las pruebas usarán SQLite real y el reloj inyectado existente.

## Paso 2: exponer endpoints HTTP autorizados y actualizar el contrato OpenAPI

**Archivos:**

- `src/LocalAssistant.Api/Contracts/ConversationApiContracts.cs`
- `src/LocalAssistant.Api/Endpoints/ConversationEndpoints.cs`
- `docs/api/openapi.yaml`
- `tests/LocalAssistant.Tests/Api/ConversationEndpointTests.cs`
- `tests/LocalAssistant.Tests/Documentation/OpenApiDocumentTests.cs`

Añadir `GET /api/conversations`, `GET /api/conversations/{conversationId}` junto al DELETE ya existente, y `GET /api/conversations/{conversationId}/history`. Modelar DTOs HTTP nuevos, sin reutilizar mensajes internos. Validar cursores y límites en la frontera, conservar `CancellationToken` y traducir fallos esperados sin revelar propietario ni estado interno.

Requerir `PrivateBearer`, loopback y `conversations.read` en las tres operaciones mediante la misma comprobación explícita de identidad y claim de scope empleada por los endpoints de documentos y memoria. Devolver el mismo `404` para conversación inexistente o ajena y `503` si la persistencia está deshabilitada. Documentar security, parámetros, cursores, límites y respuestas en OpenAPI.

**Pruebas:** ausencia de bearer y scope insuficiente, acceso de propietario, `404` igual para ajena e inexistente, `503`, parámetros de paginación, historial público y sintaxis/coherencia de OpenAPI.

## Paso 3: conceder y migrar el scope de lectura de conversaciones

**Archivos:**

- `src/LocalAssistant.Api/Security/InstallationIdentityStore.cs`
- `tests/LocalAssistant.Tests/Api/InstallationIdentityStoreTests.cs`
- Dobles de identidad afectados bajo `tests/LocalAssistant.Tests/TestDoubles/`.

Elevar el esquema de identidad de instalación a 5 e incorporar `conversations.read` al conjunto de scopes del propietario. Migrar de los esquemas 1, 2, 3 y 4 de forma atómica e idempotente, preservando instalación, propietario y fecha. Mantener el modelo actual de capacidades heredadas temporalmente por clientes bearer; no añadir administración de scopes por cliente.

**Pruebas:** bootstrap nuevo, migración de cada esquema anterior, repetición idempotente, preservación de datos estables y bearer con el nuevo scope.

## Paso 4: evolucionar el estado local DPAPI sin perder la credencial

**Archivos:**

- `src/LocalAssistant.TerminalClient/PrivateClientCredentialStore.cs`
- `tests/LocalAssistant.Tests/TerminalClient/DpapiPrivateClientCredentialStoreTests.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`

Evolucionar el contrato del almacén local para cargar y guardar `ClientId`, credencial y `LastConversationId` opcional mediante un estado versionable y compatible con el JSON existente. Reutilizar el reemplazo atómico de archivo. Distinguir una actualización del último ID de un fallo total: si guardar o limpiar el ID falla, conservar el estado anterior y permitir la conversación de la sesión.

No persistir bearer, desafíos, mensajes, historial, argumentos ni resultados de herramientas.

**Pruebas:** estado antiguo sin ID, estado con ID, actualización y limpieza, fallo de reemplazo que conserva credencial e ID previos, y ejecución DPAPI real condicionada a Windows.

## Paso 5: ampliar el cliente HTTP de terminal para lectura paginada

**Archivos:**

- `src/LocalAssistant.TerminalClient/PrivateApiContracts.cs`
- `src/LocalAssistant.TerminalClient/PrivateApiClient.cs`
- `tests/LocalAssistant.Tests/TerminalClient/PrivateApiClientTests.cs`

Añadir métodos para listado, detalle e historial con DTOs públicos. Implementar análisis estricto de contratos, cursores tratados como valores opacos y clasificación de `404`, `503`, timeout, desconexión y respuesta inválida. Reutilizar la renovación acotada de sesión únicamente ante un `401` bearer no estructurado y solo una vez por operación de lectura.

**Pruebas:** cabecera bearer, rutas y query string correctos, paginación, `404` concluyente, `503` recuperable, timeout incierto y ausencia de reintentos fuera del `401` permitido.

## Paso 6: implementar reanudación asistida y selector textual

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`

Tras abrir una sesión válida, consultar la preferencia local. Si existe un último ID, validar únicamente con detalle y mostrar `[R]eanudar [N]ueva [L]istar conversaciones` cuando sea válido. `404` limpia la preferencia; `503`, timeout, desconexión, cancelación, `5xx` o contrato inválido no la limpian y degradan a conversación nueva sin bloquear el chat.

Implementar `/conversations` con navegación por cursor y selección explícita. Cargar una selección valida con detalle e historial públicos. Antes de cambiar desde una conversación activa, solicitar completion; si falla, no cambiar el ID actual ni la preferencia. Actualizar `LastConversationId` solo después de una respuesta válida que confirme el identificador, incluidas respuestas conversacionales válidas.

**Pruebas:** las tres decisiones de reanudación, selector paginado, selección propia, completion previa a cambio, fallo de completion que mantiene la conversación, `404` que limpia, errores inciertos y `503` que conservan, fallo de persistencia que no bloquea, y salida sin secretos.

## Documentación y no objetivos

Actualizar `docs/api/openapi.yaml`, `README.md`, `docs/SECURITY.md` y `docs/ROADMAP.md` en el mismo cambio funcional. Describir la lectura limitada por principal y scope, cursores, historial saneado, degradación ante persistencia deshabilitada y que completion solo solicita indexación.

No añadir el modelo de estados, TUI, voz, nuevos proveedores, acceso a SQLite desde el terminal, navegación de conversaciones de otros principales, resúmenes por LLM, RAG ni gestión de scopes por cliente.

## Verificación

1. `dotnet format LocalAssistant.sln --no-restore --verify-no-changes`.
2. `dotnet build LocalAssistant.sln -c Release --no-restore`.
3. `dotnet test LocalAssistant.sln -c Release --no-restore`.
4. Ejecutar específicamente las pruebas nuevas de infraestructura SQLite, endpoints de conversaciones, identidad y cliente terminal.
5. Validar `docs/api/openapi.yaml` mediante la prueba documental existente.
6. Ejecutar `git diff --check`.
7. Antes de la PR, ejecutar la revisión obligatoria del diff, centrada en propiedad, cursores, persistencia atómica, exposición de historial y secretos.
