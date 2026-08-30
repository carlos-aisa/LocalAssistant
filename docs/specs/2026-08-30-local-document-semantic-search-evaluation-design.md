# Diseño: evaluación de búsqueda semántica en documentos locales

## Objetivo

Decidir con datos si los embeddings locales mejoran la recuperación de documentos
permitidos respecto a la búsqueda textual literal ya implementada. La evaluación no
habilita una nueva capacidad para Jarvis ni modifica la API pública.

## Alcance

El incremento añadirá un banco de pruebas sintético y versionado, junto con un
evaluador manual. Cada caso definirá una consulta en lenguaje natural y el
identificador del documento que debería aparecer entre los primeros resultados.

El corpus contendrá documentos ficticios `.txt`, `.md`, `.json` y `.csv`, con
vocabulario deliberadamente distinto entre la consulta semántica y el documento
esperado. No incluirá datos personales, archivos reales ni rutas del equipo.

El evaluador comparará, para los mismos casos:

1. La búsqueda textual literal existente sobre el contenido permitido.
2. Una búsqueda semántica experimental que obtiene embeddings exclusivamente del
   endpoint Ollama local ya configurado.

El informe JSON no conservará texto de consultas ni contenido documental. Registrará
la versión del corpus, el modelo, identificadores de caso, posición del documento
esperado, acierto dentro del límite y latencia de cada estrategia.

## Límites y seguridad

- El corpus se usará solo desde pruebas y el evaluador manual; no se registrará como
  una fuente documental del usuario.
- Ollama recibe únicamente el contenido sintético y las consultas sintéticas. El
  evaluador no acepta rutas ni documentos arbitrarios como argumentos.
- No se añaden endpoints, herramientas LLM, scopes, persistencia, índice vectorial,
  watcher, worker, RAG ni cambios en la recuperación conversacional.
- La evaluación no se ejecuta en CI porque depende del modelo y hardware local. Las
  pruebas que validen el corpus y el cálculo de métricas serán deterministas y no
  requerirán red, Ollama ni GPU.

## Diseño técnico

El núcleo expondrá contratos de evaluación independientes de Ollama y del sistema de
archivos. El corpus se cargará desde recursos de prueba y tendrá identificadores
estables. Una implementación literal reutilizará la coincidencia de contenido ya
existente; la implementación semántica será un adaptador experimental que recibe un
proveedor de embeddings.

El ranking semántico comparará el embedding de la consulta con el de cada documento
mediante similitud de coseno, ordenará de mayor a menor y aplicará el mismo límite de
resultados que la referencia literal. Los vectores solo vivirán durante la ejecución
del evaluador.

El script manual validará que exista un modelo de embeddings local, ejecutará ambos
evaluadores para todo el corpus y escribirá el informe bajo `artifacts/`, excluido de
Git. Un fallo de Ollama abortará la evaluación con un error explícito, sin afirmar
que existe una medición semántica válida.

## Criterio de decisión

El informe permitirá comparar acierto y latencia por caso y en conjunto. No establece
todavía un umbral de adopción: tras una ejecución local reproducible se decidirá si
la mejora observada justifica diseñar una capacidad de búsqueda semántica para
documentos permitidos.

## Verificación

- Pruebas deterministas de carga de corpus, ranking literal, similitud y métricas.
- Pruebas de que el informe no contiene texto de consultas ni contenido documental.
- Prueba de sintaxis del script PowerShell.
- Ejecución manual documentada con Ollama local, fuera de CI.

## No objetivos

No se implementa búsqueda semántica en producción, indexación persistente, ingesta
automática, recuperación RAG, lectura de documentos reales, OCR, búsqueda de código
ni protección nueva frente a prompt injection documental. Esta última continuará
siendo un incremento de seguridad independiente si se habilita contenido documental
para el modelo.
