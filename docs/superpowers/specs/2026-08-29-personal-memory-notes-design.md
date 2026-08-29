# Diseño: notas de memoria personal persistentes

## Objetivo

Permitir que una persona autenticada guarde, consulte y elimine notas personales
explícitas, como una preferencia estable, sin mezclar esos datos con conversaciones
ni hacer que el modelo los use automáticamente.

El primer incremento resuelve almacenamiento privado y controlado. No cambia el
contexto de chat, las herramientas disponibles al modelo ni las llamadas a proveedores.

## Alcance funcional

Una nota personal contiene únicamente texto aportado de forma explícita por el
cliente. Por ejemplo: «Prefiero que las recetas indiquen alternativas sin lactosa».

Cada nota tendrá un identificador opaco generado por el servidor, el identificador del
principal propietario, el texto, las fechas de creación y última modificación, y su
fecha de caducidad. El texto se recortará y deberá contener entre 1 y 2.000 caracteres.
No se admitirán etiquetas, categorías, archivos adjuntos, formato enriquecido ni
actualización de una nota en este incremento.

## Contrato HTTP

La API incorporará las siguientes operaciones, separadas de los recursos de
conversación:

- `POST /api/memories/personal` requiere `memory.personal.write`. Recibe
  `{ "text": "..." }`, crea la nota para el principal autenticado y devuelve `201`
  con su representación y cabecera `Location`.
- `GET /api/memories/personal` requiere `memory.personal.read`. Devuelve solamente
  las notas no caducadas del principal autenticado, ordenadas por última modificación
  descendente. Acepta `limit`, con valor predeterminado de 50 y rango de 1 a 100.
- `DELETE /api/memories/personal/{memoryId}` requiere `memory.personal.write`.
  Elimina solo la nota del principal autenticado y devuelve `204`. Una nota inexistente,
  caducada o perteneciente a otro principal devuelve `404`.

El propietario no se aceptará en el cuerpo, ruta ni consulta: se obtendrá siempre del
principal autenticado. Las operaciones sin autenticación devolverán `401`, y un
principal autenticado sin el scope requerido devolverá `403` antes de acceder al
almacenamiento. Texto, identificadores y límite inválidos devolverán `400`.

Las representaciones de creación y listado expondrán `id`, `text`, `createdAtUtc`,
`modifiedAtUtc` y `expiresAtUtc`. No expondrán el identificador del propietario.
`DELETE` es una acción directa y explícita del cliente autenticado; no se registrará
como herramienta del modelo ni podrá originarse mediante el bucle de herramientas.

## Persistencia y ciclo de vida

Las notas se guardarán en una tabla SQLite independiente llamada `PersonalMemories`.
Compartirá el archivo de base de datos y la configuración ya opt-in de
`LocalAssistant:ConversationPersistence`, pero no reutilizará tablas, contratos ni
consultas de conversaciones. La memoria personal solo estará disponible cuando
`ConversationPersistence:Enabled` sea `true`; con la persistencia privada desactivada,
las tres rutas devolverán `503` y no crearán ningún archivo ni dato persistente.

La retención configurada en `ConversationPersistence:RetentionDays` se aplicará también
a las notas. El valor predeterminado seguirá siendo 30 días. Al crear una nota se
calculará `expiresAtUtc` con el reloj inyectado. Antes de crear, listar o eliminar, el
almacenamiento purgará las notas caducadas. El borrado selectivo se ejecutará en una
única operación condicionada por identificador y propietario, para que una nota de otro
principal no pueda eliminarse por error ni por carrera.

El contrato del núcleo será específico de memoria personal y no dependerá de SQLite ni
de tipos HTTP. La infraestructura implementará ese contrato, inicializará la tabla y
su índice por propietario y caducidad, y usará consultas parametrizadas.

## Seguridad y límites

- Los scopes de lectura y escritura son independientes. El scope de conversaciones no
  concede acceso a memoria personal.
- Las notas son datos privados: no se registrarán, no se incluirán en mensajes de chat,
  no se enviarán a proveedores ni se usarán para inferencia, recuperación o RAG.
- La separación por propietario se aplicará en todas las lecturas y borrados, además de
  verificarse en el endpoint.
- SQLite no cifra por sí mismo los datos en reposo. El despliegue debe proteger el
  archivo de base de datos y sus copias como dato privado, conforme al ADR 0025.
- No se añaden memoria compartida, memoria de módulo, memoria administrativa,
  importación/exportación, sincronización, embeddings, búsqueda semántica, resúmenes
  automáticos ni edición de notas.

## Pruebas y documentación

Las pruebas del núcleo cubrirán validación de texto y límite. Las pruebas de
infraestructura cubrirán creación, listado ordenado y limitado, aislamiento entre
propietarios, eliminación condicionada y caducidad mediante `TimeProvider` controlado.
Las pruebas HTTP cubrirán `401`, scopes independientes, validación, creación, listado,
eliminación y la respuesta `503` con persistencia privada desactivada.

Se actualizarán OpenAPI, README, arquitectura, seguridad y roadmap para describir el
recurso, su dependencia de la persistencia privada, la retención y la prohibición de
uso automático por el modelo. No se requiere un ADR nuevo: concreta el ámbito de
memoria personal y el ciclo de vida ya establecidos en los ADR 0019 y 0025.

## Criterios de aceptación

- Una persona con `memory.personal.write` puede crear una nota y recibe una
  representación sin información de propietario.
- Una persona con `memory.personal.read` solo lista sus propias notas no caducadas;
  una nota de otro principal nunca se revela ni se elimina.
- La nota caduca con la retención configurada y deja de estar disponible sin depender
  del reloj real en las pruebas.
- Desactivar `ConversationPersistence` impide cualquier persistencia de notas y hace
  que las rutas respondan `503`.
- Ninguna nota se incorpora al contexto del modelo, se transmite a un proveedor o se
  expone como herramienta del modelo.
