# Diseño: conversaciones reanudables del cliente terminal

## Propósito

El tercer incremento de la fase 5 permite al cliente terminal .NET retomar una conversación privada de manera asistida y seleccionar otra conversación propia cuando lo solicite. El cliente continúa comunicándose exclusivamente por HTTP loopback y no accede a SQLite, identidad ni contratos internos.

Las conversaciones pertenecen siempre al principal humano autenticado. El `ClientId` identifica el dispositivo técnico y solo conserva localmente una preferencia: `LastConversationId`. Conocer ese identificador no concede acceso a la conversación.

## Alcance

Se incorporan tres partes separadas y comprobables:

1. Contratos HTTP bearer para listado, detalle e historial de conversaciones propias.
2. Persistencia compatible y atómica de `LastConversationId` junto a la credencial DPAPI del cliente.
3. Reanudación asistida y el comando textual `/conversations`.

No se introduce la máquina explícita de estados del incremento 4, una TUI, voz, RAG, acceso interprincipal, acceso directo del cliente a SQLite ni una conversación marcada como terminada.

## Contratos HTTP y autorización

Se añaden, bajo `PrivateBearer` y loopback, las siguientes operaciones:

- `GET /api/conversations`: listado paginado de conversaciones del principal actual.
- `GET /api/conversations/{conversationId}`: metadatos autorizados para validar o seleccionar una conversación sin descargar su historial.
- `GET /api/conversations/{conversationId}/history`: historial público y paginado de esa conversación.

Las tres requieren el nuevo scope `conversations.read`. El estado de instalación se elevará al esquema 5 para concederlo al propietario único tanto en instalaciones nuevas como en las ya existentes de los esquemas 1 a 4. La migración será idempotente y atómica, conservará ID de instalación, principal propietario y fecha original, y no creará scopes configurables por cliente: los clientes activos heredan temporalmente las capacidades del propietario.

El listado y el historial aceptan un cursor opaco opcional y un límite opcional. El servidor impone límites máximos y orden estable, y devuelve un cursor opaco de la siguiente página solo cuando exista más resultado. El cliente no interpreta ni fabrica cursores.

Los tres endpoints devuelven exactamente el mismo `404` cuando el identificador no existe o pertenece a otro principal. Cuando la persistencia de conversaciones está deshabilitada, los tres devuelven `503 Service Unavailable`. Los errores no incluyen historial, propietario ni detalles internos.

## DTOs públicos

Los DTOs HTTP son nuevos y no exponen `ConversationMessage` ni tipos internos.

`ConversationSummaryResponse` contiene únicamente:

- `conversationId`;
- `title`;
- `lastActivityAtUtc`;
- `indexingRequestedAtUtc`, opcional.

`indexingRequestedAtUtc` expresa una solicitud de indexación derivada de `completion`; no representa una conversación cerrada ni impide seguir enviando mensajes.

`ConversationDetailResponse` usa los mismos metadatos para la validación puntual. `ConversationHistoryPageResponse` añade una lista de `ConversationHistoryEntryResponse` y el cursor siguiente. Cada entrada contiene rol público (`user` o `assistant`), contenido visible y marca temporal. Nunca incluye prompts de sistema, contexto de recuperación, argumentos o resultados de herramientas, ni detalles internos de error.

El título se calcula de manera determinista desde el primer mensaje visible del usuario, con normalización, truncado seguro y fallback fijo si no existe. El listado no invoca un modelo ni genera resúmenes nuevos.

## Estado local compatible

El estado DPAPI evoluciona de forma compatible para admitir el `LastConversationId` opcional junto a `ClientId` y la credencial protegida. Los estados existentes sin ese campo siguen cargando correctamente. Guardar la preferencia usa el mismo reemplazo atómico que la credencial.

Un fallo de persistencia al actualizar o limpiar el último ID conserva el archivo anterior, no elimina la credencial y no impide que la sesión actual converse. El bearer, desafíos, mensajes, historial, argumentos y resultados de herramientas no se persisten.

El cliente modifica `LastConversationId` únicamente tras recibir una respuesta HTTP válida que confirma el ID: después de un turno con respuesta conversacional válida, al seleccionar una conversación con detalle válido o al completar una selección válida.

Un `404` concluyente durante la validación del último ID lo limpia. Un timeout, desconexión, cancelación, `5xx`, `503` o respuesta inválida lo conserva.

## Experiencia de terminal

Tras autenticarse, el cliente carga la preferencia local. Si contiene un ID, pide sus metadatos al endpoint de detalle:

- con detalle válido, muestra por ejemplo `Última conversación: “Pruebas de recordatorios” — ayer 22:14` y ofrece `[R]eanudar  [N]ueva  [L]istar conversaciones`;
- con `404`, limpia la preferencia y comienza una conversación nueva;
- con error incierto o `503`, informa de que no pudo validar la reanudación y comienza una conversación nueva sin borrar la preferencia.

`R` carga el historial público paginado y establece la conversación actual. `N` no realiza completion y comienza una conversación nueva. `L` abre el selector textual `/conversations`, que pagina el listado y permite elegir una conversación por su índice en la página actual, avanzar, retroceder o volver al chat.

Al seleccionar una conversación distinta mientras existe una conversación activa, el cliente solicita primero su `completion`. Si la solicitud falla, no cambia la conversación actual ni la preferencia local. Si tiene éxito, recupera detalle e historial de la elegida, y solo después actualiza `LastConversationId`. Listar nunca completa la conversación actual.

Un `401` bearer no estructurado puede usar la renovación de sesión acotada ya existente y reintentar una sola vez las operaciones de lectura. No se reintentan automáticamente timeouts, desconexiones, respuestas `5xx` ni operaciones conversacionales ya procesadas.

## Pruebas y contratos

Las pruebas de integración HTTP cubrirán bearer y `conversations.read`, paginación, límites del servidor, aislamiento por principal con `404` indistinguible, persistencia deshabilitada con `503`, título determinista e historial sin campos internos o sensibles. También cubrirán la concesión y migración del scope en el esquema 5.

Las pruebas del cliente cubrirán reanudación asistida, `404` que limpia el ID, errores inciertos que lo conservan, degradación ante `503`, paginación y selección, completion previa a selección, actualización solo tras respuestas válidas y ausencia de secretos en salida o estado. Las pruebas del almacén validarán compatibilidad, escritura atómica y conservación de la credencial ante un fallo al guardar el último ID.

`docs/api/openapi.yaml`, README, SECURITY y ROADMAP se actualizarán junto a la implementación para reflejar únicamente los contratos y comportamientos entregados.

