# ADR 0011: Prohibir autoaprobación y mutación de la instancia activa

- Estado: Aceptada
- Fecha: 2026-08-20

## Contexto

`Controlled Self-Extension` permitirá que Jarvis ayude a crear skills, tools,
connectors, modules y capacidades de satélite. El código generado, sus tests y su
manifiesto proceden del mismo proceso no confiable. Permitir que ese proceso se
apruebe o modifique directamente el sistema que aplica sus límites eliminaría la
separación entre propuesta, evidencia y autoridad.

Los tipos de extensión tampoco tienen el mismo riesgo. Una skill declarativa, una
tool con efectos, un connector con red y secretos, un module con estado, una
capacidad física y un cambio del núcleo necesitan ciclos de revisión diferentes.

## Decisión

Jarvis podrá proponer y generar extensiones, pero nunca autoaprobarlas ni modificar,
instalar o desplegar directamente sobre su instancia activa. Todo trabajo ocurrirá
en repositorio, rama y entorno aislados y producirá artefactos versionados para una
revisión externa al agente generador.

Cada petición se clasificará con el mecanismo más pequeño suficiente. Generar,
integrar, instalar, activar y desplegar serán autorizaciones independientes ligadas
a un principal verificado. Una extensión ordinaria no podrá modificar políticas de
seguridad, elevar permisos ni usar el mecanismo de extensiones para cambiar el
núcleo. Los cambios del núcleo seguirán el flujo humano normal del producto.

Activación, monitorización, suspensión y rollback operarán sobre artefactos revisados
y compatibles. Compilar o superar tests, especialmente tests generados por el mismo
agente, no constituirá aprobación.

## Consecuencias

- La autoextensión será un flujo de desarrollo asistido, no automodificación en vivo.
- Habrá más pasos y confirmaciones antes de obtener una capacidad activa.
- Skills, tools, connectors, modules, satélites y core changes podrán aplicar
  políticas proporcionales sin convertir toda petición en módulo.
- La instancia activa, el motor de autorización y la auditoría deberán quedar fuera
  del alcance modificable de las extensiones.
- Instalación y activación requerirán versionado, compatibilidad, health checks,
  desactivación y rollback probados.
- El primer experimento será pequeño, reversible y de bajo riesgo; módulos completos
  y cambios del núcleo permanecerán posteriores o exploratorios.
