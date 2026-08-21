# ADR 0022: Vincular conversaciones autenticadas a su principal

- Estado: Aceptada
- Fecha: 2026-08-21

## Contexto

La API podía recuperar y ampliar una conversación al recibir su `ConversationId`, sin
conservar quién la había iniciado. La API key local ya aporta un `PrincipalId`
verificable, pero no justifica aún un sistema de cuentas, sesiones, roles ni una base
de datos. Persistir conversaciones privadas sin esta frontera mezclaría identidad y
el identificador técnico de la conversación.

## Decisión

El almacén de conversaciones conserva en memoria el `OwnerPrincipalId` con el que se
creó una conversación autenticada. El orquestador comprueba ese propietario antes de
leer historial, añadir un turno o resolver una confirmación. Una discrepancia se
presenta como `conversation_not_found`, igual que una conversación inexistente.

Una conversación iniciada sin principal permanece sin propietario. Sigue disponible
para el flujo público actual, pero se clasifica como efímera y no podrá promoverse ni
persistirse automáticamente como conversación privada.

## Consecuencias

- La API key educativa protege ya las conversaciones que cree, aunque solo configure
  un principal y no sustituya identidad multiusuario.
- El modelo y el cliente no pueden asignar ni cambiar el propietario.
- La propiedad no sobrevive un reinicio ni resuelve retención, borrado, backup,
  revocación o autorización compartida; esas decisiones acompañarán la persistencia.
- El contrato HTTP añade `404` para una conversación inaccesible y evita revelar su
  existencia a un principal distinto.
