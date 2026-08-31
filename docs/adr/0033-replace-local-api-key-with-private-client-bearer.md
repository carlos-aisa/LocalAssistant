# ADR 0033: Reemplazar la API key local por bearer de cliente privado

## Estado

Aceptada. Sustituye ADR 0016 para la autenticación HTTP general.

## Contexto

ADR 0016 introdujo una API key local como mecanismo educativo incremental. La fase 4
necesita credenciales revocables por cliente, sesiones de vida limitada y separación
entre propietario, cliente y sesión. Mantener la API key como mecanismo alternativo
en el ejecutable permitiría reactivar la frontera obsoleta mediante configuración.

## Decisión

Las interfaces HTTP privadas aceptan únicamente tokens bearer opacos emitidos para
clientes registrados y solo desde loopback. La API no registra ni configura un handler
de API key. El hash histórico de la key puede mantenerse exclusivamente para migrar el
estado de instalación; no autentica ninguna operación HTTP.

Los dobles de autenticación que necesiten pruebas antiguas se alojan y registran solo
en el proyecto de pruebas. El claim de scope pertenece a la autorización común, no a
un mecanismo de autenticación concreto.

## Consecuencias

- Caducidad, rotación y revocación de cliente invalidan bearer sessions.
- Las pruebas de endpoint migran progresivamente a sesiones bearer reales.
- ADR 0016 sigue siendo histórico, pero no describe la frontera HTTP vigente.
