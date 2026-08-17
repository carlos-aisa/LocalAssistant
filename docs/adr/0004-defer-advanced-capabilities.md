# ADR 0004: Posponer persistencia y capacidades avanzadas

- Estado: Aceptada
- Fecha: 2026-08-17

## Contexto

Persistencia, RAG, voz, Home Assistant, MQTT y servicios externos tienen decisiones
de datos, seguridad y operación que no pueden validarse con el primer flujo.

## Decisión

Usar memoria en proceso y excluir esas capacidades del código actual. Mantenerlas
en el roadmap hasta poder construir un incremento vertical y medible para cada una.

## Consecuencias

La versión actual pierde datos al reiniciar y no es un asistente doméstico. A cambio,
su protocolo central puede entenderse y probarse sin infraestructura ni hardware
especial.
