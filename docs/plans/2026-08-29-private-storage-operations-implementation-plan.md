# Plan de implementación: operación segura del almacenamiento privado local

## Alcance y decisión confirmada

Este plan aplica la especificación
`docs/specs/2026-08-29-private-storage-operations-design.md`. Cierra la evaluación
operativa de la persistencia privada actual mediante documentación verificable para
SQLite, `installation-identity.json`, backups y restauración.

No se modifican ejecutables, endpoints, opciones, esquemas, permisos, ACL ni cifrado.
Las afirmaciones se limitarán a lo que ya hace el repositorio: rutas absolutas,
ubicación predeterminada bajo `LocalApplicationData/LocalAssistant`, SQLite local sin
cifrado propio, propiedad de conversaciones y notas, retención y borrado selectivo
sobre la base activa.

## 1. Crear la guía operativa de almacenamiento privado

**Archivo nuevo:** `docs/OPERATIONS.md`.

- Crear una sección dedicada a la persistencia privada local que identifique como
  privados el archivo SQLite, su directorio, los posibles archivos auxiliares de
  SQLite y `installation-identity.json`.
- Distinguir la ruta predeterminada de las rutas configuradas por el operador y exigir
  rutas locales privadas, no compartidas, temporales, extraíbles ni sincronizadas sin
  controles equivalentes.
- Documentar una lista de comprobación manual, orientada a Windows, para confirmar la
  cuenta que ejecuta la API, permisos del directorio, cifrado del volumen y protección
  de cada copia antes de activar `ConversationPersistence`.
- Explicar que SQLite no cifra datos en reposo, que LocalAssistant no aplica ACLs ni
  crea backups, y que permisos de archivos o cifrado de disco no protegen frente a un
  administrador malicioso, malware bajo la misma cuenta o una API expuesta.

## 2. Definir las reglas de backup y restauración

**Archivo:** `docs/OPERATIONS.md`.

- Indicar que una copia de la base, de sus archivos auxiliares y del estado de
  identidad es el mismo dato privado y debe conservar controles de acceso, cifrado y
  retención equivalentes.
- Documentar la restauración como procedimiento del operador: detener la aplicación,
  restaurar una copia consistente de la base y el estado de identidad compatible, y
  volver a iniciar con una configuración válida.
- Aclarar que restaurar no modifica propietarios, scopes ni expiración, no borra otras
  copias históricas y puede reintroducir datos presentes en el punto de restauración.
- No añadir comandos que modifiquen permisos, creen copias o restauren archivos. La
  guía debe describir requisitos y límites, no automatizar una operación destructiva.

## 3. Alinear documentación de producto, seguridad y ADR

**Archivos:** `README.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`,
`docs/ROADMAP.md`, `docs/adr/0024-use-sqlite-for-local-conversation-persistence.md`
y `docs/adr/0025-define-private-storage-lifecycle.md`.

- Enlazar la guía operativa desde el README junto a la configuración de persistencia,
  sin duplicar instrucciones extensas.
- Actualizar arquitectura y seguridad para distinguir la protección de despliegue de
  las garantías que impone la aplicación: aislamiento por propietario, retención y
  borrado sobre la base activa.
- Completar los ADR 0024 y 0025 con la evaluación: el operador custodia permisos,
  cifrado de volumen, backups y restauración; SQLite no aporta cifrado propio ni
  borrado global de copias históricas.
- Marcar como evaluado el punto de protección en reposo, backups y restauración de la
  fase 4. Mantener explícitamente pendientes cualquier cifrado adicional, gestión de
  claves, sincronización, backup automatizado o recuperación de cuentas.

## 4. Verificación y revisión

- Contrastar cada afirmación de la guía con
  `SqliteConversationStore.ResolveDatabasePath`,
  `SqlitePersonalMemoryStore.ResolveDatabasePath` y
  `FileInstallationIdentityStore.ResolveStateDirectory`.
- Verificar que la documentación no anuncie API, cifrado, ACL, backup o restauración
  automatizados que el producto no implementa.
- Ejecutar `dotnet format LocalAssistant.sln --verify-no-changes --no-restore` y
  `dotnet build LocalAssistant.sln --configuration Release --no-restore`; aunque no
  habrá cambios de código, confirman que la rama no ha acumulado regresiones.
- Ejecutar `dotnet test LocalAssistant.sln --configuration Release --no-build
  --no-restore` y `git diff --check`.
- Revisar el diff con la checklist del repositorio, verificando especialmente que no
  se hayan introducido rutas personales, secretos, comandos destructivos ni garantías
  de seguridad no demostrables.

## No objetivos

- No se cifra SQLite ni se introduce gestión, rotación o recuperación de claves.
- No se crean backups, restauraciones, comandos, endpoints, jobs ni configuración
  nueva.
- No se cambian la autenticación educativa, los scopes, la propiedad, la retención,
  los borrados ni el contrato OpenAPI.
- No se aplican ni se validan automáticamente ACLs o permisos de sistema operativo.
