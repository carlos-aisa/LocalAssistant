# ADR 0028: Usar recuperación híbrida local para conversaciones autenticadas

- Estado: Aceptada
- Fecha: 2026-08-30

## Contexto

Una conversación nueva puede retomar un tema tratado días antes sin conocer el
identificador de la conversación original. Recuperar literalmente todo el historial
sería costoso y aumentaría innecesariamente la exposición de datos privados. La
capacidad debe respetar propiedad, retención y borrado de las conversaciones
autenticadas; no debe incorporar un servicio cloud, una base vectorial ni un proceso
independiente.

## Decisión

La misma SQLite de conversaciones mantiene un índice derivado con FTS5 para texto
literal, un embedding serializado con el identificador de su modelo y metadatos
acotados de tema, resumen y palabras clave. Ollama local genera los embeddings y el
resumen estructurado después de quince minutos de inactividad configurables.

La recuperación combina coincidencias literales y similitud coseno local solo entre
vectores del mismo modelo. Cada consulta exige el identificador del propietario y lo
filtra en SQLite. El orquestador la activa de forma conservadora ante peticiones de
continuación o historial y entrega el resultado como contexto de sistema transitorio,
no como mensajes persistidos.

El `BackgroundService` forma parte de la API y reintenta estados pendientes durante
sus sondeos. Si una conversación cambia mientras se procesa, el resultado se descarta.
Los fallos del resumen no eliminan un embedding válido y se reintentan sin regenerarlo.

## Consecuencias

La búsqueda puede recuperar temas con palabras distintas a las originales y sigue
funcionando literalmente si falta un embedding o un resumen. El índice comparte las
limitaciones de protección en reposo, backups y restauración de SQLite. No hay soporte
para conversaciones anónimas, memoria genérica, reconocimiento de voz, búsqueda
global, egreso externo ni escala de varias instancias. Una base vectorial u otro
worker solo se considerarán si existe evidencia de volumen, calidad o operación que lo
justifique.
