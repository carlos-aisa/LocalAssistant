# LocalAssistant

LocalAssistant es un proyecto personal y educativo para comprender cómo se construye un
asistente de IA local: conversación, proveedores de lenguaje, herramientas,
seguridad, memoria, conectores y observabilidad.

La primera iteración es deliberadamente pequeña. Una API recibe un mensaje, un
proveedor fake determinista decide si responde directamente o solicita una
herramienta, el orquestador ejecuta el ciclo de tool calling y la API devuelve la
respuesta con una traza básica.

No necesita GPU, Docker, Ollama, acceso a Internet ni claves de API durante la
ejecución.

## Estado actual

Implementado:

- API HTTP en .NET 8.
- Contratos de conversación independientes del proveedor.
- Proveedor fake programado mediante secuencias explícitas.
- Registro cerrado de herramientas.
- Herramienta determinista de fecha y hora UTC basada en `TimeProvider`.
- Bucle explícito de tool calling con cancelación, timeouts y límite de iteraciones.
- Metadatos de impacto y confirmación de herramientas.
- Conversaciones en memoria.
- Logging estructurado sin contenido de conversación.
- Pruebas unitarias y de integración HTTP.

No implementado: Ollama, proveedores externos, persistencia, voz, wake word, RAG,
Home Assistant, MQTT, MCP, autenticación, interfaz gráfica ni ejecución de comandos.

## Arquitectura actual

```mermaid
flowchart LR
    Client[Cliente HTTP] --> Api[LocalAssistant.Api]
    Api --> Orchestrator[Orquestador explícito]
    Orchestrator --> Fake[Proveedor fake]
    Orchestrator --> Registry[Registro de herramientas]
    Registry --> Time[Herramienta de hora UTC]
    Orchestrator --> Memory[(Conversaciones en memoria)]
```

`LocalAssistant.Api` compone y expone la aplicación. `LocalAssistant.Core` contiene el dominio y
el ciclo de ejecución. `LocalAssistant.Tests` prueba ambos sin servicios externos.

## Requisitos

- SDK de .NET 8. El proyecto se creó con `8.0.202` y permite revisiones posteriores
  de .NET 8 mediante `global.json`.

Conviene utilizar la revisión de seguridad más reciente del SDK 8.x.

## Compilar y probar

Desde la raíz del repositorio:

```powershell
dotnet restore LocalAssistant.sln
dotnet build LocalAssistant.sln --configuration Release --no-restore
dotnet test LocalAssistant.sln --configuration Release --no-build
```

Los tests no usan el reloj real, red, Docker, Ollama ni ningún proveedor externo.

## Ejecutar la API

```powershell
dotnet run --project src/LocalAssistant.Api -- --urls http://localhost:5100
```

La comprobación de salud queda disponible en `http://localhost:5100/health`.

### Escenario 1: respuesta directa

En otra terminal de PowerShell:

```powershell
$body = @{ message = "Hola"; scenario = "direct" } | ConvertTo-Json
Invoke-RestMethod -Method Post `
  -Uri http://localhost:5100/api/conversations/messages `
  -ContentType application/json `
  -Body $body
```

El fake responderá `Fake response: Hola` en una iteración y sin herramientas.

### Escenario 2: herramienta de fecha y hora

```powershell
$body = @{ message = "¿Qué hora es?"; scenario = "time" } | ConvertTo-Json
Invoke-RestMethod -Method Post `
  -Uri http://localhost:5100/api/conversations/messages `
  -ContentType application/json `
  -Body $body
```

El fake solicitará `get_current_time`, recibirá su JSON y generará la respuesta
final en una segunda iteración. La respuesta incluye `conversationId`, `tools`,
`iterations`, `timings` y `error`.

El campo `scenario` solo existe para demostrar de forma reproducible el proveedor
fake. No pretende ser el mecanismo futuro de selección de modelos.

## Límites importantes

- Las conversaciones se pierden al reiniciar y no soportan coordinación entre
  varias instancias de la API.
- La aprobación de herramientas es un punto de extensión técnico, no identidad ni
  autorización completa.
- Los timeouts detienen la espera cooperativa; una implementación de herramienta
  que ignore el token podría seguir trabajando internamente.
- El fake demuestra el protocolo, no inteligencia ni comprensión del lenguaje.
- No existe todavía un `LocalAssistant.Worker`: se añadirá cuando haya una tarea de fondo
  concreta que lo justifique.
- El proyecto se distribuye bajo la licencia MIT.

Consulta [la arquitectura](docs/ARCHITECTURE.md), [la visión](docs/VISION.md),
[la seguridad](docs/SECURITY.md), [el roadmap](docs/ROADMAP.md),
[OpenAPI](docs/api/openapi.yaml) y [los estándares](docs/standards/README.md) para
continuar.

## Evolución prevista, no implementada

```mermaid
flowchart TB
    Channels[API, UI y voz] --> Router[Enrutador híbrido]
    Router --> Local[Modelos locales / Ollama]
    Router --> External[Proveedores externos opcionales]
    Router --> Tools[Herramientas y conectores]
    Tools --> HA[Home Assistant / MQTT]
    Tools --> MCP[APIs / MCP]
    Router --> Memory[Memoria y RAG]
    Worker[Worker de tareas largas] --> Memory
    Worker --> Tools
    Observability[Observabilidad] -.-> Router
    Observability -.-> Worker
```

Las cajas de este segundo diagrama son objetivos de evolución; no describen
componentes disponibles en la versión actual.
