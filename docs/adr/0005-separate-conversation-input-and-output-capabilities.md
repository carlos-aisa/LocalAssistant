# ADR 0005: Separar capacidades de entrada y salida

- Estado: Aceptada
- Fecha: 2026-08-17

## Contexto

El acceso por voz futuro se distribuirá por habitaciones. Un dispositivo pequeño
puede aportar micrófono y wake word sin disponer de un altavoz adecuado, mientras
un Google Nest Hub de la misma habitación puede reproducir TTS o mostrar contexto
mediante Google Cast sin proporcionar audio de entrada a LocalAssistant.

Modelar una conversación como perteneciente a un único dispositivo impediría esta
composición y mezclaría identidad de origen, contexto espacial y destino de la
respuesta.

## Decisión

Modelar entrada y salida como capacidades independientes. Una habitación podrá
asociar uno o varios dispositivos con capacidades distintas, y una conversación
mantendrá su propia identidad. El origen de un turno informará la selección de
salida, pero no la determinará de forma irreversible.

Esta decisión no introduce todavía entidades, registros ni campos opcionales en
los contratos actuales. Esos conceptos se añadirán con el primer pipeline de voz y
satélite que pueda utilizarlos y probarlos.

## Consecuencias

- Un satélite con micrófono podrá responder mediante un Nest Hub de su habitación.
- Un dispositivo podrá ofrecer entrada, salida o ambas capacidades.
- La selección de destino necesitará políticas, disponibilidad y fallback.
- Habitación, dispositivo y conversación tendrán ciclos de vida diferentes.
- La transferencia entre habitaciones podrá añadirse sin cambiar la identidad de
  la conversación.
- Autenticación, privacidad y estado operativo deberán evaluarse por dispositivo y
  por flujo de audio.
- El routing será más explícito y algo más complejo que asumir respuesta en el
  dispositivo de origen.
