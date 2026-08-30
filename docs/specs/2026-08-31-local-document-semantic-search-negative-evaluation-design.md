# Diseño: evaluación de resultados negativos de búsqueda semántica local

## Objetivo

Extender el banco de pruebas manual de búsqueda semántica para que también mida
cuándo un documento no debe recuperarse. La ampliación permite calibrar el umbral
de similitud configurado frente a falsos positivos, sin cambiar el buscador de
producción ni sus contratos.

## Alcance

El corpus sintético y versionado admitirá dos clases de casos:

1. Positivo: declara `expectedDocumentId`; ese documento debe aparecer dentro del
   límite solicitado y alcanzar el umbral de similitud.
2. Negativo: omite `expectedDocumentId`; ningún primer resultado debe alcanzar el
   umbral de similitud.

El informe conservará exclusivamente identificadores y métricas: el resultado
esperado, posición del documento esperado cuando exista, mejor puntuación,
acierto, falso positivo y latencia. No incluirá la consulta ni contenido de los
documentos sintéticos.

## Diseño técnico

El contrato del caso hará opcional el identificador esperado. La validación del
corpus seguirá exigiendo que los identificadores positivos existan y que cada caso
tenga un identificador y una consulta no vacíos.

La evaluación semántica recibirá el umbral de similitud como parámetro explícito.
Para un caso positivo, será un acierto solo si el documento esperado aparece en el
ranking limitado y su puntuación alcanza el umbral. Para un caso negativo, será un
falso positivo solo si el primer resultado alcanza ese umbral. El criterio refleja
la búsqueda de producción, que descarta resultados semánticos por debajo del
umbral antes de devolverlos.

La evaluación literal conserva su finalidad comparativa. No inventará una
puntuación semántica ni aplicará el umbral; seguirá informando si el documento
positivo esperado aparece en el límite y, en casos negativos, si devuelve algún
resultado literal.

El ejecutable manual continuará fuera de CI y empleará únicamente Ollama local y
el corpus sintético. La configuración de producción sigue siendo la fuente del
umbral cuando se ejecute manualmente, para que la medición sea representativa de
la configuración que se pretende calibrar.

## Datos y flujo

1. Se carga y valida el corpus, incluyendo casos negativos sintéticos.
2. El evaluador crea embeddings efímeros de documentos y consultas para la
   estrategia semántica.
3. Ordena por similitud, conserva las puntuaciones necesarias para evaluar el
   límite y aplica la semántica positiva o negativa del caso.
4. El informe agregado expone recuentos de aciertos, fallos y falsos positivos,
   además del detalle seguro por identificador de caso.

## Errores y límites

- Un corpus que refiera un identificador positivo inexistente es inválido.
- El umbral debe estar dentro del intervalo válido de similitud `[-1, 1]`.
- Un proveedor que cambie de modelo o dimensiones durante la ejecución sigue
  invalidando la evaluación.
- Una indisponibilidad de Ollama impide la medición manual; nunca se sustituye por
  un resultado sintético ni afecta a CI.

## Verificación

- Pruebas deterministas para un negativo limpio, un falso positivo que supera el
  umbral y un positivo rechazado por no alcanzarlo.
- Pruebas de validación del corpus y de que el informe serializado no filtra
  consultas ni contenido.
- Prueba de que el ejecutable manual usa el umbral configurado.
- Formato, compilación Release y suite de pruebas sin Ollama, red ni GPU.

## No objetivos

No se modifica el algoritmo de búsqueda de documentos, el índice persistente, la
API, los permisos, la indexación ni el comportamiento conversacional. Tampoco se
establece todavía un valor definitivo para el umbral: esta evaluación aporta los
datos locales para decidirlo posteriormente.
