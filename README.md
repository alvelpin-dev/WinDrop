# WinDrop

Implementación nativa del protocolo AirDrop de Apple para Windows 11, escrita desde cero a
partir de investigación pública del protocolo.

> ## ⚠️ Lee esto antes que nada
>
> **Una aplicación de Windows no puede aparecer en la hoja de AirDrop de un iPhone.** No es una
> limitación de esta implementación: AirDrop con dispositivos iOS viaja obligatoriamente sobre
> **AWDL**, un protocolo de capa 2 propietario que exige inyectar tramas 802.11 crudas.
> **Ningún driver de Windows lo permite** — es una restricción del modelo NDIS, no del hardware.
>
> **Está verificado empíricamente**, no solo documentado: con un iPhone real (iOS 26) en la misma
> red Wi-Fi y la hoja de AirDrop abierta, el teléfono anuncia por mDNS varios servicios de Apple
> (`_remotepairing._tcp`, `_apple-mobdev2._tcp`) pero **nunca `_airdrop._tcp`**.
> Ver [investigación §9.1](docs/01-RESEARCH-airdrop-protocol.md).
>
> Para transferir con un iPhone, la aplicación incluye un **modo iPhone** que sí funciona hoy y
> no requiere instalar nada en el teléfono. No es AirDrop y no se presenta como tal.

## Descargar

El ejecutable portable está en [`build/WinDrop.exe`](build/). No requiere instalación ni tener
.NET instalado: el runtime va dentro.

La primera vez, Windows pedirá permiso del Firewall. **Hay que aceptarlo**: sin él, la aplicación
no puede escuchar en la red y ni el descubrimiento ni las transferencias funcionan.

## Qué funciona y qué no

| Escenario | Estado |
|-----------|--------|
| Windows ↔ Windows (dos instancias de WinDrop) | ✅ Protocolo AirDrop real |
| Windows ↔ OpenDrop en la misma red | ✅ Previsto por diseño |
| **Windows ↔ iPhone vía modo iPhone** | ✅ Sin instalar nada en el teléfono |
| **Windows ↔ iPhone vía AirDrop nativo** | ❌ **Imposible** (AWDL — ver aviso) |
| Windows ↔ Mac | ⚠️ Sin verificar; previsiblemente limitado |
| Modo «Solo contactos» | ❌ Requiere certificados firmados por Apple |

## Funciones

- **Recibir** — se anuncia por mDNS como servicio AirDrop y atiende `/Discover`, `/Ask` y
  `/Upload` sobre HTTPS en el puerto 8770. Cada transferencia pide confirmación.
- **Enviar** — arrastra archivos, elige destino, con progreso y cancelación.
- **Modo iPhone** — servidor web local con código QR. Se abre en Safari y permite mover archivos
  en ambos sentidos sin instalar nada en el teléfono.
- **Historial** — transferencias recientes con su estado.
- **Ajustes** — nombre del dispositivo, carpeta de destino, visibilidad y notificaciones.
- **Logs** — registro detallado en `%LOCALAPPDATA%\WinDrop\logs\`.

## Arquitectura

```
src/
  AirDrop.Core/        bplist, CPIO, mensajes del protocolo, UTIs, hashes, saneado de rutas
  AirDrop.Discovery/   códec DNS, responder y browser mDNS, transporte UDP multicast
  AirDrop.Server/      receptor HTTPS:8770 y servidor web del modo iPhone
  AirDrop.Client/      emisor
  AirDrop.App/         interfaz WPF con tema Fluent
tools/
  AirDrop.Diagnostics/ escáner mDNS para diagnosticar el descubrimiento
```

El protocolo no sabe nada de la interfaz, y la interfaz no sabe nada de sockets. El transporte
está tras una abstracción para que un futuro puente AWDL sea un plugin, no una reescritura.

## Compilar

Requiere el SDK de .NET 10.

```powershell
.\build.ps1
```

Ejecuta los tests y deja el ejecutable portable en `build/`. Con `-SkipTests` omite las pruebas.

```bash
dotnet test AirDrop.slnx
```

**201 tests**, incluidos los de extremo a extremo que recorren el protocolo completo sobre TLS
real, y los de seguridad frente a travesía de directorios.

## Documentación

- [Investigación del protocolo](docs/01-RESEARCH-airdrop-protocol.md) — cómo funciona AirDrop, qué es implementable y por qué
- [Arquitectura](docs/02-ARCHITECTURE.md) — decisiones de diseño
- [Alternativas sin coste](docs/03-ALTERNATIVAS-SIN-COSTE.md) — qué es posible con cada hardware

## Principios

- **Todo local.** Sin servidores externos, sin telemetría. Ni siquiera el código QR se genera
  en línea.
- **Sin comportamientos inventados.** Lo que no se puede implementar se documenta en la propia
  interfaz, no se finge.
- **Código propio.** Los proyectos existentes (OpenDrop, OWL, AirDrop Anywhere) se han usado
  para *comprender* el protocolo, no como fuente de código.

## Licencia

Pendiente de definir.
