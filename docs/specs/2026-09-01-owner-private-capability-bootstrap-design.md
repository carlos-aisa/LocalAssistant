# Diseño: capacidades privadas del propietario y bootstrap sin API key

## Objetivo

Completar las capacidades de la instalación de propietario único sin conservar una
segunda credencial HTTP obsoleta. Los clientes privados bearer del propietario deben
poder buscar y leer documentos, buscar contenido documental y confirmar recordatorios.

## Decisión

El propietario único recibe durante el bootstrap los scopes de servidor:

- `documents.search`
- `documents.read`
- `documents.content.search`
- `reminders.write`

No se introduce una operación administrativa para editar scopes en esta etapa. El
servidor sigue siendo la única fuente de permisos y los clientes bearer no pueden
solicitarlos ni modificarlos.

El bootstrap de instalaciones nuevas deja de generar o mostrar una API key. El estado
de instalación pasa al esquema 4 y no contiene hash de API key ni un secreto asociado.
Los esquemas 1, 2 y 3 permiten leer el hash legado únicamente para migrar el estado;
el esquema 4 lo descarta y `InstallationIdentity` deja de exponerlo al código de
producción. Los dobles de autenticación de pruebas usan sus propios datos de prueba y
no dependen de esa propiedad.

Se elimina `AllowEducationalApiKeyMigration`, porque ya no altera ningún camino de
producción. Los dobles de API key permanecen exclusivamente en el proyecto de pruebas.

## Migración de instalaciones existentes

Al cargar una instalación con esquema 1, 2 o 3, el almacén la migra al esquema 4. La
migración añade los cuatro scopes definidos arriba, descarta `ApiKeySha256` y conserva
sin cambios el identificador de instalación, el propietario y la fecha de creación
original. Es idempotente: una instalación ya migrada no acumula scopes ni cambia sus
datos estables. La reescritura usa el mismo reemplazo atómico de archivo que el
bootstrap, por lo que no deja un estado parcial.

Esto garantiza que las instalaciones existentes reciben las mismas capacidades que
las nuevas y no continúan devolviendo `403` para documentos o recordatorios.

## Alcance temporal por cliente

En esta etapa los clientes no tienen permisos independientes. Cada sesión bearer de
un cliente activo hereda todas las capacidades del propietario de la instalación,
incluidos los cuatro scopes nuevos. La revocación y rotación siguen siendo por
cliente, pero no constituyen autorización diferenciada. La restricción de permisos por
cliente queda aplazada hasta la gestión doméstica de usuarios y capacidades.

## Decisión sustituida

Esta decisión sustituye exclusivamente la restricción sobre scopes adicionales de
`2026-08-29-bootstrap-personal-memory-scopes-design.md`. El documento histórico se
mantiene como registro de su contexto; la presente decisión define el alcance efectivo
de propietario único para la fase 4.

## Flujo

```text
--bootstrap-owner
  -> propietario con scopes privados completos
  -> --bootstrap-private-client
  -> credencial de cliente una sola vez
  -> sesión bearer temporal
  -> documentos, recordatorios y demás interfaces privadas en loopback
```

## Compatibilidad y errores

Las instalaciones existentes con un hash de API key se siguen leyendo solo durante la
migración, sin usar ese valor para autenticación HTTP. Tras reescribirlas al esquema 4,
el campo legado queda ausente. El bootstrap conserva su semántica de un único
propietario y responde como ya lo hace cuando la instalación existe.

## Pruebas y documentación

Las pruebas cubrirán los scopes nuevos del bootstrap y de la migración desde los
esquemas 1, 2 y 3, su idempotencia y la conservación de datos estables. También
cubrirán el acceso HTTP bearer real a documentos y recordatorios, la ausencia de API
key en el resultado y salida del bootstrap, y la herencia temporal de scopes por
cliente. README, seguridad, arquitectura y el contrato operativo describirán los
scopes efectivos y la eliminación de la opción inerte.
