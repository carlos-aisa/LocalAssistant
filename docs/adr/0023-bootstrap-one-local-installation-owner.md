# ADR 0023: Inicializar un único propietario mediante un comando local

- Estado: Aceptada
- Fecha: 2026-08-21

## Contexto

La API key configurada permite experimentar con un principal, pero deja al operador
la creación manual de una clave y no identifica una instalación. Un endpoint HTTP de
bootstrap permitiría que el primer cliente de la red reclamase el propietario. Aún no
existen usuarios persistentes, un proveedor de identidad, recuperación administrativa
ni datos privados persistentes que justifiquen introducirlos.

## Decisión

La API ofrece el comando local `--bootstrap-owner`, que no inicia el servidor HTTP.
Genera un identificador de instalación, un único principal propietario y una API key
aleatoria. Muestra la clave una sola vez en la consola y persiste únicamente su hash
SHA-256, junto con los metadatos mínimos, `installation.owner`,
`memory.personal.read` y `memory.personal.write`, en
`LocalApplicationData/LocalAssistant/installation-identity.json` por defecto. La ruta
puede configurarse para despliegues y pruebas, pero debe ser absoluta.

Los archivos creados antes de esos scopes usan el esquema 1. Al leer un archivo válido
de ese esquema, el almacén conserva identidad, hash y fecha, añade solo los dos
scopes de memoria y publica el esquema 2 de forma atómica. `installation.owner` no es
un permiso comodín y esta migración no concede documentos, recordatorios ni
capacidades futuras.

La creación usa publicación atómica y una ejecución posterior se rechaza. El servidor
usa esa identidad cuando no está activa la configuración educativa de API key; si se
encuentran ambas fuentes, falla al arrancar para no elegir implícitamente un
propietario. El archivo se valida antes de aceptar peticiones y no se exponen sus
errores ni la clave mediante HTTP o logs.

## Consecuencias

La instalación ya puede tener un propietario estable entre reinicios sin abrir una
superficie de autoalta remota. La protección depende todavía del usuario y permisos
del sistema operativo que custodian el directorio local; no sustituye cifrado en
reposo, HTTPS, recuperación, revocación, cuentas domésticas ni autenticación apta
para exposición pública. Borrar el estado equivale a una reinstalación y requerirá un
flujo explícito de recuperación cuando haya datos privados.
