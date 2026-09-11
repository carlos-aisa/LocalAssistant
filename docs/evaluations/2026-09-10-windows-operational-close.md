# Validación manual — cierre operativo de Windows (incremento 8, fase 5)

Estado: **ejecutada y superada** (2026-09-11). Este documento es el runbook reproducible
y el registro de la ejecución real; no sustituye a las pruebas automatizadas.

## Alcance de esta validación

Cubre exclusivamente lo que añade el incremento 8: publicación `win-x64`, configuración
fuera del binario, `--diagnostics`, `--version`, `--help` y el script de smoke test. La
secuencia interactiva de conversación (emparejamiento, un turno real, `/voice`,
`/repeat`, `/stop`, `/exit`) no cambió en este incremento — su código
(`TerminalClientApplication`) no se tocó — y ya quedó validada manualmente en
`docs/evaluations/2026-09-09-windows-tts-manual-validation.md`; no se repite aquí para
no añadir conversaciones de prueba a una instalación real. Queda disponible para que el
usuario la reconfirme sobre el ejecutable publicado cuando lo considere oportuno.

## Ejecución

- Equipo: Windows 11 Pro 10.0.26200, PowerShell 5.1.
- `dotnet publish src/LocalAssistant.TerminalClient/LocalAssistant.TerminalClient.csproj -c Release -r win-x64 --self-contained false -o <dir>`
  produce `LocalAssistant.TerminalClient.exe` y `appsettings.json` (con los valores por
  defecto, sin secretos) en la salida.
- `--version` imprime `LocalAssistant.TerminalClient 1.0.0+<revisión>` y termina en `0`
  sin tocar red ni estado local.
- `--help` enumera `--base-url=`, `--provider=`, `--scenario=`, `--request-timeout=`,
  `--plain`, `--diagnostics`, `--version` y `--help`, el prefijo de entorno y la
  ubicación de `appsettings.json`.
- `--diagnostics` con la API real parada: `Health: unreachable` y la línea de ayuda para
  arrancarla manualmente; con la API real arrancada (`dotnet run --project
  src/LocalAssistant.Api`): `Health: reachable`. Ambos casos terminan en `0`.
- El informe de `--diagnostics` mostró el estado local real de este equipo (`format: 2`,
  `ClientId` legible, sin `LastConversationId`) sin descifrar el payload DPAPI en ningún
  momento, y 2 voces SAPI habilitadas como recuento (nunca sus nombres).
- Precedencia de configuración verificada con las tres fuentes activas a la vez: una
  variable de entorno `LocalAssistant__TerminalClient__Scenario` quedó anulada por
  `--scenario=` en la línea de comandos, y `BaseUrl`/`RequestTimeout` se sirvieron desde
  `appsettings.json` al no haber ni variable ni argumento para ellos.
- `--help --diagnostics` prioriza `--help`; `--diagnostics --version` prioriza
  `--version`; un `--provider` inválido devuelve código `2` incluso combinado con
  `--diagnostics`; un argumento desconocido sigue devolviendo `2`.
- `dotnet list package --vulnerable --include-transitive` sobre el cliente: sin avisos.
- `scripts/Invoke-TerminalClientSmoke.ps1` (sin `-SkipManual` sustituido por verificación
  manual de la sección guiada; con `-SkipManual` para la parte automática) terminó en
  `PASS` con código `0`. Se comprobaron a mano sus tres modos de fallo, restaurando el
  estado real del equipo entre pruebas:
  - `-PublishDir` apuntando a un directorio sin el `.exe` → `FAIL` claro y código `1`.
  - Un `.wav` sembrado en el directorio de publicación → detectado y código `1`.
  - Una clave `credential` sembrada en `private-client.json` (tras respaldar el archivo
    real) → detectada y código `1`; una vez restaurado el archivo real, `--diagnostics`
    volvió a mostrar el mismo `ClientId` que antes de la prueba, confirmando que no se
    perdió ni corrompió el estado real.
- Sin procesos `LocalAssistant.TerminalClient`, `LocalAssistant.Api` ni `testhost`
  residuales, y sin ningún archivo `.wav`, tras toda la sesión.

Conclusión: incremento 8 verificado. Se marca el punto 8 del `ROADMAP.md`; la fase 5
queda cerrada con sus ocho incrementos.

## Entorno requerido

- Windows con PowerShell.
- Runtime .NET 8 instalado (ya presente si en el equipo corre la API).
- Directorio local del cliente anotado antes de comenzar, sin incluir su ruta personal
  ni datos de la credencial en la evidencia.

## Secuencia reproducible

1. Publicar: `dotnet publish src/LocalAssistant.TerminalClient -c Release -r win-x64 --self-contained false -o <dir>`
   y comprobar que `<dir>` contiene el `.exe` y `appsettings.json`.
2. Ejecutar `--version` y `--help` y comprobar código `0` y el contenido esperado.
3. Con la API parada, ejecutar `--diagnostics` y comprobar `unreachable` más la ayuda de
   arranque manual.
4. Arrancar la API (`dotnet run --project src/LocalAssistant.Api`) y repetir
   `--diagnostics`; comprobar `reachable`.
5. Repetir `--diagnostics` variando `BaseUrl`/`Provider`/`Scenario`/`RequestTimeout` por
   `appsettings.json`, por variable de entorno y por argumento, comprobando la
   precedencia CLI > entorno > fichero > defecto en el origen que reporta cada clave.
6. Ejecutar `scripts/Invoke-TerminalClientSmoke.ps1` (con `-SkipManual` para la parte
   automática, sin él para completar a mano el emparejamiento, un turno, la voz y los
   cierres) y comprobar `PASS`.
7. Comparar el directorio de publicación y el de trabajo antes y después para confirmar
   que no aparecen `.wav`, y que no quedan procesos residuales.
8. Opcional: reconfirmar a mano sobre el ejecutable publicado el emparejamiento, un
   turno real, `/voice`, `/repeat`, `/stop` y `/exit` — mismo procedimiento que
   `docs/evaluations/2026-09-09-windows-tts-manual-validation.md`, código no modificado
   por este incremento.

## Evidencia a registrar tras la ejecución

- Versión de Windows y PowerShell.
- Fecha, comandos ejecutados y resultado de cada paso.
- Ausencia de secretos, texto conversacional, nombres completos del inventario de voces
  y rutas personales.
