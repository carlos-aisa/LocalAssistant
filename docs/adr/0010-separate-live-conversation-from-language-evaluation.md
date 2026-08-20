# ADR 0010: Separar conversación en vivo y evaluación de idioma

- Estado: Aceptada
- Fecha: 2026-08-20

## Contexto

El tutor de inglés debe mantener una conversación natural y, al mismo tiempo,
clasificar errores, detectar tendencias, generar un informe y actualizar un perfil.
Realizar todo el análisis pedagógico antes de responder aumentaría la latencia y
provocaría interrupciones contrarias al objetivo de fluidez.

Algunas correcciones sí pueden ser urgentes según la política elegida, pero la
mayoría de mejoras de estilo, agregaciones y ejercicios pueden calcularse después
del turno o al final de la sesión.

## Decisión

Separar el camino conversacional de baja latencia del evaluador pedagógico, el
generador de informes y la actualización del perfil. El camino en vivo mantendrá el
role-play y aplicará solo las correcciones que la política de sesión declare
inmediatas. Los demás análisis producirán observaciones asociadas a su evidencia y
podrán completarse de forma diferida.

La separación comenzará como responsabilidad lógica dentro del despliegue existente.
Solo se introducirá un sistema de trabajos o worker cuando el análisis deba sobrevivir
a una petición, reinicio o sesión larga. No se seleccionan modelos ni infraestructura.

## Consecuencias

- La latencia conversacional podrá medirse y optimizarse sin esperar al informe.
- Una política de corrección seguirá pudiendo interrumpir ante errores críticos.
- Evaluación, informe y perfil necesitarán correlación, idempotencia y tratamiento de
  resultados tardíos o cancelados.
- El usuario podrá continuar hablando mientras se procesa análisis no urgente.
- La eventual consistencia del perfil deberá ser visible y no convertirá inferencias
  pendientes en hechos confirmados.
- La arquitectura será algo más compleja que una única llamada síncrona, pero no
  justifica todavía otro proceso.
