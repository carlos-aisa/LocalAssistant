# ADR 0032: Adoptar la dirección de búsqueda semántica documental local

- Estado: Aceptada
- Fecha: 2026-08-30

## Contexto

La búsqueda documental actual localiza coincidencias literales de contenido. Se
evaluó una alternativa semántica con `embeddinggemma` sobre un corpus sintético de
ocho documentos y seis consultas parafraseadas, sin datos ni rutas reales.

La búsqueda literal recuperó 0 de 6 documentos esperados. La semántica recuperó 6 de
6 en primera posición. Generar los embeddings del corpus costó 4,0 segundos y cada
consulta posterior tardó 52 ms de media, con un máximo de 65 ms.

## Decisión

Se adopta la dirección de una futura búsqueda semántica local para la carpeta
documental permitida. Su diseño deberá usar un índice persistente o una indexación
explícita: recalcular los embeddings de todos los documentos en cada consulta no es
aceptable como comportamiento de producto.

La capacidad seguirá siendo independiente de la lectura documental y de RAG. Requerirá
su propio scope, autorización previa a recuperar contenido, límites de tamaño y
resultados, borrado del índice al retirar documentos y protección frente a instrucciones
hostiles contenidas en archivos.

## Consecuencias

La evaluación manual no cambia la API ni expone búsqueda semántica a Jarvis. El
siguiente incremento deberá producir primero una especificación de la capacidad y no
añadirá una base vectorial, watcher, worker o ingesta automática sin una necesidad
medida.
