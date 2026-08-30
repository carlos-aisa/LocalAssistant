# ADR 0030: Indexar tras el cierre explícito de una conversación

- Estado: Aceptada
- Fecha: 2026-08-30

## Decisión

`POST /api/conversations/{conversationId}/completion` marca una conversación
autenticada y propia para indexado en segundo plano. La operación es idempotente y
no espera al embedding ni al resumen. La inactividad configurable sigue siendo el
fallback ante cierres inesperados.

## Consecuencias

El cliente de terminal llama a esta operación antes de `/exit`. El documento de
búsqueda conserva su embedding y resumen previos mientras el nuevo texto queda
pendiente, evitando una ventana sin índice semántico utilizable.
