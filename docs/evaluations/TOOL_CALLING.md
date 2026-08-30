# Evaluación de tool calling con Ollama

Esta evaluación manual mide el comportamiento de herramientas con un modelo Ollama
local. No forma parte de CI porque depende de Ollama, del modelo instalado y del
hardware local.

## Casos y criterio

`scripts/Evaluate-OllamaToolCalling.ps1` ejecuta ocho casos independientes:

- tres preguntas en español e inglés que requieren la hora UTC actual;
- dos conversiones de temperatura que requieren `convert_temperature`;
- una explicación de UTC que no requiere consultar la hora;
- una explicación de conversión de temperatura que no requiere calcularla;
- una petición que menciona literalmente `get_current_time`, pero no debe ejecutar
  ninguna herramienta.

Cada caso se repite tres veces por defecto. Las preguntas de fecha u hora actual no
dependen de que el modelo solicite una herramienta: el servidor añade antes de la
llamada al proveedor la traza exitosa `authoritative-current-time` con el nombre
`get_current_time`. El evaluador exige esa traza, aunque el modelo responda de forma
directa. Una conversión de temperatura solo supera la evaluación cuando existe una
única traza correcta con el nombre esperado y una respuesta final después de al
menos dos iteraciones. Un caso explicativo exige cero trazas y una iteración. Todos
los casos requieren además ausencia de error de orquestación.

El informe JSON incluye identificadores de caso, decisiones, iteraciones y tiempos.
No conserva prompts ni respuestas. La etiqueta `-Model` debe coincidir manualmente
con el modelo configurado en la API; la versión actual no expone esa configuración
mediante HTTP.

## Ejecución

Arranca la API en una terminal:

```powershell
$env:LocalAssistant__Ollama__Model = "qwen3:1.7b"
dotnet run --configuration Release --project src/LocalAssistant.Api -- `
  --urls http://localhost:5100
```

Ejecuta la evaluación desde otra terminal:

```powershell
powershell -NoProfile -ExecutionPolicy Bypass `
  -File .\scripts\Evaluate-OllamaToolCalling.ps1 `
  -Model "qwen3:1.7b" `
  -Runs 3
```

`-ExecutionPolicy Bypass` solo se aplica a ese proceso y no modifica la política
del sistema. El informe se escribe bajo `artifacts/`, que está excluido de Git. El
script devuelve código de salida `1` si falla algún caso, lo que permite comparar
modelos o configuraciones con el mismo conjunto.

## Resultado observado

El siguiente resultado es la línea base anterior a la evaluación multiherramienta.
No valida todavía la selección de `convert_temperature`; se actualizará al ejecutar
el nuevo conjunto de ocho casos con un modelo y hardware identificados.

Evaluación realizada el 18 de agosto de 2026 con Ollama `0.32.14`,
`qwen3:1.7b`, `Think: false`, ventana de contexto 4096, CPU y aproximadamente
8 GB de RAM:

| Grupo | Aciertos | Total |
| --- | ---: | ---: |
| Herramienta necesaria | 9 | 9 |
| Herramienta innecesaria | 6 | 6 |
| Total | 15 | 15 |

La latencia media fue 11,9 segundos, la mediana 9,7 segundos y el rango fue de
2,8 a 31,2 segundos. El máximo corresponde al primer caso después de cargar el
modelo.

El 100 % solo describe esta muestra pequeña y este entorno. No demuestra calidad
semántica general, robustez frente a reformulaciones, argumentos complejos,
conversaciones largas ni resistencia a entradas adversarias. La segunda herramienta
introduce la primera prueba de selección entre herramientas, pero no sustituye una
evaluación más amplia.
