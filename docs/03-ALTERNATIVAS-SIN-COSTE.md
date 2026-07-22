# Alternativas sin coste para llegar al iPhone

Respuesta a la pregunta: **¿hay alguna forma de hacer esto sin comprar hardware?**

Investigación realizada sobre el hardware real de este equipo. Fecha: 2026-07-22.

---

## 1. El hardware de este PC

```
Wi-Fi        Realtek 8851BE Wireless LAN WiFi 6 PCI-E NIC   (conectado, 433 Mbps)
Ethernet     Realtek PCIe GbE Family Controller             (desconectado)
Bluetooth    Realtek Bluetooth Adapter, con soporte BLE     ✅
```

Dos consecuencias inmediatas:

- ✅ **El Bluetooth LE sirve.** La capa 1 de AirDrop (advertisement Continuity `0x05`) es
  implementable con este hardware.
- ❌ **El Wi-Fi es PCIe, no USB.** Esto cierra la ruta WSL2.

### Por qué el Wi-Fi PCIe cierra la ruta WSL2 `[CONFIRMADO]`

WSL2 corre sobre Hyper-V y **solo admite passthrough de dispositivos USB** (vía `usbipd-win`).
El passthrough PCIe no está soportado por el conmutador virtual de WSL2 — es una petición de
característica abierta en el repositorio de Microsoft, no una configuración que se pueda
activar.

Es decir: aunque el chip Realtek fuese capaz de AWDL, **WSL2 no puede verlo**.

### Y aunque pudiera verlo: el chip tampoco sirve `[PROBABLE]`

El RTL8851BE usa el driver `rtw89` en Linux. El estado de *monitor mode* e *inyección de
paquetes* para este chip figura como **desconocido/no confirmado** en las bases de datos de
referencia. Lo que sí está documentado es el patrón general: los chips Intel y Realtek
recientes suelen servir para escaneo pasivo pero **fallan en inyección**. Y OWL no necesita
inyección a secas, necesita *active monitor mode* (con ACK), que es aún más restrictivo.

**Conclusión: el Wi-Fi integrado de este PC no puede hacer AWDL, ni bajo Windows ni bajo Linux.**

---

## 2. Alternativas evaluadas

### ❌ Dual boot / Live USB de Linux con el Wi-Fi integrado
No funciona (chip incapaz, §1). Y aunque funcionara, el resultado sería *Linux con OpenDrop*,
no una aplicación de Windows. No cumple el objetivo.

### ❌ Wi-Fi Direct de Windows
Protocolo distinto e incompatible con AWDL. Un iPhone no responde a Wi-Fi Direct. Descartado
en el documento de investigación §7.2.

### ❌ Npcap en modo monitor
Solo captura **pasiva**. No existe ningún driver de Windows con inyección 802.11. Sin
inyección no hay Action Frames, y sin Action Frames no hay AWDL.

### ❌ Usar un Android como puente
La interoperabilidad Quick Share ↔ AirDrop (Pixel 10, y en 2026 Samsung, Xiaomi, OPPO,
OnePlus, vivo, Honor) es real, pero requiere **soporte a nivel de chipset** para imitar AWDL
— no es un parche de software. Además, aunque tuvieras un teléfono compatible, no expone
ninguna API para actuar de relé hacia un PC. No es una ruta.

### ❌ Esperar a la apertura por la DMA europea
Apple ha optado por abrir **Wi-Fi Aware**, no AWDL, y esas APIs **exigen instalar una app en
el iPhone** — justo lo que este proyecto descarta. Además, a 22 de marzo de 2026 ninguna de
las 56 solicitudes formales de interoperabilidad había producido una solución. No es una ruta
a corto plazo.

---

## 3. Lo único gratis que podría funcionar: hardware que ya tengas

El puente AWDL (Ruta B) **no exige comprar nada si ya dispones de alguno de estos**:

| Ya tienes… | ¿Sirve? | Notas |
|-----------|---------|-------|
| Un **adaptador USB Wi-Fi** antiguo | ✅ **Muy probablemente** | Los buenos son Atheros **AR9271** (TL-WN722N **v1**) o **RTL8812AU**. Merece la pena mirar en el cajón. |
| Una **Raspberry Pi 3 / 4** | ⚠️ Con matices | El Wi-Fi integrado (Broadcom) requiere el parche de firmware *nexmon*. Es la plataforma para la que se documentó OWL originalmente. |
| Un **portátil viejo** con Wi-Fi Intel/Atheros | ✅ Posible | Se instala Linux y OWL. El Intel 8265 está reportado como funcional con OWL. |
| Un **Mac** (aunque sea antiguo) | ✅ Sí | Habla AWDL de forma nativa. Es el puente más fiable de todos. |
| Un **router viejo** con OpenWrt | ❌ | Sin soporte AWDL ni portabilidad de OWL. |

> **Esta es la pregunta clave a responder antes de descartar la Ruta B: ¿tienes alguno?**
> Si la respuesta es sí, el objetivo original —aparecer en la hoja de AirDrop del iPhone— es
> alcanzable **sin gastar un euro**.

Si la respuesta es no, el coste mínimo real es un adaptador AR9271 (~15-20 €). No hay forma
de rebajarlo: el requisito es físico, no de software.

---

## 4. El plan B honesto: transferencia sin AirDrop y sin instalar nada en el iPhone

Si no hay hardware disponible, **el objetivo funcional —mover archivos entre el iPhone y este
PC sin instalar nada en el teléfono— sí es alcanzable hoy, al 100 %, gratis.** Lo que no es
alcanzable es que ocurra *a través de la hoja de AirDrop*.

`[CONFIRMADO]` La app **Archivos** de iOS incluye de fábrica un cliente **SMB**
(*Explorar → ⋯ → Conectar a servidor → `smb://192.168.x.x`*). No requiere instalar nada:
es una función nativa de iOS desde iOS 13.

Esto permite construir, dentro de esta misma aplicación:

- **iPhone → Windows**: la app levanta un recurso compartido local; desde Archivos o desde la
  hoja de compartir del iPhone se guardan las fotos directamente en el PC.
- **Windows → iPhone**: los archivos aparecen en Archivos del iPhone, o se sirven por HTTP
  local con un código QR que se abre en Safari.

**Ventajas:** funciona hoy, con este hardware, sin instalar nada en el iPhone, sin servidores
externos, todo local.
**Desventaja honesta:** no es AirDrop. Son 3-4 toques en el iPhone en lugar de dos, y el
iPhone no "descubre" el PC solo.

Esto **no sustituye** al objetivo del proyecto ni se presentará como AirDrop en la UI. Es un
modo adicional, claramente etiquetado, para que la aplicación sea útil desde el primer día
mientras la Ruta B siga bloqueada por hardware.

---

## 5. Resumen para decidir

| Objetivo | Coste | Estado |
|----------|-------|--------|
| Protocolo AirDrop completo, Windows ↔ Windows y ↔ OpenDrop | **0 €** | ✅ Se construye ya (Ruta A) |
| **Aparecer en la hoja de AirDrop del iPhone** | **0 € si ya tienes hardware compatible**, si no ~15-20 € | ⚠️ Bloqueado por hardware, no por software |
| Mover archivos iPhone ↔ PC sin instalar nada en el iPhone | **0 €** | ✅ Vía SMB/HTTP local (§4) |
| Modo "Solo contactos" | — | ❌ Imposible: certificados firmados por Apple |
