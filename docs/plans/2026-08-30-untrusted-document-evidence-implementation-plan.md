# Plan de implementación: evidencia documental no confiable

## Objetivo

Materializar el contrato de evidencia documental no confiable sin entregar todavía
documentos a Jarvis. El incremento debe dejar una frontera reutilizable y comprobable
para una futura recuperación documental, sin cambiar el historial conversacional, los
endpoints, los scopes ni la selección de herramientas.

## Supuestos confirmados

- La lectura y búsqueda documentales actuales siguen siendo operaciones de API para un
  cliente autenticado; no se modificarán.
- La evidencia solo se preparará para un consumidor futuro del contexto del modelo.
- La defensa se basa en separación de roles, validación en servidor y prohibición de
  efectos de privilegio; no en filtrar silenciosamente palabras del documento.

## Incremento 1: contrato de evidencia acotada

**Archivos:** nuevo contrato en `src/LocalAssistant.Core/Documents/`; pruebas nuevas
en `tests/LocalAssistant.Tests/Documents/`.

1. Definir `UntrustedDocumentEvidence` con ruta relativa, extracto limitado y una
   marca de origen inmutable. Validar nulos, texto vacío, rutas absolutas y extractos
   que superen el máximo ya aprobado de 280 caracteres.
2. No reutilizar `ConversationMessage` ni `ToolResultMessage`: la evidencia no es un
   mensaje conversacional, una instrucción del sistema ni un resultado de herramienta.
3. Probar aceptación de una ruta relativa y rechazo determinista de entradas inválidas.
   Las pruebas comprobarán el contrato observable, no sus métodos privados.

## Incremento 2: composición segura preparada para un consumidor futuro

**Archivos:** contrato o compositor mínimo en `src/LocalAssistant.Core/Documents/`;
pruebas en `tests/LocalAssistant.Tests/Documents/`.

1. Añadir un compositor explícito que transforme una colección de evidencias en un
   bloque de contexto documental con delimitadores estables y una instrucción de
   tratamiento como evidencia no confiable.
2. El resultado del compositor no tendrá capacidad para registrar herramientas,
   añadir scopes, elegir proveedores ni construir argumentos. No se conectará al
   orquestador, `LanguageProviderRequest` ni `OllamaLanguageProvider` en este
   incremento.
3. Probar que una instrucción hostil contenida en el extracto queda dentro del bloque
   delimitado y que la advertencia de no obedecer instrucciones documentales se emite
   fuera de dicho contenido. Probar también orden estable, límites y colección vacía.

## Incremento 3: documentación y cierre de la fase documental

**Archivos:** `docs/ARCHITECTURE.md`, `docs/SECURITY.md`, `docs/ROADMAP.md` y, solo
si los cambios reales lo requieren, `README.md`.

1. Documentar que el contrato es una preparación sin inyección de documentos al LLM,
   sin RAG ni nuevas operaciones HTTP.
2. Marcar como realizada la protección estructural contra instrucciones hostiles de
   documentos, indicando de forma explícita que no constituye una garantía frente a
   modelos ni sustituye autorización y validación de herramientas.
3. No cambiar OpenAPI: no hay modificación de contrato público.

## Seguridad, privacidad y concurrencia

- La evidencia no se persiste, registra, audita ni envía a Ollama en este incremento.
- Las rutas absolutas y contenido sin límites se rechazan antes de crear el contrato.
- La autorización actual permanece previa a las operaciones documentales; ningún texto
  documental puede influir en principal, scopes, confirmaciones o herramientas.
- El compositor será puro y sin estado compartido; no introduce locks, workers ni
  problemas de concurrencia.

## Verificación

1. `dotnet format LocalAssistant.sln --no-restore --verify-no-changes`
2. `dotnet build LocalAssistant.sln -c Release --no-restore`
3. Pruebas nuevas de `UntrustedDocumentEvidence` y composición documental.
4. `dotnet test LocalAssistant.sln -c Release --no-restore`
5. `git diff --check`

## No objetivos

No se añaden RAG, herramienta documental para el LLM, recuperación automática,
embeddings adicionales, clasificación de contenido, sanitización heurística, scopes,
endpoints, proveedores, almacenamiento, telemetría de documentos ni garantías de que
un modelo concreto ignore contenido malicioso.
