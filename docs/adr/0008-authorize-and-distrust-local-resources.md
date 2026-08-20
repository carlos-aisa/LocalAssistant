# ADR 0008: Autorizar recursos locales y tratar su contenido como no confiable

- Estado: Aceptada
- Fecha: 2026-08-20

## Contexto

`BatchCooking` y futuros módulos necesitarán migrar o generar archivos locales. Una
herramienta de rutas arbitrarias ampliaría el alcance del modelo al perfil completo
del usuario. Incluso un archivo legítimamente autorizado puede contener fórmulas,
macros, vínculos, prompt injection o contenido malicioso.

La autorización del lugar y la confianza en el contenido resuelven riesgos distintos.
También son diferentes leer, extraer, importar, exportar, crear, sobrescribir y
eliminar.

## Decisión

Los módulos solo accederán a recursos o ámbitos registrados previamente para un
principal y módulo. La lectura será el permiso predeterminado; escritura y acciones
destructivas se autorizarán por separado. El servicio validará el destino resuelto,
formato, tamaño y límites en cada operación y permitirá revocar accesos.

Todo documento importado se tratará como dato no confiable, nunca como instrucción
del sistema. La extracción no ejecutará contenido activo. Importar exigirá
previsualización y confirmación y conservará origen y versión. Crear una salida
producirá un archivo nuevo por defecto; sobrescribir o eliminar el original requerirá
otra autorización.

No se define todavía una API, formato de registro, librería de extracción ni almacén
de procedencia concretos.

## Consecuencias

- No existirá una herramienta equivalente a leer cualquier ruta indicada por el
  modelo.
- Un permiso de carpeta no concederá automáticamente escritura ni confianza en sus
  archivos.
- La migración será más visible y requerirá pasos de revisión.
- Los importadores deberán aplicar defensas específicas por formato y pruebas contra
  path traversal, contenido activo, prompt injection y agotamiento de recursos.
- Versiones o hashes permitirán comparar cambios y evitar reimportaciones ambiguas.
