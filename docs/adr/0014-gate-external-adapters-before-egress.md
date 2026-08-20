# ADR 0014: Interponer una pasarela antes de cualquier adaptador externo

## Contexto

La política de egreso puede decidir sobre descriptores, pero no evita por sí sola
que un componente entregue valores directamente a un proveedor. El primer contrato
externo debe mantener SDKs, destinos, credenciales y red fuera del núcleo
conversacional y demostrar que una denegación impide invocar el adaptador.

## Decisión

Los contratos independientes del proveedor viven en `LocalAssistant.Core` y la
pasarela se implementa en `LocalAssistant.Infrastructure`. Los adaptadores forman
una allowlist y declaran nombre, destino fijo y operaciones permitidas. La petición
contiene campos que asocian un descriptor clasificado con su valor exacto.

La pasarela rechaza adaptadores u operaciones desconocidos y nombres de campo
duplicados, evalúa todos los descriptores y solo construye el payload para el
adaptador cuando la decisión es permitida. No registra valores y convierte una
excepción del adaptador en un error seguro. Los primeros adaptadores son dobles de
prueba y no realizan red.

## Consecuencias

Existe un punto único y probado para aplicar la política antes de un futuro egreso,
sin fijar proveedor ni SDK. El aislamiento técnico frente a un componente que evite
deliberadamente la pasarela no está resuelto: dependerá de la topología, permisos de
red o separación de procesos. El primer proveedor real deberá añadir timeout,
credenciales seguras, límites, tratamiento de contenido no confiable y auditoría
proporcional sin debilitar esta frontera.
