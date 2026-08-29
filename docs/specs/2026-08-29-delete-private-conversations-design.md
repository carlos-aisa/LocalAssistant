# Diseño: borrado selectivo de conversaciones privadas

## Objetivo

Permitir que el propietario autenticado elimine de forma explícita una conversación
privada persistida, incluidos todos sus mensajes. La operación completa el ciclo de
vida definido en ADR 0025 sin convertir el borrado en una herramienta del modelo ni
abrir un mecanismo genérico de destrucción remota.

## Contrato HTTP

La API añadirá `DELETE /api/conversations/{conversationId}`.

La solicitud debe incluir la cabecera única y exacta:

```text
X-LocalAssistant-Confirm-Delete: true
```

La ruta no recibe cuerpo. Solo se permite a un principal autenticado mediante una API
key válida; el propietario se obtiene exclusivamente del claim
`ClaimTypes.NameIdentifier`. No se añadirá un scope nuevo: la autenticación del
propietario y la confirmación explícita son los requisitos del primer vertical slice,
sin transformar `installation.owner` en un permiso comodín ni conceder capacidades
futuras.

Las respuestas serán:

- `204 No Content` si se eliminan la conversación y todos sus mensajes del propietario.
- `400 Bad Request` si la cabecera falta, se repite o su valor no es exactamente
  `true`.
- `401 Unauthorized` si el cliente no está autenticado.
- `404 Not Found` si la conversación no existe, es anónima, ha caducado o pertenece a
  otro principal. La respuesta no distingue estos casos.
- `503 Service Unavailable` si la persistencia privada está desactivada. No se abrirá
  ni creará el archivo SQLite en este caso.

## Flujo y concurrencia

El endpoint validará primero que la persistencia esté activada, después la identidad y
la cabecera de confirmación. Adquirirá el bloqueo por `ConversationId` usado por el
orquestador antes de modificar el estado. De este modo, un turno activo y un borrado
no pueden entrelazarse dentro del proceso.

Bajo ese bloqueo, el endpoint borrará transaccionalmente los mensajes y los metadatos
de la conversación solo cuando el identificador y el propietario coincidan. También
invalidará cualquier confirmación de herramienta pendiente para esa conversación. No
quedará una confirmación reutilizable si posteriormente se crea otra conversación con
el mismo identificador.

La coordinación entre procesos permanece fuera de alcance, igual que el bloqueo de
turnos actual.

## Límites

- Solo se eliminan conversaciones autenticadas y persistidas; las conversaciones
  anónimas continúan siendo efímeras y no se pueden borrar por esta ruta.
- La operación no borra auditoría, notas personales, documentos, recordatorios,
  backups ni otros recursos.
- No se registra el contenido de la conversación, la API key ni la cabecera de
  confirmación.
- No se añade una herramienta, un endpoint masivo, restauración, papelera ni
  administración de permisos.

## Cambios de diseño interno

El contrato de conversación incorporará la operación de borrado condicionado por
propietario. Sus implementaciones en SQLite, memoria y el adaptador compuesto
mantendrán la separación entre persistencia autenticada y conversación efímera. El
almacén de confirmaciones incorporará una operación explícita para retirar una
confirmación pendiente sin ejecutarla.

El endpoint dependerá de esos contratos, del bloqueo por conversación y de la opción
de persistencia; no dependerá directamente de sentencias SQL ni de detalles del
adaptador SQLite.

## Pruebas y documentación

Las pruebas cubrirán la eliminación completa del propietario, la cabecera obligatoria,
la autenticación, el aislamiento frente a otro propietario, los identificadores no
encontrados, las conversaciones anónimas, la persistencia desactivada y la retirada de
una confirmación pendiente. Se actualizarán OpenAPI, README, arquitectura, seguridad,
roadmap y ADR 0025 para reflejar el contrato y sus límites.

## Criterios de aceptación

- El propietario autenticado que aporta la cabecera exacta puede eliminar una de sus
  conversaciones persistidas y todos sus mensajes.
- Ningún otro propietario, cliente anónimo ni conversación efímera obtiene acceso a
  ese borrado.
- La operación no revela existencia ni propiedad mediante diferencias entre respuestas
  `404`.
- Una confirmación pendiente queda invalidada junto con la conversación eliminada.
- La persistencia desactivada no crea el archivo SQLite.
