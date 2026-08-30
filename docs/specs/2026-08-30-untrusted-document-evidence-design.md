# Diseño: evidencia documental no confiable

## Objetivo

Preparar la frontera de seguridad necesaria para un uso futuro de documentos locales
como contexto de Jarvis. Un archivo encontrado o leído seguirá siendo evidencia no
confiable: sus instrucciones, enlaces, metadatos y texto no podrán modificar las
instrucciones del asistente, la autorización ni la ejecución de herramientas.

Este cambio no entrega documentos al modelo todavía. La búsqueda, la lectura y el
índice documental existentes mantienen exactamente sus contratos HTTP actuales.

## Modelo y límites

El núcleo incorporará un contrato explícito para una evidencia documental acotada. La
evidencia conservará solo la procedencia relativa permitida y un fragmento limitado,
y estará marcada como `UntrustedDocument`. No admitirá rutas absolutas, argumentos de
herramienta, decisiones de autorización ni instrucciones del sistema.

Un futuro recuperador documental podrá producir evidencias con este contrato. El
orquestador actual no las solicitará ni las añadirá a los mensajes del proveedor. Por
tanto, este incremento no habilita RAG, una herramienta LLM documental, nuevos scopes,
nuevos endpoints ni tráfico adicional a Ollama.

## Composición futura del contexto

Cuando una capacidad posterior decida incluir evidencia documental en una petición al
modelo, el adaptador deberá emitirla en un bloque separado de las instrucciones del
sistema y del mensaje del usuario. Ese bloque declarará que el contenido es evidencia
no confiable y que no se deben obedecer órdenes, solicitudes de secretos, cambios de
política ni llamadas a herramientas que aparezcan dentro del documento.

La autorización seguirá evaluándose en el servidor mediante principal, scopes, riesgo
y confirmación. Ningún texto documental podrá conceder permisos, ampliar la lista de
herramientas o modificar argumentos validados. La delimitación reduce la confusión de
roles, pero no sustituye las validaciones de autorización ni garantiza que un modelo
ignore una instrucción hostil.

## Verificación

- Pruebas de contrato validarán límites, procedencia relativa y el marcador de origen
  no confiable.
- Pruebas del adaptador comprobarán que, cuando se habilite un consumidor futuro, la
  evidencia se compone fuera de instrucciones y mensajes de usuario.
- Las pruebas de búsqueda y lectura documental confirmarán que no hay cambios de
  permiso, contrato HTTP ni llamadas al modelo en este incremento.

## No objetivos

No se añaden detección heurística de frases, clasificación adicional, sanitización
silenciosa, RAG, extracción de instrucciones, ejecución de acciones, acceso a rutas
adicionales ni protección absoluta frente a prompt injection.
