# Plan de implementación: umbral embeddinggemma en desarrollo

## Objetivo

Aplicar la calibración manual de `0.40` para `embeddinggemma` exclusivamente en el
entorno Development, conservando `0.78` como valor base para despliegues y modelos no
calibrados.

## Paso 1: override de desarrollo

**Archivo:** `src/LocalAssistant.Api/appsettings.Development.json`

Añadir `LocalAssistant:DocumentSemanticSearch:MinimumSimilarity: 0.40` junto a la
configuración de Ollama de desarrollo ya existente. No incluir claves, rutas
personales ni otros valores de despliegue.

## Paso 2: documentación de calibración

**Archivo:** `docs/evaluations/LOCAL_DOCUMENT_SEMANTIC_SEARCH.md`

Registrar que `0.40` es el override de Development validado con `embeddinggemma` y
el corpus sintético versión 2; mantener explícito que no es una recomendación global
ni un ajuste automático.

## Verificación

1. Ejecutar una prueba de composición de configuración que compruebe que Development
   enlaza `0.40` y que la configuración base conserva `0.78`.
2. `dotnet format LocalAssistant.sln --no-restore --verify-no-changes`
3. `dotnet build LocalAssistant.sln -c Release --no-restore`
4. `dotnet test LocalAssistant.sln -c Release --no-restore`
5. `git diff --check`

## No objetivos

No se cambia el algoritmo híbrido, el corpus, el umbral base, API, permisos,
persistencia, índices ni el comportamiento de otros modelos de embeddings.
