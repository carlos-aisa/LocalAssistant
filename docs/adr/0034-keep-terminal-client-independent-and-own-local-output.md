# ADR 0034: Mantener el cliente terminal independiente y la salida en el dispositivo local

## Estado

Aceptada.

## Contexto

La fase 5 necesita un cliente terminal .NET que haga utilizable la API privada sin
acoplar la interfaz a proyectos, almacenamiento o autenticación internos. También
necesita salida hablada incremental, pero `ConversationOrchestrator` ya delimita la
respuesta como texto y la arquitectura reserva un plano multimedia separado.

Incluir audio en el contrato conversacional, compartir SQLite o reutilizar servicios
internos desde el cliente debilitaría esas fronteras, haría más difícil probar el
canal real y adelantaría decisiones de transporte o proveedor que aún carecen de un
caso validado.

## Decisión

El cliente terminal será un proceso independiente que depende solo de la API HTTP
pública en loopback. No referenciará proyectos del servidor ni accederá directamente
a SQLite, identidad, secretos o contratos internos.

El plano de conversación y control seguirá intercambiando texto, sesiones, mensajes,
confirmaciones y estados HTTP. La síntesis, el buffering, la reproducción, silenciar,
detener y repetir serán responsabilidades locales del plano de salida del cliente. El
orquestador no generará audio ni incluirá audio Base64 en sus respuestas.

## Consecuencias

- Las pruebas del cliente ejercitarán HTTP real o dobles de dicho límite, no el núcleo
  ni la infraestructura directamente.
- Las credenciales de cliente se protegen localmente; el bearer temporal no se
  persiste.
- Los contratos necesarios para listar o leer conversaciones se añadirán solo cuando
  existan comportamiento, autorización por propietario y pruebas HTTP reales.
- La selección de TUI, motor de TTS, transporte multimedia y posible lanzador de la
  API sigue siendo una decisión de implementación posterior.
