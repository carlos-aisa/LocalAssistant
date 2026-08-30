# Plan de implementación: recuperación de conversaciones autenticadas

- Diseño de referencia: `docs/specs/2026-08-30-authenticated-conversation-retrieval-design.md`
- Estado: implementado y verificado
- Fecha: 2026-08-30

## Objetivo y límites

Implementar una recuperación híbrida y local de contexto de conversaciones anteriores
del mismo principal autenticado. La recuperación será selectiva, estará limitada a
unos pocos fragmentos con procedencia y no modificará el historial fuente.

Este incremento no añade memoria genérica, perfiles inferidos, conversaciones
anónimas, reconocimiento de voz, RAG documental, procesos externos, servicios cloud
ni una base vectorial. No se añadirá un endpoint HTTP: el comportamiento se integra
de forma interna antes de la llamada normal al proveedor.

## Decisiones de implementación que fija este plan

- El índice comparte la misma base SQLite y la misma retención que las conversaciones.
  El borrado explícito y el borrado por expiración eliminarán los datos derivados en
  la misma transacción que la conversación.
- Solo se indexará texto de mensajes de usuario y de asistente. Los argumentos y
  resultados de herramientas no se copiarán al índice.
- La búsqueda literal se resolverá localmente con FTS5 de SQLite. Las representaciones
  semánticas se guardarán como vectores serializados y se compararán en el proceso;
  no se creará otra base de datos.
- El resumen, tema, palabras clave y embedding se crearán exclusivamente con Ollama
  local. Se configurará un modelo de embeddings explícito; el modelo de chat ya
  configurado generará el resumen estructurado. Si esa indexación falla, el historial
  y la búsqueda literal permanecerán utilizables y el trabajo se reintentará.
- Un `BackgroundService` alojado en la API revisará periódicamente conversaciones
  inactivas durante al menos quince minutos. La lógica de una pasada se extraerá a un
  servicio invocable para probarla con `ManualTimeProvider`; al iniciar la API se
  podrán recuperar trabajos pendientes.
- La detección de una petición retrospectiva será determinista y conservadora. Solo
  activará recuperación ante una petición explícita sobre el historial o expresiones
  de continuación acompañadas de un tema. No se consultará el índice en cada turno.
- El contexto recuperado se añadirá como mensaje `System` transitorio, tras el
  contexto del perfil y antes del historial actual. Nunca se persistirá como un
  mensaje de la conversación. Una coincidencia clara instruirá a Jarvis para indicar
  brevemente que retoma el tema; una ambigua le instruirá para pedir elección; una
  ausencia de coincidencias no modificará la llamada normal al proveedor.

## Pasos de ejecución

### 1. Definir contratos del núcleo y límites de datos

Modificar o crear:

- `src/LocalAssistant.Core/Conversations/ConversationRetrievalContracts.cs`
- `src/LocalAssistant.Core/Orchestration/ConversationOrchestrator.cs`
- `tests/LocalAssistant.Tests/Conversations/ConversationRetrievalContractsTests.cs`

Responsabilidad:

1. Añadir contratos independientes de SQLite y Ollama para el documento derivado,
   candidatos recuperados, resultado de recuperación y generación local de datos
   semánticos. Los tipos validarán límites de longitud, número de palabras clave,
   tamaño de fragmentos y dimensiones de vector antes de que alcancen el orquestador.
2. Separar tres responsabilidades: decidir si un mensaje merece búsqueda, consultar
   candidatos ya autorizados y crear el contexto transitorio que recibirá el modelo.
   El contrato de consulta recibirá siempre `ownerPrincipalId` y nunca expondrá una
   operación de búsqueda sin propietario.
3. Mantener estos contratos en `Core`, sin tipos HTTP, SQL ni SDK de Ollama. Propagar
   `CancellationToken` por cada operación asíncrona.

Validación:

- Pruebas unitarias para validación de entradas, límites y resultados vacíos.
- Pruebas de la política determinista: petición explícita, continuación con tema,
  conversación normal y frases demasiado vagas que no deben consultar el índice.

### 2. Añadir el almacenamiento derivado y la búsqueda literal privada

Modificar o crear:

- `src/LocalAssistant.Infrastructure/Conversations/SqliteConversationStore.cs`
- `src/LocalAssistant.Infrastructure/Conversations/SqliteConversationRetrievalStore.cs`
- `src/LocalAssistant.Infrastructure/Conversations/ConversationRetrievalOptions.cs`
- `tests/LocalAssistant.Tests/Infrastructure/SqliteConversationStoreTests.cs`
- `tests/LocalAssistant.Tests/Infrastructure/SqliteConversationRetrievalStoreTests.cs`

Responsabilidad:

1. Crear una migración idempotente para las tablas de estado y documentos derivados,
   además de la tabla virtual FTS5 necesaria para el texto indexable. El documento
   tendrá identificador de conversación y propietario, última actividad, versión y
   estado de indexación, resumen, tema, palabras clave, texto literal y vector con
   versión de modelo.
2. Actualizar de forma barata el texto indexable y la fecha de actividad cuando se
   persista un mensaje de usuario o asistente. Las conversaciones anónimas seguirán
   pasando por `InMemoryConversationStore` y no podrán crear documentos derivados.
3. Implementar la consulta literal con filtro de propietario en la propia consulta,
   antes de seleccionar candidatos o fragmentos. Devolver solo una cantidad acotada
   de candidatos con fecha y texto preparado, no el historial completo.
4. Integrar la limpieza de tablas y FTS en `DeleteOwnedAsync` y `DeleteExpiredAsync`
   dentro de las transacciones existentes. Una operación de otro propietario no debe
   borrar ni devolver el documento de nadie.
5. Definir opciones explícitas para activar la capacidad, el retraso de inactividad,
   frecuencia de sondeo y límites de candidatos/contexto; validar valores positivos y
   mantenerla desactivada por defecto.

Validación:

- Persistencia entre instancias, filtrado estricto por propietario y exclusión de
  anónimas.
- Coincidencia literal, límites y ausencia de resultados.
- Borrado selectivo y retención eliminan mensajes, documento y entrada FTS; no quedan
  huérfanos.
- Las migraciones funcionan contra una base creada por la versión anterior.

### 3. Implementar enriquecimiento semántico exclusivamente con Ollama local

Modificar o crear:

- `src/LocalAssistant.Infrastructure/LanguageModels/Ollama/OllamaConversationIndexingProvider.cs`
- `src/LocalAssistant.Infrastructure/LanguageModels/Ollama/OllamaOptions.cs`
- `src/LocalAssistant.Infrastructure/LanguageModels/Ollama/OllamaModelInspector.cs`
- `tests/LocalAssistant.Tests/Infrastructure/OllamaConversationIndexingProviderTests.cs`
- `tests/LocalAssistant.Tests/Infrastructure/OllamaModelInspectorTests.cs`

Responsabilidad:

1. Implementar el contrato del núcleo con dos llamadas locales: resumen estructurado
   y acotado mediante `/api/chat`, y embeddings mediante `POST /api/embed`. El modelo
   de embeddings será una configuración independiente y explícita; se conservará su
   identificador con cada vector para impedir comparaciones entre modelos distintos.
2. Validar respuestas HTTP, JSON, recuentos, valores finitos y dimensión homogénea de
   vectores. Rechazar respuestas inválidas sin persistir datos parciales ni registrar
   contenido conversacional.
3. Extender la comprobación de configuración para exigir que el modelo de embeddings
   esté instalado cuando se active recuperación semántica. No cambiar la validación
   de herramientas del modelo de chat ya existente.
4. Calcular similitud coseno localmente solo entre vectores del mismo modelo y combinar
   ese resultado con la puntuación literal y una bonificación reciente pequeña. Si no
   hay vector válido, mantener la vía literal sin intentar ningún proveedor externo.

Validación:

- Pruebas HTTP deterministas para ruta, cuerpo, mapeo de respuesta, errores, timeout
  y cancelación; no requieren Ollama, GPU ni red.
- Pruebas de ranking para equivalencia semántica, incompatibilidad de modelo y fallback
  literal tras error de indexación.

### 4. Programar y reintentar la indexación tras inactividad

Modificar o crear:

- `src/LocalAssistant.Infrastructure/Conversations/ConversationIndexingCoordinator.cs`
- `src/LocalAssistant.Api/HostedServices/ConversationIndexingHostedService.cs`
- `src/LocalAssistant.Api/Program.cs`
- `tests/LocalAssistant.Tests/Infrastructure/ConversationIndexingCoordinatorTests.cs`
- `tests/LocalAssistant.Tests/Api/ConversationIndexingHostedServiceTests.cs`

Responsabilidad:

1. Seleccionar únicamente conversaciones autenticadas pendientes cuya última actividad
   supere el retraso configurado. Bloquear o marcar el trabajo de modo que una pasada
   no procese dos veces una conversación activa ni sobrescriba un turno llegado durante
   la indexación.
2. Construir una entrada de indexación acotada a partir del historial fuente y pedir al
   proveedor local resumen, tema, palabras clave y vector. Guardar el resultado solo
   si la versión de actividad sigue siendo la que se seleccionó.
3. Persistir un estado técnico de reintento seguro (sin textos en logs), con intentos
   acotados por ciclo. El siguiente sondeo y el arranque de la API podrán reprocesar
   pendientes; no habrá proceso, cola ni worker separado.
4. Registrar el servicio hospedado únicamente cuando la persistencia y la recuperación
   estén habilitadas. Detenerse mediante el token del host y no bloquear el arranque
   esperando una inferencia.

Validación:

- Con `ManualTimeProvider`, demostrar que no hay enriquecimiento antes de quince
  minutos y que se produce exactamente una actualización después.
- Demostrar reanudación tras recrear servicios con la misma SQLite, prevención de
  escritura obsoleta, fallo recuperable y propagación de cancelación.

### 5. Integrar recuperación selectiva en el orquestador

Modificar o crear:

- `src/LocalAssistant.Core/Orchestration/ConversationOrchestrator.cs`
- `src/LocalAssistant.Core/Orchestration/ConversationRetrievalPolicy.cs`
- `src/LocalAssistant.Core/Orchestration/ConversationRetrievedContextFormatter.cs`
- `tests/LocalAssistant.Tests/Orchestration/ConversationOrchestratorTests.cs`
- `tests/LocalAssistant.Tests/TestDoubles/` (dobles de recuperación mínimos)

Responsabilidad:

1. Después de validar que el principal es dueño de la conversación actual y antes de
   construir la petición del proveedor, evaluar la política con el mensaje de usuario
   actual. El flujo anónimo, las confirmaciones pendientes y las llamadas posteriores
   al proveedor no harán búsquedas retrospectivas adicionales.
2. Consultar solo el almacén derivado usando el principal autenticado. Formatear el
   contexto claro con fecha, identificador de conversación y fragmentos acotados como
   datos no confiables, sin instrucciones ejecutables ni permisos.
3. Para candidatas comparables, entregar una instrucción transitoria y una lista breve
   de temas/fechas que obligue a pedir aclaración. Para un único resultado claro,
   instruir una mención breve de que se retoma el contexto. Para cero resultados,
   conservar exactamente la secuencia normal de mensajes.
4. Mantener el perfil de instalación como contexto transitorio en cada llamada y no
   persistir ni el perfil ni el contexto recuperado. La recuperación no alterará el
   registro de herramientas, sus políticas, confirmaciones ni auditoría.

Validación:

- Pruebas de orquestación para no consultar en turno normal o anónimo, aislamiento por
  propietario, coincidencia clara, ambigüedad, ausencia de candidatos y límites de
  contexto.
- Verificar que los mensajes enviados al proveedor contienen solo el contexto
  transitorio permitido y que `IConversationStore` conserva exclusivamente el
  historial real.

### 6. Configurar, documentar y registrar la decisión arquitectónica

Modificar o crear:

- `src/LocalAssistant.Api/appsettings.json`
- `README.md`
- `docs/ARCHITECTURE.md`
- `docs/OPERATIONS.md`
- `docs/SECURITY.md`
- `docs/ROADMAP.md`
- `docs/adr/0028-local-authenticated-conversation-retrieval.md`

Responsabilidad:

1. Documentar los valores de configuración, incluidos activación explícita, modelo de
   embeddings local, retraso de quince minutos, almacenamiento bajo la ruta SQLite y
   el comportamiento cuando Ollama no está disponible. No incluir modelos ni datos
   personales por defecto en `appsettings.json`.
2. Explicar que el índice es privado y derivado, que comparte retención, borrado,
   permisos de carpeta y copias de seguridad con conversaciones, y que logs/auditoría
   no contienen consultas ni fragmentos. Incluir la operación de revisar y descargar
   el modelo local de embeddings sin convertirla en una dependencia de CI.
3. Registrar en el ADR la elección de FTS5 + vectores locales en SQLite, la prohibición
   de egreso externo y por qué se usa un componente hospedado, no un proceso separado.
4. Marcar el punto correspondiente de la fase 4 como completado solo tras implementar
   y verificar el incremento. No modificar `docs/api/openapi.yaml`: no se añade ni se
   cambia contrato HTTP público.

Validación:

- Revisión documental contra el comportamiento final y comprobación de que no aparecen
  claves, historiales, resúmenes, palabras clave ni vectores de ejemplo reales.

### 7. Ejecutar las puertas de calidad y la revisión estructurada

Ejecutar, tras implementar los pasos anteriores:

```powershell
dotnet format --verify-no-changes
dotnet build --configuration Release
dotnet test --configuration Release
git diff --check
```

Revisar además el diff con la checklist local del repositorio, en especial:

- filtros de propietario en cada consulta y borrado;
- transacciones de SQLite y migración desde instalaciones existentes;
- datos de conversación ausentes de logs, errores, auditoría y configuración;
- cancelación del servicio hospedado y de las llamadas HTTP a Ollama;
- ausencia de red, Ollama o tiempo real en las pruebas deterministas.

## Resultado verificable

Una conversación autenticada e inactiva se indexa localmente tras quince minutos.
Cuando su propietario formula una petición retrospectiva con tema, Jarvis recibe un
contexto pequeño y trazable de una coincidencia clara o solicita elegir entre temas
comparables. Un visitante anónimo, otro propietario, una conversación borrada o
expirada y una búsqueda sin candidato no reciben contexto privado alguno.
