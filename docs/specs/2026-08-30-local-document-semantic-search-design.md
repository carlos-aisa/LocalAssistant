# Diseño: búsqueda semántica de documentos locales

## Objetivo

Ampliar la búsqueda de contenido de la carpeta documental permitida para recuperar
archivos por significado, además de coincidencias literales, sin crear RAG automático
ni exponer el contenido completo a Jarvis.

## Contrato y autorización

La capacidad permanece bajo `documents.content.search` y el endpoint actual de
búsqueda de contenido. No se crean un scope, herramienta LLM ni endpoint alternativo.
Cada resultado conserva sus metadatos y referencia protegida, e incorpora un único
extracto de hasta 280 caracteres. La lectura completa sigue requiriendo
`documents.read`.

## Índice y sincronización

SQLite almacenará un índice documental separado del historial conversacional en el
directorio privado de estado. Solo indexará `.txt`, `.md`, `.json` y `.csv` dentro
del límite de 1 MiB ya aplicado a la lectura de contenido. No almacenará rutas
absolutas.

Cada archivo se divide en fragmentos acotados y cada fragmento conserva la identidad
protegida del documento, ruta relativa, tamaño, fecha de modificación, posición y
texto derivado necesario para obtener un extracto. El embedding local se almacena
junto con su modelo y versión de origen.

Antes de una búsqueda, una sincronización perezosa compara tamaño y fecha de
modificación dentro de la raíz autorizada. Añade archivos nuevos, reconstruye los
modificados y elimina las entradas cuyos archivos ya no existen. No añade watcher,
worker, escaneo de discos ni carpetas adicionales.

## Recuperación

La consulta combina coincidencias literales y similitud de coseno de embeddings
locales. Se agrupan fragmentos por documento, se ordenan resultados con orden estable
y se entrega el extracto del fragmento mejor posicionado. Las puntuaciones internas,
los vectores y el contenido completo no se devuelven.

Si Ollama o el modelo de embeddings no están disponibles, la operación mantiene la
búsqueda literal existente. No inventa resultados semánticos ni falla una consulta
literal válida por la indisponibilidad del índice.

## Privacidad y retención

El índice, embeddings y extractos son datos privados derivados del contenido local.
No se registran en logs ni auditoría, no se entregan al modelo conversacional y no se
envían a proveedores externos. El endpoint Ollama configurado debe ser local; una
configuración remota no es fallback autorizado para esta capacidad.

La retirada o modificación de un archivo elimina o reconstruye sus datos derivados
en la siguiente sincronización. El índice se elimina al desactivar o borrar el
almacenamiento documental de la instalación.

## Verificación

- Pruebas SQLite realistas para altas, modificaciones, borrados y aislamiento del
  índice documental respecto a conversaciones.
- Pruebas deterministas de fragmentación, ranking híbrido, extracto máximo,
  referencias protegidas, formatos y límites.
- Pruebas HTTP de autorización y degradación literal si falta el embedding local.
- Pruebas HTTP falsas del adaptador Ollama, sin red ni GPU.

## No objetivos

No se añaden RAG, respuesta generada sobre documentos, watcher, worker, base vectorial,
OCR, indexación de repositorios, rutas arbitrarias, acceso de módulos ni protección
automática frente a prompt injection en contenido que aún no se entrega al modelo.
