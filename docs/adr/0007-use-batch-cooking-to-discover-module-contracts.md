# ADR 0007: Usar BatchCooking para descubrir el contrato de módulos

- Estado: Aceptada
- Fecha: 2026-08-20

## Contexto

Jarvis necesitará módulos funcionales sin incorporar cada dominio al núcleo. Diseñar
un SDK general antes de disponer de un módulo real obligaría a anticipar manifiesto,
ciclo de vida, permisos, persistencia, eventos, automatizaciones, interfaz y
compatibilidad sin evidencia de uso.

`BatchCooking` reúne conversación, estado privado, herramientas, confirmaciones,
planificación, feedback, archivos, automatizaciones y dispositivos. Es suficientemente
completo para revelar necesidades de extensibilidad y puede entregar primero un MVP
escrito sin depender de voz o integraciones externas.

## Decisión

`BatchCooking` será el primer módulo doméstico de referencia y permanecerá separado
del núcleo. Se creará primero una capacidad mínima de módulos, se implementará el
módulo manualmente y se corregirá el contrato con lo aprendido. Solo después se
estabilizará un SDK o modelo general de extensiones.

El núcleo proporcionará capacidades transversales y no conocerá recetas,
ingredientes, platos, inventario ni menús. No se fija todavía formato de manifiesto,
mecanismo de carga, packaging, proceso separado ni API definitiva.

## Consecuencias

- El contrato de extensibilidad crecerá a partir de responsabilidades ejecutables y
  pruebas de contrato reales.
- `BatchCooking` podrá evolucionar sin contaminar el dominio conversacional.
- Algunas decisiones del primer contrato serán deliberadamente revisables hasta que
  un segundo módulo demuestre su generalidad.
- La estabilización del SDK llegará más tarde que el MVP funcional.
- La autoextensión de Jarvis dependerá además de especificación, sandbox, revisión y
  publicación controladas; no forma parte del MVP.
