# Plan de implementación: respuestas temporales autoritativas

## Objetivo

Impedir que una petición de fecha u hora actual dependa de que el proveedor LLM
solicite voluntariamente `get_current_time`. El servidor resolverá el dato con
`TimeProvider` antes de llamar al proveedor y lo entregará como contexto transitorio.

## Alcance y decisiones

- Añadir una política determinista pequeña para detectar peticiones de hora o fecha
  actual en español e inglés, incluidas consultas compuestas.
- Resolver UTC siempre con `TimeProvider`; usar la zona IANA del hogar solo cuando
  el principal tenga `household.profile.read`. Sin zona autorizada, exponer solo UTC
  y el motivo de limitación.
- Añadir una traza de capacidad autoritativa con un nombre estable distinto de una
  llamada propuesta por el modelo. La resolución no ejecuta una herramienta del LLM
  ni modifica estado.
- Mantener `get_current_time` como herramienta opcional para compatibilidad, pero
  no depender de ella para la garantía.
- Añadir instrucciones de sistema que prohíban inventar acceso a Internet, datos de
  entrenamiento dinámicos o ejecuciones inexistentes.

## Pasos

1. Crear contratos de resolución temporal y la política de detección en
   `LocalAssistant.Core`. Validar expresiones positivas y negativas sin NLU general.
2. Adaptar `CurrentTimeTool` o un resolvedor compartido para producir UTC, hora
   local, identificador de zona y offset mediante `TimeProvider` y `TimeZoneInfo`.
   Traducir zona inválida a error controlado.
3. Integrar la resolución previa en `ConversationOrchestrator`, antes de construir
   la solicitud al proveedor. Añadir el contexto de sistema estructurado, no
   persistirlo en la conversación y registrar una traza con duración.
4. Actualizar pruebas unitarias del orquestador y de hora: proveedor que responde
   directamente, consultas compuestas, verano e invierno, ausencia/denegación de
   zona y negativos. Usar `ManualTimeProvider` y dobles locales.
5. Actualizar el evaluador de Ollama, documentación de arquitectura/seguridad y,
   solo si cambia el DTO HTTP observable, OpenAPI.

## No objetivos

No se añade acceso a Internet, NLU genérico, conversión libre de zonas, extracción
automática de ubicación, reconocimiento de voz ni una segunda llamada al LLM.

## Verificación

`dotnet format LocalAssistant.sln --verify-no-changes --no-restore`, build Release,
tests de orquestación y herramientas, suite completa, `git diff --check` y revisión
estructural de SQL, autorización, contexto LLM y trazabilidad.
