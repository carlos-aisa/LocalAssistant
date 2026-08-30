# Plan de implementación: evaluación negativa de búsqueda semántica local

## Objetivo

Ampliar la evaluación manual del índice documental con casos negativos y métricas de
falsos positivos. El cambio calibra el umbral ya usado por la búsqueda semántica de
producción; no modifica dicha búsqueda, su API, permisos ni el ciclo de indexación.

## Decisiones y supuestos

- Un caso sin `expectedDocumentId` representa una consulta para la que no debe
  devolverse ningún resultado semántico que supere el umbral.
- Para mantener el ejecutable manual independiente de la configuración de un
  despliegue de la API, el umbral será un argumento explícito, con el mismo valor
  predeterminado que `DocumentSemanticSearchOptions` (`0.78`). El informe siempre
  registrará el valor usado. La documentación indicará que debe ejecutarse con el
  valor configurado en la API cuando difiera del predeterminado.
- La evaluación literal no fabrica puntuaciones semánticas: para negativos informa
  que devolvió algún resultado literal, pero no decide el falso positivo semántico.
- El corpus continúa siendo sintético, versionado y apto para pruebas sin Ollama.

## Paso 1: ampliar contratos y semántica de evaluación

**Archivos:**

- `src/LocalAssistant.Core/Documents/DocumentSemanticSearchEvaluation.cs`

**Cambios:**

1. Hacer opcional `DocumentSearchEvaluationCase.ExpectedDocumentId`; conservar la
   validación de identificador y consulta y validar la referencia solo cuando el
   caso sea positivo.
2. Incorporar al resultado el tipo de expectativa, puntuación superior y los
   indicadores necesarios para distinguir acierto, fallo positivo y falso positivo
   semántico, sin incluir consulta ni contenido.
3. Añadir al informe recuentos agregados calculados a partir de sus resultados:
   aciertos, fallos y falsos positivos.
4. Cambiar `EvaluateSemanticAsync` para recibir y validar `minimumSimilarity` en
   `[-1, 1]`. Conservar el ranking estable y el límite; evaluar positivos por la
   puntuación del documento esperado y negativos por la puntuación del primer
   resultado.
5. Mantener `EvaluateLiteral` como baseline textual: positivos se evalúan por
   posición y negativos exponen que hubo recuperación literal, sin reutilizar el
   indicador de falso positivo semántico.

**Límites:** los vectores siguen siendo efímeros; se propaga cancelación al proveedor;
un cambio de modelo o dimensión permanece como error explícito.

## Paso 2: corpus y pruebas deterministas

**Archivos:**

- `tests/LocalAssistant.Tests/Documents/Fixtures/document-semantic-search-corpus.json`
- `tests/LocalAssistant.Tests/Documents/DocumentSemanticSearchEvaluationTests.cs`

**Cambios:**

1. Incrementar la versión del corpus y añadir consultas negativas sintéticas que no
   describan ninguno de sus documentos.
2. Adaptar los dobles de embeddings para declarar puntuaciones controladas y probar:
   positivo recuperado por encima del umbral, positivo presente pero rechazado por
   debajo de él, negativo limpio y negativo con falso positivo por encima del
   umbral.
3. Verificar límites, dimensiones incompatibles y que el informe serializado no
   contiene consultas ni contenido. Las pruebas no contactarán con Ollama, red ni
   GPU.

## Paso 3: propagar el umbral al ejecutable manual

**Archivos:**

- `src/LocalAssistant.DocumentSearchEvaluation/Program.cs`
- `scripts/Evaluate-LocalDocumentSemanticSearch.ps1`
- `docs/evaluations/LOCAL_DOCUMENT_SEMANTIC_SEARCH.md`

**Cambios:**

1. Añadir la opción `--minimum-similarity` al ejecutable, con validación de rango,
   valor predeterminado `0.78` y presencia explícita en el informe seguro.
2. Añadir el parámetro PowerShell equivalente y reenviarlo al ejecutable. Mantener
   endpoint local, modelo obligatorio y salida bajo `artifacts/`.
3. Documentar cómo leer `MinimumSimilarity` de la configuración de la API y cómo
   ejecutar la evaluación con ese mismo valor. Describir las métricas nuevas y que
   ningún resultado manual cambia el umbral por sí mismo.
4. Validar la sintaxis PowerShell sin llamar a Ollama.

## Documentación y no objetivos

La especificación aprobada describe el alcance; la guía de evaluación se actualizará
en el mismo cambio de comportamiento. No se modifican `docs/api/openapi.yaml`,
`docs/ROADMAP.md`, almacenamiento, esquema SQLite, autorización, endpoints, proveedor
Ollama ni el algoritmo de `HybridDocumentContentSearch`.

## Verificación

1. `dotnet format LocalAssistant.sln --no-restore --verify-no-changes`
2. `dotnet build LocalAssistant.sln -c Release --no-restore`
3. `dotnet test LocalAssistant.sln -c Release --no-restore`
4. `git diff --check`
5. Analizar la sintaxis de `scripts/Evaluate-LocalDocumentSemanticSearch.ps1` con el
   parser de PowerShell, sin descargar ni ejecutar modelos locales.

El evaluador con Ollama se ejecutará manualmente solo cuando el modelo local esté
disponible; no es un requisito de CI ni de las pruebas deterministas.
