# Diseño: perfil global del asistente de instalación

## Objetivo

Permitir que el propietario de una instalación asigne mediante conversación un nombre
al asistente, por ejemplo «Jarvis», y que ese nombre se aplique de forma fiable a todas
las conversaciones posteriores. El nombre no será un dato del historial ni una nota de
memoria personal.

## Alcance

Se introducirá un perfil global de instalación, `AssistantProfile`, con un único campo
implementado: `DisplayName`. Su valor por defecto será `LocalAssistant`.

El perfil será extensible mediante campos estructurados que se diseñarán y autorizarán
por separado. Este incremento no crea una lista genérica de pares clave-valor, no
extrae preferencias automáticamente de conversaciones ni implementa otros ajustes de
personalidad, idioma, tono o comportamiento.

## Almacenamiento y ciclo de vida

El perfil se almacenará como `assistant-profile.json` en el directorio de estado de
instalación ya configurado mediante `LocalAssistant:Installation:StateDirectory`. Si
el archivo no existe, el sistema usará el perfil predeterminado sin crear datos hasta
que el propietario cambie el nombre.

El adaptador escribirá el archivo de forma atómica y validará sus datos al leerlos. El
nombre será texto no vacío, recortado, con longitud acotada y sin caracteres de
control. El archivo, sus copias y restauraciones se tratarán como datos privados bajo
los mismos controles operativos que `installation-identity.json`.

El perfil no dependerá de `ConversationPersistence`: representa una propiedad de la
instalación, no de una conversación ni de una base SQLite opcional. Restaurar una copia
del perfil repone el nombre existente en ese punto; la aplicación no fusionará ni
adivinará valores entre copias.

## Cambio conversacional confirmado

Se añadirá la herramienta allowlisted `set_assistant_name`, con un argumento
`displayName`. El proveedor puede solicitarla al interpretar una petición explícita del
usuario como «a partir de ahora te llamarás Jarvis», pero no decide la autorización:

1. Solo un principal autenticado con `installation.owner` podrá llegar a la ejecución.
2. La herramienta requerirá confirmación explícita y el servidor retendrá la llamada
   exacta, incluido el nombre propuesto, antes de modificar el perfil.
3. Al aprobarla, el adaptador validará y guardará el nuevo nombre. Al rechazarla o si
   falta autorización, el perfil no cambiará.

Una conversación anónima, un principal sin ese scope, un argumento inválido o texto
procedente de un documento no pueden persistir un nombre. La herramienta no admite
otros campos del perfil ni claves arbitrarias.

## Contexto para proveedores

Antes de cada llamada a un proveedor, el orquestador obtendrá el perfil actual y
añadirá una instrucción de sistema de confianza que indique el nombre configurado. Esa
instrucción no se persistirá en el historial de la conversación ni podrá originarse en
texto del usuario.

El contrato de mensajes incorporará el rol `System`, y el adaptador de Ollama lo
serializará como un mensaje `system`. El perfil se volverá a consultar en cada llamada
del bucle de herramientas: tras aprobar `set_assistant_name`, la respuesta final del
mismo turno ya conoce el nombre actualizado. Los proveedores fake conservarán su
comportamiento determinista, pero sus pruebas verificarán que la instrucción no se
confunde con un mensaje de usuario.

## Arquitectura y autorización

El contrato del perfil permanecerá en el núcleo, independiente de JSON y del sistema
de archivos. Un adaptador de la API implementará ese contrato mediante el archivo de
estado. La herramienta dependerá exclusivamente del contrato y del perfil de riesgo
existente; no recibirá rutas, permisos de archivo ni acceso arbitrario al estado de
instalación.

El alcance es global para la instalación actual de propietario único. La futura
identidad doméstica podrá decidir si el perfil sigue siendo del hogar o introduce
preferencias por usuario, sin reutilizar un nombre de asistente como memoria personal.

## Verificación

Las pruebas cubrirán el perfil por defecto, lectura y escritura atómica, validación de
nombre, y que un perfil corrupto se rechaza. También cubrirán la autorización y
confirmación de `set_assistant_name`, la ausencia de cambios tras rechazo, la
actualización visible dentro del mismo turno y el mapeo de la instrucción `System` a
Ollama. Se actualizarán README, arquitectura, seguridad, operación y OpenAPI solo si
la superficie HTTP existente refleja metadatos de la herramienta sin requerir cambios
de contrato.

Se ejecutarán formato, build Release, toda la suite y revisión del diff para comprobar
que no aparecen nombres, rutas o credenciales reales en logs, pruebas o documentos.

## Criterios de aceptación

- Un propietario autenticado puede solicitar y confirmar el cambio a `Jarvis`.
- El nombre se conserva tras reiniciar la API y aparece como instrucción de confianza
  en una conversación nueva.
- Un cliente anónimo o sin `installation.owner` no puede cambiarlo.
- Rechazar la confirmación o enviar un valor inválido conserva el perfil anterior.
- El historial de conversaciones y las notas personales no se usan como almacenamiento
  ni fuente implícita del nombre.
