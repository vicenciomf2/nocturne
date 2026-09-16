# Backlog: qué queda por hacer en la cadena de glucosa

Estado al cierre de la sesión autónoma del 2026-09-16. Lo entregado está en la rama
`claude/superpowers-skills-review-bz4ljk`; esto es lo que **no** se hizo y por qué, ordenado por
retorno.

Cada ítem dice si está bloqueado por una decisión de producto (no lo decide un agente sin
supervisión), por el entorno (falta Docker o el stack levantado), o simplemente por tiempo.

---

## 1. Decisiones de producto pendientes

Estas se dejaron sin tocar a propósito. Cambian comportamiento observable sobre PHI y la decisión
es del mantenedor.

### 1.1 La ventana de dedup de v1 descarta lecturas reales — **el de mayor impacto**

`EntryReadService.MatchInWindow` declara duplicado si el dispositivo coincide y `|Δ mg/dL| < 0.01`
en cualquier punto de ±5 min, y `V1/EntriesController.cs:624` pasa `windowMinutes: 5`. La cadencia
CGM también es de 5 min y la ventana es inclusiva en ambos extremos, así que **la lectura que llega
justo después de una almacenada con el mismo valor entero se descarta y se le devuelve al uploader
como si se hubiera guardado**.

Caracterizado en `EntryDuplicateWindowDataLossTests` (commiteado, en verde: documenta el
comportamiento actual, no lo cambia). No se tocó porque `WindowEndsAreInclusive` fija el caso
deliberadamente, como paridad con el probe SQL anterior.

Opciones, de menor a mayor cambio:
- **a)** Buscar por clave de sincronización cuando la entrada la trae (`FindBySyncIdentifierAsync`
  en `SyncKeyedRepositoryBase.cs:61`) y usar la ventana solo como fallback.
- **b)** Bajar la ventana a la cadencia del dispositivo menos un margen, en vez de 5 min fijos.
- **c)** Igualar a Nightscout: dedup por timestamp exacto. Es lo que hace el objetivo de
  compatibilidad; la ventana de valor es invención de Nocturne y estrictamente más agresiva.

Restricción en cualquier caso: preservar el eco de un objeto por entrada enviada, o NightscoutKit
reintenta para siempre.

### 1.2 Piso de intervalo de sync para LibreLinkUp

Cloudflare limita por IP de salida, que en un despliegue compartido es la de todos los tenants: uno
que sondee agresivo es la caída de todos. Se reporta que 3 min lo dispara y 5 no. Se intentó poner
`MinValue = 5` y **se revirtió**: es un cambio de política que invalidaría la configuración de un
tenant ya puesto en 1-3 minutos. Necesita decidir qué pasa con los existentes.

### 1.3 Guardia de timestamp futuro para glucosa

`BasalInjection` y `ReservoirReport` rechazan timestamps a más de 5 min en el futuro; glucosa no.
La única defensa es clampear `LastReadingAt` después de escribir la fila mala
(`SensorGlucoseRepository.cs:243-250`), lo que admite que el problema existe. Rechazar podría
perder datos de un dispositivo con reloj desviado — de ahí que sea decisión y no fix.

### 1.4 `Entry.Units` se acepta y se ignora

Un uploader que mande `{units: "mmol", sgv: 5.5}` obtiene 5.5 **mg/dL** persistido. Puede ser
correcto (en Nightscout `sgv` es siempre mg/dL y `units` es metadato v3), o puede ser corrupción
silenciosa. No se tocó porque "arreglarlo" mal divide mg/dL reales por 18. Requiere confirmar la
semántica exacta de Nightscout antes de mover nada.

### 1.5 El centinela LOW como observación censurada

Hoy el 39 que manda un dispositivo cuando se niega a nombrar un valor bajo queda excluido de las
métricas (correcto: no es el número 39). Pero **sí fue un episodio hipoglicémico real**. Tratarlo
como censurado por la izquierda en vez de excluirlo es lo estadísticamente correcto y es trabajo
aparte.

---

## 2. Bloqueado por el entorno

Sin Docker no hay Testcontainers ni stack levantado, así que nada de esto se pudo ejercitar.

- **La ruta HTTP de `scripts/export-glucose.cs`.** Compila y rechaza mal uso; el paginado contra la
  API real está sin probar.
- **Tests de integración.** `tests/Integration/**` falla entero aquí por falta de Docker — no por
  los cambios de esta rama (se verificó que los fallos son de arranque de contenedor).
- **Pipeline NSwag + frontend.** Ningún cambio de esta rama toca DTOs expuestos, así que no
  hizo falta regenerar; si se añade `reading_kind` al modelo, sí.
- **El connector contra LibreLinkUp real.** Todo lo de FreeStyle está probado contra un transporte
  guionado. Nada se ha ejercitado contra Abbott.

---

## 3. La columna `reading_kind`

Escrita y analizada en `2026-09-16-reading-kind-migration-UNCOMMITTED.md`, sin integrar por
instrucción. El clasificador ya vive y ya se aplica **en lectura**, que es lo que cierra el daño
clínico. La columna añade poder consultarlo y filtrarlo en SQL.

Con ella vienen: el hook `NormalizeAsync` en `V4RepositoryBase`, el override en
`SensorGlucoseRepository`, y el test guardián de que ninguna escritura se lo salte.

---

## 4. Lo que el connector de Libre todavía no hace

- **Seguir el redirect de región automáticamente.** Hoy se diagnostica con el nombre de la región
  correcta pero no se reintenta solo. Seguirlo escondería la mala configuración y pagaría el
  round-trip extra en cada sync, porque la región corregida no se escribe de vuelta.
- **`/llu/connections/{id}/logbook`.** Unos 14 días de datos de eventos. No es backfill denso, pero
  es más de lo que hay hoy (nada).
- **Alarmas del sensor.** `isHigh`/`isLow` y `alarmRules` no se deserializan. Es literalmente la
  diferencia entre un Libre 1 y un Libre 2.
- **Identidad del sensor.** `sensor` (serial, activación) y `activeSensors[]` se descartan, así que
  el connector no distingue un Libre 2 de un Libre 3, no puede emitir eventos de cambio de sensor,
  y `Device` es la constante `"libre-connector"` para todos.
- **Dirección de tendencia.** Solo la lectura más reciente trae flecha; el resto queda
  `NotComputable` aunque la serie ordenada tiene todo lo necesario para derivarla.
- **Expiración del token.** El JWT dura 180 días (`duration: 15552000000`); el provider asume 24 h
  y fuerza un re-login diario innecesario.

---

## 5. Duplicación que sigue en pie

La investigación encontró estas y solo se resolvió la primera.

- ~~Dos cálculos de completitud~~ — resuelto: `AssessDataSufficiency` ahora delega en
  `CalculateCgmActivePercent`.
- **Cuatro implementaciones de categorización** con **tres** fuentes de umbrales:
  `GlucoseBucketResolver` (de `TargetRangeEntry`), `TenantOverviewService.Classify` (de las *reglas
  de alerta* más config), `StatisticsService`/`GlucoseZoneScale` (de `GlycemicThresholds`) y
  `ProfileLoadStage` (54/70/180/250 hardcodeados). No existe un registro de umbrales por tenant: el
  overview los reconstruye desde las reglas de alerta, así que **borrar una regla de alerta cambia
  en silencio cómo se clasifican las lecturas**.
- **Cuatro rangos de plausibilidad en lectura**: `< 600`, `< 1000`, `<= 10000`, `> 0 && !NaN`.
  Ahora hay un clasificador que podría absorberlos todos.
- **Dos autoridades de dedup** sobre la misma tabla: `DeduplicationService` (30 s, ±1 mg/dL,
  cross-source) y el probe de ventana de v1.

---

## 6. Calidad de datos ya construida y sin conectar

- **`DataExclusion`**: `CompressionLowService` escribe los spans, `StateSpansController` los lista y
  el motor de alertas los evalúa — pero **ninguna estadística ni lectura de glucosa filtra por
  ellos**. Aceptar una sugerencia de compression low hoy no mueve ningún número. (La investigación
  inicial dijo "no se leen"; la verificación adversarial lo corrigió a "no se honran".)
- **`SensorIntegrityDetector`**: sí se consume, en el reporte `/reports/data-quality/sensor-integrity`.
  No persiste nada, deliberadamente. Podría alimentar el pase nocturno que ya existe
  (`CompressionLowDetectionService`) para levantar sugerencias de revisión.
- **`CalibrationEvents` y `SensorWarmups`** siguen en cero fijo en `DataQuality`. No son derivables
  de una serie de glucosa: hacen falta los registros de calibración y las sesiones de sensor.
- **Sesiones de sensor.** No existen. `PatientDevice.StartDate` es un `DateOnly`, demasiado grueso
  para un calentamiento de 60 minutos. Sin ellas no se puede inferir `WarmUp` salvo por el
  centinela explícito.

---

## 7. Métricas de consenso que faltan

Verificadas contra Battelino et al., *Diabetes Care* 2019;42(8):1593-1603.

- **GMI** = 3.31 + 0.02392 × media mg/dL — confirmar que existe y usa esta fórmula.
- **Banda de TIR configurable**: el consenso pide 70-180 para no-embarazadas y **63-140 para
  embarazo**, y "Time in Tight Range" (70-140) viene en camino. Hoy los umbrales están fijos en
  varios sitios.
- **Gate de completitud en la respuesta**: marcar las métricas bajo 70% como no válidas según
  consenso, y señalar específicamente TBR y CV entre 70-80%, que son las que se degradan primero
  (Cichosz et al. 2025 encontró que hace falta ≥80% para R² > 0.95).
- **MAGE**: si se implementa, etiquetarlo como **no consensuado** — no está en ninguna de las dos
  tablas del consenso.

---

## 8. Infraestructura

- **Aspire 13.3.0 → 13.5.3.** Pinneado en `Directory.Packages.props` y en el SDK del AppHost.
- **`Microsoft.Extensions.Http.Resilience` 10.0.0 → 10.9.0.** Cruza nueve minors de un paquete
  cuyos defaults moldean el comportamiento de los connectors; el pipeline propio
  (`ConnectorResilience`) debería estar aislado, pero los dos `AddStandardResilienceHandler` de
  `CompatibilityProxyServiceExtensions` hay que re-probarlos.
- **Leader election** si alguna vez corre más de una réplica de la API: hoy cada instancia sondearía
  cada tenant. `DistributedLock.Postgres` (advisory lock, MIT, sin tablas nuevas) es lo más barato
  que resuelve exactamente eso.
- **`BackgroundServiceExceptionBehavior`**: el default es `StopHost`. Conviene confirmar que el
  poller envuelve cada tick en try/catch, o una excepción de connector se lleva la API.
