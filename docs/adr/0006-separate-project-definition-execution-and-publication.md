# ADR 0006: Separar definición, ejecución y publicación de proyectos

- Estado: Aceptada
- Fecha: 2026-08-19

## Contexto

La visión futura permite que una conversación evolucione desde una idea hasta una
especificación y, eventualmente, cambios de código. Una frase como «impleméntalo» es
ambigua: puede expresar intención de avanzar, pero no define por sí sola repositorio,
alcance, herramientas, agente, coste, destino de publicación ni efectos permitidos.

Si transcripción, estado del proyecto, ejecución y publicación compartiesen un único
ciclo, una inferencia conversacional podría convertirse accidentalmente en una
acción. También se acoplaría el dominio a un agente de programación o proveedor Git
concreto y sería difícil reanudar, revisar, probar y auditar el proceso.

## Decisión

Separar cuatro responsabilidades y ciclos de vida:

1. conversación y definición estructurada del proyecto;
2. especificación revisable y confirmación de decisiones;
3. ejecución de código dentro de un alcance aislado y aprobado;
4. publicación, integración o despliegue mediante autorizaciones independientes.

Texto y voz serán canales sobre el mismo estado estructurado, no su fuente única de
verdad. Los documentos derivados conservarán estados de revisión y procedencia. La
intención «impleméntalo» iniciará comprobaciones de preparación, presentación del
alcance, propuesta de plan y solicitud de autorización; no ejecutará inmediatamente
ni concederá permisos ilimitados.

Las aprobaciones estarán ligadas a principal, proyecto, repositorio, operación,
alcance y vigencia. Aprobar edición o ejecución no aprobará commit, publicación de
rama, pull request, despliegue ni acciones irreversibles.

Los futuros agentes de programación se conectarán mediante una frontera independiente
del proveedor y trabajarán únicamente dentro del sandbox y presupuesto autorizados.
Esta decisión no introduce ahora contratos, agentes, almacenamiento, procesos,
endpoints ni integración Git.

## Consecuencias

- Un proyecto podrá definirse de forma incremental, reanudarse y revisarse sin
  ejecutar código.
- Varios proyectos podrán mantener estado, documentos y permisos aislados.
- Las transiciones serán comprobables, auditables y ensayables primero con un agente
  simulado.
- Agentes locales, externos o especializados podrán sustituirse sin filtrar sus SDKs
  al dominio.
- La experiencia requerirá más estados visibles y confirmaciones que una orden única.
- Persistencia, identidad, autorización y gestión de artefactos deberán existir antes
  de una ejecución real segura.
- Un worker duradero podrá justificarse cuando un trabajo deba sobrevivir a una
  petición o reinicio, pero no se añade preventivamente.
- Quedan pospuestos el esquema definitivo, nombres de estados, agente, proveedor,
  sandbox, almacenamiento, worker, broker e integración Git concretos.
