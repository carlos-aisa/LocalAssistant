# ADR 0036: Conversación textual y TTS neuronal local tras adaptadores con selección explícita

## Estado

Aceptada.

## Contexto

El [ADR 0034](0034-keep-terminal-client-independent-and-own-local-output.md) mantiene el
cliente terminal independiente y sitúa la síntesis, el buffering y la reproducción en el
plano local de salida, con el orquestador limitado a texto. El incremento 7 de la Fase 5
implementó ese plano con SAPI (`System.Speech`) en Windows.

Se plantea evolucionar la salida hablada hacia un proveedor neuronal local. El candidato
actual es Chatterbox Multilingual (~500M parámetros), que requiere Python, un modelo
descargado y, previsiblemente, GPU. El equipo objetivo es una RTX 3060 Ti de 8 GB que ya
ejecuta Ollama con `qwen3.5:9b`. Esa adopción todavía necesita validación técnica y
operativa (calidad, latencia, VRAM, convivencia, recuperación) y **no es irreversible**.

Antes de esa validación conviene fijar las decisiones estructurales que no dependen del
motor concreto y que evitan que un experimento se presente como arquitectura consolidada:
cómo se transporta el audio, dónde se ejecuta el modelo y cómo se selecciona el proveedor.

## Decisión

1. **La API conversacional permanece estrictamente textual y el audio usa una frontera
   separada.** Ningún endpoint de conversación transporta audio ni audio Base64. Cuando
   exista transporte de audio (canal de voz de un dispositivo, satélites) será una
   frontera propia, no el contrato textual. Refuerza el ADR 0034.

2. **Los motores neuronales locales se ejecutan como procesos o servicios aislados
   detrás de adaptadores.** El modelo no vive en el proceso .NET. Un servicio local
   independiente carga el modelo una sola vez, expone una frontera local acotada
   (conceptualmente `GET /health`, `GET /voices`, `POST /synthesize`) y devuelve audio en
   un formato conocido, inicialmente WAV completo. Un adaptador .NET
   (`ChatterboxSpeechSynthesizer` u otro) lo consume detrás de `ISpeechSynthesizer` e
   `ISpeechVoiceCatalog`, sin ejecutar Python, descargar modelos ni gestionar drivers.
   El endpoint se limita a loopback y recibe solo el texto ya autorizado, el idioma, un
   identificador lógico de voz y parámetros acotados.

3. **El proveedor de TTS se selecciona de forma explícita y diagnosticable, con
   capacidades declaradas y fallback controlado.** Configuración conceptual
   `SpokenOutput.Provider = Sapi | Chatterbox | None` y
   `SpokenOutput.FallbackProvider = Sapi | None` (nombres no definitivos). Al seleccionar
   un proveedor se comprueba configuración, se hace health check, se usa el adaptador si
   responde y, si no, se informa de forma comprensible y se aplica la política de fallback
   (SAPI o degradación a texto). No hay cambio de proveedor silencioso. Cada proveedor
   declara sus capacidades (selección de voz e idioma, velocidad, volumen, streaming, voz
   de referencia, cancelación); un adaptador no ignora en silencio una preferencia que no
   soporta ni se mapean arbitrariamente los rangos de SAPI a parámetros neuronales.

El motor neuronal concreto queda **sin decidir** hasta completar una evaluación acotada
fuera del producto. SAPI permanece como implementación real y fallback de bajo coste.

## Consecuencias

- El cierre de la Fase 5 (incremento 8) no depende del proveedor neuronal.
- El contrato de síntesis deberá incorporar idioma explícito y capacidades por proveedor;
  hoy no los representa.
- Las voces neuronales serán perfiles lógicos autorizados resueltos por el servicio; el
  cliente nunca envía rutas, archivos ni audio de referencia.
- El mismo adaptador y frontera se reutilizan en el canal de voz de un dispositivo
  (Fase 10) y en satélites (Fases 13–16).
- La seguridad del servicio (loopback, límites, cadena de suministro, muestras de voz,
  degradación) se detalla en [SECURITY.md](../SECURITY.md).
- Si la evaluación descarta Chatterbox, estas tres decisiones siguen siendo válidas para
  cualquier otro motor neuronal local.
