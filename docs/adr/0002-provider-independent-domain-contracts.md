# ADR 0002: Contratos de proveedor propios

- Estado: Aceptada
- Fecha: 2026-08-17

## Contexto

LocalAssistant deberá comunicarse con Ollama y quizá proveedores externos. SDKs como
`Microsoft.Extensions.AI` ofrecen interoperabilidad, pero también tipos y
comportamientos que podrían extenderse por todo el dominio.

## Decisión

Definir `ILanguageProvider` y mensajes propios mínimos. Implementar primero un fake
secuencial. Evaluar cada SDK como adaptador de infraestructura, no como modelo de
dominio.

## Consecuencias

Cambiar de proveedor no cambia el orquestador. Será necesario escribir traducciones
para cada SDK y ampliar conscientemente los contratos cuando aparezcan streaming,
multimodalidad o detalles específicos.
