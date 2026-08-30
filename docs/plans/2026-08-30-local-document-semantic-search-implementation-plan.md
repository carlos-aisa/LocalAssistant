# Plan de implementación: búsqueda semántica de documentos locales

## Objetivo

Incorporar recuperación híbrida de documentos permitidos, combinando literal y
semántica bajo el permiso existente `documents.content.search`. La entrega conserva
el endpoint actual, devuelve un extracto limitado y degrada a literal si Ollama local
no puede producir embeddings.

## Decisiones y límites

- Un único scope y endpoint existentes; se ampliará el DTO de resultado y OpenAPI
  porque el extracto es visible al cliente.
- Un almacén SQLite documental propio, independiente de `conversations.db`, ubicado
  junto al estado privado configurado por `SqliteConversationStoreOptions`.
- Sin watcher ni worker: cada búsqueda sincroniza la raíz permitida y solo reindexa
  archivos nuevos o cuyo tamaño o modificación UTC cambien.
- Únicamente `.txt`, `.md`, `.json` y `.csv` hasta 1 MiB; nunca rutas absolutas.
- No se envía contenido a ningún endpoint que no sea Ollama local configurado; no se
  registra contenido, extractos, consultas, vectores ni puntuaciones.

## Incremento 1: contratos, fragmentación e índice SQLite

**Archivos:** contratos nuevos en `src/LocalAssistant.Core/Documents/`; nuevo
`SqliteDocumentSemanticIndex` y opciones en `src/LocalAssistant.Infrastructure/Documents/`;
pruebas SQLite en `tests/LocalAssistant.Tests/Infrastructure/`.

1. Definir contratos para un fragmento indexado, resultado híbrido y extracto de 280
   caracteres. Validar identidad relativa, límites, embedding finito y dimensiones.
2. Implementar fragmentación determinista por límites de caracteres, con posición
   estable y sin cortar caracteres de control. Extraer solo texto ya permitido por
   `DocumentFilePolicy`.
3. Crear el esquema SQLite separado para documentos, fragmentos, modelo, embedding,
   tamaño y última modificación. Parametrizar todo SQL y purgar en la misma operación
   los fragmentos de documentos borrados o modificados.
4. Añadir pruebas de altas, cambios, borrados, formatos no permitidos, máximo de
   tamaño, extracto y aislamiento respecto a conversaciones.

## Incremento 2: sincronización perezosa y ranking híbrido

**Archivos:** `FileSystemDocumentContentSearch.cs` o un coordinador documental
dedicado; `OllamaTextEmbeddingProvider.cs`; pruebas de infraestructura y adaptador.

1. Antes de buscar, enumerar exclusivamente la raíz autorizada, ignorando enlaces e
   inaccesibles con la política actual. Comparar metadatos y generar embeddings solo
   para fragmentos pendientes.
2. Crear el embedding de la consulta, combinar coincidencia literal y similitud de
   coseno, agrupar por documento y resolver empates de forma estable. Devolver solo
   los resultados limitados y el extracto del mejor fragmento.
3. Si no existe modelo, falla Ollama o su respuesta es inválida, registrar únicamente
   el fallo seguro y ejecutar el buscador literal actual. No persistir resultados
   parciales corruptos ni presentar semántica como disponible.
4. Probar con un proveedor de embeddings falso: ranking, degradación, cancelación,
   cambio de modelo y que los documentos no modificados no se reembeben.

## Incremento 3: composición HTTP, contrato y operación

**Archivos:** `DocumentEndpoints.cs`, `DocumentSearchApiContracts.cs`, `Program.cs`,
`docs/api/openapi.yaml`, pruebas de `DocumentEndpointTests.cs`, arquitectura,
seguridad y operaciones.

1. Registrar el índice y coordinador como servicios locales thread-safe usando la
   configuración privada existente, y activar semántica solo si existe
   `EmbeddingModel` local válido.
2. Añadir `excerpt` opcional al resultado HTTP, sin exponer ruta absoluta, score,
   vector ni texto completo. Mantener autorización `documents.content.search` antes
   de la sincronización e indexado.
3. Actualizar OpenAPI y pruebas HTTP para permiso, extracto, referencia protegida,
   degradación literal y errores seguros.
4. Documentar ruta privada, retención derivada, backup, endpoint Ollama local y la
   ausencia explícita de RAG, watcher y worker. Actualizar el roadmap solo cuando el
   flujo esté implementado y probado.

## Verificación

En cada incremento: formato, build Release, pruebas afectadas, suite completa y
`git diff --check`. En el tercero, validar además OpenAPI. La evaluación manual con
Ollama sigue fuera de CI; ninguna prueba predeterminada depende de red, GPU o modelo.

## No objetivos

No se agregan nuevos permisos, API alternativa, RAG, respuesta generada sobre
documentos, base vectorial, indexación de repositorios, watcher, worker, OCR ni
acceso a ubicaciones no permitidas.
