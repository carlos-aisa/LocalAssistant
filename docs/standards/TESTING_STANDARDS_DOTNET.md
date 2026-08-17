# Estándares de testing .NET

## Principios

- xUnit y `dotnet test` forman la base actual.
- Probar comportamiento observable, no detalles internos ni el framework.
- Cada test debe ser determinista, repetible, independiente y legible.
- El código generado con decisiones, reglas o tratamiento de errores requiere tests.
- No añadir Moq, NSubstitute o FluentAssertions si los dobles simples y asserts de
  xUnit expresan mejor el escenario.

## Categorías

- **Unitarias:** orquestación, políticas, validación, transformaciones y errores en
  aislamiento.
- **Integración:** composición DI, serialización y endpoints mediante
  `WebApplicationFactory` y HTTP real en proceso.
- **End-to-end:** se añadirán solo cuando existan servicios o canales reales y no
  deben duplicar las categorías anteriores.

La estructura de tests debe reflejar los módulos de producción y los nombres deben
describir el resultado esperado.

## Aislamiento

- Inyectar `TimeProvider` para cualquier comportamiento temporal.
- No depender de red, Ollama, GPU, Docker, variables personales ni orden de ejecución
  en la suite predeterminada.
- Programar el proveedor fake mediante pasos explícitos; no inferir escenarios desde
  palabras accidentales del prompt.
- Los fallos de proveedor, herramienta, cancelación, timeout y límites deben poder
  reproducirse mediante dobles controlados.

## Persistencia futura

Si se adopta EF Core:

- No usar `Microsoft.EntityFrameworkCore.InMemory` como prueba relacional.
- Usar SQLite con conexión abierta o PostgreSQL cuando importe la paridad.
- Probar migraciones, constraints, consultas e aislamiento de datos.
- Incorporar entonces una guía EF específica adaptada desde `PROMPTS`.

## Calidad y CI

- La suite debe compilar sin warnings y ejecutarse en GitHub Actions.
- Un fallo de test debe fallar CI.
- La cobertura es una señal, no un sustituto de escenarios significativos. Se
  establecerá un umbral cuando exista medición estable; no se escribirán tests
  triviales para aumentar un porcentaje.
- Antes de completar: `dotnet format --verify-no-changes`, build Release y tests
  relevantes.
