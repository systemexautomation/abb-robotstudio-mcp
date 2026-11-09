# Installer le serveur MCP ABB RobotStudio (RobotStudio 2026)

Dernière mise à jour : 6 novembre 2026 · Camille

Ce guide explique comment installer, sur un poste, le serveur MCP qui permet à Claude (Claude Desktop ou Claude Code) de lire et de piloter une station **RobotStudio 2026** et son **contrôleur virtuel**.

Il décrit ce qui a été validé sur le poste de référence : Windows 11 x64, RobotStudio 26.2.11700, contrôleur virtuel RobotWare 7.

**Sommaire** : [1. Objectif et architecture](#1-objectif-et-architecture) · [2. Pourquoi une adaptation](#2-pourquoi-une-adaptation-à-robotstudio-2026) · [3. Prérequis](#3-prérequis) · [4. Installation pas à pas](#4-installation-pas-à-pas) · [5. Configurer le client Claude](#5-configurer-le-client-claude) · [6. Vérification](#6-vérification) · [7. Les outils](#7-les-outils) · [8. Dépannage](#8-dépannage) · [9. Mettre à jour](#9-mettre-à-jour) · [10. Désinstaller](#10-désinstaller) · [11. Sécurité et limites](#11-sécurité-et-limites-connues) · [Annexe](#annexe--partir-du-projet-dorigine)

---

## 1. Objectif et architecture

Le projet adapte le projet communautaire `abb-robotstudio-mcp` (LiskinLabs), conçu à l'origine pour RobotStudio 2025, à **.NET 10**, version requise par RobotStudio 2026. Claude lance le serveur MCP. Celui-ci parle à l'add-in en HTTP pour piloter la station, et directement au contrôleur virtuel en REST pour les outils `rws_*`.

```
Claude Desktop / Claude Code
        │  MCP (stdio)
        ▼
Serveur MCP (Node.js)  ── dist/index.js
        │
        ├── outils rs_*   ── HTTP  ──▶ http://localhost:58080 ──▶ Add-in « ClaudeBridge »
        │                                                         (dans RobotStudio : station, pièces,
        │                                                          simulation, SDK RobotStudio)
        │
        └── outils rws_*  ── HTTPS ──▶ https://127.0.0.1:80 ────▶ Robot Web Services
                                                                  du contrôleur virtuel
                                                                  (RAPID, signaux, fichiers)
```

Quatre composants, trois liens :

| Composant | Rôle | Où il s'exécute |
|---|---|---|
| **Claude** (Desktop ou Code) | Lance le serveur MCP et appelle ses outils | Sur le poste |
| **Serveur MCP** (TypeScript, dossier `src/`) | Expose 72 outils à Claude | En dehors de RobotStudio, lancé par Claude |
| **Add-in ClaudeBridge** (C#, dossier `addin/`) | Donne accès au SDK RobotStudio (station, pièces, simulation) | Dans RobotStudio, écoute sur `127.0.0.1:58080` uniquement |
| **Contrôleur virtuel** | Fait tourner le RAPID de la station et expose les Robot Web Services | Lancé par RobotStudio |

L'add-in n'écoute que sur la boucle locale : il n'est pas joignable depuis le réseau.

> **Sécurité.** Ce serveur est prévu pour un **contrôleur virtuel**. Ne le pointez jamais vers un robot réel sans validation préalable : plusieurs outils écrivent des modules RAPID, des signaux et des fichiers, et peuvent démarrer le programme.

---

## 2. Pourquoi une adaptation à RobotStudio 2026

RobotStudio 2026 est passé de .NET Framework 4.8 à .NET 10, et les add-ins compilés pour l'ancien runtime ne s'y chargent plus : la page des Add-Ins signale alors une incompatibilité. Les détails sont dans les [notes de version du SDK 2026.1](https://developercenter.robotstudio.com/api/robotstudio/articles/releases/robotstudio-sdk-2026-1.html) et dans le [guide de migration vers .NET 10](https://developercenter.robotstudio.com/api/robotstudio/articles/Introduction/DotNetMigration.html).

L'add-in `ClaudeBridge` a donc été migré. Le dépôt d'origine ne contient pas cette migration : il faut utiliser la version adaptée (branche `update-to-2026`), pas celle de LiskinLabs telle quelle.

| Fichier | Modification |
|---|---|
| `addin/ClaudeBridge.csproj` | Cible `net10.0-windows`. Références ABB prises dans `RobotStudio 2026\Bin` (propriété `RsSdkDir`). Newtonsoft.Json passé en package NuGet. Références `System.Net.Http*` et `System.Web` retirées. |
| `addin/ClaudeBridgeAddIn.cs` | Renommages de l'API 2026 : `RsIrc5Controller` devient `VirtualController`, `Irc5Controllers` devient `VirtualControllers`, `TryGetIrc5Controller` devient `TryGetVirtualController`. Un alias `using` lève l'ambiguïté avec le namespace `ABB.Robotics.Controllers`. Lecture des positions et boîtes englobantes corrigée (les vecteurs du SDK exposent `x`, `y`, `z` et `min`, `max`, que l'ancien code lisait par réflexion sous d'autres noms). |
| `addin/ClaudeBridge.rsaddin` | `MinimumHostVersion` à `26.1`, ajout d'un `ApplicationId`. |
| `install-addin.ps1` | Se lance avec `-RobotStudioVersion "2026"`. Refuse de copier tant que RobotStudio tourne (il verrouille la DLL) et affiche la vraie cause d'un échec de copie. |
| `src/rws-client.ts` | Le contrôleur virtuel de RobotStudio 2026 répond en **HTTPS** avec un certificat auto-signé et demande une authentification **Basic**. Le client accepte ce certificat uniquement pour la boucle locale et gère le Basic (uniquement en HTTPS ou vers la boucle locale). |
| `src/index.ts` | Le serveur reste compatible avec l'add-in en HTTP, mais plusieurs outils ont été ajoutés ou étendus : filtres de `rs_get_station_objects`, `rs_get_object_parts`, `rs_get_station_logic`, `rs_create_part_from_body`, `rs_remove_part_bodies`, `rs_set_part_frame`. |

---

## 3. Prérequis

| Prérequis | Version validée | Remarque |
|---|---|---|
| Windows | 11 x64 | |
| **RobotStudio 2026** | 26.2.11700 | Dossier `C:\Program Files (x86)\ABB\RobotStudio 2026\Bin` (les DLL .NET 10). Le dossier voisin `Bin-net48` ne doit **pas** servir à compiler. |
| **Node.js** et npm | 24.18 | Seule la version 24 a été testée. Une version 20 ou plus récente devrait convenir (le serveur utilise `fetch` et les en-têtes `Set-Cookie` natifs de Node). |
| **SDK .NET** (pas seulement le runtime) | 10.0.400 | Sert à compiler l'add-in. Vérifier avec `dotnet --list-sdks`. Visual Studio 2026 convient mais n'est pas obligatoire. |
| Git | | Pour récupérer le dépôt. |
| **Droits administrateur** | | Uniquement pour copier l'add-in dans `Program Files (x86)`. |
| **Claude Desktop** (installé et connecté à un compte) **et/ou Claude Code** | Claude Desktop 4.16336.0 | Les menus peuvent différer dans d'autres versions. |
| **Un contrôleur virtuel** dans la station | | Nécessaire pour les outils `rws_*`. |

Aucun SDK RobotStudio séparé n'est nécessaire : la compilation lit les DLL ABB directement dans le dossier `Bin` de RobotStudio.

---

## 4. Installation pas à pas

### 4.1 Récupérer le projet

```powershell
git clone https://github.com/systemexautomation/abb-robotstudio-mcp.git abb-robotstudio-mcp
cd abb-robotstudio-mcp
git checkout update-to-2026
```

> Le projet est publié sur le dépôt de Systemex Automation (fork du projet d'origine). Clonez-le par exemple dans `Documents\src\abb-robotstudio-mcp`.

### 4.2 Vérifier le chemin du SDK

Dans `addin\ClaudeBridge.csproj`, la propriété `RsSdkDir` doit pointer vers le dossier `Bin` de RobotStudio 2026, celui qui contient `ABB.Robotics.RobotStudio.dll` :

```xml
<RsSdkDir>C:\Program Files (x86)\ABB\RobotStudio 2026\Bin</RsSdkDir>
```

Si RobotStudio est installé ailleurs, ne modifiez pas le fichier : indiquez le dossier à la compilation (étape suivante) avec `-p:RsSdkDir="D:\ABB\RobotStudio 2026\Bin"`.

### 4.3 Compiler le serveur et l'add-in

```powershell
npm ci
npm run build
npm test

dotnet build addin/ClaudeBridge.csproj -c Release
```

Résultat attendu :
- `npm run build` produit `dist\index.js` ;
- `npm test` : 4 tests réussis ;
- `dotnet build` : `Build succeeded`, et l'add-in est écrit dans `addin\bin`. L'avertissement `MSB3277` (conflit de version `WindowsBase`) est connu et sans effet.

Les dossiers `dist/` et `addin/bin/` ne sont **pas** versionnés : il faut les compiler sur chaque poste.

### 4.4 Installer l'add-in dans RobotStudio

1. **Fermez complètement RobotStudio.** Fermer la fenêtre ne suffit pas toujours : le processus peut continuer en arrière-plan et garde `ClaudeBridge.dll` verrouillée. Vérifiez qu'il n'en reste aucun :
   ```powershell
   Get-Process RobotStudio -ErrorAction SilentlyContinue
   ```
   La commande ne doit rien afficher. Sinon, terminez le processus depuis le Gestionnaire des tâches (onglet *Détails*).

2. Ouvrez **PowerShell en tant qu'administrateur**, placez-vous dans le dossier du projet et lancez :
   ```powershell
   powershell -NoProfile -ExecutionPolicy Bypass -File .\install-addin.ps1 -RobotStudioVersion "2026"
   ```
   Le script copie `ClaudeBridge.dll` et `ClaudeBridge.rsaddin` dans deux dossiers :
   - `C:\Program Files (x86)\ABB\RobotStudio 2026\Bin\Addins\ClaudeBridge`
   - `C:\Program Files (x86)\Common Files\ABB\RobotStudio\Addins\ClaudeBridge`

   Il compile l'add-in lui-même s'il ne l'est pas déjà. Il s'arrête avec un message clair si RobotStudio est encore ouvert ou si la copie est refusée.

   `Newtonsoft.Json` n'est pas à copier : RobotStudio 2026 le fournit déjà dans son dossier `Bin` (version 13.0.4 sur le poste de référence). Seuls `ClaudeBridge.dll` et `ClaudeBridge.rsaddin` sont déployés, et l'add-in se charge normalement.

3. Lancez **RobotStudio** et ouvrez une station avec un contrôleur virtuel. L'add-in se charge seul au démarrage (`autoLoad="true"`).

---

## 5. Configurer le client Claude

Le serveur se règle par des variables d'environnement, déclarées dans la configuration du client.

| Variable | Valeur recommandée | Rôle |
|---|---|---|
| `ABB_BRIDGE_URL` | `http://localhost:58080` | Adresse de l'add-in ClaudeBridge |
| `ABB_RWS_URL` | `https://127.0.0.1:80` | Adresse des Robot Web Services du contrôleur virtuel |
| `ABB_RWS_USER` | `Default User` | Utilisateur RWS (valeur d'usine d'ABB pour un contrôleur virtuel) |
| `ABB_RWS_PASS` | `robotics` | Mot de passe RWS (valeur d'usine d'ABB, pas un secret) |
| `ABB_RWS_ALLOWED_HOSTS` | *(facultatif)* | Hôtes RWS supplémentaires autorisés, séparés par des virgules |

**Pourquoi `https` et non `http` pour `ABB_RWS_URL` ?** Le contrôleur virtuel de RobotStudio 2026 répond en HTTPS sur le port 80, avec un certificat auto-signé. Avec `http://`, il coupe la connexion et les outils `rws_*` répondent `fetch failed`. Le serveur accepte ce certificat **uniquement** pour `127.0.0.1`, `localhost` et `::1` ; toute autre adresse reste vérifiée normalement. Le port 80 est celui observé avec `Vrchost64` ; si les outils `rws_*` échouent, vérifiez le port réel (étape 6).

### 5.1 Claude Desktop

1. **Quittez complètement Claude Desktop** : icône dans la zone de notification, puis *Quitter*. Fermer la fenêtre ne recharge pas la configuration.

2. **Ouvrez le fichier `claude_desktop_config.json`.** Le plus sûr est de passer par l'application : *Paramètres → Développeur → Modifier la configuration*. Si l'onglet *Développeur* n'existe pas dans votre version, modifiez le fichier directement. Son emplacement dépend du type d'installation :

   | Emplacement | Sur le poste de référence |
   |---|---|
   | `%LOCALAPPDATA%\Claude-Data\claude_<identifiant>\claude_desktop_config.json` | **C'est celui qui est lu** (installation empaquetée de Claude Desktop) |
   | `%APPDATA%\Claude\claude_desktop_config.json` | N'existe pas |
   | `%LOCALAPPDATA%\Packages\Claude_<identifiant>\LocalCache\Roaming\Claude\` | Existe mais ne contient pas de configuration |

   Pour retrouver le bon fichier :
   ```powershell
   Get-ChildItem "$env:LOCALAPPDATA\Claude-Data" -Recurse -Filter claude_desktop_config.json
   ```
   Sur une installation classique, c'est `%APPDATA%\Claude\claude_desktop_config.json`.

3. **Ajoutez l'entrée `abb-robotstudio` dans `mcpServers`**, en gardant le reste du fichier et un JSON valide si d'autres serveurs existent déjà :
   ```json
   {
     "mcpServers": {
       "abb-robotstudio": {
         "command": "node",
         "args": ["C:/Users/<utilisateur>/Documents/src/abb-robotstudio-mcp/dist/index.js"],
         "env": {
           "ABB_BRIDGE_URL": "http://localhost:58080",
           "ABB_RWS_URL": "https://127.0.0.1:80",
           "ABB_RWS_USER": "Default User",
           "ABB_RWS_PASS": "robotics"
         }
       }
     }
   }
   ```
   Utilisez le **chemin absolu** de `dist/index.js`, avec des `/` (ou des `\\` doublés), adapté au poste.

4. **Relancez Claude Desktop.**

### 5.2 Claude Code

Créez un fichier `.mcp.json` à la racine du dossier de travail depuis lequel vous lancez Claude Code, avec le même contenu que ci-dessus. Au premier lancement, Claude Code peut demander d'approuver ce serveur. La commande `/mcp` affiche ensuite son état.

---

## 6. Vérification

L'installation est bonne quand `rs_ping` répond depuis Claude. Quatre contrôles, du plus proche de RobotStudio au plus proche de Claude. RobotStudio doit être ouvert, avec une station et son contrôleur virtuel.

1. **Dans RobotStudio.** Ouvrez une station. La page des Add-Ins ne signale plus d'incompatibilité, et la fenêtre *Sortie* affiche :
   ```
   ClaudeBridge HTTP server started on port 58080 (TcpListener)
   ```

2. **Dans un terminal, l'add-in :**
   ```powershell
   curl.exe http://localhost:58080/ping
   ```
   Réponse attendue :
   ```json
   {
     "status": "ok",
     "server": "ClaudeBridge",
     "port": 58080,
     "transport": "TcpListener",
     "stationOpen": true,
     "stationName": "NomDeVotreStation"
   }
   ```
   `stationOpen` vaut `false` tant qu'aucune station n'est ouverte.

3. **Dans un terminal, le contrôleur virtuel.** Trouvez le processus qui sert le port 80 :
   ```powershell
   Get-NetTCPConnection -State Listen -LocalPort 80 |
     Select-Object LocalAddress, LocalPort, @{n='Processus';e={(Get-Process -Id $_.OwningProcess).ProcessName}}
   ```
   Le processus attendu est `Vrchost64`. Puis :
   ```powershell
   curl.exe -k https://127.0.0.1:80/rw/system
   ```
   Un code `401` (authentification demandée) est bon signe : le contrôleur répond. L'option `-k` est nécessaire à cause du certificat auto-signé.

4. **Dans Claude.** Ouvrez une nouvelle conversation et demandez :
   - « Fais un `rs_ping`. » → le serveur `ClaudeBridge`, le port `58080`, le transport `TcpListener`, `stationOpen` à `true` et le nom de la station ouverte ;
   - « Donne-moi l'état du contrôleur par les Robot Web Services. » → une réponse sans erreur. Une erreur `fetch failed` signale un problème de protocole ou de certificat (voir le dépannage).

Si les outils `rs_*` n'apparaissent pas dans Claude, passez au dépannage.

---

## 7. Les outils

Le serveur expose **72 outils** : 42 outils `rs_*` (SDK RobotStudio via l'add-in) et 30 outils `rws_*` (Robot Web Services).

### 7.1 Ce qui ne modifie rien

| | Outils |
|---|---|
| **SDK (23)** | `rs_ping`, `rs_get_station`, `rs_get_station_logic`, `rs_get_station_objects`, `rs_get_object_parts`, `rs_get_tasks`, `rs_get_modules`, `rs_read_module`, `rs_read_variable`, `rs_list_variables`, `rs_get_execution_errors`, `rs_get_screenshot`, `rs_controller_status`, `rs_simulation_status`, `rs_get_paths`, `rs_get_path_targets`, `rs_read_config`, `rs_check_collisions`, `rs_get_position`, `rs_get_io_signals`, `rs_read_io_signal`, `rs_validate_rapid`, `rs_check_safety` |
| **RWS (13)** | `rws_controller_status`, `rws_execution_state`, `rws_get_tasks`, `rws_get_modules`, `rws_read_module`, `rws_read_variable`, `rws_get_position`, `rws_get_io_signals`, `rws_read_io`, `rws_get_speed_override`, `rws_event_log`, `rws_list_files`, `rws_read_file` |

### 7.2 Ce qui modifie la station, le contrôleur ou la simulation

| | Outils |
|---|---|
| **SDK (15)** | `rs_create_part_from_body`, `rs_remove_part_bodies`, `rs_set_part_frame`, `rs_save_station`, `rs_write_module`, `rs_write_variable`, `rs_write_io_signal`, `rs_write_config`, `rs_create_path`, `rs_create_target`, `rs_start_simulation`, `rs_stop_simulation`, `rs_pause_simulation`, `rs_reset_simulation`, `rs_set_sim_speed` |
| **RWS (17)** | `rws_set_controller_url`, `rws_set_motors`, `rws_start_program`, `rws_stop_program`, `rws_reset_pp`, `rws_write_module`, `rws_load_module_from_file`, `rws_unload_module`, `rws_save_program`, `rws_load_program`, `rws_restart_controller`, `rws_write_variable`, `rws_write_io`, `rws_set_speed_override`, `rws_write_file`, `rws_request_mastership`, `rws_release_mastership` |

Garde-fous à connaître :
- Les outils qui modifient la géométrie de la station (`rs_create_part_from_body`, `rs_remove_part_bodies`, `rs_set_part_frame`) forment **une seule étape d'annulation** (Ctrl+Z dans RobotStudio) et **n'enregistrent pas** la station. C'est à vous de faire Ctrl+S.
- `rs_start_simulation` et `rws_start_program` refusent de démarrer s'ils détectent des erreurs RAPID dans le journal du contrôleur, sauf avec `force=true`. Cela ne remplace pas la prudence.
- Les écritures RAPID sont validées (équilibre des blocs) avant envoi ; `rs_validate_rapid` permet de le faire seul.
- Les chemins de fichiers du contrôleur sont contrôlés : `..` est refusé et la partition `$SYSTEM` est protégée.
- `rws_set_controller_url` change la cible RWS **pour la session en cours seulement**. Au redémarrage, c'est `ABB_RWS_URL` qui s'applique.

### 7.3 Exemples de demandes

| Vous demandez | Outil utilisé |
|---|---|
| « Liste les objets de la station dont le nom contient *Layout*, avec leur boîte englobante. » | `rs_get_station_objects` (`nameFilter`, `bbox`) |
| « Que contient cette pièce ? » | `rs_get_object_parts` |
| « Quels sont les signaux, connexions et composants intelligents de la Station Logic ? » | `rs_get_station_logic` |
| « Où sont les repères `Frame_Place_*` dans la station ? » | `rs_get_station_logic` (`frameFilter`) |
| « Lis le module RAPID *RunCycle* de la tâche *T_ROB1*. » | `rs_read_module` |
| « Lis en direct la variable *Pallet* du module *Init*. » | `rws_read_variable` |
| « Fais une capture de la vue 3D. » | `rs_get_screenshot` |

### 7.4 Conseils d'usage

- **Grandes stations** : une réponse de plus de 1 Mo est refusée par le client MCP. Filtrez avec `nameFilter` et `limit`, et lisez `matchedCount` / `truncated` dans la réponse.
- **Pièces importées de la CAO** (des milliers de corps sans nom) : `rs_get_object_parts` avec `mode="bodies"` ou `"clusters"` lit le maillage d'affichage et répond en quelques secondes. N'utilisez pas `source="faces"` sans raison : ce calcul exact peut figer RobotStudio pendant plusieurs minutes.
- **Modules RAPID** : `rs_read_module` lit au besoin la copie du module sur le disque du contrôleur virtuel, y compris les fichiers `.modx`. Cette copie peut être plus ancienne que ce qui est chargé dans le contrôleur ; la réponse indique le fichier lu et sa date.
- **Écritures RWS** : certaines écritures exigent la *mastership* du contrôleur. Demandez-la avec `rws_request_mastership` avant d'écrire, puis rendez-la avec `rws_release_mastership`. `rws_write_module` la gère automatiquement.

---

## 8. Dépannage

Les problèmes ci-dessous ont tous été rencontrés pendant la mise au point.

| Symptôme | Cause probable | Solution |
|---|---|---|
| PowerShell refuse d'exécuter `install-addin.ps1` | Politique d'exécution | Lancer `powershell -NoProfile -ExecutionPolicy Bypass -File .\install-addin.ps1 -RobotStudioVersion "2026"` |
| « Cannot copy files to … » alors que vous êtes administrateur | RobotStudio tourne encore (fenêtre fermée, processus actif) et verrouille la DLL | Terminer `RobotStudio.exe` dans le Gestionnaire des tâches, puis relancer le script. Le script le détecte maintenant et l'indique |
| « RobotStudio 2026 not found at … » | RobotStudio installé ailleurs ou autre version | Adapter `$rsBase` dans le script, ou passer `-RobotStudioVersion` |
| L'add-in est signalé incompatible avec la version 2026 | Add-in compilé pour .NET Framework 4.8, ou `MinimumHostVersion` absent | Recompiler en `net10.0-windows` (c'est le cas du dépôt adapté) et vérifier `MinimumHostVersion` à `26.1` dans le `.rsaddin` |
| Erreur de compilation : le namespace `ABB` est introuvable, ou `Could not locate the assembly "ABB.Robotics.RobotStudio"` | `RsSdkDir` ne pointe pas vers les bonnes DLL | Chercher `ABB.Robotics.RobotStudio.dll` avec `Get-ChildItem`, utiliser le dossier `Bin` de la 2026 (pas `Bin-net48`) et l'indiquer avec `-p:RsSdkDir="<dossier Bin>"` |
| `RsIrc5Controller` introuvable, ou `VirtualController` ambigu | Renommages de l'API 2026 non appliqués | Appliquer les renommages de la section 2 et ajouter l'alias `using VirtualController = ABB.Robotics.RobotStudio.Stations.VirtualController;` |
| L'add-in échoue au chargement avec une erreur Newtonsoft | `Newtonsoft.Json.dll` introuvable au chargement (non rencontré sur le poste de référence, où RobotStudio fournit la 13.0.4) | Copier `Newtonsoft.Json.dll` à côté de `ClaudeBridge.dll` dans `Bin\Addins\ClaudeBridge`, puis relancer RobotStudio |
| Avertissement `MSB3277` sur `WindowsBase` | Conflit de versions, non bloquant | Ignorer, ou ajouter `<NoWarn>$(NoWarn);MSB3277</NoWarn>` au `.csproj` |
| `curl http://localhost:58080/ping` ne répond pas, ou `rs_ping` échoue | RobotStudio fermé, add-in non chargé ou non installé | Ouvrir RobotStudio avec une station, lire la fenêtre *Sortie*, réinstaller l'add-in (RobotStudio fermé). La version minimale requise est 26.1 |
| `rs_ping` répond mais un autre appel `rs_*` ne revient pas | L'interface de RobotStudio est occupée par un calcul lourd (l'add-in traite une opération à la fois sur son fil d'interface) | Attendre la fin. Éviter `source="faces"` sur les très grosses pièces |
| Aucun outil `rs_*` dans Claude | Claude pas vraiment quitté, JSON invalide, ou `dist\index.js` absent ou mal référencé | Quitter depuis la zone de notification, valider la syntaxe du JSON, vérifier que `dist/index.js` existe (`npm run build`) et que le chemin est absolu, puis relancer |
| La nouvelle configuration est ignorée | Claude Desktop tourne encore dans la zone de notification, ou le fichier modifié n'est pas celui que l'application lit | Le quitter complètement, puis le relancer. Vérifier l'emplacement du fichier (section 5.1) |
| Pas d'onglet *Développeur* dans les paramètres | Interface différente selon la version | Modifier directement le fichier `claude_desktop_config.json` |
| Tous les outils `rws_*` répondent `fetch failed` | `ABB_RWS_URL` en `http://` alors que le contrôleur est en HTTPS, port différent, ou contrôleur non démarré | Utiliser `https://127.0.0.1:80`, puis redémarrer Claude. Contrôler le port réel avec l'étape 3 de la vérification et ajuster `ABB_RWS_URL` |
| `RWS GET … → 401` / « Access denied. Login required » | Identifiants refusés, ou serveur MCP trop ancien (il ne savait négocier que l'authentification Digest, alors que le contrôleur demande Basic) | Mettre à jour vers la branche `update-to-2026`, recompiler, vérifier `ABB_RWS_USER` et `ABB_RWS_PASS` |
| « RWS URL host … is not in the allowlist » | Hôte non autorisé | Ajouter l'hôte dans `ABB_RWS_ALLOWED_HOSTS` |
| Après une mise à jour de RobotStudio, l'add-in ne se charge plus | Les DLL ABB ont changé | Recompiler l'add-in (`dotnet build …`) et le réinstaller |
| `rs_read_module` ou `rws_read_module` : « Cannot read module … » | Module système non exportable, ou ancienne version du serveur | Mettre à jour. Pour un module système, lire son fichier source dans le dossier d'installation du module (ex. `%LOCALAPPDATA%\ABB\RobotWare\<option>\RAPID`) |

---

## 9. Mettre à jour

```powershell
git pull
npm ci
npm run build
dotnet build addin/ClaudeBridge.csproj -c Release
```

Puis, **RobotStudio fermé**, relancer `install-addin.ps1` en administrateur (étape 4.4), rouvrir RobotStudio, et quitter / relancer Claude Desktop.

## 10. Désinstaller

1. Fermer RobotStudio.
2. Supprimer les deux dossiers `ClaudeBridge` cités à l'étape 4.4.
3. Retirer l'entrée `abb-robotstudio` de la configuration du client Claude (et supprimer `.mcp.json` si vous l'avez créé).

## 11. Sécurité et limites connues

Ce pont ne se limite pas à la lecture : Claude peut aussi écrire du RAPID, modifier des variables, activer les moteurs, lancer un programme ou une simulation. Le [README du projet](https://glama.ai/mcp/servers/LiskinLabs/abb-robotstudio-mcp) indique que `rs_start_simulation` et `rws_start_program` vérifient d'abord le journal d'événements du contrôleur, ce qui ne remplace pas la prudence.

- Travaillez sur un **Virtual Controller**. Ne pointez jamais `ABB_RWS_URL` vers un robot réel. La liste d'hôtes autorisés contient par défaut l'adresse habituelle d'un contrôleur réel (`192.168.125.1`) : ne la visez pas sans validation.
- Les identifiants `Default User` et `robotics` sont les valeurs d'usine d'ABB pour un contrôleur virtuel. Ne les réutilisez pas ailleurs.
- L'add-in migré ne fonctionne qu'avec RobotStudio 2026 et les versions plus récentes (`MinimumHostVersion` à `26.1`). Pour la 2025, utilisez le projet d'origine.
- Validé uniquement avec **RobotStudio 2026 (26.2)** et son contrôleur virtuel sous Windows 11.
- L'authentification Basic n'est envoyée qu'en HTTPS ou vers la boucle locale.
- Le certificat auto-signé du contrôleur n'est accepté que pour la boucle locale ; un contrôleur réel sur le réseau n'a donc pas été testé avec ce serveur.
- Le montage a été validé avec Claude Desktop 4.16336.0 ; les menus peuvent différer dans d'autres versions.

---

## Annexe : partir du projet d'origine

Cette annexe ne sert que si vous devez refaire la migration à partir du projet LiskinLabs. Dans le dépôt adapté, tout est déjà en place : les fichiers de référence sont `addin/ClaudeBridge.csproj`, `addin/ClaudeBridge.rsaddin`, `addin/ClaudeBridgeAddIn.cs` et `install-addin.ps1`.

Points à reproduire :

1. **`ClaudeBridge.csproj`** : cible `net10.0-windows`, `UseWindowsForms` à `true`, propriété `RsSdkDir` vers le dossier `Bin` de RobotStudio 2026, références ABB avec `<Private>false</Private>` (RobotStudio fournit lui-même ces DLL), et `Newtonsoft.Json` 13.0.3 en `PackageReference`. Les références `System.Net.Http*` et `System.Web` sont retirées.
2. **`ClaudeBridge.rsaddin`** : `<MinimumHostVersion>26.1</MinimumHostVersion>` et un `<ApplicationId>` (ici `com.claudebridge.addin`).
3. **`ClaudeBridgeAddIn.cs`** : appliquer les renommages de la section 2, puis ajouter cet alias avec les autres `using` :
   ```csharp
   using VirtualController = ABB.Robotics.RobotStudio.Stations.VirtualController;
   ```
4. **`install-addin.ps1`** : se lance avec `-RobotStudioVersion "2026"` et copie `ClaudeBridge.dll` et `ClaudeBridge.rsaddin`. Il n'est pas nécessaire de copier tout le dossier `addin\bin` : `Newtonsoft.Json` est fourni par RobotStudio (voir l'étape 4.4).
