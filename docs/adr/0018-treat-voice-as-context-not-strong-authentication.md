# ADR 0018: Tratar la voz como contexto y no como autenticación fuerte

- Estado: Aceptada
- Fecha: 2026-08-20

## Contexto

Wake word y reconocimiento de hablante pueden fallar por ruido, grabaciones, cambios
de voz o presencia de varias personas. Un satélite y una habitación suelen ser
compartidos. Confundir dispositivo autenticado con persona autenticada permitiría
acciones o respuestas sensibles con una prueba insuficiente.

## Decisión

La voz podrá aportar una identidad probable y otras señales contextuales, pero no
será la única prueba para acciones sensibles o administrativas. El contexto
distinguirá usuario confirmado, probable, desconocido, invitado o autenticación
insuficiente. Cuando el riesgo supere la confianza disponible, Jarvis solicitará
`step-up` mediante un canal personal adecuado antes de revelar datos o ejecutar.

La política de salida evaluará además habitación, dispositivo compartido,
sensibilidad y preferencias. Podrá resumir, negar la reproducción o enviar el
resultado a un dispositivo personal.

## Consecuencias

- Autenticar un satélite no autentica al hablante.
- No se dictarán PIN, secretos, tokens ni información privada detallada para superar
  una comprobación.
- Identificación de hablante podrá mejorar comodidad, pero sus errores no concederán
  permisos.
- Passkey, aplicación, biometría personal, PIN introducido o código temporal siguen
  siendo opciones futuras dependientes de la interfaz real.
- Las experiencias de voz sensibles tendrán más fricción de forma deliberada.
