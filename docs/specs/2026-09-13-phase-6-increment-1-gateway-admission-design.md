# Diseño: fase 6, incremento 1 — admisión real del gateway externo

## Objetivo

Conectar en la composición de producción la política de egreso y el gateway ya
existentes, sin publicar todavía una herramienta externa ni un adaptador HTTP real.
El incremento prueba que una herramienta externa solo puede ser visible y ejecutarse
si atraviesa estructuralmente `IExternalToolsGateway`.

El producto continúa siendo de uso doméstico y no comercial. Open-Meteo no forma parte
de este incremento; llegará con geocodificación en el incremento 6.2.

## Límites

No se añaden endpoints HTTP, proveedores externos, URLs configurables por modelo,
coordenadas, caché, scopes meteorológicos, credenciales de terceros ni una herramienta
artificial al catálogo de producción. No se habilita globalmente
`ToolExposure.ControlledExternal` ni se reclasifica como `Local` una herramienta
externa.

## Admisión estructural

Se introduce en el núcleo un `GatewayBackedTool` sellado y creado exclusivamente por
composición. Este wrapper implementa `ITool`, conserva su `ToolDefinition` y envuelve
una operación externa que no implementa `ITool` ni recibe acceso al gateway. La operación
recibe argumentos no confiables y debe validarlos en tiempo de ejecución antes de
construir una `ExternalToolRequest` de adaptador, operación, propósito y campos
clasificados fijos: el schema no es una garantía. Si falla, devuelve
`invalid_tool_arguments` sin invocar al gateway; el wrapper también traduce
`ArgumentException` y `JsonException` de esa validación al mismo resultado. El wrapper es el único componente que
delegará en `IExternalToolsGateway` y traducirá su resultado a `ToolExecutionResult`
seguro. Su constructor rechaza una operación cuya exposición declarada no sea
`ControlledExternal`.

La marca de que una tool atraviesa el gateway se deriva de su tipo, no de un booleano
de metadatos. Para cada instancia actual del registro, el orquestador construye un
`ToolPolicyTarget`: metadata de la definición y una ruta estructural derivada
internamente (`Standard` o `GatewayBacked`). La disponibilidad para el modelo, la
reevaluación inmediatamente antes de ejecutar y la reevaluación al resolver una
confirmación construyen ese mismo target desde la misma instancia actual y llaman a la
misma evaluación. La combinación tiene estas reglas:

- `ControlledExternal + Standard` se deniega con `external_gateway_required`;
- `ControlledExternal + GatewayBacked` continúa a la evaluación de
  `DefaultToolRiskPolicy` para scopes, sensibilidad, coste y confirmación;
- `Local + GatewayBacked` es una composición inválida y se deniega como error de
  configuración segura;
- una tool aprobada inicialmente se vuelve a comprobar antes de ejecución o de
  resolución de confirmación, evitando eludir cambios de contexto o registro.

La identidad, scopes y demás autorización continúan en `ToolPolicyContext`. La ruta
estructural vive solo en `ToolPolicyTarget`, derivado por el registro/orquestador al
identificar el wrapper sellado: no se añade un booleano ni se mezcla esa propiedad en el
contexto de identidad. Esto preserva la denegación por defecto de
`DefaultToolRiskPolicy` para herramientas externas convencionales.

## Composición y gateway

La API registra `IEgressPolicy` con `DefaultEgressPolicy` y
`IExternalToolsGateway` con `ControlledExternalToolsGateway`. En producción el gateway
se construye con allowlist vacía: no existe egreso real hasta que 6.2 registre un
adaptador de destino fijo.

El gateway recibe unas opciones tipadas y validadas que sí tienen consumidor en 6.1:
tiempo máximo total de operación, máximo de operaciones simultáneas y máximo de bytes
del resultado normalizado. La composición de producción acepta respectivamente de 100 ms
a 30 s, de 1 a 4 operaciones y de 1 KiB a 128 KiB; todos se validan al arrancar. El
timeout del gateway debe ser estrictamente menor que `Orchestration.ToolTimeout` para
que una operación externa lenta conserve el código `external_gateway_timeout` en vez de
competir con el límite exterior del orquestador. La configuración predeterminada usa
ocho segundos frente a los diez del orquestador.
gateway toma un permiso sin espera; si no hay uno disponible devuelve
`external_gateway_busy`, por lo que no introduce una cola ilimitada. Con el permiso,
crea un token enlazado con vencimiento total y lo propaga al adaptador. Una cancelación
del solicitante se propaga sin traducirse; solo el vencimiento de su CTS de deadline se
traduce a `external_gateway_timeout`; una cancelación autónoma del adaptador se traduce
a `external_adapter_failed`. Si un adaptador ignora el token, el gateway devuelve ese
timeout pero conserva su permiso de concurrencia hasta que el adaptador termina; no se
permite exceder el límite real. Antes de devolver el resultado, serializa su forma
normalizada en UTF-8 y rechaza un exceso con `external_result_too_large`; una forma de
éxito inconsistente o no serializable se traduce también a `external_adapter_failed`.

La configuración tipada del futuro adaptador HTTP se reservará para 6.2; no se crean
opciones muertas ni clientes HTTP sin consumidor. Host fijo, redirecciones, límites de
stream, Content-Type y validación JSON del proveedor pertenecen al primer adaptador HTTP
de geocodificación y se diseñarán allí.

`ControlledExternalToolsGateway` continúa evaluando la política antes de llamar a un
adaptador. Audita, sin payloads, coordenadas ni contenido, el nombre del adaptador, la
operación, la decisión, la duración y el resultado seguro. Nunca registra mensajes
completos de excepción: pueden contener URL, coordenadas, payload o respuestas externas.
El primer adaptador real añadirá los controles de transporte dentro de estos límites.

## Flujo

```text
ToolDefinition → catálogo filtrado por política y tipo
  → llamada del modelo → reevaluación por política y tipo
  → GatewayBackedTool → IExternalToolsGateway
  → política de egreso → allowlist de adaptadores → adaptador
```

Una denegación en cualquiera de las dos evaluaciones no construye payload para el
adaptador ni realiza HTTP.

## Errores, cancelación y seguridad

Los errores de gateway se traducen a códigos estables (`egress_denied`,
`external_adapter_not_found`, `external_operation_not_allowed`,
`external_gateway_busy`, `external_gateway_timeout`, `external_result_too_large` y
`external_adapter_failed`) sin detalles de destino, payload ni mensaje de excepción. Los
`CancellationToken` se propagan hasta gateway y adaptador. No se reintentan operaciones
externas.

## Pruebas de aceptación

Las pruebas deterministas, sin Internet, cubrirán:

- DI de producción resuelve política y gateway con allowlist vacía;
- una `GatewayBackedTool` de test es visible y ejecutable si política y scope lo
  permiten;
- la misma tool deja de ser visible cuando cambia el contexto y la reevaluación previa
  impide la llamada al adaptador;
- una `ControlledExternal` convencional permanece invisible y denegada, incluso si
  existe gateway;
- `Local + GatewayBacked` se rechaza durante la composición y una operación con
  exposición distinta de `ControlledExternal` no puede envolverla;
- argumentos malformados devuelven `invalid_tool_arguments` y no invocan al gateway;
- una denegación de egreso nunca ejecuta el adaptador;
- solo adaptadores registrados y operaciones declaradas pueden seleccionarse; el
  solicitante no aporta ningún destino;
- timeout total, cancelación, tamaño del resultado normalizado, concurrencia, errores y
  auditoría se prueban con dobles deterministas, sin transporte HTTP.

## Consecuencias documentales

Se actualizarán arquitectura, seguridad y roadmap para describir el gateway compuesto
pero sin proveedores externos activos. ADR 0013 y ADR 0014 siguen vigentes. Se añade un
ADR breve para registrar que la admisión externa solo puede ocurrir mediante el wrapper
sellado por composición; la elección de proveedor y política de caché queda para un
incremento posterior.
