# Roadmap

El orden es orientativo. Cada fase debe aportar un flujo comprobable antes de
introducir infraestructura adicional.

## Fase 1 — Protocolo mínimo (actual)

- API de conversación.
- Proveedor fake secuencial.
- Bucle explícito de herramientas.
- Herramienta de hora, políticas iniciales, logging y tests.
- Conversaciones en memoria.

## Fase 2 — Primer modelo local

- Adaptador de Ollama detrás de `ILanguageProvider`.
- Detección de capacidades reales de tool calling por modelo.
- Pruebas de contrato reutilizables entre fake y proveedores.
- Límites de contexto y cancelación de inferencia.

## Fase 3 — Persistencia y recuperación

- Elegir almacenamiento tras medir el patrón de acceso.
- Persistir conversaciones y trazas con retención configurable.
- Ingesta pequeña de documentación y RAG evaluable.
- Protección frente a prompt injection procedente de documentos.

## Fase 4 — Automatización doméstica

- Integración de solo lectura con Home Assistant.
- Acciones con cambio de estado y confirmación verificable.
- MQTT solo cuando existan eventos que lo requieran.
- Auditoría y separación de credenciales por conector.

## Fase 5 — Voz

- Reconocimiento y síntesis intercambiables.
- Wake word configurable y procesamiento local cuando sea viable.
- Manejo de interrupciones y estados de escucha visibles.

## Fase 6 — Enrutamiento híbrido

- Políticas de privacidad, dificultad, coste y disponibilidad.
- Proveedores externos opcionales y desactivados por defecto.
- Evaluaciones para justificar cada decisión de routing.

## Fase 7 — Herramientas técnicas

- Conectores API y MCP con allowlists y permisos mínimos.
- Asistente de programación local limitado a workspaces aprobados.
- Planificación, vista previa y confirmación antes de cambios.

## Decisiones pospuestas

- `LocalAssistant.Worker`: aparecerá con la primera tarea larga que necesite sobrevivir a
  una petición HTTP.
- Broker de mensajes: solo si API y worker necesitan desacoplamiento duradero.
- Base vectorial: se elegirá con un corpus y métricas reales.
- Open WebUI: posible canal externo, no parte del núcleo.
- OpenTelemetry: se añadirá con un backend o necesidad de diagnóstico concreta.
- Dependabot: se reconsiderará al estabilizar la publicación y política de updates.
