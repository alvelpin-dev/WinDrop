# Arquitectura

Decisiones derivadas de [01-RESEARCH-airdrop-protocol.md](01-RESEARCH-airdrop-protocol.md).

## Stack elegido

| Capa | Elección | Motivo |
|------|----------|--------|
| Runtime | **.NET 10 (LTS), C#, x64** | Acceso nativo a WinRT (BLE) sin FFI manual; `System.Security.Cryptography.Pkcs` para CMS/PKCS#7 ya en la BCL; Kestrel como servidor HTTPS de grado producción |
| UI | **WinUI 3 / Windows App SDK** | Fluent Design nativo, tema claro/oscuro del sistema, Mica, animaciones — es la UI nativa de Windows 11 |
| Tests | **xUnit** | Estándar del ecosistema |

**Por qué C# y no Rust:** el trabajo duro de este proyecto está en BLE (WinRT), CMS/PKCS#7 y
un servidor HTTPS con peticiones bloqueantes de larga duración. En C# las tres cosas son
biblioteca estándar o WinRT directo. En Rust habría que envolver WinRT a mano y montar la
pila TLS/HTTP, sin ganancia real: **este proyecto no está limitado por CPU, está limitado por
el protocolo.** Los módulos de parsing (bplist, CPIO) son los únicos con carga de cómputo y
son triviales en cualquiera de los dos.

## Estructura de la solución

```
src/
  AirDrop.Core/            Modelos, contratos, primitivas. Sin dependencias de Windows.
    Protocol/                bplist (lectura/escritura), UTI, plists de Discover/Ask/Upload
    Archives/                CPIO (odc + newc), gzip
    Identity/                Hashes de contacto, PKCS#7, validación de cadena Apple
    Abstractions/            IAirDropTransport, IDiscoveryService, ITransferSink...

  AirDrop.Discovery/       mDNS/DNS-SD propio (UDP 5353) + BLE Continuity (WinRT)
    Mdns/                    Responder y browser, registros PTR/SRV/TXT/AAAA
    Ble/                     Publisher y watcher de Continuity 0x05

  AirDrop.Server/          Receptor: Kestrel:8770, /Discover /Ask /Upload
  AirDrop.Client/          Emisor: cliente HTTPS, construcción del CPIO, progreso
  AirDrop.Transport/       Implementaciones de IAirDropTransport
    Infrastructure/          Wi-Fi normal / Ethernet  (Ruta A)
    AwdlBridge/              Puente a host con AWDL   (Ruta B)

  AirDrop.App/             WinUI 3: UI, notificaciones, ajustes, historial

tests/
  AirDrop.Core.Tests/      bplist, CPIO, hashes — vectores fijos, sin red
  AirDrop.Discovery.Tests/ Serialización mDNS, parsing de advertisements BLE
  AirDrop.Integration.Tests/ Transferencia completa contra loopback

tools/
  awdl-bridge/             Scripts de configuración del puente (Ruta B)
```

## Principio rector

> **Ninguna clase de protocolo puede saber sobre qué transporte viaja.**

`IAirDropTransport` expone: interfaces disponibles, direcciones a las que hacer bind, y el
canal de anuncio. `Infrastructure` y `AwdlBridge` son intercambiables en caliente. Esto es lo
que hace que la Ruta B sea un plugin y no una reescritura, y lo que permite testear todo el
protocolo sobre loopback sin hardware.

## Flujo de módulos

**Recibiendo:** `BleWatcher` detecta un emisor → `MdnsResponder` anuncia `_airdrop._tcp` →
`AirDropServer` acepta TLS:8770 → `/Discover` responde identidad → `/Ask` levanta el diálogo
de la UI y **bloquea** → `/Upload` descomprime gzip → `CpioReader` extrae →
`TransferManager` escribe en disco → notificación de Windows.

**Enviando:** `MdnsBrowser` + `BleWatcher` listan destinos → el usuario elige →
`CpioWriter` empaqueta → `AirDropClient` hace `/Discover` → `/Ask` → `/Upload` con progreso
y cancelación.

## Reglas transversales

- **Logs estructurados** (`Microsoft.Extensions.Logging`) con categorías separadas:
  `Discovery`, `Handshake`, `Transfer`, `Protocol`, `Transport`. Un fallo de interoperabilidad
  debe poder diagnosticarse solo con el log.
- **Cero red externa.** Todo local. Sin telemetría.
- **Nada simulado.** Si una capacidad no se soporta (§10 del research), la UI lo dice.
