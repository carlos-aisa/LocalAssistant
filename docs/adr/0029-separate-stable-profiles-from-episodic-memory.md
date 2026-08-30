# ADR 0029: Separar perfiles estables de la memoria episódica

- Estado: Aceptada
- Fecha: 2026-08-30

## Decisión

El nombre preferido de cada principal y la ubicación/zona horaria del hogar se
guardan en perfiles estructurados independientes, dentro del directorio privado de
instalación. No se infieren de conversaciones ni se recuperan mediante búsqueda.
El orquestador los añade como contexto transitorio únicamente después de comprobar
el principal y los scopes correspondientes.

## Consecuencias

El nombre del asistente, el perfil personal y el perfil doméstico no se confunden.
Los perfiles persisten entre reinicios, mientras que una nota personal y una
conversación conservan su propia retención. Varios usuarios y hogares son evolución
futura; voz y reconocimiento de hablante no se implementan aquí.
