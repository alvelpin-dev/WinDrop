# AirDrop para Windows 11

Implementación nativa del protocolo AirDrop de Apple para Windows 11, escrita desde cero a
partir de investigación pública del protocolo.

> ## ⚠️ Lee esto antes que nada
>
> **Una aplicación de Windows no puede aparecer en la hoja de AirDrop de un iPhone sin
> hardware adicional.** No es una limitación de esta implementación: AirDrop con dispositivos
> iOS viaja obligatoriamente sobre **AWDL**, un protocolo de capa 2 propietario que exige
> inyectar frames 802.11 crudos. **Ningún driver de Windows lo permite** — es una restricción
> del modelo NDIS, no del hardware.
>
> El análisis completo, con fuentes, está en
> [`docs/01-RESEARCH-airdrop-protocol.md`](docs/01-RESEARCH-airdrop-protocol.md) §7.
>
> Este proyecto **no simula compatibilidad**. Implementa el protocolo AirDrop real y es
> explícito sobre qué funciona y qué no.

## Estado

🚧 En desarrollo. Investigación completada; construyendo el núcleo del protocolo.

- [x] Investigación del protocolo y análisis de viabilidad
- [x] Serialización de binary property lists (`bplist00`)
- [x] Archivos CPIO (`odc` y `newc`), en streaming
- [x] Mensajes del protocolo, UTIs y hashes de contacto
- [ ] Descubrimiento mDNS / DNS-SD
- [ ] Servidor HTTPS `:8770` (`/Discover`, `/Ask`, `/Upload`)
- [ ] Cliente de envío
- [ ] Advertisement BLE Continuity
- [ ] Interfaz WinUI 3
- [ ] Modo SMB (transferencia con iPhone sin AirDrop)

## Qué funciona y qué no

| Escenario | Estado |
|-----------|--------|
| Windows ↔ Windows (dos instancias de esta app) | 🚧 En desarrollo — protocolo AirDrop real |
| Windows ↔ OpenDrop en la misma red | 🚧 En desarrollo |
| Windows ↔ Mac | ⚠️ Por verificar; previsiblemente limitado |
| **Windows ↔ iPhone, sin hardware extra** | ❌ **Imposible** (AWDL — ver aviso) |
| **Windows ↔ iPhone, con puente AWDL** | 📋 Diseñado, no implementado — requiere adaptador Wi-Fi compatible |
| Windows ↔ iPhone vía SMB (sin AirDrop) | 🚧 En desarrollo — funciona sin instalar nada en el iPhone |
| Modo "Solo contactos" | ❌ Requiere certificados firmados por Apple |

Ver [alternativas sin coste](docs/03-ALTERNATIVAS-SIN-COSTE.md) para el análisis de qué es
posible con cada hardware.

## Compilar

Requiere el SDK de .NET 10.

```bash
dotnet test AirDrop.slnx
```

## Documentación

- [Investigación del protocolo](docs/01-RESEARCH-airdrop-protocol.md) — cómo funciona AirDrop, qué es implementable y por qué
- [Arquitectura](docs/02-ARCHITECTURE.md) — decisiones de diseño
- [Alternativas sin coste](docs/03-ALTERNATIVAS-SIN-COSTE.md) — qué se puede hacer con cada hardware

## Principios

- **Todo local.** Sin servidores externos, sin telemetría.
- **Sin comportamientos inventados.** Lo que no se puede implementar se documenta, no se finge.
- **Código propio.** Los proyectos existentes (OpenDrop, OWL, AirDrop Anywhere) se han usado
  para *comprender* el protocolo, no como fuente de código.

## Licencia

Pendiente de definir.
