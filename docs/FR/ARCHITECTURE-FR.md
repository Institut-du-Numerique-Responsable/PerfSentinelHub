# Comment le Hub s'articule

Cinq schémas, chacun répondant à une question que le README énonce en prose mais ne
dessine pas. Les sources vivent dans [`../diagrams/mmd/`](../diagrams/mmd), les SVG
rendus dans [`../diagrams/svg/`](../diagrams/svg). Modifier un schéma veut dire modifier
le `.mmd` et réexporter les deux thèmes, jamais retoucher le SVG à la main.

## L'ensemble d'un seul coup d'œil

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/hub-integration_dark.svg">
  <img alt="Comment le Hub, la flotte, le navigateur et le moteur s'articulent" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/hub-integration.svg">
</picture>

Un seul Hub sert deux publics qui ne se recouvrent jamais. Le navigateur reçoit le
lanceur et rien d'autre. Un greffon d'IDE ou un job de CI reçoit `/api/findings` et
n'ouvre jamais un écran. Ce partage n'est imposé par aucune authentification, il est
simplement ce que chaque client demande, et il vaut mieux le savoir avant de lire l'une
ou l'autre surface. `Hub:Auth` place le côté navigateur derrière une connexion et laisse
`/api/findings` ouvert, voir [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md).

Le moteur apparaît deux fois sur ce tableau et c'est le même binaire les deux fois :
une fois comme sous-processus que le Hub lance pour produire un rapport, une fois comme
daemon dont il collecte. Rien du Hub ne réside dans le moteur, et rien du moteur ne
réside dans le Hub.

## Push et poll, et lequel des deux possède la joignabilité

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/push-and-poll_dark.svg">
  <img alt="Le chemin de push, le chemin de poll, et le fait qu'un seul possède" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/push-and-poll.svg">
</picture>

Les deux chemins écrivent des findings. Un seul écrit la joignabilité.

Un daemon qui pousse avec succès prouve qu'il peut joindre le Hub. Il ne prouve rien
sur la capacité du Hub à le joindre, qui est une autre route à travers un autre jeu de
pare-feux, et c'est cette direction dont un opérateur a besoin quand une source se tait.
Le gestionnaire d'import ne touche donc aucune colonne de joignabilité : il peut
rafraîchir le `producer_version` d'une ligne existante et rien de plus. Une source dont
le push arrive alors que son poll échoue continue de rapporter `unreachable_since`, et
c'est juste plutôt que périmé.

Les deux chemins diffèrent aussi de contre-pression, délibérément. Le poll bloque sur le
verrou d'écriture parce qu'il est le travail programmé du Hub lui-même et qu'il peut
attendre. Un import abandonne au bout de cinq secondes avec `503 Retry-After: 1`, parce
qu'un téléverseur lent ne doit pas pouvoir bloquer la collecte de toute la flotte.

Un poll lit jusqu'à quatre choses d'un daemon, dans l'ordre : son statut, ses findings,
ses incidents et ses acquittements actifs. Seuls les deux premiers décident de la
joignabilité. Les deux autres sont consignés à part, dans `incident_reads` et
`ack_reads`, parce qu'une clé refusée ou une route absente sur l'un ou l'autre ne dit
rien de la capacité du daemon à répondre.

La lecture des acquittements reflète et n'écrit jamais sur le daemon. Elle demande
`GET /api/acks?include_toml=true` avec l'identifiant de lecture de la source et remplace
les lignes de cette source dans `source_acks` par la réponse, en une transaction, de
sorte qu'un acquittement révoqué sur le daemon part au poll suivant. Chaque ligne garde
d'où vient l'acquittement, `daemon` pour un acquittement pris à l'exécution et `toml`
pour un acquittement de la baseline de CI. Quand une signature porte les deux, c'est la
ligne de la baseline qui est gardée, comme le fait la recherche du daemon lui-même. Un
daemon antérieur à 0.24.0 n'est jamais interrogé : il ignore le paramètre et ne liste que
ses acquittements d'exécution, une réponse qu'on ne peut pas distinguer de celle d'un
daemon sans baseline, donc sa lecture est consignée `absent`. Une liste qui atteint le
plafond du daemon, mille lignes, est reflétée et consignée `truncated`, puisque sa fin,
là où se trouve la baseline, peut manquer.

## Ce que fait réellement une analyse lancée

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/analysis-run_dark.svg">
  <img alt="Un run, de la soumission au rapport embarqué" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/analysis-run.svg">
</picture>

Deux choses de cette séquence se manquent facilement.

La première est que la validation a lieu **avant** la mise en file. Une combinaison
impossible, une fenêtre de trois heures contre un daemon qui en garde dix minutes, est
refusée pendant que l'opérateur regarde encore le formulaire, et non découverte trois
minutes plus tard sous la forme d'un run échoué.

La seconde est qu'un run vaut deux lancements du moteur, et non un. Les sous-commandes
de query émettent du texte, du JSON ou du SARIF, et seule `report` écrit du HTML, donc
la source est lue vers un JSON de rapport, puis ce JSON est rendu. Une source daemon
saute entièrement le premier lancement, puisque son propre `/api/export/report` renvoie
déjà exactement ce JSON.

## Trois horloges de rétention

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/retention-clocks_dark.svg">
  <img alt="Findings, fenêtre de statut et rapports rendus expirent chacun sur son horloge" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/retention-clocks.svg">
</picture>

Aucune des trois ne se déduit des autres, et elles sont appliquées par trois mécanismes
différents.

Les findings expirent sur un worker qui tourne une fois par jour et purge par tranches,
de sorte qu'une purge longue ne puisse pas rejeter les imports pendant toute sa durée. Les
incidents copiés depuis un daemon suivent cette même horloge, purgés sur le `last_seen_ms`
du Hub et jamais sur le `at_ms` de l'alerte.
Les jours d'observation, le relevé des jours où chaque source a vu un finding, la suivent
aussi, sur l'horloge du Hub et par jour entier : un jour part une fois qu'il se trouve
tout entier avant la date limite, donc le jour où tombe cette limite est gardé. Ils ne
portent aucune clé étrangère, parce qu'une cascade depuis une tranche de purge tiendrait
le verrou d'écriture bien plus longtemps que la tranche elle-même, donc un finding purgé
peut laisser une ligne d'observation derrière lui. Cette orpheline est invisible, une
lecture n'atteint une observation qu'à travers la ligne de sa source, et elle part avec
son jour. Les regroupements où chaque source a vu un finding suivent la même horloge, sur
leur propre `last_seen_ms`, sans clé étrangère pour la même raison, et une orpheline y est
tout aussi invisible : une lecture n'en atteint une qu'à travers un finding. Les attributs
de regroupement que chaque source a captés sur un finding la suivent de la même façon, sur
leur propre `last_seen_ms`. Les paires que partagent les findings gelés d'un incident partent
avec leur incident.

Le miroir des acquittements suit aussi cette horloge, sur `read_at_ms`, l'heure de la
lecture qui a écrit la ligne. Elle ne fait pas vieillir un acquittement : chaque lecture
réussie réécrit les lignes de sa source, donc une ligne ne vieillit qu'une fois que le
Hub a cessé de refléter cette source, parce qu'elle a été retirée ou parce que sa liste
a échoué pendant toute la rétention. `ack_reads` et `incident_reads` tiennent une ligne
par source et ne sont jamais purgés.

La fenêtre de statut n'est pas un worker du tout, c'est un `CASE` évalué à la lecture,
ce qui explique que le statut d'un finding puisse changer sans que rien n'ait été écrit.
Les rapports rendus expirent sur un balayage qui tourne toutes les soixante secondes,
plus fin que la durée qu'il applique, pour que le compte à rebours que voit un lecteur
ne survive jamais au fichier qu'il décompte.

Le piège que ce schéma existe pour éviter : un poll qui omet un finding ne le résout
pas. Le tampon circulaire du daemon peut simplement l'avoir évincé, et absent n'est pas
la même chose que résolu. Seule la rétention retire une ligne.

## Ce que contient la base

Chaque table est créée dans `Storage/Schema.cs`. Un finding y est tenu deux fois : une
fois pour toute la flotte, sous sa signature aveugle au regroupement, et une fois par source
qui l'a remonté, parce que la ligne de flotte garde l'enveloppe la plus récente, quelle que
soit la source, et rien d'autre.

| Table                          | Une ligne par                                                        | Part                                       |
|--------------------------------|----------------------------------------------------------------------|--------------------------------------------|
| `findings`                     | signature, avec l'enveloppe la plus récente de toutes les sources    | rétention, sur `last_seen_ms`              |
| `finding_sources`              | signature et source, avec l'enveloppe et le service de cette source  | rétention, sur `last_seen_ms`              |
| `finding_observations`         | signature, source et jour                                            | rétention, par jour entier                 |
| `finding_groupings`            | signature, source et valeur de regroupement effective                | rétention, sur `last_seen_ms`              |
| `finding_attributes`           | signature, source, identité de regroupement et paire captée          | rétention, sur `last_seen_ms`              |
| `finding_lineage`              | signature successeur et prédécesseur                                 | avec son successeur                        |
| `endpoint_heartbeats`          | source, service et endpoint                                          | rétention, sur `last_seen_any_ms`          |
| `source_state`                 | source, la joignabilité vue par le poll                              | rétention, sur `last_attempt_ms`           |
| `source_imports`               | source, son dernier push                                             | jamais                                     |
| `incidents`                    | incident et source                                                   | rétention, sur `last_seen_ms`              |
| `incident_attributes`          | incident, source et paire que porte chaque finding gelé              | avec son incident                          |
| `source_acks`                  | source et signature                                                  | rétention, sur `read_at_ms`                |
| `incident_reads`, `ack_reads`  | source                                                               | jamais                                     |
| `analysis_runs`                | run                                                                  | `Hub:Analysis:RunRetention`                |

`finding_attributes` garde chaque attribut de regroupement qu'un daemon a capté, jusqu'aux
huit du moteur, chacun sous l'identité de regroupement avec laquelle il est arrivé : la
première paire de cette observation, ou quatre chaînes vides quand l'observation n'a pas de
première paire bien formée, auquel cas aucune autre paire n'est enregistrée.
`finding_sources.service` est le service que cette source a remonté, ce que
`findings.service` n'est plus dès que deux sources divergent. `incident_attributes` tient
les paires communes à tous les findings gelés de la copie d'un incident par une source, ou
deux chaînes vides quand ils n'en partagent aucune, dérivées de la copie stockée chaque fois
qu'une lecture la change. Une lecture qui laisse la copie telle quelle, mêmes findings ou
recapture plus pauvre, ne fait que dater ses lignes du nouveau `last_seen_ms` de la copie.
Rien ne lit encore ces trois-là. Elles sont enregistrées avant le contrôle d'accès, qui y
fera correspondre les projets, pour que l'activer ne parte pas d'un historique vide.

Chaque démarrage complète ce qu'un build plus ancien a laissé de côté : les lignes
d'attributs d'une copie dont l'observation la plus récente n'en a aucune, le service d'une
copie qui n'en a pas, tous deux depuis l'enveloppe propre à cette copie, et les paires de
chaque copie d'incident sans ligne ou datée d'autre chose que son `last_seen_ms`, ce que
seule l'écriture d'un build plus ancien laisse. Une image ramenée sous cette version puis
remontée est donc couverte au démarrage suivant. Une copie écrite avant l'existence de
l'enveloppe par source n'a ni l'un ni l'autre tant que sa source ne la remonte pas de
nouveau. Le coût de ce démarrage est mesuré dans
[BENCHMARKS-FR.md](BENCHMARKS-FR.md#démarrer-sur-une-base-remplie).

## Le contrôle d'accès, avant son application

`Hub:Access` est compilé une fois, à la première demande, en `AccessPolicy`
(`Api/AccessPolicy.cs`) : les motifs de chaque projet, les permissions de chaque rôle, les
attributions, les groupes internes par sujet membre, les ensembles d'administrateurs, et
l'empreinte SHA-256 de chaque jeton machine, jamais le jeton. `Program.cs` l'enregistre quel
que soit le réglage, parce qu'un handler partagé par les deux modes le prend comme service,
et que le Request Delegate Generator lirait un type absent du conteneur comme un corps JSON,
qu'un `GET` refuse. Contrôle d'accès désactivé, il ne construit rien et chaque portée qu'il
calcule est illimitée. Cette version refuse de démarrer avec le contrôle d'accès activé,
donc rien ne l'appelle encore.

Pour un appelant et une permission, `AccessPolicy.Scope` rend un `AccessScope`
(`Api/AccessScope.cs`) : illimité, rien, ou l'union des motifs des projets attribués. Un
`AccessPattern` reconnaît une copie de finding, une signature à une source, sur trois
choses : la source, le service propre à la copie dans `finding_sources.service`, jamais le
`findings.service` de la flotte qu'un import réécrit, et les lignes de la copie dans
`finding_attributes`. Une lecture demande qu'une ligne corresponde, quel que soit le
regroupement avec lequel elle est arrivée ; une écriture, pour `acks.write` et
`analyses.run`, demande que chaque identité de regroupement de la copie, marqueur sans
regroupement compris, porte la clé et que chacune de ses valeurs corresponde. Un run de
traces sur un service est jugé sur les copies de ce service que le Hub tient dans
l'environnement du backend, et chacune doit correspondre en écriture ; `CoversSubject`
évalue les copies qu'on lui passe, et l'appelant qu'ajoutera l'application les choisit par
environnement. Les copies d'incident correspondent sur leurs paires dans
`incident_attributes`.

`Storage/HubDatabase.Access.cs` tient la même correspondance de lecture en SQL, pour les
lectures que l'application filtrera. Une requête s'ouvre sur la CTE `ap`, qui décode une
seule fois l'unique paramètre lié `$access_patterns` au lieu d'une fois par ligne et par
motif, puis teste `visible`, les copies dans la portée, ou `IncidentAccess`. Les parties se
comparent avec `=` ou `substr`, jamais `LIKE` ni `GLOB`, qui ignorent la casse et donnent un
sens à `%` et `_`. `AccessScopeParityTests` fait passer une même table de motifs par les deux
sur les lignes qu'écrit le rattrapage de H1 lui-même, et un test explicite mesure la CTE,
voir [BENCHMARKS-FR.md](BENCHMARKS-FR.md#portée-daccès-sur-100-000-copies).

## Un run atteint toujours un état terminal

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/run-states_dark.svg">
  <img alt="Les états que traverse un run et qui déplace chaque arête" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/diagrams/svg/run-states.svg">
</picture>

Quoi qu'il arrive, une ligne ne reste pas en `running`. Un process qui meurt en cours de
run laisse une ligne orpheline, et le démarrage suivant la marque `interrupted` plutôt
que de la laisser à l'état de question.

Rien n'est rejoué. Une reprise silencieuse lancerait une seconde requête lourde vers un
backend que personne n'a demandé d'interroger deux fois, donc un run interrompu attend
qu'un humain décide. `expired` n'est pas un échec non plus : le rapport a été supprimé à
l'heure prévue et le run garde ses paramètres, donc il peut être relancé tel quel.

## Modifier un schéma

Les sources `.mmd` font foi. Elles ne portent aucun bloc de thème `%%{init}%%`, et c'est
volontaire : le thème est un argument d'export, ce qui permet à une source unique de
produire à la fois le SVG clair et le sombre.

L'export est manuel, par [mermaid.live](https://mermaid.live) : coller la source,
choisir le thème, utiliser le bouton d'export SVG. C'est ainsi qu'a été produite la
famille perf-sentinel et les deux concordent, ce qui compte puisque les SVG se côtoient
dans le même genre de document.

`mermaid-cli` n'en est pas un substitut direct. Mesuré sur les sources de ce dépôt,
`mmdc` sans option de fond écrit `hsl(80, 100%, 96.2%)` là où la famille existante a
`rgb(255, 255, 255)`, et `-t dark -b '#232030'` écrit malgré tout
`hsl(20, 1.6%, 12.4%)` plutôt que le `rgb(35, 32, 48)` de la famille, le fond propre au
thème l'emportant sur l'option. Il reste utile pour contrôler qu'une source parse, ce
qui vaut la peine avant d'ouvrir un navigateur :

```bash
npx -y @mermaid-js/mermaid-cli@latest -i docs/diagrams/mmd/<nom>.mmd -o /tmp/check.svg
```

Cette invocation exige un Chrome ou un Chromium que Puppeteer sache trouver, et le dit
clairement quand elle n'en trouve pas.

Les deux fichiers d'une paire doivent exister. Un schéma livré avec la seule variante
claire est une dalle blanche pour tout lecteur en thème sombre, ce qui est exactement la
dérive que la convention de nommage existe pour rendre visible.

## Chaque flèche, confrontée au code

Le premier schéma affirme une topologie. C'est ici qu'elle se vérifie : chaque flèche
correspond à un appel réel. Désigné par symbole plutôt que par ligne, un numéro de ligne
étant faux dès la première édition au-dessus de lui.

| Flèche                                            | Où elle vit                                                                                                    |
|---------------------------------------------------|----------------------------------------------------------------------------------------------------------------|
| Navigateur vers le Hub, le lanceur lit            | `Api/ApiEndpoints.Analysis.cs`, les quatre routes qu'il déclare                                                |
| Greffon ou CI vers le Hub                         | `Api/ApiEndpoints.cs`, `GetFindingsAsync`                                                                      |
| Daemon vers le Hub, le push                       | `Api/ApiEndpoints.cs`, `ImportFindingsAsync`, qui range par `TryUpsertBatchAsync`                              |
| Hub vers le daemon, le poll                       | `Collection/DaemonClient.cs`, `FetchStatusAsync` et `FetchFindingsAsync`                                       |
| Hub vers le daemon, l'export d'un run             | `Collection/DaemonClient.cs`, `FetchReportSnapshotAsync`                                                       |
| Hub vers le daemon, la config d'une ligne dépliée | `Collection/DaemonClient.cs`, `FetchConfigAsync`                                                               |
| Hub vers le daemon, les incidents                 | `Collection/DaemonClient.cs`, `FetchIncidentsPageAsync`, paginé par `SourcePoller` vers `UpsertIncidentsAsync` |
| Hub vers le daemon, les acquittements             | `Collection/DaemonClient.cs`, `FetchAcksAsync`, reflété par `AckReader` vers `ReplaceSourceAcksAsync`          |
| Hub vers le daemon, le relais d'acquittement      | `Api/ApiEndpoints.Acks.cs`, `RelayAckAsync`, écrit par `AckRelay` via `SendAckAsync`                           |
| La joignabilité, posée et effacée                 | `Collection/SourcePoller.cs`, les deux appels `MarkSource` et `UpsertBatchAsync`                               |
| Hub vers SQLite                                   | `Storage/Schema.cs`                                                                                            |
| Le Hub lance le moteur                            | `Analysis/AnalysisRunner.cs`, deux fois par run                                                                |
| Le moteur écrit le rapport                        | `Analysis/AnalysisRunner.cs`                                                                                   |
| Le rapport servi dans l'iframe                    | `Api/ApiEndpoints.Analysis.cs`, `GetReportAsync`                                                               |
| Hub vers api.github.com                           | `Collection/UpdateChecker.cs`, `ReadAsync`, seul appel sortant qui ne soit pas une source configurée           |
