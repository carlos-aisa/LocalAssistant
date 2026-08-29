# ADR 0027: Separar el perfil del asistente del historial conversacional

- Estado: Aceptada
- Fecha: 2026-08-30

## Contexto

El usuario puede asignar un nombre al asistente que debe mantenerse entre sesiones y
aplicar a todas las conversaciones de una instalación. El historial conversacional no
es el lugar adecuado para ese dato: puede ser efímero, pertenecer a un principal y
eliminarse por retención o borrado selectivo.

## Decisión

La instalación conserva un perfil estructurado independiente en
`assistant-profile.json`, dentro del mismo directorio privado configurado para el
estado de instalación. Su primer y único campo es `DisplayName`, cuyo valor por
defecto es `LocalAssistant`.

El orquestador lee el perfil antes de cada llamada al proveedor e incorpora un mensaje
de sistema solo en la solicitud del proveedor. No anexa ese mensaje al almacén de
conversaciones ni lo conserva en SQLite. Así, un cambio aprobado durante una llamada
de herramienta se aplica a la continuación de ese mismo turno.

La única modificación disponible ahora es la herramienta
`set_assistant_name`. Requiere `installation.owner` y confirmación exacta. No existe
un mapa genérico de preferencias ni extracción automática de hechos de conversación;
cada futuro campo tendrá contrato, autorización, validación y retención explícitos.

## Consecuencias

El nombre persiste para toda la instalación sin asociarse a una conversación ni a una
nota personal. El archivo contiene configuración privada y debe protegerse con los
mismos controles operativos que la identidad de instalación. Una restauración de
copias puede recuperar un nombre anterior. La solución no modela aún preferencias
genéricas, perfiles por usuario ni inferencia automática de parámetros.
