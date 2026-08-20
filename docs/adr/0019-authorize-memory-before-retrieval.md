# ADR 0019: Autorizar memoria antes de recuperarla para el modelo

- Estado: Aceptada
- Fecha: 2026-08-20

## Contexto

Memoria personal, datos compartidos, documentos, estado de módulos y sesiones de
invitados no tienen los mismos propietarios ni destinatarios. Recuperar primero y
ocultar después la respuesta permitiría que contenido no autorizado llegase al LLM,
influyese en su salida o apareciese en trazas y llamadas posteriores.

## Decisión

Propiedad, ámbito y autorización se aplicarán antes de buscar o recuperar contenido
para el contexto del modelo. Índices, filtros, ranking, fragmentos, metadatos y
embeddings respetarán el principal y la sesión. Conocimiento general, memoria
personal, memoria del hogar, memoria de módulo, administración y sesión efímera serán
ámbitos separables.

El contenido recuperado seguirá tratándose como dato no confiable frente a prompt
injection. Autorizar su lectura no le permitirá conceder permisos ni solicitar otras
herramientas.

## Consecuencias

- Los almacenes y consultas necesitarán propiedad y ámbito como datos de primera
  clase.
- Una sesión invitada no consultará memoria familiar aunque el resultado final fuese
  redactado.
- Cachés e índices deberán conservar aislamiento y revocación coherentes.
- Las pruebas de RAG incluirán ausencia de resultados no autorizados, no solo
  redacción de respuestas.
- La implementación concreta se pospone hasta la fase de persistencia y memoria.
