# Plan de implementación: fase 5, incremento 2 — operación segura del cliente terminal

## Alcance

Completar el segundo incremento de la fase 5 sobre el cliente .NET textual existente.
El cliente seguirá siendo un ejecutable independiente que se comunica exclusivamente
mediante HTTP(S) loopback con la API ya ejecutándose. Añadirá almacenamiento protegido
de la credencial duradera, pairing interactivo de arranque o recuperación, renovación
acotada de sesión, confirmaciones, completion y los comandos de conversación y
administración definidos en el diseño aceptado.

También se ajustará el contrato administrativo existente para que rotación y revocación
reciban `{ challenge, clientId }`, comprueben dentro de la transacción que el desafío
apunta a ese cliente y devuelvan el identificador afectado al tener éxito. El `ClientId`
no autoriza la operación: es una comprobación de coherencia adicional; el desafío sigue
siendo la única autoridad administrativa.

## Decisiones y límites

- El estado local contendrá solo `ClientId` y una credencial protegida con DPAPI
  `CurrentUser`. No guarda bearer, desafíos, mensajes, respuestas, argumentos de
  herramientas ni `ConversationId`; la reanudación durable pertenece al incremento 3.
- Cuando DPAPI no esté disponible o una escritura protegida falle, la credencial solo
  vive en memoria. El cliente no ofrece un modo alternativo de persistencia.
- Pairing no es un comando conversacional: se ofrece al arrancar cuando no hay una
  credencial utilizable o durante recuperación explícita de una credencial rechazada.
  Desafíos y credenciales se leen sin eco y nunca se aceptan desde argumentos.
- La renovación automática se activa solo para un `401` no estructurado de una operación
  bearer. Una respuesta `ConversationResponse` válida no se renueva ni reintenta, aunque
  lleve estado HTTP no 2xx, porque prueba que el orquestador procesó el turno.
- Timeout, cancelación, error de conexión, `5xx` sin contrato conversacional válido y
  respuesta exitosa inválida permanecen inciertos y no se reintentan automáticamente.
- `/admin rotate` y `/admin revoke` no aceptan argumentos. Solo operan sobre el cliente
  local. La revocación pide una confirmación inequívoca antes de solicitar el desafío.
- No se añaden listado de clientes, selección de otros dispositivos, refresh token,
  logout remoto, historial, TUI, audio, TTS ni cambios en `Chat.ps1`.

## Pasos de implementación

### 1. Convertir rotación y revocación en operaciones dirigidas y atómicas

**Archivos:**

- `src/LocalAssistant.Core/Security/PrivateClients/PrivateClientContracts.cs`
- `src/LocalAssistant.Core/Security/PrivateClients/PrivateClientAuthenticationService.cs`
- `src/LocalAssistant.Infrastructure/Security/PrivateClients/SqlitePrivateClientAuthenticationStore.cs`
- `src/LocalAssistant.Api/Contracts/PrivateClientApiContracts.cs`
- `src/LocalAssistant.Api/Endpoints/PrivateClientEndpoints.cs`
- `docs/api/openapi.yaml`

Sustituir el request administrativo genérico por un contrato dirigido con `Challenge` y
`ClientId`. Hacer que servicio y almacén reciban el cliente esperado para rotar o
revocar. Dentro de la transacción que localiza el desafío, antes de modificar cliente,
sesiones o marcarlo consumido, comparar su `Operation` y `ClientId` con los valores
esperados. Una discrepancia, desafío caducado o desafío consumido revierte la transacción
y conserva la misma respuesta no reveladora que un desafío inválido.

Cambiar la revocación para devolver el `RegisteredPrivateClient` afectado, y publicar
una respuesta HTTP `200` con `PrivateClientRevocationResponse { clientId }`, en vez de
un `204` que no permite al dispositivo comprobar el objetivo. La rotación conserva
`PrivateClientCredentialResponse`, cuyo `clientId` también se valida. Mantener la
invalidación transaccional de sesiones de rotación y revocación ya existente. Validar
campos faltantes como `400`; no incluir desafío, hashes ni metadatos del cliente en
errores o logs.

Actualizar OpenAPI para ambos request bodies, la respuesta `200` de revocación y las
descripciones de los `404`; describir el `clientId` como comprobación de coherencia, no
como credencial ni autorización.

**Pruebas:** ampliar las pruebas SQLite y HTTP para demostrar que una rotación o
revocación correcta afecta solo al destino del desafío; que un `clientId` distinto no
consume el desafío, no modifica la versión de credencial ni invalida sesiones; y que el
mismo desafío puede consumirse después con el `clientId` correcto. Verificar body,
respuesta y códigos HTTP en `ConversationEndpointTests`, además de la validez de
OpenAPI existente.

### 2. Añadir un almacén local DPAPI limitado a credenciales de cliente

**Archivos:**

- `src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj`
- Nuevo `src/LocalAssistant.TerminalClient/PrivateClientCredentialStore.cs`
- `src/LocalAssistant.TerminalClient/Program.cs`
- Nuevas o ampliadas pruebas en `tests/LocalAssistant.Tests/TerminalClient/`

Definir una abstracción pequeña, específica del estado de credencial, que permita cargar,
reemplazar y eliminar el estado del cliente local. La implementación de producción usa
la ruta por usuario de `LocalApplicationData`, serializa exclusivamente el `ClientId` y
la credencial protegida en Base64, y protege/desprotege con DPAPI
`DataProtectionScope.CurrentUser`. Añadir solo la referencia de plataforma necesaria
para `System.Security.Cryptography.ProtectedData`; el ejecutable seguirá siendo
independiente de los proyectos del servidor.

Reemplazar el archivo con un temporal en el mismo directorio y un movimiento de
reemplazo, tras proteger los bytes nuevos, para que un fallo de escritura no destruya el
estado previo. Limpiar buffers de bytes transitorios cuando sea posible. Si DPAPI,
deserialización o acceso a archivo falla, devolver un resultado seguro que no copie el
detalle sensible a la consola; no borrar automáticamente un estado que pueda pertenecer
a otra recuperación. La eliminación se ejecuta únicamente tras una revocación local
confirmada. Inyectar la abstracción desde `Program.cs` para que las pruebas no dependan
del perfil, DPAPI real ni del reloj del equipo.

**Pruebas:** estado válido, estado ausente o corrupto, fallo de protección y fallo de
reemplazo atómico; comprobar que ninguna salida ni estado persistido contiene bearer o
desafío. Usar un protector/almacén controlado en memoria para pruebas unitarias y una
prueba Windows condicionada para la integración DPAPI si la plataforma la permite.

### 3. Reestructurar el arranque, pairing y la apertura de sesión

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`
- `src/LocalAssistant.TerminalClient/TerminalConsole.cs`
- `src/LocalAssistant.TerminalClient/PrivateApiContracts.cs`
- `src/LocalAssistant.TerminalClient/PrivateApiClient.cs`
- `src/LocalAssistant.TerminalClient/Program.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`
- `tests/LocalAssistant.Tests/TerminalClient/PrivateApiClientTests.cs`

Tras health, intentar cargar el estado DPAPI y abrir sesión. Si no hay estado utilizable,
ofrecer pairing interactivo o entrada manual de `ClientId` y credencial; si una
credencial cargada recibe `401`, informar que fue rechazada sin borrar el archivo y
ofrecer esa misma recuperación. El pairing pide desafío y display name, llama al endpoint
existente, abre una sesión con la credencial recién emitida y persiste solo después de
esa validación. Una entrada manual validada también se persiste solo si DPAPI funciona.

Extender los DTOs y el adaptador HTTP local con pairing, rotación, revocación,
confirmation decision y completion. Validar cada respuesta antes de cambiar el estado
del proceso; el bearer se añade solo a rutas autenticadas. Todas las lecturas secretas
usan `ReadSecret`; los métodos de consola y los mensajes de error no reciben valores
secretos como argumentos. Borrar referencias transitorias al terminar el flujo cuando
sea práctico en .NET, sin pretender que `string` proporcione borrado criptográfico.

**Pruebas:** pairing exitoso que persiste solo después de abrir sesión; credencial manual
con DPAPI no disponible que no persiste; credencial almacenada rechazada que permanece
intacta; y ausencia de credenciales, desafíos y bearer en la salida capturada. Comprobar
las rutas, cabeceras y cuerpos con `HttpMessageHandler` determinista.

### 4. Aplicar una renovación bearer de un único reintento y conservar la incertidumbre

**Archivos:**

- `src/LocalAssistant.TerminalClient/PrivateApiClient.cs`
- `src/LocalAssistant.TerminalClient/PrivateApiContracts.cs`
- `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`
- `tests/LocalAssistant.Tests/TerminalClient/PrivateApiClientTests.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`

Representar explícitamente en el resultado de cliente si un fallo es un `401`
no estructurado de una ruta bearer y, por tanto, permite renovar. No deducirlo de un
texto de error ni de un `ConversationResponse` estructurado. El coordinador conserva la
credencial duradera en memoria, abre una sesión nueva una vez y repite exactamente la
operación bearer original una sola vez; si la renovación o el segundo intento falla,
propaga el resultado sin más reintentos.

Aplicar esta política a envío de mensajes, decisiones de confirmación y completion.
Un turno con respuesta conversacional estructurada conserva su `ConversationId`, error,
herramientas e iteraciones y no pasa por la ruta de renovación. Los resultados inciertos
se muestran como tales sin repetir el turno; el usuario puede elegir continuar sobre el
identificador conocido o iniciar una conversación nueva.

**Pruebas:** `401` no estructurado abre una sola sesión y repite una sola vez; un segundo
`401` no forma bucle; un `401` con contrato conversacional no renueva; y timeout,
cancelación, desconexión, `500` inválido y `ConversationResponse` estructurado de
proveedor no generan una segunda solicitud. Verificar que la continuidad conserva el
`ConversationId` cuando el servidor la confirma.

### 5. Implementar confirmaciones, completion y comandos textuales

**Archivos:**

- `src/LocalAssistant.TerminalClient/TerminalClientApplication.cs`
- `src/LocalAssistant.TerminalClient/TerminalConsole.cs`
- `src/LocalAssistant.TerminalClient/PrivateApiContracts.cs`
- `src/LocalAssistant.TerminalClient/PrivateApiClient.cs`
- `tests/LocalAssistant.Tests/TerminalClient/TerminalClientApplicationTests.cs`

Resolver una confirmación pendiente en vez de terminar el cliente: mostrar solo nombre
de herramienta y caducidad, pedir `approve` o `reject`, y enviar la decisión junto con
provider y scenario actuales. Procesar la respuesta conversacional resultante con las
mismas reglas de continuidad y de no exposición de argumentos.

Implementar `/new`, `/info`, `/provider fake`, `/provider ollama` y `/exit`.
`/new`, cambio de provider y salida solicitan completion de la conversación actual antes
de alterar o descartar su identificador; si completion falla, conservan la conversación
y no aplican el cambio. `/info` muestra únicamente datos no sensibles (servidor,
provider, escenario y si existe conversación), y `/provider` no acepta valores fuera
de los ya validados. Añadir `/help` como guía local coherente con los comandos.

Implementar `/admin rotate` y `/admin revoke` sin argumentos. Rotación lee desafío sin
eco, llama al endpoint con el `ClientId` local, comprueba el mismo ID en la respuesta,
abre sesión con la credencial reemplazo y solo después sustituye DPAPI atómicamente.
Si la persistencia falla tras la rotación ya aplicada, mantiene la nueva sesión solo en
memoria, conserva el archivo previo deliberadamente obsoleto y explica la recuperación
por pairing al siguiente arranque. Revocación exige una frase de confirmación explícita,
envía desafío y `ClientId`, y solo después de recibir el mismo ID elimina el estado
local, descarta bearer y finaliza el proceso. Ante cualquier respuesta ambigua o error,
el estado local no cambia.

**Pruebas:** aprobación y rechazo de confirmación, completion para `/new`, provider y
`/exit`, información sin secretos, validación de comandos, rotación correcta, destino
de rotación distinto, fallo de persistencia post-rotación, revocación confirmada y
revocación con destino discrepante. Comprobar que los desafíos no se aceptan como
argumentos ni aparecen en la salida, las cabeceras o requests no correspondientes.

### 6. Actualizar documentación y cerrar el incremento con verificación

**Archivos:**

- `README.md`
- `docs/SECURITY.md`
- `docs/ROADMAP.md`
- `docs/api/openapi.yaml`
- `docs/specs/2026-09-01-terminal-client-and-local-speech-design.md`

Documentar el flujo de primera ejecución, pairing, recuperación de sesión, limitación
loopback y ubicación/limitaciones de DPAPI, sin ejemplos de secretos. Explicar que los
desafíos administrativos se introducen sin eco, nunca en argumentos, y que rotación y
revocación solo se aplican al cliente local tras la comprobación servidor-cliente.
Registrar el caso de fallo de persistencia posterior a rotación y la recuperación por
pairing. Ajustar `SECURITY.md` con la semántica del `ClientId`, la no persistencia de
bearer y la política exacta de reintento. Marcar el incremento 2 del roadmap solo al
terminar código, contratos y pruebas.

## Verificación

Ejecutar desde la raíz, tras restaurar las dependencias si es necesario:

```powershell
dotnet format LocalAssistant.sln --no-restore --verify-no-changes
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test LocalAssistant.sln -c Release --no-restore
git diff --check
```

Validar el YAML de OpenAPI mediante `OpenApiDocumentTests`. Las pruebas HTTP usarán
`LocalAssistantApiFactory` y `HttpMessageHandler` en proceso, dispondrán de factory,
cliente, requests y respuestas y no iniciarán Kestrel ni procesos auxiliares; así no
queda un test host residual. Antes de abrir una PR, ejecutar también la revisión de
diff exigida por `AGENTS.md`.
