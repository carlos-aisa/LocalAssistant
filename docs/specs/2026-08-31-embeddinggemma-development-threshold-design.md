# Diseño: umbral de búsqueda documental para embeddinggemma en desarrollo

## Decisión

Configurar `LocalAssistant:DocumentSemanticSearch:MinimumSimilarity` en `0.40` solo
en `appsettings.Development.json` cuando se use `embeddinggemma`. La configuración
base conserva `0.78` para no imponer la calibración de un modelo local concreto a
otros despliegues ni a otros modelos de embeddings.

## Evidencia

La evaluación manual del corpus sintético versión 2 con `embeddinggemma` mostró que
los seis casos positivos quedaron en primera posición, con puntuaciones entre
`0.502` y `0.612`. Los dos negativos obtuvieron como máximo `0.299`. Con `0.78` se
descartaban todos los positivos; con `0.40` se obtuvieron ocho aciertos y cero falsos
positivos.

## Alcance

El incremento modifica únicamente la configuración de desarrollo y explica la
calibración en la documentación de evaluación. No altera el algoritmo híbrido, la
configuración base, la API, permisos, persistencia ni el corpus.

## Verificación

- Validar que la configuración de desarrollo se enlaza correctamente al iniciar la
  API con el entorno Development.
- Mantener formato, compilación Release y la suite determinista completos.

## Revisión futura

`0.40` es una calibración provisional para `embeddinggemma` y este corpus reducido.
Se debe volver a medir antes de cambiarlo o de promoverlo a la configuración base,
usando casos positivos y negativos adicionales.
