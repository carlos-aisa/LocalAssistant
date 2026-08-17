# ADR 0001: Núcleo modular antes que microservicios

- Estado: Aceptada
- Fecha: 2026-08-17

## Contexto

El sistema podría necesitar procesos separados para inferencia, voz, automatización
y trabajos largos. Separarlos ahora multiplicaría despliegues y contratos sin una
carga o fallo real que justificase cada frontera.

## Decisión

Mantener un núcleo modular en `LocalAssistant.Core` y una API ejecutable. Añadir procesos
especializados únicamente cuando posean una responsabilidad ejecutable propia. No
crear `LocalAssistant.Worker` hasta disponer de un trabajo de fondo real.

## Consecuencias

La primera versión es fácil de ejecutar y depurar. Los namespaces y contratos
marcan límites que pueden extraerse. A cambio, aislamiento de fallos y escalado
independiente quedan pospuestos.
