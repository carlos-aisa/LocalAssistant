# Diseño: scopes de memoria para el propietario de instalación

## Objetivo

Hacer utilizables las notas de memoria personal por la identidad creada con el
bootstrap local. Esa identidad conserva actualmente solo `installation.owner`, mientras
que las rutas de memoria personal exigen `memory.personal.read` y
`memory.personal.write` de forma explícita.

El incremento corrige esa falta de capacidades sin introducir usuarios, gestión de
roles, concesiones remotas ni un comodín que ignore los scopes de cada recurso.

## Alcance

El formato persistido de la identidad de instalación evolucionará del esquema 1 al
esquema 2. Una identidad creada mediante bootstrap incluirá exactamente estos scopes:

- `installation.owner`
- `memory.personal.read`
- `memory.personal.write`

Al leer un estado válido del esquema 1, el almacén migrará una única vez al esquema 2:
conservará el identificador de instalación, el principal propietario, el hash SHA-256
de la API key y la fecha original; añadirá los dos scopes de memoria personal y
reescribirá el estado actualizado de forma atómica. Lecturas posteriores no añadirán
duplicados ni volverán a modificar el archivo.

El estado del esquema 2 seguirá exigiendo un conjunto de scopes no vacío, sin valores
vacíos ni duplicados. Los estados de esquema desconocido o inválido se rechazarán, como
hasta ahora, sin intentar repararlos.

## Seguridad y límites

- La migración no recupera ni registra la API key original; solo conserva su hash ya
  persistido.
- `installation.owner` no se convierte en un bypass de autorización. Los endpoints de
  memoria continúan comprobando sus scopes exactos.
- No se conceden `documents.search`, `documents.read`,
  `documents.content.search`, `reminders.write` ni capacidades futuras. Cada ampliación
  de permisos requerirá una decisión y una migración explícitas.
- La identidad configurada mediante `LocalAssistant:Identity` no cambia. Continúa
  definiendo sus scopes exclusivamente en configuración y no puede coexistir con la
  identidad de instalación.
- No se añade ningún endpoint para leer o modificar scopes, ni una nueva identidad,
  base de datos, proveedor o secreto.

## Pruebas y documentación

Las pruebas del almacén cubrirán el bootstrap nuevo, la migración determinista de un
estado de esquema 1, la conservación de identidad/hash/fecha, la ausencia de duplicados
y la estabilidad de una segunda lectura. Las pruebas HTTP crearán una identidad mediante
bootstrap y comprobarán que su API key puede crear y listar una nota personal cuando la
persistencia privada está activada.

README, arquitectura, seguridad y roadmap explicarán los scopes personales concedidos
por bootstrap y sus límites. OpenAPI no cambia: los requisitos de las rutas ya son los
scopes exactos implementados.

## Criterios de aceptación

- Una nueva instalación creada por bootstrap recibe los dos scopes de memoria personal
  además de `installation.owner`.
- Una instalación existente de esquema 1 se migra a esquema 2 sin cambiar propietario,
  hash de API key ni fecha de inicialización.
- La clave emitida por bootstrap puede usar `POST` y `GET /api/memories/personal` si la
  persistencia está activada.
- La migración no concede otros scopes ni permite que `installation.owner` sustituya una
  comprobación de scope concreta.
