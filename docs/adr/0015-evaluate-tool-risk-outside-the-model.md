# ADR 0015: Evaluar el riesgo de herramientas fuera del modelo

## Contexto

`ReadOnly` frente a modificación de estado no refleja por sí solo el riesgo. Una
lectura de documentos, ubicación o salud puede ser sensible; una consulta externa
puede tener coste o exponer datos; y una autorización puede revocarse entre la
respuesta del modelo y la ejecución.

## Decisión

Cada herramienta declara un perfil con impacto, sensibilidad, exposición, coste,
confirmación y scopes. Una política pura decide permitir, exigir confirmación o
denegar. El orquestador aplica la decisión al catálogo entregado al proveedor y de
nuevo antes de ejecutar una llamada. El contexto actual es anónimo y no concede
scopes: perfiles privados, sensibles, con scopes o de exposición externa se deniegan
por defecto. Modificación, ejecución, coste significativo o confirmación explícita
exigen el protocolo de confirmación retenido por el servidor.

## Consecuencias

La decisión queda fuera del LLM y una lectura ya no se trata como segura por defecto.
El sistema no implementa aún autenticación, usuarios, persistencia de permisos ni
RBAC o ABAC completos. Un futuro adaptador de identidad sustituirá el contexto
anónimo mediante información verificada; no se añadirá una lista de scopes aportada
por el cliente como atajo. Las herramientas externas deberán incorporar una ruta
que demuestre su paso por el `Tools Gateway` antes de dejar de ser denegadas.
