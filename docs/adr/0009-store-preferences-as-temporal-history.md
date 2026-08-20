# ADR 0009: Conservar las preferencias como historial temporal

- Estado: Aceptada
- Fecha: 2026-08-20

## Contexto

Las preferencias domésticas no tienen toda la misma vigencia. Una alergia debe
mantener prioridad hasta revisión explícita, mientras una valoración, el cansancio
de un plato o una preferencia estacional pueden cambiar con fecha, receta,
preparación y contexto. Guardar únicamente el último valor perdería procedencia,
tendencia y capacidad de explicar una recomendación.

## Decisión

Las observaciones variables se conservarán como historial temporal en lugar de
sobrescribir el valor anterior. Cada una mantendrá conceptualmente sujeto, objeto,
valor, fecha, contexto, comentario, fuente, confianza, carácter explícito o inferido
y posible duración.

Restricciones estables y prioritarias, observaciones temporales y reglas puntuales
se distinguirán semánticamente. La preferencia actual podrá calcularse mediante
reglas inspeccionables de recencia, frecuencia, tendencia y contexto. Una tendencia
inferida podrá proponer una actualización, pero no convertirla silenciosamente en
un hecho confirmado.

No se fija todavía esquema de persistencia, algoritmo de ponderación ni modelo de
aprendizaje.

## Consecuencias

- Será posible explicar por qué se propone o evita un plato y cómo cambió una
  preferencia.
- Una valoración nueva no borrará evidencia histórica ni degradará una alergia por
  antigüedad.
- Correcciones y migraciones deberán conservar fuente, vigencia y autoría.
- El almacenamiento y las consultas serán más ricos que una tabla de último valor.
- El MVP podrá usar reglas deterministas y dejar modelos estadísticos u opacos para
  evaluaciones posteriores.
