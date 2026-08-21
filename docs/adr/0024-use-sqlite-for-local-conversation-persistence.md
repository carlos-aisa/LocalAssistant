# ADR 0024: Usar SQLite para la persistencia local de conversaciones

- Estado: Aceptada
- Fecha: 2026-08-21

## Contexto

El patrón medido en el núcleo actual es una conversación identificada por
`ConversationId`, con metadatos de propietario consultados antes de recuperar el
historial y mensajes añadidos en orden. El bloqueo de ejecución ya serializa los
turnos de una misma conversación. No existen todavía varios procesos, consultas
analíticas, sincronización entre dispositivos ni una necesidad de un servidor de base
de datos.

Persistir mediante JSON perdería restricciones, orden transaccional y una evolución
segura ante concurrencia. Incorporar EF Core no aporta una responsabilidad necesaria
para el primer vertical slice y añadir un servidor de base de datos sería
infraestructura especulativa.

## Decisión

La persistencia local de conversaciones y trazas usará SQLite en un único archivo,
situado por defecto en el directorio local de datos de la aplicación y configurable
solo mediante una ruta absoluta. El núcleo conservará contratos independientes de
SQLite; el adaptador y el esquema vivirán fuera de `LocalAssistant.Core`.

El primer adaptador persistirá únicamente conversaciones vinculadas a un principal
autenticado. Las conversaciones anónimas seguirán en memoria, públicas y efímeras.
Cada recuperación filtrará por propietario antes de devolver mensajes al
orquestador. Las inserciones de metadatos y mensajes usarán transacciones y un orden
estable. Retención, borrado selectivo y auditoría durable se implementarán como
incrementos explícitos sobre el mismo almacén.

SQLite no se considerará cifrado en reposo: los permisos del sistema operativo,
cifrado de disco, backup y restauración siguen siendo responsabilidades de despliegue
hasta que se diseñe una protección adicional verificable.

## Consecuencias

El proyecto obtiene un almacén local transaccional, portable y fácil de probar sin
Docker ni red. Deben definirse migraciones de esquema, límites de tamaño, retención,
concurrencia entre procesos, protección del archivo y recuperación ante corrupción
antes de declarar completa la memoria privada. PostgreSQL u otro almacén distribuido
solo se evaluarán cuando aparezcan sincronización, varios procesos o carga que lo
justifiquen.
