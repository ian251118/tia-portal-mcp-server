# Guia en català: estructura, funcionament i eines

Resum en català del que expliquen amb més detall [DEVELOPMENT.md](DEVELOPMENT.md) (com està fet) i
[TOOLS.md](TOOLS.md) (referència completa de cada eina amb tots els arguments), tots dos en anglès.

## Estructura del projecte

```
TiaMcpServer.csproj        projecte .NET Framework 4.8
README.md / README.ca.md   presentació, instal·lació, ús
LICENSE, CHANGELOG.md, SECURITY.md, THIRD-PARTY-NOTICES.md
src/
  Program.cs               punt d'entrada i opcions de línia d'ordres
  Log.cs                   registre (sempre per stderr)
  Mcp/                     el protocol i la seguretat; no sap res de TIA
    McpServer.cs           bucle de missatges, porta de seguretat, auditoria
    ToolDef.cs             definició d'una eina: nom, grup, classe, arguments
    ServerOptions.cs       nivells d'accés, carpetes permeses, confirmacions
    Args.cs                lectura tolerant dels arguments
    ToolDocs.cs            genera docs/TOOLS.md a partir del codi
  Tools/                   el catàleg: descripció i classe de cada eina
  Tia/                     tot el que parla amb TIA Openness
    TiaService.cs          sessió, transaccions, còpies de seguretat
    TiaService.*.cs        projecte, hardware, blocs, tags, HMI, valors en viu
    OpennessResolver.cs    localitza les DLL de Siemens instal·lades
  Lad/LadXmlBuilder.cs     converteix una descripció de segments en LAD
  S7/S7Live.cs             client S7 de només lectura (valors en viu)
tests/                     proves offline i en viu
docs/                      referència d'eines, model de seguretat, notes d'HMI
examples/                  configuració d'exemple per a Claude Desktop
```

## Com funciona

1. **El client MCP arrenca l'executable** i s'hi comunica per text: una petició JSON per línia a l'entrada, una
   resposta per línia a la sortida.
2. **En arrencar**, el programa llegeix les opcions (`--access`, `--no-save`...), localitza les DLL d'Openness de
   la instal·lació de TIA Portal i registra les eines.
3. **El client demana la llista d'eines** i rep, per a cadascuna, nom, descripció, arguments i classe de seguretat.
   Les que el nivell d'accés no permet no hi surten.
4. **Quan es crida una eina**, abans d'executar res passa per la porta de seguretat:
   - l'eina ha d'estar permesa pel nivell d'accés;
   - si és destructiva, ha de portar `confirm: true`;
   - si porta rutes de fitxer, han de ser dins de les carpetes permeses.
5. **L'eina s'executa contra TIA Portal** a través d'Openness. Si modifica el projecte, ho fa dins d'una
   transacció: si falla a mitges, es desfà tot. Si ha d'esborrar o substituir un bloc, abans en fa una còpia.
6. **El resultat torna en JSON.** Si hi ha error, el missatge indica què cal fer (per exemple, quina eina llista
   els noms vàlids).
7. **Res és definitiu** fins que es desa el projecte.

La lectura en viu del PLC va a part: no passa per TIA Portal, sinó per una connexió S7 directa que només sap
llegir.

## Les eines

Classe: **L** lectura · **E** escriptura · **E\*** escriptura que demana confirmació si sobreescriu ·
**D** destructiva (sempre demana confirmació). El model complet és a [SAFETY.md](SAFETY.md).

### Connexió, projecte i compilació

| Eina | Cl. | Funció |
|---|---|---|
| `tia_connect` | L | Connecta amb el TIA Portal obert (o n'arrenca un) |
| `tia_state` | L | Estat de la connexió, projecte obert, canvis sense desar, configuració del servidor |
| `tia_doctor` | L | Comprova la instal·lació: Openness, grup d'usuari, TIA en marxa |
| `tia_open_project` | L | Obre un fitxer de projecte |
| `tia_save_project` | E | Desa el projecte (fa definitius tots els canvis) |
| `tia_close_project` | D | Tanca el projecte; es perd el que no s'hagi desat |
| `tia_project_info` | L | Autor, versió, idiomes, productes, dispositius |
| `tia_archive_project` | L | Crea un arxiu del projecte com a punt de restauració |
| `tia_list_devices` | L | Dispositius i quin software tenen (PLC, HMI Unified, HMI clàssic) |
| `tia_compile` | E | Compila un PLC, un HMI o un sol bloc i retorna els missatges |

### PLC: blocs

| Eina | Cl. | Funció |
|---|---|---|
| `tia_list_blocks` | L | Llista de blocs amb número, llenguatge, carpeta i consistència |
| `tia_read_block` | L | Contingut d'un bloc: text per a SCL/DB/UDT, XML per a LAD/FBD |
| `tia_get_block_interface` | L | Entrades, sortides, estàtiques... amb tipus i comentaris |
| `tia_export_block` | L | Exporta un bloc a un fitxer XML |
| `tia_export_source` | L | Exporta el codi font a un fitxer `.scl` / `.db` / `.udt` |
| `tia_get_cross_references` | L | On s'usa un bloc, tipus o tag |
| `tia_import_scl` | E* | Crea o actualitza blocs des de codi SCL, compila i retorna els errors |
| `tia_import_block` | E* | Importa un bloc des d'XML |
| `tia_create_lad_block` | E* | Crea un bloc LAD a partir d'una descripció dels segments |
| `tia_create_instance_db` | E | Crea el DB d'instància d'un FB |
| `tia_create_block_group` | E | Crea una carpeta de blocs |
| `tia_delete_block_group` | D | Esborra una carpeta, només si és buida |
| `tia_delete_block` | D | Esborra un bloc (amb còpia prèvia) |

### PLC: tipus de dades, tags i taules d'observació

| Eina | Cl. | Funció |
|---|---|---|
| `tia_list_types` | L | Llista dels tipus de dades (UDT) |
| `tia_delete_type` | D | Esborra un UDT (amb còpia prèvia) |
| `tia_list_plc_tags` | L | Taules de tags amb tags, adreces, comentaris i constants |
| `tia_create_tag_table` | E | Crea una taula de tags |
| `tia_create_plc_tags` | E | Crea diversos tags d'un cop |
| `tia_create_plc_constants` | E | Crea constants d'usuari |
| `tia_update_plc_tag` | E | Canvia nom, tipus, adreça o comentari d'un tag |
| `tia_delete_plc_tag` | D | Esborra un tag |
| `tia_delete_tag_table` | D | Esborra una taula de tags sencera (amb còpia prèvia) |
| `tia_export_tag_table` | L | Exporta una taula de tags a XML |
| `tia_import_tag_table` | E* | Importa una taula de tags des d'XML |
| `tia_list_watchtables` | L | Taules d'observació amb les seves entrades |
| `tia_create_watchtable` | E | Crea una taula d'observació buida |
| `tia_export_watchtable` | L | Exporta una taula d'observació a XML |
| `tia_import_watchtable` | E* | Importa una taula d'observació des d'XML |
| `tia_delete_watchtable` | D | Esborra una taula d'observació |
| `tia_online_state` | L | Indica si TIA està en línia amb el PLC |

### HMI WinCC Unified: pantalles i elements

| Eina | Cl. | Funció |
|---|---|---|
| `tia_list_hmi_screens` | L | Pantalles amb grup, mida i pantalla d'inici |
| `tia_hmi_get_screen` | L | Tot el d'una pantalla: elements, posicions, textos, vincles, scripts |
| `tia_hmi_get_item` | L | Totes les propietats d'un element amb els seus valors |
| `tia_hmi_create_screen` | E* | Crea una pantalla (opcionalment dins d'un grup) |
| `tia_hmi_delete_screen` | D | Esborra una pantalla amb tot el que conté |
| `tia_hmi_set_start_screen` | E | Defineix la pantalla d'inici |
| `tia_hmi_set_screen_background` | E | Color o degradat de fons |
| `tia_hmi_validate` | L | Passa la validació de TIA i retorna errors i avisos |
| `tia_hmi_create_items` | E* | Crea molts elements d'un cop, amb propietats, vincles i scripts |
| `tia_hmi_create_button` | E* | Crea un botó |
| `tia_hmi_create_label` | E* | Crea un text estàtic |
| `tia_hmi_create_widget` | E* | Crea qualsevol tipus d'element (camp d'E/S, rectangle, indicador...) |
| `tia_hmi_set_widget_property` | E | Canvia qualsevol propietat d'un element |
| `tia_hmi_set_widget_color` | E | Canvia un color fix |
| `tia_hmi_set_item_bounds` | E | Mou o redimensiona un element |
| `tia_hmi_set_button_text` | E | Canvia el text d'un botó |
| `tia_hmi_set_button_graphic` | E | Posa una imatge a un botó |
| `tia_hmi_delete_screen_item` | D | Esborra un element |

### HMI WinCC Unified: scripts i vincles

| Eina | Cl. | Funció |
|---|---|---|
| `tia_hmi_set_event_script` | E | Posa l'script d'un esdeveniment a qualsevol element o a la pantalla |
| `tia_hmi_set_button_script` | E | El mateix, específic per a botons |
| `tia_hmi_remove_event_script` | D | Treu l'script d'un esdeveniment |
| `tia_hmi_bind_property_to_tag` | E | Vincula una propietat a un tag (valor directe) |
| `tia_hmi_set_property_tag_color` | E | Color segons un tag booleà (fals/cert) |
| `tia_hmi_set_property_script` | E | Calcula una propietat amb un script |
| `tia_hmi_remove_property_dynamization` | D | Treu el vincle o script d'una propietat |

### HMI WinCC Unified: tags, alarmes i panells clàssics

| Eina | Cl. | Funció |
|---|---|---|
| `tia_hmi_list_tags` | L | Tags HMI amb connexió, tag de PLC i cicle |
| `tia_hmi_create_tag_table` | E | Crea una taula de tags HMI |
| `tia_hmi_create_tags` | E* | Crea diversos tags HMI d'un cop |
| `tia_hmi_create_tag` | E* | Crea un tag HMI vinculat al PLC |
| `tia_hmi_create_internal_tag` | E* | Crea un tag intern del panell |
| `tia_hmi_delete_tag` | D | Esborra un tag HMI |
| `tia_hmi_set_tag_acquisition_cycle` | E | Canvia el cicle de lectura d'un tag |
| `tia_hmi_set_connection_enabled` | E | Activa o desactiva una connexió HMI |
| `tia_hmi_find_tag_usage` | L | On s'usa un tag a les pantalles i scripts |
| `tia_hmi_replace_tag_refs` | E* | Cerca i substitueix vincles de tags (per defecte només simula) |
| `tia_hmi_update_faceplate_interface` | E | Canvia els paràmetres d'un faceplate |
| `tia_hmi_list_alarms` | L | Classes d'alarma i alarmes |
| `tia_hmi_create_alarm` | E* | Crea una alarma discreta o analògica |
| `tia_hmi_delete_alarm` | D | Esborra una alarma |
| `tia_export_screen` | L | Panells clàssics: exporta una pantalla a XML |
| `tia_import_screen` | E* | Panells clàssics: importa una pantalla des d'XML |

### Hardware

| Eina | Cl. | Funció |
|---|---|---|
| `tia_list_ip_addresses` | L | IP i subxarxa de cada dispositiu |
| `tia_list_modules` | L | Mòduls amb slot i adreces d'E/S |
| `tia_find_hardware_catalog_entries` | L | Cerca al catàleg de hardware |
| `tia_change_device_item_type` | D | Canvia el tipus o firmware d'un mòdul |
| `tia_dump_plc_protection` | L | Nivell de protecció i ajustos de connexió del PLC |

### Valors en viu del PLC

| Eina | Cl. | Funció |
|---|---|---|
| `tia_s7_read` | L | Llegeix bytes crus d'una àrea (I, Q, M, DB) |
| `tia_s7_read_values` | L | Llegeix valors amb tipus per adreça (`I0.0`, `MW10`, `DB5.DBD4`) |
| `tia_s7_read_tags` | L | Llegeix tags pel seu nom |
| `tia_s7_cpu_info` | L | Estat RUN/STOP i referència de la CPU |

### Diagnòstic i exploració de l'API

| Eina | Cl. | Funció |
|---|---|---|
| `tia_dump_hmi_unified` | L | Bolcat complet d'un HMI Unified |
| `tia_dump_screen_item_bounds` | L | Noms i posicions dels elements d'una pantalla |
| `tia_dump_property_dynamization_colors` | L | Colors configurats en un vincle |
| `tia_debug_screen_item_creation_infos` | L | Tipus d'element que Openness permet crear |
| `tia_debug_screen_item_type_properties` | L | Propietats d'un tipus d'element |
| `tia_debug_search_types` | L | Cerca tipus dins d'Openness |
| `tia_debug_reflect_type` | L | Propietats i mètodes d'un tipus d'Openness |
| `tia_debug_device_tree` | L | Arbre complet d'un dispositiu |
| `tia_debug_device_item_attributes` | L | Tots els atributs d'un mòdul |
| `tia_inspect_other_project` | L | Resum d'un altre projecte per comparar |
