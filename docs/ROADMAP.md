# Roadmap

El orden expresa dependencias técnicas, no fechas. Cada fase debe aportar un flujo
vertical comprobable antes de introducir infraestructura adicional. En particular,
la voz se valida primero en un único dispositivo; solo después se distribuye por
habitaciones.

## Fase 1 — Protocolo mínimo (actual)

- API de conversación.
- Proveedor fake secuencial.
- Bucle explícito de herramientas.
- Herramienta de hora, políticas iniciales, logging y tests.
- Conversaciones en memoria.

## Fase 2 — Primer modelo local (en curso)

- [x] Adaptador HTTP de Ollama detrás de `ILanguageProvider`.
- [x] Selección y configuración explícitas desde la API, desactivado por defecto.
- [x] Pruebas deterministas del contrato HTTP sin red ni GPU.
- [x] Smoke test de respuesta directa y tool calling con Ollama `0.32.14` y
  `qwen3:1.7b` sobre CPU.
- [x] Validación previa y cacheada de que el modelo existe y declara `tools`.
- Evaluación de la calidad real de tool calling por modelo.
- Pruebas de contrato reutilizables entre fake y proveedores.
- Límites de contexto y cancelación de inferencia.

## Fase 3 — Herramientas y permisos

- Ampliar el catálogo con una segunda herramienta de solo lectura útil.
- Confirmación vinculada a una solicitud concreta, no aprobación global por nombre.
- Identidad y autorización mínimas antes de cualquier herramienta que cambie estado.
- Auditoría, idempotencia y políticas de exposición de resultados.

## Fase 4 — Persistencia y memoria de conversación

- Elegir almacenamiento tras medir el patrón de acceso.
- Persistir conversaciones y trazas con retención configurable.
- Resolver concurrencia de turnos sobre una misma conversación.
- Ingesta pequeña de documentación y RAG evaluable.
- Protección frente a prompt injection procedente de documentos.

## Fase 5 — Home Assistant

- Integración inicial de solo lectura.
- Registro explícito de entidades y capacidades permitidas.
- Acciones con cambio de estado únicamente tras disponer de confirmación verificable.
- MQTT solo cuando existan eventos o presencia que justifiquen desacoplamiento.
- Auditoría y separación de credenciales por conector.

## Fase 6 — Pipeline de voz en un único dispositivo

- Speech-to-text y text-to-speech intercambiables.
- Wake word configurable y local cuando sea razonable.
- Streaming o captura acotada con detección de inicio y final de turno.
- Estados visibles: inactivo, escuchando, capturando, procesando, respondiendo y error.
- Introducir el contexto mínimo de dispositivo o canal de origen con uso y tests
  reales, manteniendo sencilla la API HTTP.
- Indicador físico de captura y desactivación física del micrófono.

## Fase 7 — Primer satélite de habitación

- Elegir una plataforma solo después de un prototipo comparativo: Home Assistant
  Assist, ESP32-S3, Raspberry Pi, Android u ordenador siguen siendo candidatos.
- Registrar un satélite y asociarlo a una habitación.
- Describir capacidades de entrada, salida, pantalla, botones, indicadores y wake
  word local.
- Autenticar el dispositivo y cifrar audio y control dentro de la red doméstica.
- Monitorizar conexión, versión, estado y errores.
- Mantener continuidad de conversación dentro de la habitación.

## Fase 8 — Nest Hub como salida de habitación

- Registrar cada Google Nest Hub exclusivamente como dispositivo de salida.
- Reproducir respuestas TTS mediante Google Cast.
- Mostrar paneles de Home Assistant, avisos o contexto adecuado para una pantalla
  compartida.
- Seleccionar el Nest Hub de la habitación que originó el turno.
- No asumir acceso al micrófono, audio, wake word ni sustitución de Google Assistant.

## Fase 9 — Varios satélites y routing de habitación

- Registrar varios dispositivos y habitaciones.
- Seleccionar automáticamente una salida compatible y disponible.
- Aplicar fallback si el destino preferido está desconectado o silenciado.
- Separar identidad de conversación, habitación, dispositivo de entrada y dispositivo
  de salida.
- Diagnóstico y actualizaciones controladas de satélites.

## Fase 10 — Conversación de voz natural

- Cancelación de eco y prevención de que LocalAssistant escuche su propio TTS.
- Interrupción de una respuesta por parte del usuario (barge-in).
- Detección robusta de final de turno y conversación continua.
- Recuperación ante desconexión, error o cambio de dispositivo de salida.

## Fase 11 — Transferencia entre habitaciones

- Transferencia explícita y opcional de una conversación activa.
- Política para elegir la nueva entrada y salida sin mezclar sesiones de usuarios.
- Privacidad ante presencia múltiple y pantallas compartidas.
- Auditoría y cancelación de transferencias erróneas.

## Líneas posteriores o paralelas

### Enrutamiento híbrido

- Políticas de privacidad, dificultad, coste, latencia y disponibilidad.
- Proveedores externos opcionales y desactivados por defecto.
- Evaluaciones para justificar cada decisión de routing.

Puede avanzar después del modelo local y la persistencia; no bloquea el primer
satélite, cuyo procesamiento podrá ser completamente local.

### Herramientas técnicas

- Conectores API y MCP con allowlists y permisos mínimos.
- Asistente de programación local limitado a workspaces aprobados.
- Planificación, vista previa y confirmación antes de cambios.

## Decisiones pospuestas

- `LocalAssistant.Worker`: aparecerá con la primera tarea larga que necesite
  sobrevivir a una petición HTTP.
- Broker de mensajes: solo si API, worker o satélites necesitan desacoplamiento
  duradero; el streaming de audio no lo justifica automáticamente.
- Base vectorial: se elegirá con un corpus y métricas reales.
- Hardware de satélite: se decidirá después del pipeline de voz y un prototipo.
- Protocolo de satélite: no se elige aún entre APIs, WebSocket, Home Assistant,
  MQTT u otra opción.
- Open WebUI: posible canal externo, no parte del núcleo.
- OpenTelemetry: se añadirá con un backend o necesidad de diagnóstico concreta.
- Dependabot: se reconsiderará al estabilizar la política de actualizaciones.
