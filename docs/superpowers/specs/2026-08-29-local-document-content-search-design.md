# Diseño: búsqueda textual acotada de documentos locales

## Objetivo

Permitir localizar documentos autorizados por una frase literal de su contenido sin
ampliar la raíz permitida, revelar texto al cliente ni convertir los documentos en
conocimiento del modelo.

## Alcance

La API incorporará `GET /api/documents/content-search?text=...` como una capacidad
distinta de buscar metadatos y de leer un documento concreto. Requerirá un principal
autenticado con el nuevo scope `documents.content.search`.

La consulta exigirá una frase no vacía, recortada y limitada a 200 caracteres. Admitirá
además los filtros ya establecidos de extensión, ruta relativa, fechas de modificación
y límite de resultados. La respuesta devolverá únicamente los mismos metadatos seguros
de la búsqueda actual: referencia protegida, nombre, extensión, ruta relativa, tamaño
y fecha de modificación. No devolverá la frase coincidente, fragmentos, líneas ni
rutas absolutas.

La búsqueda abrirá únicamente `.txt`, `.md`, `.json` y `.csv` de hasta 1 MiB bajo la
raíz documental configurada. Comparará texto literal sin distinguir mayúsculas y
minúsculas. Los formatos no admitidos, archivos mayores, inaccesibles o con errores de
lectura se omitirán de forma segura; no se truncarán ni se presentarán como coincidencias.

## Diseño técnico

El núcleo definirá una consulta y un contrato específicos para búsqueda de contenido.
El adaptador de filesystem reutilizará la resolución de raíz, contención de rutas,
protección contra puntos de reanálisis y referencias protegidas ya aplicadas a la
búsqueda de metadatos. El lector abrirá el archivo una vez, comprobará su tamaño antes
de cargarlo y comparará su contenido sin crear un índice.

El endpoint validará el scope antes de resolver la consulta o tocar el filesystem. Los
filtros y el límite se validarán en el contrato del núcleo, igual que en la búsqueda de
metadatos. Una consulta inválida producirá `400`; una consulta válida sin coincidencias
devolverá `200` con una lista vacía.

## Seguridad y límites

- `documents.search` no concede búsqueda de contenido y `documents.read` tampoco la
  sustituye; las tres capacidades tienen permisos independientes.
- El contenido solo se usa para decidir si el documento coincide. No se devuelve, no
  se registra, no se incorpora a la conversación ni se entrega a un proveedor.
- La implementación no buscará fuera de la raíz, no seguirá reparse points y no
  aceptará rutas absolutas.
- El límite de 1 MiB evita cargas no acotadas. No habrá fragmentos parciales para
  archivos mayores.
- No se añaden RAG, embeddings, base vectorial, persistencia de índice, OCR, watchers,
  búsqueda semántica ni soporte de PDF, Word, Excel, imágenes o binarios.

## Pruebas y documentación

Las pruebas unitarias cubrirán validación de frase, límite y ruta. Las pruebas de
infraestructura cubrirán coincidencia sin distinción de mayúsculas, formatos admitidos,
archivos grandes, formatos no admitidos, límites de resultado y contención de raíz.
Las pruebas HTTP verificarán autenticación, el nuevo scope independiente, respuesta
sin contenido y petición inválida.

Se actualizarán OpenAPI, README, arquitectura, seguridad y roadmap para describir la
capacidad y sus límites. No se requiere ADR: aplica las fronteras de recursos locales
ya aceptadas sin seleccionar almacenamiento ni arquitectura nuevos.

## Criterios de aceptación

- Un principal con `documents.content.search` localiza un `.txt` permitido por texto
  literal y recibe solo metadatos seguros.
- La misma petición sin autenticación o sin ese scope no inspecciona archivos.
- Un archivo fuera de formato, mayor de 1 MiB o fuera de la raíz no aparece como
  coincidencia.
- La respuesta no contiene texto, fragmentos ni rutas absolutas.
- Una búsqueda no crea índice, memoria derivada ni tráfico a un proveedor.
