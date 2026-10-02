# Servidor MCP per a TIA Portal

Un servidor [MCP](https://modelcontextprotocol.io) que permet a un assistent d'IA (Claude Desktop, Claude Code o
qualsevol altre client MCP) treballar dins de **Siemens TIA Portal V20** mitjançant l'API oficial **TIA Portal
Openness**: llegir i escriure blocs de PLC en SCL i LAD, gestionar tags i tipus de dades, construir pantalles
**WinCC Unified** i llegir valors en viu del PLC.

Aquest document és un resum. La documentació completa és en anglès: [README.md](README.md).

> **No és un producte de Siemens.** Projecte independent i no oficial. Cal una instal·lació pròpia i amb llicència
> de TIA Portal; aquí no s'hi inclou cap programari de Siemens.

## Què fa

| Àrea | Exemples |
|---|---|
| **Projecte** | Connectar-se al TIA Portal obert, obrir/desar/tancar, informació del projecte, arxivar-lo com a punt de restauració |
| **Blocs de PLC** | Llistar i llegir blocs, escriure blocs des de **SCL** (es compilen en importar i es retornen els errors), crear blocs **LAD** a partir d'una descripció dels segments, interfícies, DB d'instància, carpetes, referències creuades |
| **Dades de PLC** | Taules de tags, tags i constants, tipus de dades (UDT), taules d'observació |
| **Compilació** | PLC, HMI o un sol bloc, amb els missatges del compilador |
| **HMI (WinCC Unified)** | Pantalles i grups, qualsevol element (botons, camps d'E/S, formes...), propietats, vinculació a tags, color segons tag, scripts amb comprovació de sintaxi, tags HMI, alarmes, cerca d'ús de tags |
| **Hardware** | Dispositius, mòduls amb les seves adreces d'E/S, adreces IP, catàleg |
| **Valors en viu** | Lectura d'entrades/sortides/marques/DB del PLC en marxa, per adreça o per nom de tag |

Totes les eines, amb la funció de cadascuna, i com està estructurat i com funciona el codi:
**[docs/GUIA.ca.md](docs/GUIA.ca.md)** (en català). La referència completa amb tots els arguments, generada del
codi, és a [docs/TOOLS.md](docs/TOOLS.md) (en anglès).

### Què no fa, expressament

- **No carrega res al PLC ni a l'HMI**, no es posa en línia, no arrenca ni atura la CPU.
- **No escriu al PLC en marxa**: el client S7 només sap llegir.
- **No toca blocs de seguretat (F)** ni contrasenyes o nivells de protecció.

Portar els canvis a la màquina continua sent una feina de persona, feta des de TIA Portal.

## Seguretat, en un minut

Cada eina declara la seva classe al principi de la descripció:

| Classe | Significat |
|---|---|
| `[READ-ONLY]` | No modifica mai el projecte. |
| `[WRITE]` | Crea alguna cosa o canvia una propietat. No elimina res del que hi havia. |
| `[DESTRUCTIVE]` | Esborra o substitueix contingut existent. **Es rebutja si no porta `confirm: true`**, que l'assistent només ha de posar després que hagis aprovat aquell canvi concret. |

A més:

- **Res és definitiu fins que es desa el projecte.** Si tanques sense desar, no ha passat res. Amb `--no-save`
  només pots desar tu.
- **Els canvis s'executen dins d'una transacció de TIA**: si una operació de diversos passos falla a mitges, es
  desfà sencera.
- **Còpies de seguretat automàtiques** del bloc o tipus de dades abans de substituir-lo o esborrar-lo.
- **Nivells d'accés**: `--access readonly` (només lectura), `--access write` (crear i modificar, res destructiu),
  `--access full`.

El model complet, amb el que **no** pot evitar, és a [docs/SAFETY.md](docs/SAFETY.md).

## Instal·lació ràpida

Requisits: Windows amb **TIA Portal V20** (amb l'opció Openness), l'usuari dins del grup local
**`Siemens TIA Openness`**, i el **.NET SDK 8** o superior per compilar.

```powershell
git clone https://github.com/paurieravila-ship-it/tia-portal-mcp-server.git
cd tia-portal-mcp-server
dotnet build -c Release
.\bin\Release\TiaMcpServer.exe --doctor
```

Per afegir l'usuari al grup: `lusrmgr.msc` → Grups → *Siemens TIA Openness* → afegeix el teu usuari → tanca la
sessió de Windows i torna a entrar.

Configuració a Claude Desktop (`%APPDATA%\Claude\claude_desktop_config.json`), i reinicia l'aplicació:

```json
{
  "mcpServers": {
    "tia-portal": {
      "command": "C:\\ruta\\a\\tia-portal-mcp-server\\bin\\Release\\TiaMcpServer.exe",
      "args": ["--no-save"]
    }
  }
}
```

El primer cop que es connecti, TIA Portal mostrarà un diàleg d'accés d'Openness: tria **Sí a tot**. Torna a sortir
cada vegada que es recompila l'executable.

## Bons costums

- Les primeres vegades, treballa sobre una **còpia** del projecte.
- **Revisa els canvis a TIA Portal abans de desar.**
- Que compili sense errors **no vol dir que el programa sigui correcte**: prova-ho a la màquina com provaries el
  teu propi codi.
- Mantén TIA Portal **fora de línia** mentre s'edita.

## Llicència

MIT - vegeu [LICENSE](LICENSE). Components de tercers a [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

**L'ús és sota la teva responsabilitat.** Aquest programari modifica projectes d'automatització i es lliura sense
cap garantia: ets responsable de revisar, provar i posar en marxa el que produeixi, i de la seguretat de la
màquina on acabi.
