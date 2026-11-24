# À faire — outils MCP (RobotWare 8)

Points qui concernent le code du dépôt. Les points propres à la cellule sont dans le dossier du projet RobotStudio.

- [ ] `rs_create_config_signals` ne fonctionne pas sur RobotWare 8 : le retirer ou le documenter comme non fonctionnel.
- [ ] `rs_write_io_signal` (SDK) échoue sur RobotWare 8 : utiliser `rws_write_io`.
- [ ] Outils RWS qui passent par le mastership (`/rw/mastership` renvoie 410 sur RobotWare 8) : à revoir.
- [ ] URL RWS par défaut `https://127.0.0.1:80` dans `config.ts` (proposé, non validé).
- [ ] Commentaires « RobotWare 7 » à corriger en « RobotWare 8 » (`src/rws-client.ts` et autres).
- [ ] Ajouter un outil de lecture de la pose d'un outil (`RsToolData`) si besoin.
- [ ] Erreur RobotStudio « RobAPI exception : Cannot access a disposed object, 'RobControllerConnection', RobRetainedState.Initialize() » signalée régulièrement quand les outils accèdent à la simulation (2026-11-16, 13:37:43). Piste (non prouvée) : `ConnectToController()` ouvre une connexion PC SDK Standalone à chaque appel (journal d'événements via `rs_get_execution_errors`, utilisé aussi par `rs_check_safety` et par la vérification de `rs_start_simulation`), puis la libère avec `using`. Essayer : lire le journal par RWS (`rws_event_log`), ou garder une seule connexion, et comparer.
