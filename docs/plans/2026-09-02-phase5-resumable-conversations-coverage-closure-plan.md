# Plan de implementación: cierre de cobertura de conversaciones reanudables

## Objetivo

Completar las pruebas comprometidas por el incremento 3 de la fase 5 sin alterar
contratos HTTP, persistencia ni comportamiento del cliente. El resultado esperado es
evidencia determinista de los flujos de paginación, autorización, degradación,
renovación de sesión y preferencia local ya implementados.

## Alcance y no objetivos

Se reutilizarán SQLite, `WebApplicationFactory`, `RecordingHttpMessageHandler` y los
dobles actuales. No se añadirán endpoints, scopes, tablas, modelos de estado, TUI ni
dependencias externas. Los tests no usarán red, reloj real ni almacenamiento DPAPI real
fuera de las pruebas condicionadas por plataforma ya existentes.

## Paso 1: cubrir el almacén SQLite y los endpoints de lectura

**Archivos:**

- `tests/LocalAssistant.Tests/Infrastructure/SqliteConversationStoreTests.cs`
- `tests/LocalAssistant.Tests/Api/ConversationEndpointTests.cs`

Crear conversaciones y mensajes mínimos mediante SQLite real para verificar dos páginas
estables de listado e historial, con cursor opaco y límites aplicados por servidor.
Ejecutar las mismas lecturas con un bearer sin `conversations.read`, con una conversación
de otro principal y con un identificador inexistente. Verificar respectivamente `403` y
la misma respuesta `404`; con persistencia deshabilitada, verificar `503`.

Las aserciones comprobarán contenido, orden, cursor siguiente y código HTTP observable,
sin depender de detalles internos de consultas.

## Paso 2: cubrir la frontera HTTP del cliente terminal

**Archivo:**

- `tests/LocalAssistant.Tests/TerminalClient/PrivateApiClientTests.cs`

Programar respuestas HTTP controladas para listado, detalle e historial. Comprobar una
renovación bearer única tras `401` no estructurado en cada operación, incluido el reintento
con el token nuevo. Comprobar que timeout, `500`, `503` y contrato inválido mantienen la
clasificación que permite al llamador conservar la preferencia y no reintentan de forma
automática.

## Paso 3: cubrir los flujos del terminal y el estado local compatible

**Archivos:**

- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`
- `tests/LocalAssistant.Tests/TerminalClient/DpapiPrivateClientCredentialStoreTests.cs`

Usar consola y almacén de credenciales en memoria para demostrar que `R` carga el
historial, que el selector pagina y solicita completion antes de mostrar/cambiar la
conversación, y que un fallo de completion conserva conversación y preferencia. Verificar
que `[N]ew` y `/provider` persisten la limpieza del último ID.

Para cada fallo concluyente/no concluyente de validación, historial o detalle, comprobar
la regla concreta: solo `404` limpia; timeout, `500`, `503` y contrato inválido preservan.
Añadir el caso de JSON local anterior sin `LastConversationId` para demostrar que carga la
credencial y obtiene `null` de forma compatible.

## Verificación

1. `dotnet format LocalAssistant.sln --no-restore --verify-no-changes`.
2. `dotnet build LocalAssistant.sln -c Release --no-restore`.
3. Ejecutar específicamente las clases SQLite, endpoints, `PrivateApiClient`, terminal y
   almacén DPAPI afectadas.
4. `dotnet test LocalAssistant.sln -c Release --no-restore`.
5. `git diff --check`.

