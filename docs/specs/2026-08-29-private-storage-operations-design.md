# Diseño: operación segura del almacenamiento privado local

## Objetivo

Completar la evaluación de protección en reposo, copias de seguridad y restauración
para la primera persistencia privada de LocalAssistant. El resultado será una política
operativa verificable para conversaciones y notas personales ya almacenadas en SQLite,
sin presentar SQLite como cifrado ni introducir una gestión de claves prematura.

## Alcance

La política cubrirá:

- El archivo SQLite privado y el estado de identidad de instalación.
- La ubicación predeterminada bajo `LocalApplicationData/LocalAssistant` y las rutas
  absolutas configuradas por el operador.
- Los controles que debe aplicar el despliegue: cuenta del sistema operativo,
  permisos de archivos, cifrado de disco o volumen, backups y restauración.
- Los límites de la protección actual frente a acceso administrativo al equipo,
  sincronización externa y copias ya existentes.

No añadirá cifrado de SQLite, una herramienta de backup, un endpoint de exportación o
restauración, una modificación automática de ACL, claves nuevas, sincronización ni
cambios en la API, el esquema o los scopes.

## Decisión operativa

LocalAssistant seguirá usando SQLite local y `installation-identity.json` como datos
privados bajo la custodia del despliegue. La aplicación valida rutas absolutas y usa
`LocalApplicationData/LocalAssistant` por defecto, pero no puede demostrar ni imponer
de forma portable que una carpeta configurada sea privada para todos los modelos de
cuenta y despliegue.

Por ello, el operador debe:

1. Ejecutar el proceso con una cuenta dedicada o con la cuenta local propietaria de los
   datos y limitar el acceso a esa cuenta y a los administradores estrictamente
   necesarios.
2. Mantener la base SQLite, su directorio y `installation-identity.json` fuera de
   carpetas compartidas, sincronizadas públicamente, temporales o extraíbles, salvo
   que el servicio responsable aplique controles equivalentes.
3. Proteger el volumen mediante cifrado de disco, volumen o una solución equivalente;
   SQLite no cifra su contenido por sí misma.
4. Tratar cada backup como el mismo dato privado: aplicar controles de acceso y
   cifrado equivalentes, conservar la retención necesaria y no usarlo para ampliar el
   acceso a conversaciones ni notas.
5. Restaurar de forma coordinada la base y el estado de identidad compatible. La
   restauración no cambia propietarios, scopes ni fechas de expiración y no revive una
   conversación que ya hubiera caducado o sido eliminada del backup elegido.

La guía incluirá una comprobación manual para confirmar la ruta efectiva, la cuenta de
ejecución, los permisos locales y el estado de cifrado del volumen antes de activar la
persistencia en una instalación real.

## Arquitectura y límites

La política no cambia los límites existentes:

- `SqliteConversationStore` conserva conversaciones autenticadas y notas personales
  comparten el mismo archivo configurado; las conversaciones anónimas siguen solo en
  memoria.
- La identidad instalada conserva únicamente el hash de la API key, pero ese archivo
  sigue siendo privado porque contiene el propietario y los scopes concedidos.
- La retención y el borrado selectivo operan sobre la base activa. No prometen borrar
  automáticamente una copia de seguridad histórica.
- Restaurar una copia puede reintroducir datos que existían en el momento de la copia;
  por tanto, su retención, acceso y procedimiento forman parte de la responsabilidad
  operativa, no de una garantía de borrado global.

No se afirmará que permisos de archivos o cifrado de disco resuelvan una exposición
pública de la API, la administración maliciosa del equipo, malware que ejecute bajo la
misma cuenta ni la futura gestión de usuarios domésticos.

## Documentación y verificación

La implementación actualizará una guía operativa de almacenamiento privado y las
referencias relevantes de README, arquitectura, seguridad, ADR 0024, ADR 0025 y
roadmap. La guía distinguirá el comportamiento ya implementado de obligaciones del
operador y de trabajo futuro.

No requiere pruebas de código porque no modifica comportamiento ejecutable. La revisión
verificará que las afirmaciones coinciden con la resolución actual de rutas, la
configuración de persistencia, la identidad de instalación y los límites de SQLite.

## Criterios de aceptación

- Un operador puede identificar qué archivos son privados, dónde se ubican y qué
  controles debe aplicar antes de activar la persistencia.
- La documentación explica que SQLite no cifra en reposo y que las copias requieren
  protección equivalente.
- La restauración no se presenta como una operación capaz de cambiar propiedad,
  autorización, retención o borrado selectivo.
- No se añade infraestructura, código de cifrado, automatización de backups ni una
  superficie HTTP nueva.
