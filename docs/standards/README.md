# Estándares del repositorio

Estas normas se adaptaron el 17 de agosto de 2026 desde el repositorio privado
`carlos-aisa/PROMPTS`, revisión
`3425ce3880b40ff97f28934fdd8c81461bb101a6`. Son parte del proyecto y deben
versionarse para que las reglas acompañen a todos los clones y agentes.

No se copiaron literalmente. Las fuentes contienen buenos principios generales,
pero también decisiones para otros tipos de producto que contradicen ADR ya
aceptados en LocalAssistant.

## Fuentes aplicadas

| Fuente | Destino | Adaptación |
| --- | --- | --- |
| `AGENTS.md` | `/AGENTS.md` | Ajustado al papel educativo y a la arquitectura actual. |
| `BACKEND_STANDARDS*.md` | `BACKEND_STANDARDS.md` | Conserva claridad, async, DI, validación, logging y seguridad; elimina capas y EF obligatorios. |
| `TESTING_STANDARDS_DOTNET.md` | `TESTING_STANDARDS_DOTNET.md` | Conserva determinismo y separación de pruebas; no impone mocks ni librerías adicionales. |
| `DOCUMENTATION_STANDARDS.md` | `DOCUMENTATION_STANDARDS.md` | Conserva sincronización y calidad; respeta el español actual del repositorio. |
| `OPENAPI-DOC.md` | `OPENAPI_STANDARDS.md` | Adapta ubicación, actualización y alcance al API existente. |
| `Prompt_estandarizar_repo.md` | Este proceso de selección | Se siguió su política de inspeccionar, preservar y evitar normas irrelevantes. |

## Fuentes pospuestas

- Los estándares frontend se incorporarán cuando exista una interfaz y se haya
  elegido su tecnología.
- Las guías de EF Core se incorporarán si el proyecto adopta EF Core. No se obliga
  ahora a elegir ORM ni base de datos.
- Los prompts de proyectos legacy y generación de skills son herramientas de
  mantenimiento del repositorio PROMPTS, no normas de LocalAssistant.
- Los umbrales de cobertura se decidirán después de medir una línea base; los tests
  de comportamiento siguen siendo obligatorios desde ahora.

Los documentos originales permanecen en `PROMPTS` como biblioteca reutilizable.
Esta carpeta es la versión autoritativa y específica para LocalAssistant.
