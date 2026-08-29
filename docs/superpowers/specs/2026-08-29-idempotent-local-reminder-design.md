# Diseño: recordatorio local idempotente

## Objetivo

Incorporar la primera herramienta local que cambia estado para demostrar una operación
confirmada, auditable e idempotente. El vertical slice crea un recordatorio en memoria
y no pretende ser todavía un sistema de agenda, notificaciones ni automatización.

## Alcance

La herramienta `create_reminder` recibirá un texto breve y una fecha UTC futura. Su
perfil de riesgo declarará modificación de estado, datos privados, ejecución local,
coste nulo, confirmación requerida y el scope `reminders.write`.

El orquestador asignará un identificador de operación al retener la confirmación. Lo
asociará al principal, conversación y llamada de herramienta que ya valida. El
identificador no se recibirá desde HTTP ni concederá permisos: solo evita que esa
operación confirmada produzca el mismo efecto dos veces.

Un almacén en memoria creará el recordatorio de forma atómica por principal e
identificador de operación. Si recibe de nuevo la misma operación, devolverá el
recordatorio ya creado sin insertar otro. Una operación distinta creará un recordatorio
distinto aunque sus datos coincidan. El estado se perderá al reiniciar.

## Diseño técnico

Se añadirá un contexto de ejecución interno para que el orquestador entregue a la
herramienta el principal y el identificador de operación retenido por el servidor. La
interfaz de herramientas mantendrá argumentos JSON y cancelación; el nuevo contexto
evita introducir claves de idempotencia controladas por el modelo o el cliente.

`create_reminder` validará estrictamente el objeto JSON, sus propiedades permitidas,
un texto no vacío y acotado, y una fecha UTC futura. Devolverá al proveedor una
representación JSON del recordatorio creado o recuperado. Los errores de argumentos
mantendrán el código estable `invalid_tool_arguments` y no expondrán detalles internos
al cliente HTTP.

La confirmación pendiente conservará el identificador de operación junto a la llamada
exacta. Tras aprobarla, el orquestador pasará ese identificador a la herramienta. Las
herramientas de solo lectura seguirán ejecutándose sin un identificador de operación;
el contexto hará explícita esa ausencia. La confirmación de un solo uso existente no
se convierte en una garantía durable: este incremento cubre únicamente el proceso
local actual y los reintentos de la misma operación dentro de él.

El fake añadirá el escenario `reminder`: solicitará `create_reminder`, y después de
la aprobación responderá usando el resultado estructurado. No se añadirá un endpoint
de recordatorios ni un modo de listar, editar, borrar, programar o notificar.

## Flujo

```text
cliente -> mensaje con escenario reminder
orquestador -> valida política y retiene llamada + operation id
orquestador -> 202 con confirmación
cliente -> aprueba confirmación
orquestador -> create_reminder(contexto con operation id)
almacén -> obtiene o crea recordatorio atómicamente
orquestador -> auditoría de metadatos y continuación del proveedor
cliente <- respuesta final y traza
```

## Seguridad y errores

- Un cliente anónimo o sin `reminders.write` no llega a crear recordatorios.
- Solo el principal que originó la conversación puede resolver la confirmación.
- El identificador de operación, texto y fecha no se añadirán a la auditoría actual;
  se conservarán únicamente sus metadatos ya permitidos.
- Una confirmación rechazada, expirada o de otro principal no crea recordatorios.
- La idempotencia no cubre reinicios, múltiples procesos ni un futuro proveedor de
  notificaciones. Esos límites se documentarán de forma explícita.

## Pruebas y documentación

Las pruebas unitarias cubrirán validación, clave repetida, claves distintas, aislamiento
por principal y creación concurrente. Las pruebas del orquestador comprobarán que la
confirmación entrega una única operación al almacén y que rechazo, expiración o acceso
ajeno no producen efectos. Una prueba HTTP recorrerá el escenario fake completo con
una identidad autorizada.

Se actualizarán `docs/api/openapi.yaml`, `README.md`, arquitectura, seguridad y
roadmap para describir la herramienta, scope, confirmación, límite en memoria e
idempotencia local. No se creará un ADR: aplica las decisiones existentes de los ADR
0012 y 0021 sin introducir una decisión arquitectónica nueva.

## Criterios de aceptación

- Una llamada autorizada solicita confirmación antes de crear el recordatorio.
- Dos ejecuciones de la misma operación crean exactamente un recordatorio y devuelven
  el mismo resultado.
- Dos operaciones distintas pueden crear recordatorios con los mismos datos.
- Rechazar, expirar o resolver desde otro principal no cambia el almacén.
- La API y la documentación no presentan el almacén en memoria como agenda durable.
