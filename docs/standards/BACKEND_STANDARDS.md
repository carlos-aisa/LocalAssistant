# Estándares backend

## Alcance

Estas reglas se aplican a `LocalAssistant.Core`, `LocalAssistant.Api` y futuros
adaptadores o workers. Los ADR tienen prioridad sobre recomendaciones genéricas de
arquitectura.

## Plataforma y estructura

- Usar una versión LTS de .NET disponible y fijar la selección con `global.json`.
- Mantener pocos proyectos con responsabilidades ejecutables o límites reales.
- Organizar el núcleo mediante namespaces y módulos cohesivos antes de extraer
  ensamblados o procesos.
- Usar `Microsoft.Extensions.DependencyInjection`, configuración tipada y
  `ILogger<T>` en los límites que lo necesiten.
- No hacer obligatorio un ORM, una base de datos o una arquitectura de cuatro
  capas antes de que exista persistencia real.

## Código C#

- PascalCase para tipos, propiedades y métodos; camelCase para variables y
  parámetros; prefijo `I` para interfaces.
- Los métodos asíncronos terminan en `Async` y propagan `CancellationToken`.
- No utilizar `.Result` ni `.Wait()`.
- Preferir código explícito y legible; evitar valores mágicos y abstracciones sin
  consumidores.
- Nullable, analizadores, warnings-as-errors y `.editorconfig` deben permanecer
  activos.
- Los contratos públicos deben ser pequeños, inmutables cuando sea práctico e
  independientes de SDKs de proveedores.

## Límites y errores

- Validar HTTP, configuración, respuestas del proveedor y argumentos de
  herramientas en sus fronteras.
- No exponer excepciones internas al cliente.
- Usar errores estructurados y estables para fallos esperados.
- Un `catch` debe traducir el error, añadir contexto seguro o relanzarlo; nunca
  ocultarlo silenciosamente.
- Aplicar timeouts y límites de iteración alrededor de trabajo externo o repetido.

## Herramientas de IA

- `IToolRegistry` es una allowlist; el modelo no decide qué código existe.
- Validar nombre, argumentos, impacto y confirmación antes de ejecutar.
- No añadir herramientas de shell, código generado, reflexión arbitraria o acceso
  general al sistema.
- Las acciones que cambian estado deben diseñarse con confirmación, idempotencia y
  auditoría antes de conectarse a sistemas reales.

## Logging y configuración

- Usar logging estructurado con identificadores, nombres de operación, resultado y
  duración.
- No registrar prompts, argumentos, resultados, tokens ni datos personales por
  defecto.
- Mantener secretos fuera del código y de `appsettings.json`; usar variables de
  entorno o almacenes seguros cuando aparezcan credenciales.
- Los singletons deben ser thread-safe; los servicios con estado por petición deben
  ser scoped.

## Calidad

- Todo cambio de comportamiento requiere tests.
- Restaurar, formatear, compilar Release y ejecutar tests antes de completar.
- No refactorizar código no relacionado ni introducir dependencias sin necesidad
  demostrable.
