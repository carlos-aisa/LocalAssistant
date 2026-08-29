# ADR 0025: Definir el ciclo de vida de almacenamiento privado

- Estado: Aceptada
- Fecha: 2026-08-21

## Contexto

SQLite ya conserva conversaciones autenticadas, pero la persistencia privada no se
completa solo con un archivo durable. Sin límites de retención, eliminación verificable
o separación de ámbitos, el historial se convertiría en memoria indefinida y difícil
de gobernar.

## Decisión

El primer modelo distingue estos ámbitos:

- Conversación anónima: pública, efímera y exclusivamente en memoria.
- Conversación autenticada: dato personal del principal propietario; se recupera solo
  tras verificar la propiedad y se retiene 30 días por defecto.
- Memoria compartida, de módulo y administrativa: no se almacenan todavía y no pueden
  reutilizar la tabla de conversaciones como sustituto.
- Auditoría de herramientas: conserva solo metadatos técnicos, nunca mensajes,
  argumentos ni resultados; tendrá almacenamiento y retención independientes.

La retención será configurable por instalación, con un máximo explícito documentado
cuando se exponga la configuración. La eliminación selectiva será transaccional:
eliminará una conversación autenticada y todos sus mensajes, comprobará propietario
antes de actuar y no afectará a otras conversaciones ni a auditoría. La operación HTTP
`DELETE /api/conversations/{conversationId}` requiere principal autenticado y una
cabecera de confirmación visible, `X-LocalAssistant-Confirm-Delete: true`, enviada
exactamente una vez. No habilita un borrado remoto genérico.

Los backups se consideran copias del mismo dato privado: no amplían acceso ni
retención, deben protegerse con controles del despliegue y su restauración debe
preservar propiedad y caducidad. SQLite no aporta cifrado en reposo.

## Consecuencias

La expiración y el borrado selectivo de conversaciones ya se aplican antes de ampliar
la persistencia privada. La política de 30 días es un valor inicial revisable, no una
autorización para conservar datos de forma indefinida. Cuentas multiusuario, datos
compartidos y auditoría durable requerirán sus propios contratos y controles.
