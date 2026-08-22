# Diseño de fuente documental local configurable

## Objetivo

Definir la primera fuente documental local autorizada sin exponer todavía búsqueda,
lectura de contenido ni herramientas al modelo.

## Alcance

La aplicación registrará una única raíz documental de solo lectura mediante un
contrato del núcleo independiente del sistema de archivos. Su adaptador resolverá
por defecto la carpeta Documentos del sistema operativo. La clave de configuración
`LocalAssistant:DocumentSources:DocumentsRoot` podrá reemplazar ese valor.

El valor configurado deberá ser una ruta absoluta, existente y accesible como
directorio. Una configuración inválida impedirá el arranque con un error explícito.
No se aceptarán rutas relativas, no se explorará la carpeta durante el arranque y no
se expondrán archivos a la API ni al modelo.

## Fuera de alcance

- Varias raíces, administración por API o concesiones por principal o módulo.
- Búsqueda por nombre, extensión, ruta o metadatos.
- Lectura, extracción, indexación, RAG, watchers y escritura de archivos.
- Cambios en la política de egreso o en el contrato HTTP.

## Pruebas y documentación

Las pruebas verificarán la ruta configurada y los rechazos de rutas relativas,
inexistentes o no accesibles sin depender de la carpeta real del usuario. El README,
la arquitectura y el roadmap describirán la configuración y el límite de la fuente.

## Criterios de aceptación

- La raíz autorizada puede configurarse con una ruta absoluta válida.
- La aplicación no arranca con configuraciones no válidas.
- La raíz no concede por sí misma exploración ni lectura de archivos.
- Compilación Release, formato y pruebas completas correctos.
