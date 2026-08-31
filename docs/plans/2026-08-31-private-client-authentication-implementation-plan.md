# Plan de implementación: autenticación privada mediante clientes registrados

## Alcance

Implementar el diseño `2026-08-31-private-client-authentication-design.md` con SQLite
para el estado de clientes y sesiones, DPAPI en el cliente Windows y actualización de
contratos, documentación y pruebas. Quedan fuera JWT, OAuth, cuentas familiares,
invitados y acceso remoto.

## Incrementos

1. [x] **Base de seguridad y almacenamiento:** extender el almacenamiento de identidad de instalación con el bootstrap local de
   un solo uso y el estado de clientes; usar generación criptográfica, hashes y
   operaciones SQLite transaccionales.
2. [x] **Bootstrap y pairing local:** añadir servicios de pairing, clientes, credenciales y sesiones opacas con
   expiración basada en `TimeProvider`, rotación e invalidación en cascada.
3. [ ] **Sesiones bearer:** sustituir el handler normal de API key por autenticación bearer que resuelva
   principal, cliente y sesión; verificar propagación del principal y rechazar credenciales inválidas.
4. [ ] **Migración del cliente:** añadir la frontera administrativa y los endpoints HTTP loopback; actualizar
   `docs/api/openapi.yaml` con solicitudes, respuestas, errores y seguridad reales.
5. [ ] **Cierre de fase:** migrar `Chat.ps1` a pairing/sesión y almacenamiento DPAPI, con entrada manual si
   DPAPI no está disponible; nunca persistir tokens de acceso ni enviarlos fuera de loopback.
6. [ ] Actualizar arquitectura, seguridad, operaciones y roadmap: ámbitos de memoria
   sin administración genérica y evidencia externa situada en la fase 6 de meteorología.
7. [ ] Añadir pruebas unitarias e integración HTTP de estados, transacciones, revocación,
   expiración, loopback, migración y ausencia de secretos en estado y logs.

## Verificación

`dotnet format LocalAssistant.sln --no-restore --verify-no-changes`; `dotnet build
LocalAssistant.sln -c Release --no-restore`; `dotnet test LocalAssistant.sln -c
Release --no-restore`; validación de OpenAPI y revisión estructural del diff.
