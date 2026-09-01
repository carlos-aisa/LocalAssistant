# Diseño de fase 5: cliente terminal y salida hablada local

## Estado y alcance

Diseño aceptado para orientar la fase 5. No describe funcionalidad implementada ni
añade por sí mismo proyectos, dependencias, endpoints o contratos OpenAPI.

La entrega será un cliente .NET de Windows que se conecta a una API LocalAssistant ya
en ejecución en loopback. `Chat.ps1` sigue siendo el cliente de diagnóstico y fallback.
No se incluye lanzamiento de la API, STT, captura de audio, transmisión multimedia ni
cancelación fiable de turnos.

## Base existente

`Chat.ps1` ya valida loopback, comprueba health, hace pairing y abre sesiones bearer.
Protege la credencial de cliente con DPAPI cuando puede, mantiene el token solo en
memoria y persiste la credencial después de una sesión válida. Envía mensajes, conserva
el `ConversationId` durante la ejecución, muestra errores, resuelve confirmaciones y
solicita completion al empezar una conversación nueva o salir.

La API actual ofrece sesiones, pairing administrativo, rotación y revocación con
desafío, mensajes, decisiones de confirmación, completion y borrado. Un mensaje con
`ConversationId` continúa una conversación existente solo si pertenece al principal;
un identificador inexistente o ajeno produce el mismo `404`.

No existen todavía listado de conversaciones, lectura de historial, refresh token ni
cancelación duradera de un turno. El orquestador persiste el mensaje del usuario antes
de obtener la respuesta del proveedor. Por ello un resultado de red incierto no se
puede repetir sin riesgo de duplicación.

## Arquitectura propuesta

```text
Cliente .NET independiente
  -> HTTP textual loopback -> API pública -> ConversationOrchestrator
  <- respuesta textual / confirmación / error <-

Cliente: síntesis local -> reproducción local
```

El cliente no referencia proyectos del servidor ni lee estado interno. Separa:

- **Conversación y control:** health, sesión bearer, mensaje, confirmación, completion,
  identificador de conversación y errores.
- **Salida:** síntesis, reproductor, mute, stop y repeat.

Solo la respuesta textual final entra en salida. No habrá audio Base64, TTS ni un
transporte multimedia en el servidor durante esta fase.

## Credenciales, recuperación y privacidad

La credencial registrada se guarda con DPAPI de usuario actual solo después de abrir
una sesión válida. Si no puede protegerse, se pide manualmente y no se escribe. El
bearer y los desafíos nunca se persisten. El estado local podrá recordar el último
`ConversationId`, protegido como dato privado junto a la credencial si se persiste.

Un bearer caducado o revocado se recupera creando una nueva sesión con la credencial
duradera. Solo se reintenta una petición si el `401` de autenticación prueba que el
endpoint no se ejecutó. Después de un timeout, cancelación de HTTP o desconexión tras
enviar un turno, se informa de estado incierto y el usuario decide si continúa o crea
una conversación nueva.

El cliente no registra mensajes, respuestas, secretos, cabeceras bearer, desafíos ni
argumentos de herramientas por defecto. Los artefactos temporales de audio se limitan
a la sesión y se borran al finalizar o fallar.

## Contratos futuros necesarios para reanudación visible

El ID local permite reanudar, pero un selector requiere contratos aún no implementados:

- listado paginado de conversaciones del principal, ordenado por actividad, con título
  y extracto limitados;
- lectura paginada de historial de una conversación propia;
- `404` indistinguible para conversación inexistente o ajena y ausencia de metadatos
  antes de comprobar propietario.

No se añade ningún path a OpenAPI hasta implementar esos recursos, DTOs, autorización
y pruebas HTTP. Completion no significa cierre; solo solicita indexación inmediata.

## Estados y controles

El estado visible mínimo será `Disconnected`, `Connecting`, `Authenticating`, `Ready`,
`WaitingForTurn`, `WaitingForConfirmation`, `PlayingVoice`, `RecoverableError` y
`BlockingError`. Una TUI debe derivar sus indicadores de estas transiciones, no de
temporizadores decorativos. Se ofrecerá texto plano cuando la consola sea redirigida,
no interactiva o no permita un redibujado fiable; se respetará movimiento reducido.

`mute` evita nueva salida audible, `stop` cancela reproducción ya local y `repeat`
vuelve a reproducir la última respuesta textual disponible. Ninguno cancela el turno
del servidor. La cancelación HTTP puede dejar un turno incompleto o con respuesta
persistida y se comunicará como resultado incierto.

## Incrementos y validación

1. Cliente textual: loopback, health, sesión en memoria, mensajes, ID y errores fake
   u Ollama.
2. Operación segura: credencial DPAPI/manual, pairing, renovación, comandos,
   confirmaciones, logs sin secretos y política de reintentos.
3. Reanudación: último ID y, tras ampliar la API, selector e historial propio.
4. Modelo de estado: transiciones, cierre, recuperación y resultados inciertos.
5. TUI: historial, entrada, estados reales, confirmación, accesibilidad y degradación.
6. Salida simulada: contratos de síntesis/reproductor/coordinador y pruebas
   deterministas sin motor.
7. TTS evaluado: voz, velocidad, volumen, mute, stop, repeat y degradación textual.
8. Operación Windows: publicación, configuración, diagnóstico, logs y smoke tests.

Cada incremento deberá demostrar su flujo con pruebas deterministas. La elección de
Spectre.Console, un motor de síntesis o un proveedor se pospone a una evaluación con
criterios de accesibilidad, cancelación, privacidad, instalación y degradación.
