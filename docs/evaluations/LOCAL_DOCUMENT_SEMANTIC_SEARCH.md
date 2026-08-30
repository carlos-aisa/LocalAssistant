# Evaluación de búsqueda semántica documental

La evaluación compara contenido literal y similitud semántica sobre un corpus
sintético incluido en el repositorio. No usa documentos del equipo, no habilita una
API ni convierte ningún archivo en conocimiento persistente.

Con Ollama y un modelo de embeddings local instalado, ejecútala desde la raíz:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Evaluate-LocalDocumentSemanticSearch.ps1 `
  -EmbeddingModel "nomic-embed-text" `
  -MinimumSimilarity 0.78
```

El informe se escribe en `artifacts/local-document-semantic-search.json`. Incluye
solo identificadores, expectativas, posiciones, puntuaciones, aciertos, falsos
positivos y duraciones; nunca consultas ni contenido documental. Un caso sin
`expectedDocumentId` en el corpus es negativo: cuenta como falso positivo cuando el
primer resultado semántico alcanza el umbral.

`MinimumSimilarity` debe coincidir con
`LocalAssistant:DocumentSemanticSearch:MinimumSimilarity` de la API que se quiere
calibrar. El valor predeterminado es `0.78`, igual que la configuración de ejemplo,
pero el informe registra siempre el valor recibido. La evaluación no modifica la
configuración ni ajusta el umbral automáticamente.

Para usar `embeddinggemma` en Development, configura `0.40` en tu
`appsettings.Development.json` local, a partir del corpus sintético versión 2: seis
positivos entre `0.502` y `0.612`, y dos negativos con un máximo de `0.299`. Es una
calibración local provisional; la configuración base mantiene `0.78` hasta contar con
una muestra más amplia y evaluar otros modelos.

Si Ollama no está disponible o el modelo no existe, el comando falla y no produce una
medición semántica válida.
