# À faire — outils MCP (RobotWare 8)

Points qui concernent le code du dépôt. Les points propres à la cellule sont dans le dossier du projet RobotStudio.

- [ ] `rs_create_config_signals` ne fonctionne pas sur RobotWare 8 : le retirer ou le documenter comme non fonctionnel.
- [ ] `rs_write_io_signal` (SDK) échoue sur RobotWare 8 : utiliser `rws_write_io`.
- [ ] Outils RWS qui passent par le mastership (`/rw/mastership` renvoie 410 sur RobotWare 8) : à revoir.
- [ ] URL RWS par défaut `https://127.0.0.1:80` dans `config.ts` (proposé, non validé).
- [ ] Commentaires « RobotWare 7 » à corriger en « RobotWare 8 » (`src/rws-client.ts` et autres).
- [ ] Ajouter un outil de lecture de la pose d'un outil (`RsToolData`) si besoin.
- [ ] Erreur RobotStudio « RobAPI exception : Cannot access a disposed object, 'RobControllerConnection', RobRetainedState.Initialize() » signalée régulièrement quand les outils accèdent à la simulation (2026-11-16, 13:37:43). Piste (non prouvée) : `ConnectToController()` ouvre une connexion PC SDK Standalone à chaque appel (journal d'événements via `rs_get_execution_errors`, utilisé aussi par `rs_check_safety` et par la vérification de `rs_start_simulation`), puis la libère avec `using`. Essayer : lire le journal par RWS (`rws_event_log`), ou garder une seule connexion, et comparer.
- [ ] `rs_create_config_signals` refusé (2026-11-24) : la connexion de RobotStudio au contrôleur est de type `RobControllerConnectionRobApi1` (pas un `Controller` du PC SDK) et RobotWare 8 refuse d'enregistrer l'add-in comme poste de commande (`C004AB25`, `C004AB21`). Contournement : `rws_create_signals` (Control Station distante par RWS). Piste : faire passer `rs_create_config_signals` par le même chemin, ou le retirer.
- [ ] Compatibilité RobotWare 6 (RWS 1), 7 et 8 (RWS 2) des outils `rws_*` (demandé le 2026-11-24, à traiter plus tard) : détecter la version (`GET /rw/system`), centraliser dans `rws-client.ts` la prise de droits d'écriture (mastership RWS 1, mastership RWS 2 `/rw/mastership/{domaine}/request`, Control Station en RobotWare 8), le redémarrage (`/rw/panel/ctrlstate?action=…` contre `POST /ctrl/restart` avec `restart-mode=restart` ; `rws_restart_controller` échoue en 406 sur RobotWare 8) et la création de signaux (`rws_create_signals`), puis revoir tous les outils d'écriture qui utilisent la syntaxe RWS 1 `?action=`. Référence RWS 2 : `omnicore-rws.js` du dépôt `palletizing-template`. Tester sur un contrôleur virtuel de chaque version avant d'annoncer la compatibilité.
