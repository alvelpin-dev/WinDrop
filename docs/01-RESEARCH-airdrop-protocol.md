# Investigación: el protocolo AirDrop de Apple

> Documento de investigación previo a la implementación.
> Fecha: 2026-07-22.
> Cada afirmación está marcada con su nivel de confianza y su fuente.
>
> **Leyenda de confianza**
> - `[CONFIRMADO]` — documentado en papers revisados, código open source funcional, o documentación oficial de Apple.
> - `[PROBABLE]` — coherente entre varias fuentes secundarias, pero no verificado por mí sobre tráfico real.
> - `[POR VERIFICAR]` — hipótesis de trabajo. **No implementar sin capturar tráfico real primero.**
>
> Este proyecto no copia código de ningún proyecto existente. Las fuentes se usan
> exclusivamente para comprender el protocolo.

---

## 1. Resumen ejecutivo (leer esto primero)

AirDrop no es un protocolo. Es una **pila de cinco capas** y cada una tiene un nivel de
viabilidad distinto en Windows 11:

| # | Capa | Tecnología | ¿Viable en Windows 11? |
|---|------|-----------|------------------------|
| 1 | Despertador / proximidad | BLE advertisement, Apple Continuity type `0x05` | ✅ **Sí** — WinRT `BluetoothLEAdvertisementPublisher` |
| 2 | **Enlace L2** | **AWDL (Apple Wireless Direct Link)** | ❌ **NO** — bloqueo duro, ver §7 |
| 3 | Red | IPv6 link-local sobre la interfaz AWDL | ✅ Sí (si existiera la capa 2) |
| 4 | Descubrimiento | mDNS / DNS-SD, `_airdrop._tcp.local` | ✅ **Sí** — implementable desde cero |
| 5 | Aplicación | HTTPS/TLS en `:8770`, bplist, CPIO+gzip | ✅ **Sí** — implementable desde cero |

**La conclusión que condiciona todo el proyecto:**

> Las capas 1, 3, 4 y 5 —es decir, el 90 % del protocolo AirDrop y prácticamente todo el
> código— **son implementables de forma nativa y fiable en Windows 11**. La capa 2 (AWDL)
> **no lo es**, y un iPhone **solo** hace AirDrop sobre AWDL.

Esto significa que una aplicación Windows *pura*, por bien escrita que esté, **no puede
aparecer en la hoja de AirDrop de un iPhone**. No es una cuestión de esfuerzo de
programación: es una limitación del modelo de drivers de Windows (NDIS), detallada y
justificada en §7. Cualquier proyecto que prometa lo contrario sin hardware adicional
está describiendo otra cosa (normalmente un protocolo propio tipo LocalSend, que exige
instalar una app en el iPhone — justo lo que este proyecto quiere evitar).

La ruta técnicamente honesta hacia el objetivo completo se detalla en §8: **un puente AWDL**
sobre hardware capaz, con la aplicación Windows conservando toda la lógica del protocolo.

---

## 2. Flujo completo de una transferencia AirDrop

Secuencia real cuando un iPhone envía una foto a un Mac. `[CONFIRMADO]`

```
   iPhone (emisor)                                    Mac (receptor)
        │                                                   │
        │ 1. El usuario pulsa Compartir → AirDrop           │
        │                                                   │
        │──── BLE ADV (Continuity 0x05, hashes cortos) ────>│
        │     Anuncia "voy a hacer AirDrop" + identidad      │
        │     truncada del emisor                            │
        │                                                   │
        │                        2. El receptor despierta AWDL y sharingd
        │<──────────── activa interfaz awdl0 ───────────────│
        │                                                   │
        │ 3. Ambos sincronizan AWDL (L2): election,          │
        │    availability windows, channel hopping 6/44/149  │
        │<================ AWDL data path ==================>│
        │                                                   │
        │ 4. mDNS sobre awdl0                               │
        │──── PTR? _airdrop._tcp.local ────────────────────>│
        │<─── PTR/SRV/TXT/AAAA (puerto 8770, flags) ────────│
        │                                                   │
        │ 5. TLS 1.2 sobre IPv6 link-local, puerto 8770     │
        │──── POST /Discover  (bplist: SenderRecordData) ──>│
        │<─── 200 (bplist: ReceiverComputerName, ...) ──────│
        │     El receptor aparece en la UI del iPhone       │
        │                                                   │
        │ 6. El usuario elige el destino                    │
        │──── POST /Ask  (bplist: Files[], FileIcon) ──────>│
        │                             [ el receptor muestra el diálogo ]
        │                             [ bloquea en un semáforo         ]
        │<─── 200 (bplist: ReceiverComputerName) ───────────│  (Aceptar)
        │<─── 4xx  ─────────────────────────────────────────│  (Rechazar)
        │                                                   │
        │ 7. POST /Upload (CPIO 'newc' + gzip, sin header   │
        │──── Content-Encoding) ──────────────────────────>│
        │<─── 200 ──────────────────────────────────────────│
```

Puntos críticos que condicionan el diseño:

- Los pasos 5, 6 y 7 viajan **por la misma conexión TLS**. `[CONFIRMADO]` — el receptor
  mantiene tres "slots" de petición (Discover, Ask, Upload) asociados a la conexión.
- `/Discover` es **opcional**. Un emisor puede ir directo a `/Ask`.
- El paso 1 (BLE) es lo que **despierta** el AWDL del receptor. Sin él, un iPhone en reposo
  ni siquiera tiene la interfaz `awdl0` activa. `[CONFIRMADO]` — es la limitación que
  OpenDrop documenta explícitamente.

---

## 3. Capa 1 — Descubrimiento BLE (Apple Continuity)

### 3.1 Formato del advertisement

El anuncio va en la estructura AD `0xFF` (Manufacturer Specific Data), con el Company ID
de Apple `0x004C` (little-endian en el aire: `4C 00`), seguido de uno o varios mensajes
Continuity en formato TLV. `[CONFIRMADO]`

Mensaje AirDrop (`type = 0x05`): `[CONFIRMADO]`

| Offset | Bytes | Campo |
|--------|-------|-------|
| 0 | 1 | Tipo = `0x05` |
| 1 | 1 | Longitud = `0x12` (18) |
| 2 | 8 | Ceros (padding/reservado) |
| 10 | 1 | Versión (típicamente `0x01`) |
| 11 | 2 | SHA-256 truncado del **Apple ID** |
| 13 | 2 | SHA-256 truncado del **número de teléfono** |
| 15 | 2 | SHA-256 truncado del **email** |
| 17 | 2 | SHA-256 truncado del **email 2** |
| 19 | 1 | Terminador `0x00` |

**Truncamiento:** SHA-256 del identificador normalizado, conservando solo los primeros
2 bytes (16 MSB). `[CONFIRMADO]`

> ⚠️ 2 bytes = 65 536 valores posibles. Esto es un identificador de *filtrado rápido*,
> no una prueba de identidad. El paper AirCollect demuestra que estos hashes son
> reversibles por fuerza bruta para números de teléfono. La validación real de identidad
> ocurre en la capa 5 con certificados. Nuestra implementación debe tratarlo como una
> **pista**, nunca como autenticación.

### 3.2 Viabilidad en Windows — ✅ Implementable

`Windows.Devices.Bluetooth.Advertisement.BluetoothLEAdvertisementPublisher` permite
publicar `ManufacturerData` arbitrario, incluido el Company ID `0x004C`.
`BluetoothLEAdvertisementWatcher` permite escanear y filtrar por fabricante.

`[POR VERIFICAR]` — Hay que comprobar empíricamente si Windows impone restricciones al
publicar con el Company ID de Apple, y si el intervalo de advertisement configurable es
suficientemente agresivo para que el iPhone reaccione a tiempo. **Test 1 del plan de
validación (§9).**

---

## 4. Capa 2 — AWDL

Esta es la capa que decide la viabilidad del proyecto entero.

### 4.1 Qué es

AWDL es un protocolo ad hoc propietario y no documentado, construido sobre IEEE 802.11.
Fue íntegramente reverse-engineered por el Secure Mobile Networking Lab (TU Darmstadt) en
el paper *"One Billion Apples' Secret Sauce"* (2018), y reimplementado en C en el proyecto
OWL. `[CONFIRMADO]`

Permite que dos dispositivos Apple mantengan un canal de datos de alta velocidad **a la vez**
que siguen conectados a sus redes Wi-Fi de infraestructura, alternando en el tiempo entre
el canal del AP y el canal AWDL.

### 4.2 Mecánica

`[CONFIRMADO]` — a partir del paper y de la implementación de OWL:

- **Action Frames**: frames 802.11 *Vendor Specific Action* con OUI de Apple `00:17:F2`
  y `Type = 8`. Contienen una cabecera fija más TLVs (1 byte tipo + 2 bytes longitud + valor).
- **TLVs mínimos**: para que macOS acepte el frame se requieren al menos el TLV `4`
  (*Synchronization Parameters*) y el TLV `18` (*Channel Sequence*), **y deben ser los dos
  primeros, en ese orden**.
- **Election**: los peers eligen un máster que marca la referencia temporal.
- **Availability Windows**: ventanas temporales sincronizadas (múltiplos de TU) en las que
  los peers están simultáneamente escuchando.
- **Channel hopping**: salto coordinado entre los canales 6, 44 y 149.
- **Datos**: frames 802.11 data con cabecera LLC y OUI `00:17:F2`, transportando IPv6.
- **Direccionamiento**: IPv6 link-local derivado de una MAC aleatorizada, no de la MAC fija
  del chip Wi-Fi.

### 4.3 Requisitos de hardware/SO que impone

Para hablar AWDL hace falta, obligatoriamente: `[CONFIRMADO]`

1. **Monitor mode activo** — capturar frames 802.11 crudos **y responder ACK**. El monitor
   mode pasivo no basta: sin ACK, el emisor retransmite hasta 7 veces por frame y el
   rendimiento colapsa.
2. **Frame injection** — transmitir frames 802.11 arbitrarios (los Action Frames).
3. **Control fino del canal** — cambiar de canal en milisegundos, sincronizado.

OWL exige exactamente esto y documenta el chip Atheros AR9280 como referencia de desarrollo.
Además, OWL toma la interfaz Wi-Fi en exclusiva: **no soporta estar conectado a un AP
simultáneamente**, a diferencia del AWDL nativo de Apple (que lo hace en firmware).

---

## 5. Capa 4 — Descubrimiento mDNS / DNS-SD

`[CONFIRMADO]` — servicio `_airdrop._tcp.local`, resuelto con registros PTR, SRV, TXT y AAAA.
El SRV apunta al puerto **8770**.

El registro TXT contiene ~25 claves. La más importante es `flags`, una máscara de bits que
declara capacidades. `[PROBABLE]` — valor típico observado en macOS: `1019` (`0x3FB`).

Bits identificados: `[PROBABLE]` — coherente entre el paper *Protocol Prying* y el trabajo
de bakedbean.org.uk, pero **deben confirmarse contra tráfico real antes de fijarlos en
código**:

| Bit | Valor | Significado |
|-----|-------|-------------|
| 0 | 1 | `SupportsURL` — compartir URLs |
| 1 | 2 | `SupportsDVZip` — formato comprimido propietario de Apple |
| 2 | 4 | `SupportsPipelining` — subida encadenada |
| 3 | 8 | `SupportsMixedTypes` — tipos de fichero mezclados |
| 4 | 16 | *desconocido* |
| 5 | 32 | *desconocido* |
| 6 | 64 | `SupportsIris` |
| 7 | 128 | `SupportsDiscoverMayest` — soporta `/Discover` |
| 8 | 256 | *desconocido* |
| 9 | 512 | `SupportsAssetBundle` |

Existe además un `SupportsUPP` que permite `/Upload` **sin** un `/Ask` previo. `[PROBABLE]`

> **Decisión de diseño derivada:** anunciaremos un `flags` **conservador**, declarando solo
> lo que realmente implementamos. En concreto **no** declararemos `SupportsDVZip`: DVZip es
> un formato propietario de Apple no documentado (§6.4), y declararlo haría que el emisor nos
> mandara datos que no sabemos decodificar. Declarar de menos degrada a un camino más simple;
> declarar de más rompe la transferencia.

### 5.1 Viabilidad en Windows — ✅ Implementable

Implementaremos un responder mDNS propio (UDP 5353, multicast `224.0.0.251` / `ff02::fb`).
No usaremos Bonjour de Apple: introduce una dependencia propietaria innecesaria y conflictos
con el servicio Bonjour de iTunes si está instalado.

---

## 6. Capa 5 — Protocolo de aplicación (HTTPS)

### 6.1 Transporte

- TLS sobre TCP **8770**. `[CONFIRMADO]`
- **Certificados auto-firmados y sin verificación de peer a nivel TLS.** El TLS de AirDrop
  aporta *cifrado*, no *autenticación*. `[CONFIRMADO]` — paper *Protocol Prying*.
- Consecuencia directa y **muy favorable** para nosotros: **no necesitamos un certificado
  firmado por Apple para establecer la conexión TLS.** La identidad se valida (o no) en una
  capa superior, dentro del plist.

### 6.2 Endpoints

Siete endpoints POST: `/Discover`, `/Hello`, `/Ask`, `/Upload`, `/Exchange`,
`/SharedIdentity`, `/Error`. `[CONFIRMADO]`

Los tres imprescindibles para una transferencia son `/Discover`, `/Ask` y `/Upload`.

Todos los cuerpos son **binary property lists** (`bplist00`), con
`Content-Type: application/octet-stream`. `[CONFIRMADO]`

#### `POST /Discover`

Petición: `[CONFIRMADO]`
- `SenderRecordData` — blob **PKCS#7 / CMS firmado** que contiene los hashes de los
  identificadores de contacto del emisor. Firmado por la cadena de CA de Apple.

Respuesta (200): `[CONFIRMADO]`
- `ReceiverComputerName` — el nombre que se muestra en la UI de AirDrop del iPhone
- `ReceiverModelName` — identificador de modelo
- `ReceiverMediaCapabilities` — JSON en UTF-8 embebido dentro del plist

Códigos de estado en el plist: `100` = modo Todos, `200` = contactos verificados,
`401` = solo-contactos sin coincidencia. `[PROBABLE]`

#### `POST /Ask`

Petición: `[CONFIRMADO]`
- `SenderComputerName`, `SenderModelName`, `SenderID`
- `BundleID` de la app emisora
- `ConvertMediaFormats` (bool)
- `FileIcon` — miniatura en **JPEG 2000**
- `Files` — array de metadatos, cada uno con `FileName`, `FileType` (UTI),
  `FileBomPath`, `FileIsDirectory`, `ConvertMediaFormats`

Respuesta: `ReceiverComputerName`, `ReceiverModelName`. El receptor **bloquea** esta
petición mientras el diálogo de consentimiento está en pantalla. `[CONFIRMADO]`

> **Decisión de diseño derivada:** nuestro servidor HTTP debe soportar peticiones de
> larga duración sin timeout agresivo, y el diálogo de la UI debe poder cancelar la
> petición limpiamente.

> ⚠️ `FileIcon` en JPEG 2000: .NET no decodifica JPEG 2000 de forma nativa. Al **recibir**
> podemos ignorar el icono sin consecuencias. Al **enviar**, es `[POR VERIFICAR]` si el
> iPhone tolera su ausencia. Hipótesis: sí, es opcional. **Test 4 del plan (§9).**

#### `POST /Upload`

Cuerpo: archivo **CPIO** comprimido con **gzip**. `[CONFIRMADO]`

> ⚠️ **Trampa documentada:** el cuerpo va comprimido en gzip **sin cabecera
> `Content-Encoding`**. Hay que descomprimir incondicionalmente. Esto rompe cualquier
> pipeline HTTP estándar que se fíe de las cabeceras.

Formato CPIO: variante **`odc`** (cabeceras ASCII en octal) cuando no se negocia DVZip, y
la variante **`newc`** (magic `070701`) también aparece documentada en el paper de 2026.
`[POR VERIFICAR]` — cuál se usa exactamente depende de la versión y de los flags negociados.
**Implementaremos un lector que detecte ambas por su magic.** El archivo termina con una
entrada cuyo nombre es `TRAILER!!!`.

Campos de la cabecera `odc` (todos como cadenas octales ASCII): `c_magic`, `c_dev`, `c_ino`,
`c_mode`, `c_uid`, `c_gid`, `c_nlink`, `c_rdev`, `c_mtime`, `c_namesize`, `c_filesize`.
`[CONFIRMADO]`

### 6.3 `FileBomPath` y BOM

Apple extrae el CPIO con su librería **BOM** (Bill of Materials), propietaria.
`[CONFIRMADO]` Nosotros no la necesitamos para **recibir** (basta con leer CPIO). Para
**enviar**, `FileBomPath` es una ruta relativa dentro del archivo (típicamente `./NombreArchivo`)
que debe ser coherente con las rutas del CPIO. `[PROBABLE]`

### 6.4 DVZip — componente propietario cerrado

`Content-Type: application/x-dvzip`. Formato adaptativo por chunks: cabeceras de longitud de
4 bytes big-endian envolviendo bloques zlib/deflate individuales. `[PROBABLE]`

**Estado:** formato propietario de Apple, no documentado públicamente, sin implementación
open source completa conocida. **Lo evitamos no anunciando el bit correspondiente en los
flags mDNS.** Queda registrado en §10 como limitación conocida.

### 6.5 Autenticación e identidad — el punto duro

Este es el segundo componente propietario, y determina qué modos de visibilidad podemos
soportar.

`SenderRecordData` es un blob PKCS#7 firmado que contiene los hashes SHA-256 de los emails y
teléfonos del emisor, y va firmado por la cadena de **Apple Application Integration CA**.
`[CONFIRMADO]`

Consecuencias, por modo de visibilidad:

| Modo | ¿Qué exige? | ¿Viable para nosotros? |
|------|-------------|------------------------|
| **Recibir de "Todos"** | Nada. El receptor acepta sin validar identidad. | ✅ **Sí** |
| **Enviar a un iPhone en "Todos"** | Nada firmado por Apple. | ✅ **Sí** (esto es lo que explota OpenDrop) |
| **Solo contactos** | Un certificado de identidad de Apple ID **firmado por Apple**, ligado a una cadena hardware-bound | ❌ **No** — imposible de generar |
| Auto-aceptación (mismo Apple ID) | `SDAppleIDAuthenticateCertificateChainSync` contra cadena hardware-bound | ❌ **No** |

**No existe forma legítima de generar un certificado de identidad de Apple ID.** La única vía
para "solo contactos" sería *extraer* el certificado del llavero de un Mac o iPhone del propio
usuario. Es lo que hace la herramienta de extracción de llavero de OpenDrop. Nuestro diseño:

- **Modo "Todos": soportado de forma nativa y completa.**
- **Modo "Solo contactos": no soportado.** Se expondrá en la UI como deshabilitado, con una
  explicación honesta del motivo, no oculto ni simulado.
- **Nosotros sí validaremos** la firma PKCS#7 del `SenderRecordData` entrante contra la CA
  raíz de Apple cuando esté presente, para poder **mostrar** al usuario si el emisor está
  verificado. Es información de confianza, no una puerta de acceso.

---

## 7. El bloqueo: por qué AWDL no es implementable en Windows

Esta sección justifica la afirmación más importante del documento.

### 7.1 Lo que haría falta

Del §4.3: monitor mode **activo** (con ACK), inyección de frames 802.11 arbitrarios, y
control de canal en tiempo real.

### 7.2 Lo que Windows ofrece

`[CONFIRMADO]`

- **NDIS**, el modelo de drivers de red de Windows, **no expone captura de frames 802.11
  crudos ni inyección** a nivel de aplicación. La abstracción que ofrece a los drivers Wi-Fi
  es la de un adaptador Ethernet; los frames de gestión y control 802.11 los maneja el
  firmware/driver y no son accesibles.
- **Npcap** con la opción *"Support raw 802.11 traffic (and monitor mode)"* instala un filtro
  adicional que usa la **Native WiFi API** para capturar frames 802.11 crudos. Es
  **captura pasiva únicamente**. La propia documentación y el ecosistema (Wireshark) advierten
  que el soporte es muy limitado: pocas tarjetas, a menudo sin poder cambiar de canal, y con
  radiotap sin metadatos útiles.
- **No existe ningún driver de Windows que soporte inyección de paquetes 802.11 crudos.**
  Esto incluye a los adaptadores Alfa, que sí funcionan en Linux. Es una limitación del
  modelo de drivers, no del hardware.
- **Wi-Fi Direct** (que Windows sí soporta) es un protocolo **distinto e incompatible**:
  usa P2P/WPS del Wi-Fi Alliance. No habla AWDL. Un iPhone no responde a Wi-Fi Direct.
- **Wi-Fi Aware / NAN** es el estándar del Wi-Fi Alliance *derivado* de AWDL — Apple aportó
  elementos de AWDL a NAN. Pero **NAN ≠ AWDL**: no son interoperables en el aire. Y el
  `sharingd` del iPhone habla AWDL, no NAN.

### 7.3 Conclusión

> **Implementar AWDL como software puro sobre un adaptador Wi-Fi estándar de Windows 11 es
> imposible.** No por dificultad, sino porque la capacidad necesaria (inyección de frames
> 802.11) no está expuesta por ningún driver de Windows.

Esto es coherente con toda la evidencia externa:

- El proyecto **AirDrop Anywhere** (bakedbean.org.uk, .NET/C#) intentó exactamente esto,
  chocó con esta misma pared, y acabó pivotando a **un modelo de proxy sobre hardware
  AWDL-capaz**. Su cita clave: los iPhone y iPad solo atienden AirDrop si se anuncia sobre la
  interfaz AWDL — el mDNS sobre la Wi-Fi normal **no sirve** con dispositivos iOS.
- **OWL** solo existe para Linux y macOS, y aun ahí exige tarjetas concretas.
- **Google** logró la interoperabilidad Quick Share ↔ AirDrop en el Pixel 10 sin colaboración
  de Apple, pero **controlando el firmware/driver Wi-Fi del propio teléfono** — un nivel de
  acceso al hardware que en Windows sencillamente no tenemos.
- El modo AirDrop *legacy* de macOS (Wi-Fi de infraestructura, sin AWDL) **está documentado
  como incompatible con iPhones**: hay que salir de él para poder usar AirDrop con iOS.
  Confirma que no existe un camino "sin AWDL" hacia un iPhone.

### 7.4 ¿Y la vía regulatoria (DMA)?

Investigada por si abría una puerta legítima. `[CONFIRMADO]`

La Comisión Europea obligó a Apple a ofrecer a terceros una solución de compartición Wi-Fi P2P
equivalente a AirDrop. **Apple ha optado por abrir Wi-Fi Aware, no por abrir AWDL**, mediante
frameworks de iOS 26 (Wi-Fi Aware, AccessorySetupKit, DeviceDiscoveryUI).

**Esto no resuelve nuestro caso**, y es importante entender por qué: esas APIs requieren
**una app instalada en el iPhone**, que es exactamente el requisito que este proyecto
descarta. La hoja de AirDrop nativa de iOS sigue hablando AWDL.

Estado a fecha de este documento: a 22 de marzo de 2026, ninguna de las 56 solicitudes
formales de interoperabilidad había desembocado en una solución, y Apple sigue recurriendo
las obligaciones. **No hay que contar con esta vía a corto plazo.**

---

## 8. Rutas viables hacia el objetivo

Evaluadas en orden de fidelidad al objetivo original.

### Ruta A — App Windows pura, sin hardware extra
**Alcance:** capas 1, 3, 4 y 5 completas sobre Wi-Fi de infraestructura / Ethernet.
- ✅ Protocolo AirDrop real y completo (mDNS, TLS:8770, bplist, CPIO, Discover/Ask/Upload).
- ✅ Interopera con **otra instancia de esta misma app** (Windows ↔ Windows).
- ✅ Interopera con **OpenDrop en la misma red**.
- ⚠️ Con **Macs**: `[POR VERIFICAR]`, previsiblemente solo en su modo legacy y con
  intervención manual.
- ❌ **Con iPhone: no.** Bloqueo AWDL.

**Valor:** es el 90 % del código, es la base obligatoria de cualquier otra ruta, y es
verificable end-to-end sin hardware. **Se construye primero, siempre.**

### Ruta B — Puente AWDL (la única que alcanza el iPhone) ⭐
La app Windows conserva **toda** la lógica del protocolo; delega **solo la capa 2** en un
host capaz de AWDL:

```
  ┌──────────────────────────┐        ┌───────────────────────────┐
  │  Windows 11 (esta app)   │        │  Host puente (Linux)      │
  │  UI, mDNS, TLS, bplist,  │◄──────►│  OWL → interfaz awdl0     │
  │  CPIO, transfers, BLE    │  túnel │  Adaptador Wi-Fi con      │
  │                          │        │  monitor activo+injection │
  └──────────────────────────┘        └─────────────┬─────────────┘
                                                    │ AWDL (802.11)
                                                    ▼
                                                 iPhone
```

Dos materializaciones:

- **B1 — WSL2 + adaptador USB Wi-Fi.** Todo en la misma máquina. Requiere `usbipd-win` para
  pasar el adaptador USB a WSL2 y un **kernel WSL2 recompilado** con `mac80211`, `cfg80211` y
  el driver del chip. Adaptador de referencia: **AR9271** (`ath9k_htc`, p. ej. TP-Link
  TL-WN722N **v1** — la v2/v3 llevan otro chip y **no** sirven), ~15-30 €.
  `[POR VERIFICAR]` — que `ath9k_htc` soporte *active monitor mode* (no solo monitor +
  injection) bajo WSL2. Es el riesgo técnico principal de esta ruta. **Test 6 del plan (§9).**
- **B2 — Host externo** (Raspberry Pi con adaptador compatible, o un Mac). Más fiable,
  menos elegante, requiere un segundo dispositivo encendido.

**Honestidad sobre esta ruta:** requiere hardware adicional y una configuración avanzada.
No es plug-and-play y no debe presentarse como tal. Pero es **la única ruta real** al AirDrop
nativo de un iPhone desde Windows.

### Ruta C — Protocolo propio (LocalSend/PairDrop)
❌ **Descartada.** Exige instalar una app en el iPhone. Viola el requisito central del
proyecto. Se menciona solo para dejar constancia de que se evaluó.

### Decisión arquitectónica

Construir la **Ruta A** con la capa de transporte **abstraída tras una interfaz**
(`IAirDropTransport`), de modo que la **Ruta B** sea un *plugin* de transporte adicional y no
una reescritura. Ninguna línea de la lógica de protocolo debe saber si va sobre Wi-Fi normal
o sobre un puente AWDL.

---

## 9. Plan de validación empírica

Todo lo marcado `[POR VERIFICAR]` se resuelve con estos tests. **No se escribe código
definitivo sobre una hipótesis sin ejecutar antes su test.**

| # | Test | Qué resuelve | Estado |
|---|------|--------------|--------|
| 1 | Publicar advertisement BLE Continuity `0x05` desde Windows y ver si un iPhone reacciona | ¿Windows deja usar el Company ID de Apple? | ⏳ Pendiente |
| 2 | Escanear con Windows los advertisements BLE de un iPhone al abrir su hoja AirDrop | Valida el parser y el formato del §3.1 | ⏳ Pendiente |
| 3 | Observar si un dispositivo Apple anuncia `_airdrop._tcp` por Wi-Fi de infraestructura | Confirma §5 y el alcance real de la Ruta A | ✅ **Ejecutado** — §9.1 |
| 4 | Enviar `/Ask` sin `FileIcon` | ¿Es opcional el JPEG 2000? | ⏳ Pendiente |
| 5 | Capturar un `/Upload` real y volcar los primeros bytes | ¿CPIO `odc` o `newc`? | ⏳ Pendiente |
| 6 | `ath9k_htc` bajo WSL2: `iw list` → ¿aparece *active monitor*? | Viabilidad de la Ruta B1 | ❌ Descartado — ver [03](03-ALTERNATIVAS-SIN-COSTE.md) |

### 9.1 Resultado del test 3 — `[CONFIRMADO]` con evidencia propia

**Fecha:** 2026-07-22. **Herramienta:** `tools/AirDrop.Diagnostics`, 30 segundos de escucha
activa con meta-consulta DNS-SD y consulta PTR de AirDrop.

**Entorno:** iPhone (iOS 26) conectado a la misma red Wi-Fi doméstica que el PC Windows,
desbloqueado y con la hoja de AirDrop abierta.

**Servicios anunciados por el iPhone** (`192.168.1.41`, `iPhone-de-Alvaro.local`):

```
_remotepairing._tcp     puerto 49152   ver=26, minVer=8, flags=0
_apple-mobdev2._tcp     puerto 32498   (emparejamiento de dispositivo iOS)
4c680575._sub._apple-mobdev2._tcp
```

**`_airdrop._tcp`: NO anunciado.**

**Conclusión.** El iPhone está plenamente activo en la red de infraestructura y anuncia por
mDNS varios servicios propios de Continuity. Pero **no anuncia AirDrop**, ni siquiera con la
hoja de compartir abierta. Esto **confirma con datos propios** la conclusión del §7: AirDrop
se anuncia exclusivamente sobre la interfaz AWDL. No existe una vía de descubrimiento por
Wi-Fi de infraestructura hacia un iPhone.

> Este test era la última posibilidad de que existiera un atajo que evitara AWDL. No existe.
> La limitación L1 del §10 queda verificada empíricamente, no solo documentada.

**Nota adicional:** el iPhone reporta `ver=26`, es decir iOS 26, que ya incluye los frameworks
de Wi-Fi Aware abiertos por la DMA (§7.4). Sigue sin resolver este caso, porque esas APIs
requieren una app instalada en el iPhone.

---

## 10. Limitaciones conocidas — registro permanente

Se mantiene actualizado. Nada de esto se disimula en la UI.

| # | Limitación | Causa | ¿Alternativa? |
|---|-----------|-------|---------------|
| L1 | **No se puede aparecer en la hoja AirDrop de un iPhone sin hardware adicional** | AWDL: NDIS no permite inyección 802.11. **Verificado empíricamente**, §9.1 | Ruta B (puente) |
| L2 | **Modo "Solo contactos" no soportado** | Requiere certificado de Apple ID firmado por Apple | Ninguna legítima. Se deshabilita en la UI. |
| L3 | Auto-aceptación entre dispositivos del mismo Apple ID no soportada | Cadena de certificados hardware-bound | Ninguna. Siempre habrá confirmación manual. |
| L4 | **DVZip no soportado** | Formato propietario cerrado de Apple | Se evita no anunciando el flag |
| L5 | `FileIcon` en JPEG 2000 no se genera al enviar | Sin códec JPEG 2000 nativo en .NET | `[POR VERIFICAR]` si es opcional (Test 4) |
| L6 | Extracción con BOM no replicada | Librería propietaria de Apple | Lector CPIO propio: suficiente para recibir |

---

## 11. Fuentes

Investigación, no copia de código:

- Stute et al., *One Billion Apples' Secret Sauce: Recipe for the Apple Wireless Direct Link Ad hoc Protocol* — https://arxiv.org/pdf/1808.03156
- *Protocol Prying: Systematic Vulnerability Research in the Apple AirDrop and Android Quick Share Proximity Transfer Protocols* (2026) — https://arxiv.org/html/2606.26967v1
- Open Wireless Link — https://owlink.org/
- seemoo-lab/owl — https://github.com/seemoo-lab/owl
- seemoo-lab/opendrop — https://github.com/seemoo-lab/opendrop
- Martin et al., *Handoff All Your Privacy: Personal Data Leaks in Apple Bluetooth-Low-Energy Continuity Protocols* — https://petsymposium.org/popets/2020/popets-2020-0003.pdf
- furiousMAC/continuity, mensaje AirDrop — https://github.com/furiousMAC/continuity/blob/master/messages/airdrop.md
- PrivateDrop — https://privatedrop.github.io/
- AirCollect — https://eprint.iacr.org/2021/893.pdf
- *AirDrop Anywhere*, partes 1-3 — https://bakedbean.org.uk/posts/2021-05-airdrop-anywhere-part-1/
- AirDrop security, Apple Platform Security — https://support.apple.com/guide/security/airdrop-security-sec2261183f4/web
- Npcap Reference Guide — https://npcap.com/guide/index.html
- Comisión Europea, DMA — Interoperabilidad — https://digital-markets-act.ec.europa.eu/questions-and-answers/interoperability_en
