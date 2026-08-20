# ADR 0021: Auditar metadatos de herramientas y separar errores para el cliente

- Estado: Aceptada
- Fecha: 2026-08-20

## Contexto

El bucle de herramientas necesitaba explicar qué decisión tomó sin registrar prompts,
argumentos ni resultados. Además, un error de una herramienta usa contenido útil para
que el proveedor continúe el protocolo, pero ese mismo contenido puede revelar datos
del proveedor, del recurso o de la operación si llega directamente a HTTP.

## Decisión

El núcleo emitirá eventos mediante `IToolAuditSink` para la solicitud, la decisión de
política, la confirmación y la ejecución de herramientas. El registro actual será un
sink en memoria y solo conservará identificadores, principal disponible, proveedor,
herramienta, resultado, confirmación y duración.

`ToolExecutionResult` distinguirá el contenido para el proveedor de un mensaje
opcional seguro para el cliente. Cuando una herramienta falla, la API expondrá solo
ese mensaje o una respuesta genérica; los argumentos y el contenido no se copiarán a
la auditoría.

## Consecuencias

- Las pruebas pueden verificar decisiones y transiciones sin depender de logs ni
  inspeccionar contenido sensible.
- La auditoría actual no es durable, consultable ni resistente a manipulación; esas
  propiedades se decidirán junto con persistencia y el primer vertical con efectos.
- La confirmación de un único uso no se presenta como idempotencia general. Cada
  herramienta real con cambio de estado deberá definir una clave de operación antes
  de integrarse.
