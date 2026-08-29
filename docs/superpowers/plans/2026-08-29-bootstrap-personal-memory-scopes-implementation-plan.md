# Plan de implementación: scopes de memoria del bootstrap

## Alcance y compatibilidad

Este plan implementa la especificación
`docs/superpowers/specs/2026-08-29-bootstrap-personal-memory-scopes-design.md` dentro
de la PR de notas de memoria personal. Solo evoluciona el estado de identidad de
instalación desde el esquema 1 al 2; no añade gestión de permisos ni cambia la
identidad configurada mediante `LocalAssistant:Identity`.

## 1. Versionar y migrar el estado de instalación

**Archivo:** `src/LocalAssistant.Api/Security/InstallationIdentityStore.cs`.

- Sustituir el valor de esquema actual por `2` y declarar los dos scopes personales
  como constantes explícitas junto a `installation.owner`.
- Hacer que `BootstrapAsync` persista directamente un estado de esquema 2 que contenga
  exactamente los tres scopes aprobados.
- Separar la validación de un estado leído de la construcción de la identidad pública:
  aceptar únicamente esquema 1 y esquema 2 válidos; seguir rechazando datos
  incompletos, scopes vacíos o duplicados y números de esquema desconocidos.
- Cuando `GetAsync` lea un esquema 1 válido, crear el equivalente de esquema 2 con los
  mismos identificadores, hash y fecha, y añadir los dos scopes de memoria sin
  duplicados. Guardarlo mediante archivo temporal y reemplazo atómico antes de devolver
  la identidad migrada. Si el archivo no existe, devolver `null` como hoy.
- Una lectura de esquema 2 no reescribirá el archivo. La migración no registrará ni
  expondrá hashes, API keys o scopes más allá de los claims ya entregados al
  autenticador.

## 2. Demostrar la migración y el acceso real por bootstrap

**Archivos:** actualizar
`tests/LocalAssistant.Tests/Api/InstallationIdentityStoreTests.cs` y
`tests/LocalAssistant.Tests/Api/PersonalMemoryEndpointTests.cs`.

- Ampliar la prueba de bootstrap para comprobar los dos scopes personales y el esquema
  2 persistido, manteniendo la comprobación de que la API key no aparece en disco.
- Añadir una prueba de estado de esquema 1 escrito manualmente: la primera lectura debe
  preservar instalación, propietario, hash y fecha, añadir exactamente los scopes
  nuevos y persistir esquema 2; una segunda lectura no modificará de nuevo el archivo.
- Mantener la prueba de rechazo de estado inválido y añadir cobertura para un esquema
  no reconocido si no queda cubierta por ella.
- Añadir una prueba HTTP que use una identidad creada por `BootstrapAsync`, active la
  persistencia privada y emplee la API key devuelta para crear y listar una nota. No se
  usará `LocalAssistant:Identity`, ya que debe seguir siendo incompatible con el
  bootstrap.

## 3. Sincronizar documentación y contrato operativo

**Archivos:** actualizar `README.md`, `docs/ARCHITECTURE.md`, `docs/SECURITY.md`,
`docs/ROADMAP.md` y `docs/adr/0023-bootstrap-one-local-installation-owner.md`.

- Documentar que el bootstrap concede los dos scopes de memoria personal de manera
  explícita y que los estados existentes se migran localmente al leerlos.
- Reiterar que `installation.owner` no evita la comprobación de scopes ni concede
  documentos, recordatorios o futuros permisos.
- Corregir el ADR 0023 para que describa el conjunto inicial de scopes realmente
  persistido y la migración compatible de instalaciones previas; no se crea un ADR
  nuevo porque concreta la decisión de bootstrap ya aceptada.
- Marcar el alcance de la migración de identidad como completado solo tras implementar
  y probarlo. No modificar `docs/api/openapi.yaml`: sus rutas y scopes no cambian.

## 4. Verificación y revisión

- Ejecutar `dotnet format LocalAssistant.sln --verify-no-changes --no-restore`.
- Ejecutar `dotnet build LocalAssistant.sln --configuration Release --no-restore`.
- Ejecutar `dotnet test LocalAssistant.sln --configuration Release --no-build --no-restore`.
- Revisar el diff frente a `origin/main`, confirmando la preservación de hash e
  identidad, migración idempotente, reemplazo atómico, ausencia de permisos no
  aprobados y acceso HTTP mediante bootstrap.
- Actualizar la PR #32 con el commit de corrección y repetir la revisión pre-PR antes
  de solicitar su integración.

## No objetivos

- No se crea UI, endpoint, fichero de configuración ni API de administración de
  scopes.
- No se concede un permiso comodín a `installation.owner`.
- No se migra ni modifica la identidad configurada, SQLite, conversaciones, documentos
  o recordatorios.
