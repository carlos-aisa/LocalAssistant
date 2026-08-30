# Operación del almacenamiento privado local

## Almacenamiento privado local

Cuando `LocalAssistant:ConversationPersistence:Enabled` está activado, la base de
datos SQLite contiene conversaciones autenticadas y notas personales. La aplicación
usa por defecto `%LOCALAPPDATA%\LocalAssistant\conversations.db`. Las rutas de base
de datos configuradas deben ser absolutas.

El bootstrap de identidad guarda también
`%LOCALAPPDATA%\LocalAssistant\installation-identity.json` por defecto. El directorio
de estado configurado debe ser absoluto. Aunque el archivo no guarda la API key en
texto claro, contiene la identidad del propietario, los scopes concedidos y el hash de
la API key; debe tratarse como dato privado.

El perfil global del asistente se guarda por separado como
`assistant-profile.json` en ese mismo directorio. Contiene actualmente su nombre de
presentación; no incluye conversaciones ni notas, pero sigue siendo configuración
privada de la instalación. Al restaurar una copia del directorio, el nombre vuelve al
valor que contenía esa copia.

El directorio que contiene la base, el archivo `conversations.db`, los posibles
archivos auxiliares que SQLite cree junto a ella, `installation-identity.json` y
`assistant-profile.json` forman un único límite de almacenamiento privado. Una ruta
configurada debe ser local y privada. No se debe usar una carpeta compartida,
temporal, extraíble o sincronizada por un tercero salvo que ese entorno proporcione
controles de acceso, cifrado y retención equivalentes.

Antes de habilitar la persistencia en una instalación real, el operador debe comprobar
manualmente lo siguiente:

- La cuenta del sistema operativo que ejecuta la API es la cuenta propietaria de los
  datos o una cuenta de servicio dedicada.
- Los permisos del directorio y de cada archivo privado limitan el acceso a esa cuenta
  y a los administradores estrictamente necesarios. En Windows, `Get-Acl` permite
  inspeccionar los permisos efectivos de una ruta sin modificarlos.
- El volumen donde residen los archivos está protegido mediante cifrado de disco,
  volumen o una solución equivalente. En equipos Windows administrados, el estado del
  volumen puede inspeccionarse, por ejemplo, con `Get-BitLockerVolume`.
- La misma comprobación se aplica a toda copia existente antes de permitir que almacene
  datos privados.

SQLite no cifra sus datos en reposo y LocalAssistant no crea ni valida ACLs, no
configura el cifrado del sistema operativo ni verifica que una ruta configurada sea
privada. Los permisos de archivos y el cifrado de disco tampoco protegen frente a un
administrador malicioso del equipo, malware que se ejecute con la misma cuenta o una
API expuesta sin una frontera de red y autenticación adecuada.

## Recuperación de conversaciones

`LocalAssistant:ConversationRetrieval:Enabled` permanece desactivado por defecto.
Cuando se activa junto con la persistencia de conversaciones, LocalAssistant mantiene
un índice FTS5 local de los mensajes textuales de usuario y asistente. No copia al
índice argumentos ni resultados de herramientas.

La recuperación solo se solicita para un principal autenticado y solo ante expresiones
retrospectivas o de continuación. El filtro por propietario se aplica dentro de la
consulta SQLite; una conversación anónima no se indexa. El contexto resultante se
envía de forma transitoria al proveedor y no se guarda como parte del historial.
Los límites `MaximumMatches` y `MaximumContextCharacters` restringen, respectivamente,
el número de conversaciones y caracteres entregados al proveedor.

Cuando también se configura `LocalAssistant:Ollama:EmbeddingModel`, un servicio alojado
revisa al arrancar y con la frecuencia `IndexingPollInterval` las conversaciones que
llevan inactivas al menos `IndexingDelay` (quince minutos por defecto). Genera su
embedding mediante el Ollama local configurado. Si llega un mensaje durante el trabajo,
la actualización se descarta y esa conversación vuelve a esperar el periodo completo.
Un fallo de Ollama se registra sin contenido conversacional y se reintenta en el
siguiente sondeo; la recuperación literal continúa disponible.

El índice es dato privado derivado: comparte la ruta, permisos operativos, backup,
retención y borrado de `conversations.db`. No deben registrarse consultas ni
fragmentos recuperados en logs o auditoría.

## Copias de seguridad y restauración

Una copia de `conversations.db`, de cualquier archivo auxiliar de SQLite que exista en
el mismo instante, de `installation-identity.json` y de `assistant-profile.json` es el
mismo dato privado que los archivos activos. Los backups deben conservar controles de
acceso, cifrado y retención equivalentes. LocalAssistant no crea, programa ni elimina
esas copias.

La restauración es una operación del operador. Para restaurar un punto de recuperación
consistente debe:

1. Detener la aplicación antes de manipular los archivos.
2. Restaurar conjuntamente una copia consistente de la base SQLite, de sus archivos
   auxiliares presentes en ese punto, del estado de identidad compatible y del perfil
   de asistente que se quiera recuperar.
3. Reiniciar la aplicación únicamente con una configuración válida que siga apuntando
   a rutas absolutas privadas.

La restauración repone el estado histórico elegido; no recalcula ni reasigna
propietarios, scopes o fechas de expiración. Puede reintroducir conversaciones o notas
que existían en ese punto. Tampoco elimina otras copias históricas: la retención y el
borrado de backups son responsabilidad de su sistema de custodia. Por ello, el borrado
selectivo y la retención aplicados por LocalAssistant a la base activa no constituyen
una promesa de borrado global de datos ya copiados.

La restauración de un estado de identidad anterior puede requerir que el cliente use la
API key válida para ese estado restaurado. La API key no se almacena en
`installation-identity.json`; el operador debe custodiarla mediante su mecanismo de
secretos habitual.

## Límites y trabajo futuro

Esta guía describe una política operativa para el almacenamiento actual. No añade
cifrado de SQLite, gestión o rotación de claves, validación automática de permisos,
backups o restauraciones automatizadas, sincronización ni una API de recuperación. Si
alguno de esos mecanismos se incorpora, deberá diseñarse y verificarse como un cambio
separado.
