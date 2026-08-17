# ADR 0003: Bucle de herramientas explícito

- Estado: Aceptada
- Fecha: 2026-08-17

## Contexto

Bibliotecas de IA pueden ocultar la ejecución repetida entre modelo y funciones.
El proyecto busca aprender ese protocolo y controlar seguridad, errores y trazas.

## Decisión

El orquestador implementa directamente: llamada al proveedor, inspección de tool
calls, resolución contra el registro, confirmación, ejecución, incorporación del
resultado y nueva llamada. Se aplican cancelación, timeout y máximo de iteraciones.

## Consecuencias

El flujo es inspeccionable y testeable. Mantenemos más código propio y tendremos
que adaptar diferencias entre proveedores. Un middleware automático podrá
reconsiderarse cuando el comportamiento esté comprendido y cubierto por contratos.
