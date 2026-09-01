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
persistido admite el hash legado de instalaciones anteriores solo para poder leerlas y
migrarlas; una instalación nueva no crea ese hash ni un secreto asociado.

Se elimina `AllowEducationalApiKeyMigration`, porque ya no altera ningún camino de
producción. Los dobles de API key permanecen exclusivamente en el proyecto de pruebas.

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

Las instalaciones existentes con un hash de API key se siguen leyendo sin usar ese
valor para autenticación HTTP. Las instalaciones nuevas tienen el campo legado ausente.
El bootstrap conserva su semántica de un único propietario y responde como ya lo hace
cuando la instalación existe.

## Pruebas y documentación

Las pruebas cubrirán los scopes nuevos del bootstrap, el acceso HTTP bearer real a
documentos y recordatorios, y la ausencia de API key en el resultado y salida del
bootstrap. También comprobarán que el estado legado sigue pudiendo cargarse. README,
seguridad, arquitectura y el contrato operativo describirán los scopes efectivos y la
eliminación de la opción inerte.
