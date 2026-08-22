# Diseño de búsqueda documental local por metadatos

## Objetivo

Entregar el primer flujo de descubrimiento documental: localizar archivos dentro de
la única raíz permitida sin abrirlos ni usar su contenido.

## Alcance

La API expondrá `GET /api/documents` y requerirá un principal autenticado con el
scope `documents.search`. Admitirá filtros opcionales de nombre, extensión, ruta
relativa y rango de fecha de modificación. Buscará únicamente bajo
`ILocalDocumentRoot` y devolverá referencias controladas con identificador opaco,
nombre, extensión, ruta relativa, tamaño y fecha de modificación.

Las rutas de entrada serán relativas y se validarán antes de resolverlas. La
implementación rechazará resultados cuyo destino resuelto quede fuera de la raíz,
incluidos enlaces o cambios de destino. Se impondrá un límite de resultados para
acotar recursos.

## Fuera de alcance

- Lectura o extracción del contenido de archivos.
- Búsqueda textual, índice persistente, embeddings, RAG, watchers u OCR.
- Herramientas de modelo, rutas absolutas proporcionadas por el cliente y acceso a
  otra carpeta.
- Autorización doméstica completa, múltiples raíces o concesiones por módulo.

## Pruebas y documentación

Las pruebas unitarias crearán un árbol temporal para verificar filtros, límites y
rechazo de destinos que salen de la raíz. Las pruebas HTTP cubrirán autenticación,
scope requerido y la forma de la respuesta. README, arquitectura, OpenAPI y roadmap
se actualizarán para describir la capacidad y su límite de solo metadatos.

## Criterios de aceptación

- Una llamada autorizada localiza archivos por metadatos solo dentro de la raíz.
- Una llamada anónima o sin `documents.search` no obtiene metadatos.
- La respuesta no incluye contenido ni rutas absolutas.
- Formato, build Release y todas las pruebas pasan.
