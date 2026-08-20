# ADR 0017: Combinar roles, capacidades, contexto y riesgo para autorizar

- Estado: Aceptada
- Fecha: 2026-08-20

## Contexto

Un hogar incluye propietario, adultos, menores, invitados, dispositivos y servicios.
Un rol general no expresa propiedad del dato, habitación, canal compartido, confianza
de autenticación ni riesgo de la operación. Delegar la decisión al LLM permitiría que
contenido no confiable alterase permisos.

## Decisión

La autorización se resolverá fuera del modelo mediante una política determinista que
combine rol provisional, capacidades específicas, concesiones y denegaciones,
propiedad y ámbito del recurso, contexto de dispositivo y habitación, riesgo, coste,
método de autenticación y confirmación necesaria.

Los roles aportarán valores iniciales, no permisos ilimitados. Lectura, modificación,
aprobación y ejecución serán capacidades diferentes. Los módulos declararán las
capacidades que utilizan, pero no podrán concederlas, elevarlas ni modificar el motor
de políticas. El LLM recibirá únicamente herramientas y contexto ya autorizados.

## Consecuencias

- Administrar no elimina confirmaciones, privacidad, auditoría ni protección de
  secretos.
- Un adulto solo invitará con capacidad explícita y un menor no gestionará invitados.
- Cada vertical slice deberá definir sus recursos, capacidades y riesgo observable.
- La decisión tendrá más entradas que un RBAC simple, pero podrá explicar por qué
  permite, deniega, exige `step-up` o confirmación.
- No se fija todavía catálogo definitivo, esquema de persistencia ni producto de
  identidad.
