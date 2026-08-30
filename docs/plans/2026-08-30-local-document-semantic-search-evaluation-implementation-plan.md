# Plan de implementación: evaluación semántica de documentos locales

## Objetivo

Construir un evaluador manual y reproducible que compare la búsqueda literal de
contenido con una recuperación semántica experimental sobre un corpus sintético. El
resultado informa una decisión posterior; no expone búsqueda semántica a Jarvis.

## Decisiones confirmadas

- El corpus es sintético, versionado y no contiene rutas ni datos reales.
- La comparación se limita a contenido literal frente a similitud semántica. La
  búsqueda por nombre, extensión, ruta y fecha queda fuera porque resuelve otra clase
  de consulta.
- Los embeddings proceden solo de Ollama local. La evaluación manual falla de forma
  explícita si falta el modelo y no degrada a red ni a un proveedor externo.
- Los vectores, textos y consultas solo viven durante la ejecución. El informe no
  conserva textos ni rutas, y se escribe en `artifacts/`.
- La evaluación no pertenece a CI. Las unidades que construyen corpus, rankings y
  métricas deben poder probarse sin Ollama, red ni GPU.

## Entrega incremental

Este plan se implementará en dos cambios publicables para no introducir la medición
real antes de poder validar su núcleo de forma determinista.

### Incremento 1: corpus y evaluador determinista

**Archivos previstos:**

- `src/LocalAssistant.Core/Documents/DocumentSemanticSearchEvaluation.cs`
- `tests/LocalAssistant.Tests/Documents/DocumentSemanticSearchEvaluationTests.cs`
- `tests/LocalAssistant.Tests/Documents/Fixtures/document-semantic-search-corpus.json`

**Responsabilidad:**

1. Definir contratos inmutables para documentos sintéticos, casos de evaluación,
   resultados ordenados por estrategia y métricas agregadas. Validar identificadores
   estables, límites positivos y que cada caso nombre un documento existente.
2. Cargar el corpus desde el recurso de pruebas y proporcionar casos de equivalencia
   literal y semántica en `.txt`, `.md`, `.json` y `.csv`.
3. Implementar el baseline literal sobre el texto en memoria y el ranking semántico
   sobre vectores recibidos, con similitud de coseno y orden estable cuando haya
   empate. Rechazar vectores de dimensiones incompatibles antes de calcular.
4. Calcular posición, acierto dentro de límite y duración mediante `TimeProvider`.
   Crear la proyección segura de informe que solo incluye modelo, versión, ids,
   posición, acierto y milisegundos.
5. Cubrir el comportamiento con dobles de embeddings: resultados esperados, empates,
   dimensión inválida, límites, cancelación y ausencia de texto en el informe.

**Límites:** sin `HttpClient`, Ollama, sistema de archivos productivo, DI, endpoints,
scopes, persistencia ni configuración nueva.

### Incremento 2: ejecución manual con Ollama local

**Archivos previstos:**

- `src/LocalAssistant.DocumentSearchEvaluation/LocalAssistant.DocumentSearchEvaluation.csproj`
- `src/LocalAssistant.DocumentSearchEvaluation/Program.cs`
- `LocalAssistant.sln`
- `tests/LocalAssistant.Tests/Infrastructure/OllamaTextEmbeddingProviderTests.cs`
- `scripts/Evaluate-LocalDocumentSemanticSearch.ps1`
- `docs/evaluations/LOCAL_DOCUMENT_SEMANTIC_SEARCH.md`
- `docs/OPERATIONS.md`

**Responsabilidad:**

1. Añadir un ejecutable mínimo con una única responsabilidad: cargar el corpus
   sintético, construir el evaluador, configurar `OllamaTextEmbeddingProvider` y
   ejecutar ambos rankings. No hospeda API, no registra servicios de producción y no
   recibe directorios de documentos.
2. Aceptar únicamente endpoint local, modelo de embeddings, límite y ruta de informe;
   validar argumentos antes de llamar a Ollama. Reutilizar los DTOs HTTP y validación
   de respuestas existentes del adaptador.
3. Añadir un script PowerShell que invoque el ejecutable, cree el directorio
   `artifacts/` y devuelva error distinto de cero si Ollama no está disponible o el
   informe no se completa. El script no mostrará ni persistirá el corpus ni las
   consultas.
4. Documentar instalación del modelo, comando de ejecución, estructura del informe,
   límites de privacidad y que los resultados no autorizan todavía una función de
   búsqueda semántica en producción.
5. Mantener las pruebas HTTP de `OllamaTextEmbeddingProvider` deterministas con un
   manejador HTTP falso. Verificar la sintaxis del script sin ejecutar Ollama.

**Límites:** el nuevo proyecto está justificado exclusivamente como ejecutable manual
de evaluación. No crea una base vectorial, worker, watcher, almacenamiento de
embeddings, API pública, herramienta LLM ni una ingesta RAG.

## Documentación y roadmap

Al completar el segundo incremento, actualizar `docs/evaluations/` y
`docs/OPERATIONS.md`. `docs/ROADMAP.md` no se marcará como completado hasta ejecutar
el evaluador contra un modelo local y registrar la decisión de adopción o descarte.

## Verificación

Para cada incremento: `dotnet format LocalAssistant.sln --verify-no-changes
--no-restore`, build Release, las pruebas del módulo afectado, la suite completa y
`git diff --check`.

Para el segundo: análisis de sintaxis PowerShell y una ejecución manual documentada
contra Ollama local. Esa ejecución queda fuera de CI y de las pruebas deterministas.
