# Evaluación de búsqueda semántica documental

La evaluación compara contenido literal y similitud semántica sobre un corpus
sintético incluido en el repositorio. No usa documentos del equipo, no habilita una
API ni convierte ningún archivo en conocimiento persistente.

Con Ollama y un modelo de embeddings local instalado, ejecútala desde la raíz:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Evaluate-LocalDocumentSemanticSearch.ps1 `
  -EmbeddingModel "nomic-embed-text"
```

El informe se escribe en `artifacts/local-document-semantic-search.json`. Incluye
solo identificadores, posiciones, aciertos y duraciones, nunca consultas ni contenido
documental. Si Ollama no está disponible o el modelo no existe, el comando falla y no
produce una medición semántica válida.
