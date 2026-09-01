# Plan de implementación: capacidades privadas del propietario y bootstrap sin API key

## Alcance

Implementar el diseño `2026-09-01-owner-private-capability-bootstrap-design.md` para
que todas las instalaciones de propietario único, nuevas y existentes, concedan las
capacidades privadas actuales mediante sesiones bearer. No se añaden endpoints de
administración de scopes ni permisos independientes por cliente.

## Pasos

1. **Esquema 4 de identidad de instalación**
   - Cambiar `InstallationIdentityStore.cs` para aceptar los esquemas 1, 2 y 3 como
     entrada heredada y persistir exclusivamente el esquema 4.
   - Hacer opcional el hash legado solo en la representación persistida de esquemas
     anteriores; eliminarlo de `InstallationIdentity` y del resultado de bootstrap.
   - Crear instalaciones nuevas sin API key ni hash asociado, preservando el
     identificador de instalación, propietario y fecha original durante la migración.
   - Mantener el reemplazo de archivo atómico y la idempotencia de `GetAsync`.

2. **Scopes efectivos del propietario**
   - Añadir `documents.search`, `documents.read`, `documents.content.search` y
     `reminders.write` al conjunto de scopes de propietario de instalaciones nuevas.
   - Incluir los mismos scopes durante la migración desde los esquemas 1, 2 y 3,
     sin duplicados y sin convertir `installation.owner` en comodín.
   - Conservar el modelo actual: `PrivateBearerAuthenticationHandler` emite los
     scopes del propietario para cualquier cliente activo; no se introducen scopes
     configurables por cliente.

3. **Eliminar configuración y salida obsoletas**
   - Quitar `AllowEducationalApiKeyMigration` de `PrivateClientOptions` y sus
     configuraciones en el host y las pruebas.
   - Ajustar `Program.cs` para que `--bootstrap-owner` muestre únicamente el
     propietario inicial y nunca solicite custodiar una API key.
   - Adaptar los dobles de autenticación al proyecto de pruebas para que posean su
     propio hash/credencial de fixture y no consuman datos de `InstallationIdentity`.

4. **Pruebas deterministas**
   - Actualizar `InstallationIdentityStoreTests` para verificar bootstrap de esquema
     4 sin API key, migración desde 1, 2 y 3, eliminación del hash, conservación de
     datos estables, scopes completos e idempotencia.
   - Actualizar los dobles y los tests de endpoint que construyen identidades de
     instalación.
   - Añadir integración HTTP con un bearer creado mediante el flujo real que pruebe
     búsqueda/lectura/búsqueda de contenido documental y confirmación de recordatorio.
     Las pruebas deben demostrar que las capacidades se heredan por cliente sin
     inyectar scopes artificiales.

5. **Documentación y cierre**
   - Actualizar README, ARCHITECTURE, SECURITY, OPERATIONS y ROADMAP para describir
     esquema 4, migración de scopes, ausencia de API key nueva y herencia temporal de
     capacidades por cliente.
   - Corregir el estado de fase 4: solo podrá declararse completada cuando las pruebas
     y la revisión de seguridad de este incremento estén cerradas.

## Verificación

1. `dotnet format LocalAssistant.sln --no-restore --verify-no-changes`
2. `dotnet build LocalAssistant.sln -c Release --no-restore`
3. `dotnet test LocalAssistant.sln -c Release --no-restore --logger "console;verbosity=minimal"`
4. Confirmar que no queda un proceso `testhost` tras la suite.
5. Ejecutar `git diff --check` y la revisión pre-PR obligatoria sobre el diff.

## No objetivos

- Gestión administrativa de scopes.
- Permisos diferenciados por cliente.
- Usuarios domésticos, invitados, JWT, OAuth o acceso remoto.
