# Estándares OpenAPI

## Fuente de verdad

La especificación estática de la API se encuentra en `docs/api/openapi.yaml` y usa
OpenAPI 3.0. Debe describir únicamente endpoints y modelos implementados.

## Cambios que obligan a actualizarla

- Añadir, quitar o renombrar endpoints.
- Modificar métodos, parámetros, headers o request bodies.
- Cambiar campos, tipos, nulabilidad o formatos de DTOs.
- Cambiar códigos HTTP, errores o requisitos de seguridad.

El mismo cambio debe incluir tests HTTP que demuestren el contrato relevante.

## Contenido mínimo

- `info` con título, versión, descripción y licencia.
- Servidores solo para entornos realmente definidos.
- Descripción y `operationId` estable por operación.
- Request y responses con todos los códigos implementados.
- Esquemas reutilizables bajo `components/schemas`.
- `securitySchemes` únicamente cuando exista autenticación.
- Ejemplos cuando aclaren comportamiento no obvio.

## Convenciones

- Paths orientados a recursos y métodos HTTP que expresen intención.
- DTOs separados de tipos internos del dominio.
- Descripciones de la especificación en inglés para consumidores técnicos.
- No inventar entornos, autenticación, endpoints ni campos futuros.
- Validar sintaxis y coherencia antes de completar un cambio de contrato.
