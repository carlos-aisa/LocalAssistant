# ADR 0038: Admitir herramientas externas solo mediante un wrapper compuesto del gateway

## Contexto

Los ADR 0013 y ADR 0014 definen una política de egreso y una allowlist del gateway,
pero una herramienta con exposición `ControlledExternal` aún necesita una garantía
estructural de que no puede evitarlo.

## Decisión

Solo un `GatewayBackedTool` sellado, creado por composición, puede admitirse como
herramienta externa. Envuelve una operación que no es `ITool` y no puede acceder a
`IExternalToolsGateway`. Identidad y scopes siguen siendo entradas de política; la
admisión estructural se evalúa separadamente al filtrar catálogo e inmediatamente antes
de ejecutar.

## Consecuencias

Las herramientas `ControlledExternal` convencionales permanecen denegadas. Ningún
booleano de metadatos puede afirmar uso del gateway, y ninguna herramienta externa se
reclasifica como local. El primer adaptador de producción queda aplazado a la fase 6.2.
