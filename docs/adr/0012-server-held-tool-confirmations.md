# ADR 0012: Confirmar llamadas de herramientas retenidas por el servidor

## Contexto

La primera versión aceptaba una lista de nombres de herramientas aprobadas en el
mensaje HTTP. Ese dato no identificaba una llamada concreta, sus argumentos ni su
caducidad, por lo que no era una confirmación segura para una operación de impacto.

## Decisión

El orquestador conserva en el servidor la llamada exacta solicitada por el modelo,
incluidos identificador, herramienta, argumentos, proveedor, llamadas posteriores y
caducidad. Desde ADR 0016 conserva además el principal autenticado que la originó,
cuando existe. La API devuelve `202` con una representación visible de esa llamada. La
decisión posterior solo contiene `approved`; no puede sustituir argumentos. Una
exclusión añade un resultado de herramienta explícito y permite al proveedor cerrar
la conversación. Un bloqueo por conversación evita resolver o iniciar dos turnos a
la vez dentro del proceso.

## Consecuencias

La confirmación es de un solo uso y evita ejecutar una llamada distinta a la
presentada. El almacenamiento actual está en memoria: se pierde al reiniciar y no
incluye gestión de usuarios, propiedad de conversaciones, autorización duradera ni
auditoría. Es un límite educativo y no autoriza exponer la API ni conectar acciones
reales de alto impacto.
