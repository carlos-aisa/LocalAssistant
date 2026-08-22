# Diseño de concurrencia de conversaciones

## Objetivo

Completar el requisito de concurrencia de la fase 4 validando la serialización por
conversación dentro del proceso ya existente y documentando su límite.

## Alcance

`ConversationOrchestrator` ya obtiene un `IConversationExecutionLock` antes de
acceder a los metadatos o al historial. Este incremento no modifica ese
comportamiento de producción ni el contrato HTTP.

Se añadirán dos pruebas deterministas del orquestador:

1. Dos turnos de la misma conversación se serializan. La segunda llamada al
   proveedor no puede iniciarse hasta que el primer turno libera su respuesta y
   recibe el historial escrito por el primero.
2. Un segundo turno que espera el bloqueo puede cancelarse. No llama a su proveedor
   ni añade su mensaje de usuario a la conversación.

Las pruebas usan sincronización de tareas en lugar de aserciones sobre tiempo
transcurrido, por lo que son deterministas y no dependen del reloj real.

## Fuera de alcance

- Bloqueo durable, entre procesos o distribuido.
- Cola, reintentos o un nuevo esquema de persistencia.
- Cambios en autorización, propiedad, selección de proveedor o respuestas de API.

## Documentación

El roadmap marcará como completada la concurrencia de turnos de la fase 4. El README
indicará que la serialización se limita a un proceso de aplicación, por lo que un
despliegue con varios procesos requerirá un mecanismo futuro de coordinación.

## Criterios de aceptación

- La compilación Release finaliza sin advertencias.
- Pasa la suite determinista completa.
- Las dos conductas de concurrencia descritas están cubiertas por pruebas.
- La documentación coincide con el límite dentro de proceso.
