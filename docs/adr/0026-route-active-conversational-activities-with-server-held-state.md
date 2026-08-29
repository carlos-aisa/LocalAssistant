# ADR 0026: Enrutar actividades conversacionales activas con estado retenido por el servidor

- Estado: Aceptada
- Fecha: 2026-08-29

## Contexto

Una conversación identifica un canal lógico de mensajes e historial, pero no expresa
por sí sola qué trabajo conversacional está en curso. Capacidades como el
`Conversational English Coach` necesitan conservar un objetivo y una política durante
varios turnos, sin que cada mensaje obligue a un modelo general a decidir de nuevo qué
capacidad lo atenderá.

El [ADR 0010](0010-separate-live-conversation-from-language-evaluation.md) separa el
camino conversacional de baja latencia de la evaluación pedagógica diferida. No decide
cómo se identifica una actividad activa, cómo se enruta un turno hacia ella ni quién
valida su ciclo de vida. Esas responsabilidades afectan igualmente a identidad,
autorización, persistencia, módulos, proveedores, concurrencia y recuperación.

## Decisión

El núcleo tratará una actividad conversacional como una entidad conceptual distinta de
la conversación. Tendrá identidad, tipo, propietario, conversación asociada, objetivo,
configuración, fechas, contexto mínimo, política de retención y resultado propios.
La conversación seguirá siendo el canal de mensajes; un principal seguirá resolviéndose
y autorizándose fuera de `ConversationId`.

Para cada mensaje, una frontera común autenticará y autorizará al principal, resolverá
la conversación autorizada y su actividad activa, aplicará controles universales y
elegirá el handler y perfil de proveedor. Si hay una actividad de inglés activa, el
turno se dirigirá a su handler sin una predecisión de un LLM general. La activación
podrá ser explícita desde la interfaz o una propuesta estructurada desde conversación
general, pero el servidor validará tipo, propietario, configuración y concurrencia
antes de crearla.

El estado y sus transiciones pertenecen al servidor. Se contemplan `Requested`,
`Starting`, `Active`, `Ending`, `Completed`, `Suspended`, `Cancelled`, `Expired` y
`Failed`; las transiciones repetibles serán idempotentes y seguras ante concurrencia.
El cierre será una intención explícita, aunque un handler pueda solicitarlo de forma
estructurada. Una expresión ambigua no cerrará la actividad. Cerrar una actividad no
elimina la conversación ni impide iniciar otra más adelante.

Una herramienta continúa siendo una operación acotada solicitada por un modelo. Una
actividad completa no se modelará como una única herramienta. El modelo puede proponer
acciones, pero no decidirá por sí solo enrutamiento, propietario, transición ni
autorización.

La responsabilidad lógica de una actividad será independiente de qué modelo esté
instalado, residente o se ejecute simultáneamente. Los perfiles y políticas elegirán
proveedores autorizados según privacidad, herramientas, latencia, calidad, hardware,
memoria, coste, concurrencia y categorías de contenido. Esta decisión no selecciona
modelos, instancias, GPU, offload, planificador, endpoints, tablas, clases ni workers.

## Consecuencias

- Una conversación podrá contener varias actividades a lo largo del tiempo y
  continuar antes, durante y después de ellas, por texto o voz.
- Controles como cancelar, suspender, reanudar y terminar seguirán disponibles aunque
  una capacidad esté activa; actividades de emergencia o administrativas podrán tener
  prioridad mediante autorización de servidor.
- Suspensión, reinicio, inactividad, fallo de proveedor, informe tardío y actividades
  incompatibles exigirán una política explícita de recuperación, retención y límites.
- El usuario podrá continuar conversando mientras el análisis o informe pedagógico se
  completa después; una inferencia de perfil no se confirmará automáticamente.
- La primera implementación podrá usar el despliegue y los modelos existentes. Un
  sistema de trabajos duradero solo será necesario cuando deba sobrevivir a una
  petición, reinicio o duración de sesión.
- La implementación concreta deberá respetar las decisiones de propiedad de
  conversaciones, autorización previa a recuperar contexto y ciclo de vida de datos
  privados recogidas en los ADR 0019, 0022, 0024 y 0025.
