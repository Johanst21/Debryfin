# AGENTS.md — Debryfin

> Contrato de trabajo para agentes (Antigravity). Léelo entero antes de tocar código.
> Análisis completo con evidencias y fuentes: `ANALYSIS.md` (v2). **Cada tarea de la sección 7 es autocontenida**: no necesitas leer el análisis para ejecutarla.
> Las tareas se toman **en orden de prioridad y número** (P0-01, P0-02…). Respeta el campo "Depende de".

## 1. Descripción del proyecto

Debryfin es un **plugin de servidor Emby** en C#. Para cada ítem `.strm` cuyo contenido es un enlace de hoster (p. ej. 1fichier), obtiene un enlace directo mediante un proveedor Debrid, lo refresca cuando caduca y lo sirve a Emby como `MediaSourceInfo`. Hoy funcionan **Real-Debrid y AllDebrid (producción: no romper)**. Se van a añadir **TorBox y Premiumize**, y más proveedores en el futuro, sobre una abstracción `IDebridProvider`. Lo usa un administrador de servidor Emby doméstico.

## 2. Stack y entorno

- **Emby objetivo: 4.10+** (no 4.9).
  - Implicación **TFM**: el host 4.10 corre sobre **.NET 6** (reportado en 4.10.0.40) y el SDK de plugins es `netstandard2.0`. **No uses `net8.0`/`net6.0`** para el plugin. TFM recomendado: `netstandard2.0` (decisión final en P0-03).
  - Implicación **Core**: `MediaBrowser.Server.Core` 4.10 solo existe como **prerelease** en NuGet (`4.10.0.24-beta2`). Hay que fijar la versión exacta; `4.9.*` no la recoge. **No compilar contra 4.11**: un plugin compilado contra un SDK más nuevo que el host no carga.
  - Estado actual del repo (antes de P0-03): `netstandard2.1`, Core `4.9.*` (resuelto 4.9.1.90), `System.Text.Json 8.0.5`, `System.Memory 4.5.5`.
- Lenguaje: C# (LangVersion por defecto del TFM; `using var` permitido). Tests: `net8.0` (solo el proyecto de tests).
- Gestor de paquetes: NuGet (`nuget.org` + MyGet de Emby, ver `nuget.config`).
- **No hay servidor Emby en tu entorno**: no puedes validar comportamiento en runtime. Todo lo que dependa de Emby se marca **"a verificar en 4.10"** y se deja para la prueba de humo del usuario.
- Persistencia de settings: XML de configuración de Emby (el enum `DebridProvider` se serializa: **solo se amplía al final**).

## 3. Estructura del repositorio

Estado actual (plano):

```
Debryfin.csproj  nuget.config  Plugin.cs  DebryfinOptions.cs  DebridService.cs  UrlProvider.cs  thumb.jpg
```

Estructura objetivo (se crea de forma incremental, tarea a tarea; **no muevas ficheros masivamente**):

```
Providers/       IDebridProvider.cs  DebridProviderBase.cs  ProviderRegistry.cs  LegacyProviderAdapter.cs
                 RealDebridProvider.cs  AllDebridProvider.cs  TorBoxProvider.cs  PremiumizeProvider.cs
Resolution/      ILinkResolver.cs  LinkResolver.cs  ResolveOutcome.cs  ProviderPolicy.cs  NegativeCache.cs  RateGate.cs
Http/            DebridHttp.cs  SecretRedactor.cs
Strm/            IStrmStore.cs  StrmRefresher.cs
State/           LinkStore.cs
Matching/        FilenameMatcher.cs
Configuration/   ProviderKeyResolver.cs
tests/Debryfin.Tests/
docs/            adr/  compat-4.10.md  ADDING_A_PROVIDER.md
```

Reglas de ubicación:
- Un proveedor nuevo = **una clase en `Providers/`** + registro en `Plugin.cs` + propiedad de settings + tests. Nada más.
- Código sin dependencias de Emby (matching, resolver, redactor, proveedores) → testeable en `tests/`.
- **NO tocar / NO versionar**: `bin/`, `obj/`. No editar binarios.
- **NO cambiar**: GUID del plugin (`Plugin.cs`, `d090d156-79db-4784-b119-68c484750329`) ni el nombre del recurso embebido `Debryfin.thumb.jpg`.
- Al crear `tests/`, añade `<Compile Remove="tests/**" />` y `<EmbeddedResource Remove="tests/**" />` al `Debryfin.csproj` (si no, el globbing compila los tests dentro del plugin).

## 4. Comandos frecuentes

```bash
# Restaurar y compilar
dotnet restore
dotnet build -c Release

# Tests (tras P0-01)
dotnet test tests/Debryfin.Tests -c Release

# Formato / lint (tras añadir .editorconfig)
dotnet format --verify-no-changes

# "Pack": el entregable de un plugin Emby es el DLL (no un .nupkg)
dotnet publish -c Release -o artifacts/publish
#   → copiar solo artifacts/publish/Debryfin.dll (y dependencias que P0-03 decida empaquetar, si alguna)
```

**Despliegue manual para probar en Emby 4.10 (lo hace el usuario; el agente NO despliega en servidores):**

```text
1. Parar Emby (si es Docker: parar el contenedor, no solo el servicio).
2. Copiar Debryfin.dll a la carpeta "plugins" del directorio de datos de Emby   [rutas habituales — A VERIFICAR en tu instalación]
     Windows : %AppData%\Emby-Server\programdata\plugins
     Linux   : /var/lib/emby/plugins
     Docker  : <volumen /config>/plugins
3. Arrancar Emby y comprobar en el log del servidor que aparece:
     "Debryfin ha sido cargado correctamente."
4. Abrir la configuración del plugin y lanzar una reproducción real de un .strm (RD y AD).
```

## 5. Convenciones de código

- Nomenclatura: tipos y métodos `PascalCase`; privados `_camelCase`; async con sufijo `Async`; interfaces `IPascalCase`.
- Formato: 4 espacios, llaves en línea nueva (estilo actual), `using` ordenados y sin usar los innecesarios.
- **Async**: propagar siempre `CancellationToken`; `ConfigureAwait(false)` en código de biblioteca; nunca `.Result`/`.Wait()`.
- **HTTP**: **un único `HttpClient` compartido** a través de `DebridHttp` (timeout explícito, User-Agent con versión). El requisito del propietario es `IHttpClientFactory`, pero **`IHttpClientFactory` no es parte de la BCL de `netstandard` y no consta que Emby 4.10 lo registre (a verificar en 4.10)**. Hasta confirmarlo, `DebridHttp` es la abstracción; si se confirma que Emby ofrece la factoría, `DebridHttp` la envuelve sin cambiar a los proveedores.
- **Errores**: nunca devolver `null` como señal de fallo en código nuevo; usar `UnrestrictResult`/`ResolveOutcome` con `ErrorKind`. Capturar excepciones solo en límites (red, E/S de ficheros).
- **Secretos**: la API key solo vive en settings y en memoria. **Prohibida en URLs** (salvo `token=` de TorBox `requestdl`, inevitable), en logs, en mensajes de excepción, en tests y en el repo. Todo log y toda excepción pasan por `SecretRedactor`.
- **Logging**: `ILogger` de Emby; formato `Debryfin[<proveedor>]: <acción> <resultado> (<ms>)`; `Error` solo para fallos no recuperables; jamás la URL directa completa.
- **Ficheros del usuario (`.strm`)**: escritura atómica (temporal + reemplazo) y siempre bajo lock por ruta.
- Comentarios: explican el *porqué*. Prohibidos `CORREGIDO`, `CORRECCIÓN AQUÍ` y código comentado.
- **Dependencias de APIs externas**: no inventes endpoints ni campos. Si no puedes confirmar uno con la documentación oficial, marca **"a verificar"** con el enlace y pregunta.

## 6. Reglas para el agente

**Puedes hacer sin preguntar**
- Refactors locales sin cambio de comportamiento observable; tests; documentación; `.gitignore`/`.editorconfig`.
- Añadir al proyecto de tests solo estos paquetes: `Microsoft.NET.Test.Sdk`, `xunit`, `xunit.runner.visualstudio`, `NSubstitute`.
- Implementar las tareas del backlog tal como están descritas, incluidas las propiedades de settings **explícitamente listadas** en P1-07.

**Debes preguntar antes** (y esperar respuesta)
- **Cambiar TFM o versiones de `MediaBrowser.Server.Core` / `System.Text.Json`**: la propuesta está en P0-03, pero el resultado (y volver a 4.9.1.90 si falla) lo confirma el usuario tras su prueba de humo.
- Añadir o quitar **cualquier otro paquete** NuGet.
- Tocar la **API pública de los proveedores existentes** (firma de `DebridService.UnrestrictLinkAsync`, comportamiento observable de RD/AD, formato de peticiones HTTP a RD/AD más allá de lo descrito en P1-03).
- **Cambiar settings**: renombrar/eliminar propiedades de `DebryfinOptions` o alterar valores por defecto existentes. (Añadir las listadas en P1-07 está pre-aprobado.)
- Cambiar el formato en disco de `.strm`, `.strm.original` o de cualquier estado persistido (P1-11).
- Implementar proxy real o endpoints HTTP propios (P2-09).

**No hagas nunca**
- Commitear secretos, API keys, rutas locales, `bin/` u `obj/`.
- Borrar o renombrar datos persistidos de configuración de usuarios, ni `.strm` de la biblioteca; toda tarea masiva sobre ficheros tiene modo *dry-run* por defecto.
- Cambiar el GUID del plugin o el nombre del recurso embebido.
- Llamar a APIs reales de proveedores en tests (usa `HttpMessageHandler` falso).
- Tocar CI/CD sin avisar (P2-14 requiere confirmación).
- Dar por verificado algo que dependa de Emby real: dilo en la descripción del cambio.

**Git**
- Ramas: `fix/<tarea>-<slug>`, `feat/...`, `refactor/...` (p. ej. `fix/p0-01-strm-race`). Un PR por tarea.
- Commits: Conventional Commits (`fix: serializa la escritura de .strm por ruta (P0-01)`).
- Cada PR enumera: tarea, criterio de aceptación cumplido, qué quedó "a verificar en 4.10".

## 7. Backlog accionable

Referencias de línea según el código actual; si el fichero cambió, localiza por nombre de método.

### P0 — Bloqueantes (en este orden)

- [ ] **P0-01 · Race condition en `ResolveAndCacheUrlAsync` (`UrlProvider.cs:109-123`)**
  - Archivos: `UrlProvider.cs`, `Strm/IStrmStore.cs` (nuevo), `Strm/StrmRefresher.cs` (nuevo), `Debryfin.csproj` (`Compile Remove="tests/**"`), `tests/Debryfin.Tests/` (nuevo)
  - Depende de: —
  - Problema: dos llamadas concurrentes para el mismo `.strm` pueden ver ambas "no existe `.original`"; una escribe `.original = fuente` y `.strm = urlDirecta`; la otra lee entonces el `.strm` (ya resuelto) y **sobrescribe `.original` con la URL directa**, perdiendo la fuente. Además `WriteAllTextAsync` no es atómico.
  - Criterio de aceptación:
    1. **Primero el test**: extraer la lógica a `StrmRefresher` con una interfaz mínima `IStrmStore` (Exists, ReadAllTextAsync, WriteAtomicAsync, GetLastWriteUtc) y un `Func<string, CancellationToken, Task<string>> unrestrict` inyectable. Escribir un test xUnit con un `IStrmStore` en memoria con retardos (`Task.Delay`) que lanza **20 llamadas concurrentes** sobre el mismo ruta y comprueba (a) `.original` == fuente original, (b) el proveedor se invoca **exactamente 1 vez**, (c) el `.strm` final == URL directa.
    2. **Reproducir**: ejecutar el test contra el código sin arreglar y **pegar el fallo en la descripción del PR**.
    3. **Arreglo**: `ConcurrentDictionary<string, SemaphoreSlim>` (comparador `OrdinalIgnoreCase`); releer estado **dentro** del lock; escritura atómica (fichero temporal + `File.Replace`, o `File.Move` si el destino no existe; `File.Move(…, overwrite)` no existe en netstandard2.0); `.original` solo se escribe si no existe.
    4. El test pasa; `UrlProvider` delega en `StrmRefresher` sin cambiar su comportamiento externo.
  - Notas: `IStrmStore` envuelve `IFileSystem` de Emby (firmas de `ReadAllTextAsync/WriteAllTextAsync/GetLastWriteTimeUtc/FileExists`: **a verificar en 4.10** al compilar). No cambies TTL (72 h) ni la lógica de hermanos aquí.

- [ ] **P0-02 · ID de fuente estable (eliminar `GetHashCode()`)**
  - Archivos: `UrlProvider.cs` (`BuildMediaSourceInfo`, línea ~151; pasar `BaseItem item`), `MediaSourceId.cs` (nuevo), tests
  - Depende de: P0-01
  - Problema: `sourcePath.ToLowerInvariant().GetHashCode()` es aleatorio por proceso en .NET: el ID cambia en cada reinicio. El comentario "mismo ID que el path evita duplicados" es incorrecto. Los proveedores nuevos heredarían el ID inestable.
  - Criterio de aceptación: `MediaSourceId.For(Guid itemId, string path)` devuelve `itemId.ToString("N")`; si `itemId == Guid.Empty`, SHA1 hex de la ruta en minúsculas (`BitConverter.ToString(sha1.ComputeHash(Encoding.UTF8.GetBytes(...))).Replace("-", "")`; `Convert.ToHexString` no existe en netstandard2.0/2.1). Tests: mismo resultado en 2 llamadas y entre instancias; resultado distinto para ítems distintos. Eliminado el comentario engañoso.
  - Notas: no se puede comprobar en runtime si Emby sigue mostrando duplicados con este ID: **a verificar en 4.10** (prueba del usuario con un `.strm` con varias versiones).

- [ ] **P0-03 · Compatibilidad con Emby 4.10+ (TFM, Core, System.Text.Json) con decisión explícita**
  - Archivos: `Debryfin.csproj`, `nuget.config`, `Plugin.cs` (`Configuration`), `UrlProvider.cs` (línea ~144), `docs/adr/0001-tfm-core-stj.md` (nuevo), `docs/compat-4.10.md` (nuevo)
  - Depende de: —
  - ⚠ **Requiere confirmación del usuario** antes de fusionar (cambia TFM y paquetes).
  - Qué hacer (en una rama; **no fusionar sin el resultado de la prueba de humo**):
    1. `TargetFramework` → `netstandard2.0`.
    2. `MediaBrowser.Server.Core` → versión **exacta** `4.10.0.24-beta2` (prerelease; sin comodines).
    3. Quitar la referencia directa a `System.Memory`.
    4. `System.Text.Json`: opción A (preferida) referencia `6.0.x` **sin copiar el ensamblado al output** (el host .NET 6 lo trae; un host con 8.0 también lo resuelve); opción B usar `IJsonSerializer` de Emby y eliminar la dependencia. Elegir A salvo que no compile.
    5. `Plugin.Configuration` y `UrlProvider`: tolerar `null`/excepción en `GetOptions()` (valores por defecto + log **una sola vez**). Contexto: bug reportado en 4.10.0.40 con `Configuration` nula (https://github.com/MediaBrowser/Emby/issues/3993); estado en 4.10.1+ **a verificar en 4.10**.
    6. `dotnet build -c Release`. Si hay errores de API contra 4.10.0.24-beta2, **anótalos en el ADR y pregunta**; no cambies semántica para "hacer compilar".
    7. Rellenar `docs/compat-4.10.md` con la checklist (compila / carga / config / STJ / RD / AD) y la lista de "a verificar", y **detenerte** para que el usuario ejecute la prueba de humo (sección 4).
  - Criterio de aceptación: (a) compila en Release con 0 errores contra 4.10.0.24-beta2 en `netstandard2.0` **o** el ADR documenta qué falla y cuál es el plan B (volver a Core 4.9.1.90, que según la regla de carga sigue siendo cargable en hosts ≥ 4.9.1); (b) ADR con la decisión; (c) existe un test del helper de configuración segura; (d) el usuario confirma el humo en su Emby 4.10.x.
  - Notas: **no retargetear a `net6.0`/`net8.0`; no compilar contra 4.11.x.** Mínimo soportado resultante = versión del SDK usado (4.10.0.24).

- [ ] **P0-04 · Abstracción `IDebridProvider` sin romper Real-Debrid ni AllDebrid**
  - Archivos: `Providers/IDebridProvider.cs`, `DebridProviderBase.cs`, `ProviderRegistry.cs`, `LegacyProviderAdapter.cs` (nuevos), `Http/DebridHttp.cs`, `Http/SecretRedactor.cs` (nuevos), `DebridService.cs`, `Plugin.cs`
  - Depende de: P0-03
  - Contrato (implementar tal cual):
    ```csharp
    public interface IDebridProvider {
        DebridProvider Id { get; }                       // enum existente (solo ampliar al final)
        string DisplayName { get; }
        TimeSpan DefaultLinkTtl { get; }                 // RD/AD: 72 h (comportamiento actual)
        Task<UnrestrictResult> UnrestrictAsync(string sourceLink, string apiKey, CancellationToken ct);
        Task<AccountInfo> GetUserInfoAsync(string apiKey, CancellationToken ct);
        Task<ValidationResult> ValidateApiKeyAsync(string apiKey, CancellationToken ct);
    }
    public enum ErrorKind { None, AuthFailed, RateLimited, Unsupported, NotFound, Transient, Pending, Permanent }
    ```
  - Estrategia *strangler* (cero cambio de comportamiento para RD/AD): `LegacyProviderAdapter` envuelve los métodos existentes `CallRealDebrid` / `CallAllDebrid` (hacerlos `internal`, **sin tocar su cuerpo**) y traduce `null` → `UnrestrictResult.Fail(Transient)`. `ProviderRegistry` registra los dos adapters; la composición manual se hace en el constructor de `Plugin`. `DebridService.UnrestrictLinkAsync` **mantiene su firma pública** y delega en el registro. Para TorBox/Premiumize (no registrados) la fachada registra `Warn` "proveedor no implementado" y devuelve `null` (hoy fallaban en silencio contra endpoints incorrectos).
  - `DebridHttp`: un `HttpClient` compartido, mismo User-Agent actual (`Debryfin-Emby-Plugin/1.0`) y mismo comportamiento de timeout que hoy hasta P1-09. `SecretRedactor`: elimina `apikey=`, `token=`, cabeceras `Authorization` en cualquier texto de log o excepción.
  - Criterio de aceptación: build OK; RD y AD hacen **exactamente las mismas peticiones HTTP** que antes (test de caracterización mínimo con `HttpMessageHandler` falso que captura método/URL/cabeceras/cuerpo); tests de `ProviderRegistry` (registrar, obtener, duplicado, no registrado) y de `SecretRedactor`; sin estáticos nuevos.
  - Notas: la inyección de servicios propios en Emby es **a verificar en 4.10**; usa composición manual en `Plugin` (plan B ya aceptado). No cambies `DebryfinOptions`.

- [ ] **P0-05 · Caché negativa + backoff ante fallos del proveedor**
  - Archivos: `Resolution/ILinkResolver.cs`, `LinkResolver.cs`, `ResolveOutcome.cs`, `ProviderPolicy.cs`, `NegativeCache.cs` (nuevos), `UrlProvider.cs`/`Strm/StrmRefresher.cs`, `DebridService.cs`
  - Depende de: P0-04 (y P0-01)
  - Criterio de aceptación:
    1. `ILinkResolver.ResolveAsync(string sourceUrl, CancellationToken ct)` aplica: caché positiva por `(proveedor, sourceUrl)` con TTL = `DefaultLinkTtl` (RD/AD 72 h), **coalescing** de llamadas simultáneas por clave, caché negativa por `ErrorKind` (Transient 2→10 min con backoff exponencial y jitter; RateLimited = `RetryAfter` o 15 min; Unsupported/Permanent 60 min; AuthFailed 30 min), reseteo en éxito.
    2. Tras un fallo, **no se repite la petición** durante la ventana de backoff (test con reloj inyectable y proveedor falso: 1 llamada al proveedor en N invocaciones).
    3. `StrmRefresher` usa `ILinkResolver` en lugar de llamar directo.
    4. El log por fallo incluye proveedor, `ErrorKind`, mensaje sin secretos; "falta API key" se registra como `Warn`, como máximo una vez por minuto.
  - Notas: RD/AD siguen devolviendo `Transient` genérico hasta P1-01/02 (el adapter no distingue causas). Reloj: `Func<DateTime>` inyectable. Valores por defecto en constantes; configurables en P1-07/P2-03.

- [ ] **P0-06 · Evitar resolución masiva por `baseName` vacío (añadida)**
  - Archivos: `UrlProvider.cs` (bloque de película, líneas ~63-71), tests
  - Depende de: —  (recomendado ejecutarla justo después de P0-02)
  - Problema: el patrón `^(.*?)(?:\s\(?\d{4}\)?|\[|\()` produce grupo vacío con nombres que empiezan por `(` o `[` (`(500) Days of Summer (2009).strm`, `[4K] Peli.strm`); `StartsWith("")` es verdadero para todos ⇒ se intentan resolver **todos** los `.strm` del directorio.
  - Criterio de aceptación: si `baseName` queda vacío se usa el nombre de fichero completo sin extensión; test (lógica de selección extraída a función pura) con esos dos nombres en un directorio con otros `.strm`: solo se seleccionan los del mismo título. Sin cambios para nombres que hoy funcionan.
  - Notas: la solución completa de matching es P1-10; esta es la guarda mínima.

### P1 — Alto

- [ ] **P1-01 · Migrar Real-Debrid a la nueva abstracción (mantener comportamiento)**
  - Archivos: `Providers/RealDebridProvider.cs` (nuevo), `LegacyProviderAdapter.cs`, `DebridService.cs`, tests
  - Depende de: P0-04, P0-05
  - Criterio de aceptación: **primero** test de caracterización del comportamiento actual: `POST https://api.real-debrid.com/rest/1.0/unrestrict/link`, cabecera `Authorization: Bearer <key>`, cuerpo `application/x-www-form-urlencoded` con `link`, éxito = campo `download`. **Después** mover el cuerpo a `RealDebridProvider` y que el mismo test siga verde. Clasificación de errores nueva: 401/403 → AuthFailed; 429 → RateLimited (respetar `Retry-After`); 5xx/timeout/red → Transient; respuesta correcta sin `download` → Permanent; mensaje de error de RD en el log (campo `error` del JSON: **a verificar** en https://api.real-debrid.com). Lectura con `ParseAsync(stream, ct)`.
  - Notas: **no cambiar** URL, cabeceras ni cuerpo de la petición. Retirar el registro del adapter de RD solo cuando el test de caracterización pase.

- [ ] **P1-02 · Migrar AllDebrid a la nueva abstracción (mantener comportamiento)**
  - Archivos: `Providers/AllDebridProvider.cs` (nuevo), `LegacyProviderAdapter.cs`, `DebridService.cs`, tests
  - Depende de: P0-04, P0-05
  - Criterio de aceptación: test de caracterización previo: `GET https://api.alldebrid.com/v4/link/unlock?agent=Debryfin&apikey=<key>&link=<link escapado>`, éxito = `data.link`. Mover a `AllDebridProvider` con el test en verde. Errores: respuesta `status:"error"` → leer `error.code`/`error.message` (códigos exactos **a verificar** en https://docs.alldebrid.com); mapeo provisional: `AUTH_*` → AuthFailed, `LINK_HOST_NOT_SUPPORTED` → Unsupported, `LINK_DOWN` → NotFound, resto → Transient; respuesta "delayed" (sin `link`) → Pending.
  - Notas: **mantener método GET y `apikey=` en esta tarea**; el cambio a Bearer es P1-03 (PR separado, reversible).

- [ ] **P1-03 · AllDebrid: API key en cabecera `Authorization: Bearer`**
  - Archivos: `Providers/AllDebridProvider.cs`, tests
  - Depende de: P1-02
  - Criterio de aceptación: la petición lleva `Authorization: Bearer <key>` y la URL **no contiene `apikey=`** (test que lo verifica); `agent=Debryfin` se mantiene; método **GET conservado** (la documentación de AD lista `link/unlock` también como POST; migrar a POST es opcional y fuera de esta tarea).
  - Notas: la documentación de AD recomienda Bearer (https://docs.alldebrid.com). **A verificar con cuenta real**: que AD acepte solo Bearer; el usuario debe probar una reproducción tras fusionar. Plan de vuelta atrás: revert del PR.

- [ ] **P1-04 · Real-Debrid ya conforme: blindar "sin credenciales en URL" + redactor**
  - Archivos: `tests/…/NoSecretsInUrlTests.cs` (nuevo), `Http/SecretRedactor.cs`
  - Depende de: P1-01
  - Criterio de aceptación: tests que recorren las peticiones capturadas de todos los proveedores migrados y fallan si la URL contiene `apikey=` o `token=` (lista blanca: TorBox `requestdl`); tests del `SecretRedactor` (cadenas con `apikey=`, `token=`, `Authorization: Bearer`); verificación de que ninguna excepción registrada contiene la key.
  - Notas: RD ya usa Bearer (`DebridService.cs:61`); esta tarea solo añade protección de regresión.

- [ ] **P1-05 · Implementar TorBox con el flujo correcto (`createwebdownload` + `requestdl`)**
  - Archivos: `Providers/TorBoxProvider.cs` (nuevo), `DebridService.cs` (eliminar el stub roto `CallTorBox`), `Plugin.cs` (registro), tests
  - Depende de: P0-05, P1-04
  - **No usar `/v1/api/link/unrestrict`** (no figura en la documentación ni en los SDK oficiales).
  - Flujo (fuentes: https://github.com/TorBox-App/torbox-sdk-py/blob/main/documentation/services/WebDownloadsDebridService.md y https://docs.torbox.app — **verificar contra la documentación oficial**):
    1. `POST https://api.torbox.app/v1/api/webdl/createwebdownload`, `Authorization: Bearer <key>`, cuerpo con `link` (opcionales: `name`, `as_queued`, `add_only_if_cached`). Respuesta: `data.webdownload_id` (string).
    2. Si el hoster no está en caché TorBox lo descarga primero: esperar con **polling acotado (máx. ~25 s)**; si no está listo → `ErrorKind.Pending` (el backoff de `LinkResolver` reintenta). Campos de estado de `GET /v1/api/webdl/mylist`: **a verificar**; alternativa aceptable: intentar el paso 3 y tratar el error de "no listo" como Pending.
    3. `GET https://api.torbox.app/v1/api/webdl/requestdl?token=<key>&web_id=<id>`: la clave va **en query** (inevitable) → **redactar siempre**. La respuesta trae en `data` un **string** con la URL directa.
  - Criterio de aceptación: tests con `HttpMessageHandler` falso: éxito en caché, éxito tras espera, Pending por tiempo, 401 → AuthFailed, 429 → RateLimited, error de red → Transient; la key **no aparece** en ningún log (test con el redactor); `DefaultLinkTtl` = **2 h** (enlace abierto ~3 h según los SDK; **a verificar**).
  - Notas: el enlace de TorBox caduca en horas: el TTL por proveedor (P0-05) debe aplicarse; con TorBox el `.strm` se reescribirá más a menudo (ver P1-11).

- [ ] **P1-06 · Implementar Premiumize con `Authorization: Bearer`**
  - Archivos: `Providers/PremiumizeProvider.cs` (nuevo), `DebridService.cs` (eliminar el stub `CallPremiumize`), `Plugin.cs`, tests
  - Depende de: P0-05, P1-04
  - Especificación verificada en https://www.premiumize.me/api:
    - Auth: `Authorization: Bearer <apikey>` (**no** usar `?apikey=`, es legacy).
    - Resolver: `POST https://www.premiumize.me/api/transfer/directdl`, cuerpo form `src=<link>`. Respuesta `{"status":"success","content":[{"path","size","link"}]}` → usar **`content[0].link`** (los campos `location/filename/filesize` y `stream_link` están deprecados). Si hay más de un elemento: elegir el de mayor `size` y registrarlo en `Debug`.
    - Errores: HTTP 200 con `{"status":"error","message","code"}`; HTTP 500 = `unknown_error`. Mapeo: `link_generation_failed|transient_error|unknown_error|service_down` → Transient; `service_limit_reached|account_limit_reached|rate_limit_reached` → RateLimited; `service_unsupported` → Unsupported; `not_found|invalid_request|permanent_error` → Permanent; `authentication_failed|permission_denied` → AuthFailed.
    - `GetUserInfoAsync`/validación: `GET /api/account/info` (`customer_id`, `premium_until` null = cuenta gratuita, `limit_used`).
  - Criterio de aceptación: tests por cada código de error del mapeo + éxito + varios elementos en `content`; la URL de la petición **no contiene** la key; `DefaultLinkTtl` por defecto 12 h (**a verificar**).
  - Notas: el campo `location` que usa el stub actual está deprecado: no reutilizar el código antiguo.

- [ ] **P1-07 · Settings por proveedor + validación de API key**
  - Archivos: `DebryfinOptions.cs`, `Configuration/ProviderKeyResolver.cs` (nuevo), `Providers/*` (`GetUserInfoAsync`/`ValidateApiKeyAsync`), `Plugin.cs`, tests
  - Depende de: P1-01, P1-02, P1-05, P1-06
  - **Pre-aprobado (solo aditivo)**: añadir `RealDebridApiKey`, `AllDebridApiKey`, `TorBoxApiKey`, `PremiumizeApiKey` (`[IsPassword]`), `EnableFallback` (bool, `false`), `LinkTtlHours` (int, `0` = automático), `NegativeCacheMinutes`, `RequestTimeoutSeconds`, `MaxConcurrentRequests` (con valores por defecto conservadores). **No tocar** `Provider`, `ApiKey`, `UseProxyMode` ni el orden/valores del enum (un proveedor nuevo se añade al final).
  - Criterio de aceptación: `ProviderKeyResolver.Get(id)` devuelve la key específica del proveedor; si está vacía y `id == Provider`, usa el `ApiKey` heredado (una instalación existente se comporta igual); tests de esa lógica. Validación: cada proveedor implementa `ValidateApiKeyAsync` — RD `GET https://api.real-debrid.com/rest/1.0/user` (**a verificar**), AD `GET https://api.alldebrid.com/v4/user` (Bearer; en la documentación), TorBox `GET /v1/api/user/me` (Bearer; forma de la respuesta **a verificar**), Premiumize `GET /api/account/info`. Al arrancar se valida la key del proveedor principal y se registra el resultado (sin secretos).
  - Notas: validación **al guardar** y botón "Probar conexión" dependen de las capacidades de `GenericEdit` (**a verificar en 4.10**) → P2-01.

- [ ] **P1-08 · Fallback automático entre proveedores**
  - Archivos: `Resolution/LinkResolver.cs`, tests
  - Depende de: P1-07
  - Criterio de aceptación: con `EnableFallback`, la cadena es proveedor principal → los demás con key, saltando los que estén en caché negativa o marcados "no sanos" (AuthFailed reciente); Unsupported/NotFound/RateLimited/Transient pasan al siguiente; resultado de éxito se cachea con el TTL **del proveedor que respondió**. Tests: principal falla → responde el segundo; todos fallan → `ResolveOutcome` con el último error y backoff; con fallback desactivado se comporta como antes.
  - Notas: loguear qué proveedor respondió. No consumir cuota de un proveedor sin key.

- [ ] **P1-09 · Throttle global y timeout por proveedor (añadida)**
  - Archivos: `Resolution/ProviderPolicy.cs`, `RateGate.cs` (nuevo), `UrlProvider.cs`/`Strm/StrmRefresher.cs`, `Http/DebridHttp.cs`
  - Depende de: P0-05
  - Criterio de aceptación: nunca más de `MaxConcurrentRequests` (por defecto 3) peticiones simultáneas por proveedor; cada petición se cancela a los `RequestTimeoutSeconds` (por defecto 15 s) con `CancellationTokenSource.CreateLinkedTokenSource` + `CancelAfter`; `GetMediaSources` resuelve **en línea solo el ítem solicitado**; los hermanos se refrescan en segundo plano con el mismo throttle (cambio de comportamiento: documentarlo en `CHANGELOG.md`). Tests de concurrencia máxima y de timeout.
  - Notas: la lectura del cuerpo HTTP debe respetar el token (usar `ResponseHeadersRead` + `ReadAsStreamAsync` + `JsonDocument.ParseAsync(stream, ct)`).

- [ ] **P1-10 · `FilenameMatcher` con tests (añadida)**
  - Archivos: `Matching/FilenameMatcher.cs` (nuevo), `UrlProvider.cs` (líneas ~50-71), tests
  - Depende de: P0-06
  - Criterio de aceptación: `Regex` estático compilado; `S01E10` **no** coincide con `S01E100`; soporta `S01E01E02`, 3-4 dígitos y `1x01`; `Alien` no agrupa con `Alien 3`; `(500) Days of Summer (2009)` y `[4K] Peli` bien agrupados; ≥ 15 casos de tabla. Lógica pura (recibe nombres, devuelve coincidencias).
  - Notas: no cambiar qué se considera "misma película/episodio" para los nombres que hoy funcionan sin test que lo justifique.

- [ ] **P1-11 · Estado propio (`LinkStore`) y deriva del `.strm` (añadida)**
  - Archivos: `State/LinkStore.cs`, `State/LinkRecord.cs` (nuevos), `Strm/StrmRefresher.cs`, `UrlProvider.cs`
  - Depende de: P0-01, P0-05
  - ⚠ **Preguntar antes de empezar** (cambia el formato en disco).
  - Criterio de aceptación: origen y última URL resuelta por ítem en un JSON en el directorio de datos del plugin (escritura atómica); si el `.strm` cambia por un tercero (contenido distinto de la última URL resuelta y no es una URL conocida) se trata como **origen nuevo**; la detección de "sin resolver" deja de depender de `1fichier.com`/`uptobox.com`; migración desde `.strm.original`; modo *dry-run* para cualquier operación masiva. Tests de deriva y migración.
  - Notas: el usuario **no confirmó** si otros procesos reescriben los `.strm`: se asume que sí. Probar en Emby real que reproduce sin reescribir el `.strm` es **a verificar en 4.10**.

- [ ] **P1-12 · Higiene del repositorio (añadida)**
  - Archivos: `.gitignore`, `.editorconfig` (nuevos)
  - Depende de: —
  - Criterio de aceptación: `.gitignore` con `bin/`, `obj/`, `*.user`, `.vs/`, `artifacts/`; `git ls-files` sin `bin/`/`obj/`; ningún fichero versionado con `/mnt/c/Users` ni `/home/`. Preguntar antes de reescribir historia.
  - Notas: los `obj/` entregados contienen rutas locales y usuario.

### P2 — Medio

- [ ] **P2-01 · Validación de la key al guardar / botón "Probar conexión"**
  - Archivos: `DebryfinOptions.cs`, `Plugin.cs`, `Providers/*`
  - Depende de: P1-07
  - Criterio de aceptación: según lo que permita `GenericEdit` (**a verificar en 4.10**): validación al guardar o acción explícita con resultado legible (válida / inválida / cuenta gratuita / cuota). Sin capacidad confirmada: dejar el log de arranque y documentarlo.
  - Notas: nunca mostrar la key en mensajes.

- [ ] **P2-02 · Panel / estado de proveedores (health check)**
  - Archivos: `Resolution/LinkResolver.cs`, `Plugin.cs`, página de configuración
  - Depende de: P1-07, P1-08
  - Criterio de aceptación: por proveedor: sano/no sano, último error (sin secretos), nº de resoluciones y fallos, caducidad de cuenta (`GetUserInfoAsync`). Solo visible para administradores.
  - Notas: depende de lo que permita la UI de 4.10 (**a verificar**).

- [ ] **P2-03 · Rate-limit configurable por proveedor**
  - Archivos: `Resolution/RateGate.cs`, `ProviderPolicy.cs`, `DebryfinOptions.cs`
  - Depende de: P1-09
  - Criterio de aceptación: peticiones/minuto por proveedor además de concurrencia; valores por defecto tomados de la documentación oficial de cada proveedor (**a verificar**; hasta entonces conservadores). Tests con reloj inyectable.
  - Notas: añadir propiedades de settings solo con aprobación (no listadas en P1-07).

- [ ] **P2-04 · Logs estructurados por proveedor**
  - Archivos: `Providers/DebridProviderBase.cs`, `Resolution/LinkResolver.cs`
  - Depende de: P0-05
  - Criterio de aceptación: todos los logs usan `Debryfin[<proveedor>]: <acción> <resultado> (<ms>)`; niveles correctos; test que verifica ausencia de secretos y de URLs directas completas en la salida.

- [ ] **P2-05 · Tests de integración con mocks por proveedor (tests de contrato)**
  - Archivos: `tests/Debryfin.Tests/Providers/ProviderContractTests.cs` (base genérica), un fichero por proveedor
  - Depende de: P1-01, P1-02, P1-05, P1-06
  - Criterio de aceptación: una base `ProviderContractTests<T>` ejecuta para cada proveedor: éxito, 401, 429, JSON inválido, timeout y "sin credenciales en URL"; sin llamadas de red reales.

- [ ] **P2-06 · Plantilla de proveedor genérico para futuros (Debrid-Link, EasyDebrid…)**
  - Archivos: `Providers/Template/TemplateDebridProvider.cs.txt`, `docs/ADDING_A_PROVIDER.md`, plantilla de tests
  - Depende de: P2-05
  - Criterio de aceptación: guía de 1 página con los 5 pasos (clase, valor del enum **al final**, registro en `Plugin.cs`, propiedad de settings + `ProviderKeyResolver`, tests de contrato); la plantilla compila al renombrarla. **No inventa endpoints**: usa marcadores y exige verificar la documentación de cada proveedor.

- [ ] **P2-07 · Tarea programada de refresco de enlaces (N-02)**
  - Archivos: `Tasks/RefreshLinksTask.cs` (nuevo)
  - Depende de: P1-09, P1-11
  - Criterio de aceptación: `IScheduledTask` (firma **a verificar en 4.10**) que refresca enlaces que caducan en < 24 h respetando throttle, con progreso; `GetMediaSources` no resuelve hermanos.

- [ ] **P2-08 · Reglas por hoster (N-04)**
  - Archivos: `Resolution/LinkResolver.cs`, `DebryfinOptions.cs`
  - Depende de: P1-08
  - Criterio de aceptación: mapa dominio → proveedor preferente (p. ej. `1fichier → AllDebrid`); si falla, cae a la cadena normal. Cambia settings: **preguntar antes**.

- [ ] **P2-09 · Proxy real con soporte `Range` (N-05)**
  - Archivos: `Proxy/` (nuevo), `UrlProvider.cs`
  - Depende de: P1-11
  - ⚠ **Preguntar antes**: endpoint HTTP propio, ancho de banda del servidor, autenticación, riesgo SSRF.
  - Criterio de aceptación: con `UseProxyMode` el cliente nunca recibe la URL directa; `Range`/seek funcionan.

- [ ] **P2-10 · Modo "revertir" (N-07)**
  - Archivos: `Tasks/RestoreStrmTask.cs` (nuevo)
  - Depende de: P1-11
  - Criterio de aceptación: restaura los `.strm` desde sus orígenes; **dry-run por defecto**; informe de lo que haría.

- [ ] **P2-11 · Validación de enlace antes de servir y re-resolución en 403/404/410 (N-08, N-14)**
  - Archivos: `Resolution/LinkResolver.cs`
  - Depende de: P1-09
  - Criterio de aceptación: comprobación ligera (HEAD o `Range: bytes=0-0`) con timeout corto; si falla, re-resolver una vez; tolerar hosters que no admiten HEAD. Tests con servidor HTTP falso.

- [ ] **P2-12 · Limpieza de código y versión única (F-22, F-23)**
  - Archivos: todos los `.cs`, `Debryfin.csproj`
  - Depende de: P1-01, P1-02
  - Criterio de aceptación: sin `usings` sin usar ni comentarios residuales; `<Version>` único del que derivan `AssemblyVersion`, `FileVersion`, `ProductVersion` y el User-Agent (`Debryfin/<versión>`); si `Supports` no pertenece a `IMediaSourceProvider` en 4.10 (**a verificar**), eliminarlo. Cambiar el User-Agent es un cambio observable: confirmar que no es un problema para los proveedores.

- [ ] **P2-13 · Documentación (README, CHANGELOG, LICENSE)**
  - Archivos: `README.md`, `CHANGELOG.md`, `LICENSE` (nuevos)
  - Depende de: P1-07
  - Criterio de aceptación: instalación en 4.10, proveedores y su configuración, límites conocidos (key en texto plano en la config de Emby, DirectPlay expone la URL directa), solución de problemas. Preguntar qué licencia quiere el usuario. Sin rutas ni claves reales.

- [ ] **P2-14 · CI básico**
  - Archivos: `.github/workflows/build.yml`
  - Depende de: P0-01, P1-12
  - ⚠ **Avisar antes de crear** (toca CI).
  - Criterio de aceptación: restore, build Release, `dotnet format --verify-no-changes` y `dotnet test`; artefacto = DLL.

## 8. Definición de "hecho"

- `dotnet build -c Release` sin errores ni warnings nuevos; `dotnet test` pasa y el cambio trae tests propios (en bugs: el test falla antes y pasa después, con el fallo pegado en el PR).
- `dotnet format --verify-no-changes` limpio.
- Sin secretos en logs ni en URLs (salvo `token=` de TorBox `requestdl`, redactado), sin rutas locales y sin `bin/obj` en el diff.
- RD y AD: peticiones HTTP idénticas a las anteriores salvo lo descrito expresamente en la tarea (verificado por tests de caracterización).
- Sin `TODO`/`FIXME` nuevos ni código comentado.
- `ANALYSIS.md` actualizado si el cambio afecta a hallazgos; `README.md`/`CHANGELOG.md` si cambia comportamiento observable.
- Lo que dependa de Emby real queda indicado como **verificado** o **a verificar en 4.10** en la descripción del PR.
