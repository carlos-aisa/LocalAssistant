# ADR 0031: Distinguir identidad de dispositivo, sesión y hablante

- Estado: Aceptada
- Fecha: 2026-08-30

## Decisión

Un dispositivo o satélite registrado identifica el equipo, no a quien habla. La
sesión puede tener un principal autenticado; cada turno futuro podrá registrar actor,
método de identificación y confianza. Un hablante será confirmado, probable,
desconocido o invitado según la evidencia.

## Consecuencias

La diarización separa voces pero no autentica personas. El reconocimiento de hablante
solo podrá mejorar comodidad; datos personales y acciones sensibles exigirán una
identidad humana autenticada y, cuando corresponda, un canal personal reforzado.
Una conversación de voz puede contener varios hablantes y no heredará por ello el
perfil personal del propietario de la sesión.
