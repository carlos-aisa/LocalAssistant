# Autenticación privada mediante clientes registrados

## Objetivo

Reemplazar la API key educativa como acceso normal a interfaces privadas por clientes
registrados, credenciales duraderas protegidas y access tokens opacos temporales. La
instalación continúa teniendo un único propietario; no se introducen cuentas familiares,
invitados, JWT, OAuth, proveedor de identidad ni acceso remoto.

## Decisiones

- El cliente y el propietario son identidades distintas. El servidor determina siempre
  el propietario y sus capacidades.
- La credencial duradera del cliente y el access token de sesión son secretos distintos.
  El servidor guarda exclusivamente hashes SHA-256 comparados con tiempo constante.
- La credencial se muestra una vez, se rota de forma atómica y puede recuperarse entre
  ejecuciones mediante DPAPI en Windows. Si DPAPI no está disponible, el cliente exige
  introducirla manualmente y no escribe secretos en texto plano.
- El access token permanece solo en memoria del cliente y expira según `TimeProvider`.
- Revocar un cliente invalida de forma transaccional su credencial y todas sus sesiones.
  Rotar la credencial mantiene el cliente `Active`, pero invalida las sesiones anteriores.
- Los tokens bearer y los endpoints que los aceptan se limitan a loopback hasta disponer
  de TLS para clientes remotos. Loopback no protege frente a procesos locales maliciosos.
- El bootstrap de una instalación nueva es local, de un uso y solo está disponible si no
  existe ningún cliente. La API key educativa, desactivada por defecto, solo permite
  migrar instalaciones existentes en `Development` con opción explícita y loopback.

## Flujo

```text
Bootstrap local único o migración educativa controlada
→ pairing de un uso
→ cliente Active + credencial mostrada una vez
→ creación de sesión
→ access token temporal en memoria
→ API privada
```

Las operaciones administrativas de pairing, rotación y revocación requieren una
frontera administrativa resuelta por el servidor; una sesión cotidiana no basta.
Los secretos no pueden aparecer en URL, logs, auditoría ni mensajes de error.

## Contrato conceptual

Se expondrán operaciones para iniciar/completar pairing, abrir sesión, rotar y revocar.
Los DTOs, paths y códigos definitivos se documentarán en OpenAPI junto con la
implementación. No habrá endpoint de registro público.

## Pendientes documentales de fase 4

La memoria compartida será un ámbito explícito del hogar. La memoria de módulo se
aislará por hogar y módulo. Configuración, autorización y auditoría seguirán siendo
recursos administrativos separados, no una memoria genérica. La caché y procedencia
de evidencia externa pertenecen a la futura fase de meteorología.

## Pruebas

Pruebas deterministas cubrirán bootstrap, consumo y caducidad de pairing, hashes y
ausencia de secretos persistidos, sesión expirada, rotación, revocación en cascada,
restricción loopback, migración educativa y la frontera administrativa.
