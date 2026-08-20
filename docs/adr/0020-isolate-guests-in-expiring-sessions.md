# ADR 0020: Aislar invitados en sesiones temporales y revocables

- Estado: Aceptada
- Fecha: 2026-08-20

## Contexto

Una persona invitada puede usar texto, voz o un dispositivo compartido sin necesitar
una cuenta doméstica permanente. El autoalta o la herencia de permisos de la
habitación expondrían memoria y herramientas del hogar. Una sesión sin caducidad se
convertiría de hecho en una cuenta difícil de gobernar.

## Decisión

Los invitados utilizarán sesiones efímeras, aisladas, con denegación por defecto,
caducidad y revocación inmediata. Solo el propietario o un adulto con la capacidad
de invitar podrá iniciarlas. La concesión declarará anfitrión, duración, dispositivos
o habitaciones, capacidades, proveedor, límites de uso y persistencia. Un menor no
podrá crear invitados y una petición verbal desconocida no dará de alta una sesión.

Activar una sesión en una habitación no afectará a las demás. No se usará memoria
personal o doméstica ni se creará perfil persistente salvo decisión explícita y
autorizada.

## Consecuencias

- Consultas generales y tutor de inglés sin perfil podrán ofrecerse con poco estado.
- Herramientas domésticas, documentos, proyectos, compras y autoextensión quedarán
  denegados por defecto.
- El sistema necesitará expiración, revocación, cuota y aislamiento verificables
  antes de habilitar invitados reales.
- QR, enlace, código de un solo uso o aplicación siguen siendo mecanismos futuros.
- La auditoría conservará metadatos proporcionales de invitación y revocación, no la
  conversación completa.
