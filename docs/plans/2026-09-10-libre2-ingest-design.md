# FreeStyle Libre 2 → limpieza → PostgreSQL — Diseño

**Objetivo:** conexión automática de un FreeStyle Libre 2, limpieza de las lecturas y
persistencia en PostgreSQL.

**Conclusión previa:** Nocturne ya hace las tres cosas. El connector existe, el poller
existe, el camino de escritura existe. La elección de frameworks correcta es **no agregar
ninguno**. El trabajo real es reparar defectos confirmados en el connector y conectar la
maquinaria de calidad de datos que ya está escrita pero no cambia ningún número.

---

## 1. Lo que ya existe (verificado en el código)

| Capa | Dónde | Estado |
| --- | --- | --- |
| Connector LibreLinkUp | `src/Connectors/Nocturne.Connectors.FreeStyle` (712 líneas, 8 archivos) | Funciona |
| Polling automático | `ConnectorBackgroundService.cs` (583 líneas), dentro del proceso de la API | Funciona |
| Escritura a Postgres | `GlucosePublisher` → `SensorGlucoseRepository` → `V4RepositoryBase.BulkCreateAsync` | Funciona |
| Idempotencia | índice único `(tenant_id, legacy_id)` para connectors; `(tenant_id, data_source, sync_identifier)` para clientes API | Funciona |
| Aislamiento por tenant | RLS forzado; el poller hace `tenantAccessor.SetTenant(...)` por tenant | Funciona |

**El poller.** `PeriodicTimer` de 1 minuto; cada tenant se mira solo cuando le toca según su
`SyncIntervalMinutes`; `Parallel.ForEachAsync` con `MaxConcurrentTenantSyncs`; canal de *nudge*
con debounce de 10 s para reaccionar a cambios de configuración; jitter de arranque;
`PerTenantSyncTimeout` de 3 minutos.

**La escritura.** Transacción + execution strategy, dedup por `LegacyId` a nivel de lote y de BD,
`AddRange` en chunks de 500, dedup post-commit, broadcast SignalR y evaluación de alertas.

**Sin cursor almacenado.** El punto de reanudación se deriva de la última fila guardada menos
5 minutos de solape (`docs/api-descriptions/nocturne/Syncing.md`).

---

## 2. Los dos caminos reales para un Libre 2

El sensor nunca habla con la nube de Abbott. Desde la app LibreLink 2.10 (~sept 2023) el Libre 2
transmite un valor por minuto por BLE a la app de Abbott, que alimenta LibreView, que alimenta
LibreLinkUp.

| | **A. LibreLinkUp (nube)** | **B. Juggluco / xDrip+ (BLE directo)** |
| --- | --- | --- |
| Código nuevo en Nocturne | ninguno | ninguno |
| Cadencia que llega | ~15 min del historial `/graph` + 1 punto vivo por poll | 1 min |
| Profundidad histórica | ~12 h, ventana fija del proveedor | la que retenga el teléfono |
| Depende de | teléfono + app Abbott + nube + versión fijada + Cloudflare | teléfono Android |
| Estado legal | el EULA de LibreLinkUp prohíbe el acceso automatizado | fuera del alcance del EULA de Abbott |
| Plataforma | cualquiera | Android (en iOS solo LibreTransmitter, Libre 2 UE) |

**Corrección importante:** los dos caminos **no** son mutuamente excluyentes. Juggluco sube a
LibreView por su cuenta y documenta un modo "Immediate" que existe precisamente para alimentar
LibreLinkUp. Se pueden tener ambos para el mismo sensor.

**Cómo se conecta el camino B hoy**, sin escribir código: acuñar un token `noc_...` en Nocturne,
ponerlo como *API secret* en Juggluco/xDrip+ (la app envía su SHA-1, que es lo que
`ApiKeyHandler` compara), y apuntar el uploader a `https://{slug}.{dominio}/api/v1`. El tenant se
resuelve por subdominio **antes** de autenticar, así que el subdominio es obligatorio en un
despliegue multitenant.

---

## 3. Elección de frameworks

Ninguno nuevo. Cada candidato evaluado y por qué se descarta:

| Necesidad | Decisión | Descartado |
| --- | --- | --- |
| Scheduling | `BackgroundService` + `PeriodicTimer` que ya existe | Quartz.NET 4.0.1 (clustering exige AdoJobStore = 12 tablas), Hangfire (Postgres es paquete comunitario; aún depende de Newtonsoft), Coravel (sin locking distribuido, sin releases desde ene-2025) |
| Múltiples instancias | `DistributedLock.Postgres` (advisory lock) **solo si se despliega más de una réplica de la API** | cualquier scheduler con estado propio en la BD |
| Resiliencia HTTP | `Microsoft.Extensions.Http.Resilience` ya usado vía `ConfigureConnectorClient` | Polly v7 / `Microsoft.Extensions.Http.Polly` (deprecado, sigue referenciado — conviene eliminarlo) |
| Inserción masiva | EF `AddRange` en chunks, que ya está | `NpgsqlBinaryImporter` y `EFCore.BulkExtensions.BulkInsert`: PostgreSQL rechaza `COPY FROM` cuando RLS está en efecto para el rol, y los tres roles de Nocturne son `NOSUPERUSER NOBYPASSRLS` sobre tablas con `FORCE ROW LEVEL SECURITY` |
| Almacenamiento | tablas normales; particionado por rango + BRIN si algún día hace falta | TimescaleDB: las políticas RLS no se propagan a los chunks (issue abierto), y lo que vale la pena es licencia TSL |
| Aspire | el poller vive dentro de la API; si se separa, es un `AddProject<>` más | Aspire no tiene recurso de scheduling; la propuesta de agregarlo se cerró como *not planned* en 2026 |

> `COPY FROM` no está prohibido por tener RLS, sino por estar RLS *en efecto para el rol que
> conecta*. En Nocturne aplica igualmente. El patrón `COPY` a tabla temporal + `INSERT … SELECT`
> sí sería viable, pero a ~288 lecturas/día/usuario no hace falta.

---

## 4. Defectos confirmados que bloquean el camino

Todos verificados leyendo el código, con `file:line`.

1. **El caché de token nunca se invalida.** `LibreLinkAuthTokenProvider.ConnectorName` devuelve
   `"FreeStyle"`, pero el registro es `"LibreLinkUp"` y `ConnectorTokenCache` indexa por
   `connectorName.ToLowerInvariant()`. Los otros ocho connectors coinciden exactamente; FreeStyle
   es el único que no. Corregir una contraseña no surte efecto hasta ~23 h después.
   → una línea.

2. **Los fallos se reportan como sincronizaciones sanas.** Token nulo, `/llu/connections` vacío o
   `patientId` en blanco: los tres devuelven lista vacía con `SyncResult.Success = true`, así que
   `ConnectorBackgroundService.cs:515-527` escribe `isHealthy: true` y *borra el error anterior*.
   `TrackFailedRequest` solo incrementa un campo privado que nadie lee. El tenant ve una tarjeta
   verde que no ingesta nada.

3. **Espera incancelable más larga que el timeout.** `LibreLinkUpConnectorService.cs:77` llama
   `GetValidTokenAsync(config)` sin `CancellationToken`, existiendo la sobrecarga que lo acepta.
   En la rama de lockout el provider hace `Task.Delay(5 min)` contra un `PerTenantSyncTimeout` de
   3 min.

4. **Ambigüedad de fecha.** `LibreTimestampParser` prueba `M/d/yyyy` antes que `d/M/yyyy`, así que
   un valor día-primero con día ≤ 12 se interpreta mes-primero sin excepción ni log. El fallback a
   `CultureInfo.CurrentCulture` hace que el resultado dependa del locale del contenedor.

5. **La clave de dedup es una cadena con formato.** `LegacyId = "libre_" + FactoryTimestamp` (el
   string crudo). Si Abbott cambia el formato, se duplica el historial completo. Y no se fija
   `SyncIdentifier`, así que las filas son solo-inserción: una corrección aguas arriba jamás se
   aplica.

6. **Selección silenciosa del paciente equivocado.** Ante un `PatientId` que no coincide, cae a
   `Data.First()`. Para PHI esto debería fallar duro.

7. **`MaxHistoricalDays = 7` es ficción.** Ningún código de sincronización lo lee; solo se proyecta
   a la UI. `/graph` no acepta rango y `/llu/connections/{id}/logbook` ni siquiera está en
   `ApiPaths`. Una caída más larga que la ventana del proveedor pierde esos datos para siempre.

8. **No hay proyecto de tests.** FreeStyle es el **único** connector sin
   `tests/Unit/Nocturne.Connectors.<Vendor>.Tests`. Cada defecto de arriba lo habría atrapado un test.

**Dos bugs vivos fuera del connector**, encontrados de paso:

9. **La completitud se mide contra el denominador equivocado.**
   `StatisticsService.AssessDataSufficiency` tiene `expectedReadingsPerDay = 288` por defecto y se
   llama con dos argumentos (`:636`), así que el defecto siempre aplica. Un Libre alimentado desde
   el historial de 15 min puntúa ~33 % de completitud y falla la puerta de consenso del 70 %.

10. **El dedup de v1 descarta lecturas reales.** `MatchInWindow` declara duplicado si el
    dispositivo coincide y `|Δ mg/dL| < 0.01` en cualquier punto de la ventana, y
    `V1/EntriesController.cs:624` pasa `windowMinutes: 5`. Una serie plana nocturna a 5 min pierde
    lecturas, y la respuesta las devuelve como si se hubieran guardado. Afecta al camino B, no al
    connector.

---

## 5. Qué significa "limpiar" aquí

No existe un rango fisiológico de consenso. Los límites son del dispositivo: Dexcom reporta
40-400 mg/dL, **FreeStyle Libre 2/3 reporta 40-500**. Un techo global de 400 truncaría datos
legítimos de Libre.

Convención de Nightscout, que Nocturne debe respetar por compatibilidad: `< 39` es código de error,
`39` es "LOW", `> 400` es "HIGH", `9` es centinela de calentamiento. Es decir: **clasificar, no
recortar**. Una lectura descartada es irrecuperable; una mal clasificada se arregla después.

Lo que Nocturne ya tiene y no usa del todo:

- `SensorIntegrityDetector` — detector de ruido con parámetros conscientes del muestreo de 15 min
  de Libre. Sí se consume: alimenta el reporte `/reports/data-quality/sensor-integrity`. No
  persiste nada, y eso es deliberado.
- `CompressionLowDetectionService` — escribe spans `DataExclusion` que el motor de alertas evalúa y
  `StateSpansController` lista, pero que **ninguna estadística ni lectura filtra**. Aceptar una
  sugerencia de compression low hoy no cambia ningún número.
- `CanonicalGlucoseStream` — arbitraje entre fuentes múltiples, ya en uso.

Nada de esto debe correr en la ingesta. La literatura (GLU, AGATA, iglu, cgmstats) es unánime:
almacenar crudo, limpiar en lectura, y no imputar para estadísticas. Quitar compression lows sesga
el TBR hacia abajo, que es justo la métrica donde conviene ser conservador.

---

## 6. Plan

### Fase 1 — Reparar (bloqueante, sin migraciones, sin tablas nuevas)

| | Trabajo | Tamaño |
| --- | --- | --- |
| 1.1 | `ConnectorName` → `"LibreLinkUp"`. Test de reflexión que exija, para toda subclase de `AuthTokenProviderBase`, que su `ConnectorName` iguale el nombre de `[ConnectorRegistration]`. | S |
| 1.2 | `Success = false` + `result.Errors` en las tres rutas de fallo silencioso; dejar de llamar `TrackSuccessfulRequest()` con `_selectedConnection` nulo. | S |
| 1.3 | Pasar `CancellationToken` por toda la cadena de fetch. | S |
| 1.4 | Fallar duro cuando `PatientId` está configurado y no coincide, en vez de caer a `Data.First()`. | S |
| 1.5 | Piso de 5 min en `SyncIntervalMinutes` para Libre (3 min dispara el 1015 de Cloudflare). | S |
| 1.6 | Agregar `EU2` a `AllowedValues` (ya está en el `ServerMapping` del installer). | S |
| 1.7 | **Crear `tests/Unit/Nocturne.Connectors.FreeStyle.Tests`** con fixtures grabados. Bloqueante: 1.2 cambia la semántica de fallo del único connector sin red de regresión. | M |

### Fase 2 — Endurecer el connector

| | Trabajo | Tamaño |
| --- | --- | --- |
| 2.1 | `SyncIdentifier = "llu:{patientId}:{unixMillis}"` derivado del instante parseado. **No tocar `LegacyId`** — cambiarlo dejaría inalcanzable toda fila existente y duplicaría el historial. El índice único y `SyncUpsertRepositoryBase` ya existen; sin migración. | M |
| 2.2 | Desambiguar la fecha por lote: `/graph` devuelve una serie ordenada que termina cerca de ahora, así que se elige el orden que produzca monotonía y se rechaza el otro. Eliminar el fallback a `CurrentCulture`. | M |
| 2.3 | Manejar el redirect de región (`200` con `data.redirect`) y los pasos `tou`/`pp`/`verifyEmail`, hoy indistinguibles de una contraseña mala. | M |
| 2.4 | Hacer configurable la versión de cliente fijada (`4.16.0`). Abbott muró los clientes viejos el 2025-10-08 con `403 {status:920}`; hoy eso es caída total hasta publicar imagen nueva. | S |
| 2.5 | Corregir la etiqueta `MaxHistoricalDays` para que diga la verdad, y documentar en `Syncing.md` que este connector no recupera más allá de la ventana del proveedor. | S |

### Fase 3 — Limpieza en el punto de convergencia

| | Trabajo | Tamaño |
| --- | --- | --- |
| 3.1 | `protected virtual NormalizeAsync(...)` en `V4RepositoryBase`, invocado desde `BulkCreateAsync`, `InsertAsync`, `BulkUpsertByLegacyIdAsync` y `SyncUpsertRepositoryBase.CreateAsync`, siguiendo el idioma que la clase ya establece con `SplitUpsertsAsync`/`PostCommitDedupAsync`. **No colgarlo de `GlucosePublisher`**: ese hook es específico de connectors y nunca vería una subida de xDrip+. | M |
| 3.2 | `int? ReportingMinMgdl` / `ReportingMaxMgdl` en `CgmProperties`/`DeviceCatalog`. Dexcom 40-400, Libre 40-500, `null` = desconocido. Nullable es esencial: un límite mal puesto convierte hiperglicemia real en lectura descartada. | S |
| 3.3 | `enum GlucoseReadingKind { Value, LowSentinel, HighSentinel, ErrorCode, WarmUp }` + clasificador estático puro, misma forma que `SensorIntegrityDetector` y `CanonicalGlucoseStream`. | M |
| 3.4 | Columna `reading_kind` text nullable sin default — `ALTER` de solo metadatos, sin reescritura de tabla, sin índice único, así que la regla de *loser-cleanup* de `CLAUDE.md` no aplica (dejarlo dicho en la migración). `NULL` se lee como `Value`, y por eso no hace falta backfill. | M |
| 3.5 | Derivar `expectedReadingsPerDay` del `UpdateIntervalMinutes` del dispositivo (`StatisticsController.cs:1360` ya hace ese lookup) y excluir los kinds distintos de `Value` de TIR/CV/GMI. | M |
| 3.6 | Honrar los spans `DataExclusion` en estadísticas, opt-in por tenant y **reportando el conteo excluido y el delta de TBR**. | M |
| 3.7 | Test guardián: `SensorGlucoseEntity` solo llega a un `DbSet` desde `SensorGlucoseRepository`, para que una cuarta ruta de ingesta no pueda saltarse el hook. | S |

### Fase 4 — Opcional, camino B

| | Trabajo | Tamaño |
| --- | --- | --- |
| 4.1 | Corregir la ventana de dedup de v1/v3 (defecto 10): buscar por clave de sincronización cuando exista, y usar la ventana solo como fallback. Preservar el eco de un objeto por entrada enviada o NightscoutKit reintenta para siempre. | L |
| 4.2 | Documentar el emparejamiento con Juggluco/xDrip+. `ServicesController.GetBaseUrl` ya entrega la URL correcta del tenant y ya existe un flujo de pairing por código de dispositivo; apuntar la documentación a esa UI en vez de pedir que el usuario arme la URL a mano. | S |

---

## 7. Riesgos

- **LibreLinkUp es revocable.** No está documentado, es ingeniería inversa, y el EULA de Abbott
  prohíbe explícitamente el acceso automatizado. La fase 2.4 mitiga la caída más probable, no la
  exposición legal. Conviene una divulgación honesta en la UI de configuración.
- **La fase 3 no hace llegar ni una lectura más.** Es calidad, no ingesta. Si el objetivo es ver
  datos fluyendo cuanto antes, la fase 1 basta y hoy ya funciona sin tocar nada.
- **3.4 crea una discontinuidad**: filas anteriores quedan `NULL` y posteriores clasificadas. Los
  reportes no deben cruzar esa frontera en silencio.
- **Los números de volumen** (~288 lecturas/día/usuario) son aritmética sobre la cadencia, no
  medición sobre datos reales de Nocturne. Si el orden de magnitud real difiere, la conclusión de
  "sin TimescaleDB, sin COPY" hay que rehacerla.

---

## 8. Pregunta abierta

Este diseño asume que el destino es **Nocturne**. Si lo que se busca es alimentar el capstone de
glucosa (`dataset.xlsx`) con una base Postgres propia, el diseño cambia por completo: no habría
multitenancy ni RLS, la limpieza sería un paso de pipeline y no un hook de repositorio, y
convendría partir de Juggluco escribiendo directo. Confirmar antes de ejecutar la fase 1.
