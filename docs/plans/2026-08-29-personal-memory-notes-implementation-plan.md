# Plan de implementación: notas de memoria personal persistentes

## Punto de partida y decisiones confirmadas

La especificación aprobada es
`docs/specs/2026-08-29-personal-memory-notes-design.md`. El repositorio
parte de un árbol limpio en `agent/add-personal-memory-notes`; no se incorporarán los
cambios de la PR 31 mientras no estén integrados en `main`.

La capacidad reutilizará la configuración opt-in
`LocalAssistant:ConversationPersistence` y su archivo SQLite, pero tendrá contrato,
tabla y consultas propios. Se considera una elección deliberada: evita persistir una
nota cuando la persistencia privada está desactivada y evita introducir una segunda
configuración de retención para el primer incremento.

No se añade una herramienta, un proveedor, una tarea en segundo plano, una migración
externa ni una fuente de identidad nueva. La acción HTTP directa es el único modo de
crear o borrar notas; el bucle de herramientas no recibirá acceso a ellas.

## 1. Definir el contrato de memoria en el núcleo

**Archivos:** crear `src/LocalAssistant.Core/Memory/PersonalMemoryContracts.cs` y
`tests/LocalAssistant.Tests/Core/Memory/PersonalMemoryContractsTests.cs`.

- Declarar `PersonalMemory`, con identificador, propietario interno, texto y fechas
  UTC; el contrato no dependerá de HTTP ni SQLite.
- Declarar los valores de creación y consulta con validación centralizada: texto
  recortado no vacío de hasta 2.000 caracteres y límite de listado entre 1 y 100,
  con predeterminado 50. Los constructores producirán `ArgumentException` con un
  nombre de parámetro estable para que el límite HTTP lo traduzca a `400`.
- Declarar `IPersonalMemoryStore` con operaciones asíncronas y cancelables para crear,
  listar por propietario y eliminar condicionalmente por identificador y propietario.
  La interfaz devolverá una lista inmutable o de solo lectura y un booleano para el
  borrado encontrado; no expondrá SQL ni semántica de respuesta HTTP.
- Añadir pruebas unitarias de texto vacío, texto que supera el máximo, recorte válido
  y límites de listado inválidos y de valores por defecto. No se probarán detalles de
  la implementación SQLite en esta capa.

## 2. Persistir las notas en SQLite con propiedad y retención

**Archivos:** crear
`src/LocalAssistant.Infrastructure/Memory/SqlitePersonalMemoryStore.cs` y
`tests/LocalAssistant.Tests/Infrastructure/SqlitePersonalMemoryStoreTests.cs`;
actualizar `src/LocalAssistant.Api/Program.cs` solo para registrar el adaptador.

- Implementar `IPersonalMemoryStore` usando el mismo `SqliteConversationStoreOptions`,
  `TimeProvider` y archivo de base de datos de la persistencia de conversaciones. La
  inicialización creará idempotentemente `PersonalMemories` y un índice que cubra
  propietario, caducidad y orden de listado. No se modificarán las tablas de
  conversaciones.
- En creación, generar el GUID en servidor y calcular creación, modificación y
  caducidad con el reloj inyectado y `RetentionDays`. Las consultas SQL serán
  parametrizadas y propagarán `CancellationToken`.
- Antes de crear, listar o borrar, purgar las filas caducadas. El listado filtrará por
  propietario y caducidad, ordenará por modificación descendente y aplicará el límite
  validado. El borrado usará una única sentencia condicionada por identificador y
  propietario; devolverá `false` cuando la nota no exista, haya caducado o sea ajena.
- Registrar el adaptador como singleton tras `TimeProvider`, sin iniciar ni abrir la
  base de datos durante el arranque. El endpoint comprobará la opción `Enabled` antes
  de resolver el almacén, por lo que la persistencia desactivada devolverá `503` sin
  crear el archivo.
- Añadir pruebas deterministas con directorio temporal y `ManualTimeProvider` para
  persistencia entre instancias, orden y límite, aislamiento entre propietarios,
  borrado propio y ajeno, y purga de caducadas. No usar reloj real, red ni SQLite en
  memoria: se ejercitará el proveedor SQLite real en un archivo temporal.

## 3. Exponer el recurso HTTP autenticado y con scopes separados

**Archivos:** crear
`src/LocalAssistant.Api/Contracts/PersonalMemoryApiContracts.cs` y
`src/LocalAssistant.Api/Endpoints/PersonalMemoryEndpoints.cs`; actualizar
`src/LocalAssistant.Api/Program.cs` y crear
`tests/LocalAssistant.Tests/Api/PersonalMemoryEndpointTests.cs`.

- Declarar DTOs HTTP distintos de `PersonalMemory`: petición de creación, respuesta de
  nota y respuesta de lista. Solo las respuestas incluirán `id`, `text`,
  `createdAtUtc`, `modifiedAtUtc` y `expiresAtUtc`; nunca el propietario.
- Mapear `POST`, `GET` y `DELETE` en `/api/memories/personal`, con nombres y summaries
  estables. Usar los scopes exactos `memory.personal.write` y
  `memory.personal.read` antes de validar o consultar el almacén.
- Obtener siempre el propietario desde el claim `ClaimTypes.NameIdentifier`; no aceptar
  ni derivar propietario del cuerpo, la ruta o los parámetros. Responder `401` al
  cliente anónimo, `403` ante scope ausente, `400` por DTO, GUID o límite inválidos y
  `404` para borrado no encontrado o no propio. Cuando
  `ConversationPersistence:Enabled` sea `false`, responder `503` antes de tocar
  SQLite. La creación devolverá `201` y una `Location` de la nota creada; el borrado
  correcto devolverá `204`.
- Registrar el mapeo en `Program.cs`, pero no registrar ningún `ITool` ni modificar el
  registro de herramientas o el orquestador.
- Probar HTTP real en proceso con `WebApplicationFactory`: autenticación, scopes de
  lectura y escritura independientes, validación, creación y listado, borrado propio,
  `404` al intentar borrar desde otro principal usando el mismo archivo SQLite, y
  `503` con persistencia desactivada. Las pruebas compartirán solo un directorio
  temporal controlado; las dos identidades se configurarán en fábricas separadas.

## 4. Actualizar el contrato público y la documentación operativa

**Archivos:** actualizar `docs/api/openapi.yaml`, `README.md`,
`docs/ARCHITECTURE.md`, `docs/SECURITY.md` y `docs/ROADMAP.md`.

- Añadir las tres operaciones, sus `operationId`, seguridad, parámetros, cuerpos,
  respuestas `201`, `204`, `400`, `401`, `403`, `404` y `503`, y esquemas reutilizables
  para petición, nota y lista. Las descripciones OpenAPI serán en inglés y solo
  reflejarán el comportamiento implementado.
- Explicar en README que las notas personales comparten la configuración y retención
  de la persistencia privada, que requieren scopes independientes y que el archivo y
  backups deben protegerse.
- Actualizar arquitectura y seguridad para reflejar la tabla independiente, aislamiento
  de propietario, expiración, ausencia de cifrado propio y la exclusión explícita del
  contexto del modelo, proveedores y herramientas.
- Marcar en el roadmap únicamente el incremento implementado de memoria personal,
  manteniendo pendientes memoria compartida, de módulo, administrativa y recuperación
  automática. No se creará ADR: se aplican ADR 0019, 0024 y 0025 sin una nueva decisión
  arquitectónica.

## 5. Verificación y revisión antes de entregar

- Ejecutar `dotnet format LocalAssistant.sln --verify-no-changes --no-restore`.
- Ejecutar `dotnet build LocalAssistant.sln --configuration Release --no-restore`.
- Ejecutar `dotnet test LocalAssistant.sln --configuration Release --no-build --no-restore`.
- Revisar el diff con `git diff --check` y contrastar `docs/api/openapi.yaml` con los
  endpoints y DTOs efectivamente implementados.
- Hacer una revisión pre-PR proporcional al cambio de API, persistencia y seguridad,
  comprobando en especial ausencia de acceso del modelo, aislamiento por propietario,
  orden de autorización antes de SQLite, parámetros SQL, expiración y no exposición de
  propietario. Corregir hallazgos mecánicos antes de solicitar revisión humana.

## No objetivos explícitos

- No recuperar, inyectar, resumir, buscar semánticamente ni enviar notas al modelo o a
  un proveedor.
- No editar notas, añadir etiquetas, adjuntos, memoria compartida, memoria de módulos,
  memoria administrativa, importación/exportación ni sincronización.
- No cambiar la autenticación educativa actual, cifrar SQLite, crear un servicio de
  backups ni resolver retención de backups.
