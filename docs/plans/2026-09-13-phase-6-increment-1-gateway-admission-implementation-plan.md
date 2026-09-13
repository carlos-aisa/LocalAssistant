# Plan de implementación: fase 6, incremento 1 — admisión del gateway externo

## Alcance aprobado

Implementar admisión estructural de `GatewayBackedTool`, registrar política y gateway
en la API y demostrar el recorrido solo con fixtures de integración. No publicar tools
ni adaptadores externos de producción.

## Lote 1 — Contrato de admisión y política

**Archivos:** `LocalAssistant.Core/Tools`, `Security/ToolRisk`, tests unitarios
correspondientes.

1. Añadir el wrapper sellado por composición `GatewayBackedTool`, que envuelve una
   operación externa no-`ITool` sin acceso al gateway. La operación valida en runtime
   los argumentos no confiables antes de construir `ExternalToolRequest`; ante fallo
   devuelve `invalid_tool_arguments` sin despacho, también si la validación señala
   `ArgumentException` o `JsonException`. El constructor exige exposición
   `ControlledExternal` y traduce de forma segura el resultado.
2. Mantener identidad y scopes exclusivamente en `ToolPolicyContext`; la admisión
   estructural se resuelve mediante `ToolPolicyTarget` (metadata y ruta derivada de la
   instancia registrada), nunca por metadatos ni por un booleano introducido por la
   herramienta.
3. Definir una evaluación compartida: `ControlledExternal + Standard` devuelve
   `external_gateway_required`; `ControlledExternal + GatewayBacked` continúa hacia
   `DefaultToolRiskPolicy`; `Local + GatewayBacked` se rechaza como configuración
   inválida.
4. Mantener cancelación y no introducir URL, host, método o cabecera en argumentos.

**Pruebas obligatorias:** admisión de wrapper; denegación de externa convencional;
rechazo de `Local + GatewayBacked`; constructor rechaza operación no externa; argumentos
malformados devuelven `invalid_tool_arguments` sin llamar al gateway; scopes y
sensibilidad siguen aplicándose; una herramienta no puede autodeclararse admitida ni
acceder al gateway fuera del wrapper.

## Lote 2 — Catálogo y reevaluación de orquestación

**Archivos:** `ConversationOrchestrator`, contratos mínimos de herramientas y pruebas
de orquestación.

1. Construir `ToolPolicyTarget` desde la instancia actual del registro y filtrar
   definiciones con la evaluación compartida de ruta y política.
2. Construir de nuevo ese mismo target desde la instancia actual antes de ejecución y
   al resolver confirmación; no reutilizar una decisión ni metadata anteriores.
3. Conservar auditoría y errores actuales; la denegación no llega a ejecutar la tool.

**Pruebas obligatorias:** wrapper visible/ejecutable con contexto permitido; no visible
con contexto denegado; permiso retirado entre catálogo y ejecución bloquea el adaptador;
confirmación aprobada también reevalúa; externa convencional no aparece ni se ejecuta;
las tres rutas aplican exactamente las combinaciones estructurales definidas.

## Lote 3 — Composición y gateway endurecido sin proveedor

**Archivos:** `LocalAssistant.Api/Program.cs`, infraestructura de gateway, opciones
tipadas solo si tienen consumidor, pruebas de composición y gateway.

1. Añadir y validar al arranque las opciones consumidas por el gateway: timeout total
   (100 ms a 30 s), límite de concurrencia (1 a 4) y tamaño máximo del resultado
   normalizado (1 KiB a 128 KiB). Validar además que el timeout del gateway sea
   estrictamente menor que `Orchestration.ToolTimeout`; la configuración predeterminada
   será ocho segundos frente a diez. Registrar
   `DefaultEgressPolicy` e `IExternalToolsGateway` en DI con allowlist vacía.
2. Endurecer validación de metadatos de adaptador (longitud acotada, sin controles y
   formato seguro para nombre y operaciones) y preservar política antes de payload.
   Solo podrán seleccionarse adaptadores registrados y operaciones declaradas; la
   petición no contiene destino.
3. No registrar `HttpClient`, transporte genérico ni adaptador HTTP de producción.
   Host fijo, redirecciones, límites de stream, Content-Type y JSON de proveedor quedan
   expresamente para 6.2.
4. Aplicar concurrencia sin cola ilimitada: si no se obtiene permiso, devolver
   `external_gateway_busy`; aplicar el timeout total con un token enlazado y propagar la
   cancelación solicitada. Distinguir el CTS propio de deadline: solo su vencimiento
   devuelve `external_gateway_timeout`; una cancelación propia del adaptador es
   `external_adapter_failed`. Si un adaptador ignora su token, conservar el permiso hasta
   que termine aunque ya se haya devuelto timeout. Normalizar y medir el resultado antes
   de devolverlo, con `external_result_too_large` al superar el límite y
   `external_adapter_failed` para una forma de éxito inconsistente.
5. Implementar auditoría segura de decisión, duración y resultado sin valores ni
   mensajes completos de excepción.

**Pruebas obligatorias:** host API resuelve las dependencias; allowlist vacía no permite
adaptador; política denegada no llama al adaptador; campos duplicados, adaptador no
registrado y operación no declarada se rechazan; timeout total propio devuelve su código,
la cancelación del solicitante se propaga, el exceso de concurrencia no se encola, el
tamaño del resultado normalizado se limita y los errores/auditoría son seguros. Todo se
prueba mediante dobles deterministas. Una prueba de integración por el orquestador
demuestra que el deadline del gateway se observa antes del timeout exterior.

## Lote 4 — Integración controlada de demostración

**Archivos:** pruebas API/orquestación e infraestructura de test únicamente.

1. Definir una `GatewayBackedTool` y adaptador de test, no registrados por `Program.cs`.
2. Ejecutarlos por el loop conversacional real con `WebApplicationFactory`, sin
   transporte HTTP ni proveedor externo.
3. Demostrar que el camino permitido alcanza el adaptador una vez, y cada denegación
   previa deja el contador del adaptador en cero.
4. Probar únicamente controles del gateway con dobles: timeout total, cancelación,
   máximo de operaciones concurrentes, resultado normalizado excesivo, fallo de
   adaptador y auditoría sin excepción completa.

**Pruebas obligatorias:** ningún test normal usa Internet, reloj real ni proveedor real;
las aserciones verifican resultado observable y contador de despacho, no detalles
privados del framework.

## Lote 5 — Documentación y cierre

Actualizar `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/ROADMAP.md`, documentación
de pruebas y un ADR breve de admisión exclusiva mediante wrapper sellado. Distinguir
gateway compuesto de proveedor activo. No marcar como implementado tiempo, ubicación ni
Open-Meteo.

Ejecutar desde la raíz:

```powershell
dotnet format LocalAssistant.sln --verify-no-changes --no-restore
dotnet build LocalAssistant.sln -c Release --no-restore
dotnet test LocalAssistant.sln -c Release --no-build
git diff --check
```

Antes de una PR, revisar el diff con el checklist de revisión del repositorio. El
incremento se cierra solo si las pruebas demuestran ambas barreras —catálogo y
reevaluación— y ninguna herramienta ni adaptador externo de prueba aparece en la
composición de producción.
