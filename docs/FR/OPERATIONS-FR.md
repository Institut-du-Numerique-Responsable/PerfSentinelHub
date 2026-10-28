# Exploitation

## Fraîcheur et rétablissement

Le push est primaire. Chaque source est aussi pollée indépendamment, en filet de sécurité.

Un poll réussi met à jour les observations. Il ne supprime pas un finding au seul motif
qu'une réponse ultérieure du daemon l'omet : le tampon circulaire du daemon peut l'avoir
évincé, donc absent ne veut **pas** dire résolu. Seule la rétention retire des findings,
quand leur dernière observation est plus ancienne que la période configurée.

Le daemon perf-sentinel 0.11.x plafonne `/api/findings` à 1 000 lignes. Le Hub utilise ce
plafond exact et avertit quand il est atteint, l'instantané du filet pouvant être
incomplet, sauf si le daemon pousse vers le Hub et qu'une poussée de findings est arrivée dans les deux
dernières heures (voir DEPLOYMENT-FR.md). Une couverture à fort volume exige donc l'exportateur de push borné.

Les échecs laissent lisibles les findings déjà stockés. La source concernée est marquée
injoignable et réessayée avec un repli exponentiel borné, et un succès ultérieur efface
cet état. Les corps de poll sont limités à 16 Mio, les requêtes ont un timeout, les
imports sont transactionnels, et les logs n'identifient que l'identifiant de source et un
code d'erreur stable.

Le même poll copie les incidents du daemon, à partir de 0.20.0 avec `[daemon.incidents]`
activé, et la copie survit à l'anneau du daemon. Le résultat de cette lecture est
`incidents_state` sur `/api/sources`, tenu à l'écart de la joignabilité : un daemon qui
répond 404 ou 503 sur cette route ne publie pas d'incidents, un daemon qui refuse la clé
est `unauthorized`, et aucun des deux ne marque la source injoignable, puisque ses
findings viennent d'être collectés. Les incidents sont lus au même poll horaire que les
findings. Un daemon en perf-sentinel 0.29.0 ou suivant avec `[daemon.hub_export]` actif
les pousse aussi quand ils sont enregistrés, fermés ou complétés, donc un incident que son
anneau évince entre deux polls arrive quand même. Sans ce push, un incident capturé
quelques minutes avant un redémarrage du daemon peut se perdre entre deux polls, ce que
l'archive NDJSON du daemon lui-même couvre.

Ce poll est le plancher des incidents, pas l'unique chemin. `POST /api/incidents/refresh`
lit chaque daemon à la demande, et l'écran des incidents du lanceur l'appelle à chaque
ouverture, pour qu'un exploitant alerté d'un OOM kill voie ce que la flotte tient
maintenant au lieu d'attendre l'intervalle. Les daemons en sont protégés par un plancher de
dix secondes par source, sous lequel la copie stockée est servie intacte, et par une
barrière de deux rafraîchissements simultanés. `incidents_read_ms` sur `/api/sources` dit
quand chaque copie a été prise, ce qui distingue une flotte sans rien à signaler d'une
copie que personne n'a rafraîchie.

La dernière lecture du poll copie les acquittements du daemon, sa baseline de CI comprise,
depuis `GET /api/acks?include_toml=true` avec l'identifiant de lecture. Elle vient après la
lecture des incidents, sur des findings déjà stockés, et la version renvoyée par la lecture
de statut décide si elle a lieu : un daemon antérieur à 0.24.0 ignore `include_toml` et
liste ses seuls acquittements d'exécution, réponse que rien ne distingue de celle d'un
daemon sans baseline, donc il n'est jamais interrogé et la lecture est classée `absent`. Une
pré-version de ce plancher est interrogée, son suffixe retiré, et une version qui ne se
lit pas compte pour un daemon plus ancien. Ce poll est le plancher du miroir, pas l'unique
chemin : le relais relit la liste dès qu'un daemon prend un acquittement ou une révocation,
ce qui peut laisser le miroir bien plus frais que l'intervalle, voir
[API-FR.md](API-FR.md#relais-dacquittement).

`acks_state` sur `/api/sources` dit ce qu'a donné cette lecture et `acks_read_ms` quand elle
a été prise. `ok` est une liste entière, reflétée telle quelle, moins toute ligne que le Hub
refuse, écartée et comptée dans les logs sans changer l'état. `truncated` est une liste qui
a atteint le plafond propre du daemon, mille acquittements, qu'il ne pagine pas, donc sa fin
peut manquer : les lignes lues sont tout de même reflétées et servies, mais une lecture
tronquée ne suffit jamais à dire qu'un finding n'est pas acquitté, voir
[API-FR.md](API-FR.md#comment-include_acked-juge-un-finding). `absent` couvre aussi une
route que le daemon ne sert pas, `unauthorized` un daemon qui refuse la clé de lecture, et
`error` tout autre échec, son code stable dans les logs. Un daemon dont les acquittements
sont désactivés n'est pas `absent` : il répond une liste vide, classée `ok`. Aucun résultat
de cette lecture ne marque la source injoignable ni ne fait échouer le poll, dont les
findings ont été collectés avant elle, et une lecture échouée laisse en place le miroir
précédent sans que rien ne dise qu'il tient encore. Sur la page d'acquittement du lanceur,
une source qui relaie et dont l'état n'est ni `ok` ni `truncated` le voit nommé sur sa ligne
et garde les deux boutons, quand une source pour laquelle le Hub ne détient pas
d'identifiant d'acquittement n'en offre aucun, voir
[LAUNCHER-FR.md](LAUNCHER-FR.md#la-page-dacquittement).

## Démarrer sur une grosse base

Avant de répondre, le Hub applique en une transaction les migrations de schéma
qu'apporte une nouvelle version. La plupart ne coûtent rien, mais celle qui
construit un index sur `finding_sources` lit toute la table, ce qui prend de
quelques secondes à quelques minutes sur une base d'un gigaoctet ou plus. Un arrêt
en cours de route annule la migration, et le démarrage suivant la recommence. La
sonde de démarrage du chart laisse donc 5 minutes, `probes.startup.periodSeconds`
fois `probes.startup.failureThreshold` : augmentez-la pour une base plus grosse
plutôt que de laisser le kubelet redémarrer le Hub en boucle. La sonde de vivacité
laisse 5 secondes et 6 échecs (`probes.liveness`), pour que seul un Hub vraiment
bloqué redémarre.

Ce qu'une version antérieure à 0.4.0 a laissé sans lignes d'attributs est dérivé
une fois le Hub en route, pas avant : le premier passage du worker de rétention
vérifie en lecture seule s'il reste une copie ou un incident sans elles, et ne
prend le verrou d'écriture que s'il y en a. D'ici là, une telle copie ne
correspond à aucune portée par attribut.

## Métriques

`GET /metrics` sert le format texte Prometheus. Il est écrit à la main plutôt
qu'au travers d'une bibliothèque : dix-neuf familles de métriques sur des données que
le Hub détient déjà ne justifient pas une dépendance dans un service dont les
deux seuls paquets sont SQLite.

| Métrique                                                               | Type    | Ce à quoi elle répond                                             |
|------------------------------------------------------------------------|---------|-------------------------------------------------------------------|
| `perf_sentinel_hub_build_info{version}`                                | gauge   | Quelle version tourne                                             |
| `perf_sentinel_hub_source_reachable{source}`                           | gauge   | Si le dernier poll d'un daemon a réussi                           |
| `perf_sentinel_hub_source_unreachable_seconds`                         | gauge   | Depuis combien de temps il est injoignable, 0 s'il répond         |
| `perf_sentinel_hub_source_last_success_seconds`                        | gauge   | L'âge du dernier poll réussi                                      |
| `perf_sentinel_hub_analysis_queue_depth`                               | gauge   | Les runs acceptés et pas encore pris par un worker                |
| `perf_sentinel_hub_source_last_import_seconds`                         | gauge   | Dernier push d'un daemon. Pas un battement de cœur, voir plus bas |
| `perf_sentinel_hub_source_missed_findings_total{source}`               | counter | Findings sortis de l'anneau d'un daemon interrogé sans être lus   |
| `perf_sentinel_hub_import_rejected_total{reason}`                      | counter | Imports refusés, par motif                                        |
| `perf_sentinel_hub_incident_import_rejected_total{reason}`             | counter | Imports d'incidents refusés, par les motifs des findings          |
| `perf_sentinel_hub_correlation_import_rejected_total{reason}`          | counter | Imports de corrélations refusés, par les motifs des findings      |
| `perf_sentinel_hub_detection_import_rejected_total{reason}`            | counter | Pushs de détections refusés, par les motifs des findings          |
| `perf_sentinel_hub_detection_reads_total{source,outcome}`              | counter | Passages du poll sur un registre de détections, par issue         |
| `perf_sentinel_hub_detection_rows_total{source}`                       | counter | Lignes de comptes fusionnées depuis un registre interrogé         |
| `perf_sentinel_hub_analysis_runs{status}`                              | gauge   | Les runs actuellement stockés, par statut                         |
| `perf_sentinel_hub_findings{environment,finding_type,severity,status}` | gauge   | Les signatures de findings distinctes stockées, par environnement |
| `perf_sentinel_hub_findings_age_seconds`                               | gauge   | L'âge des comptes derrière `findings`                             |
| `perf_sentinel_hub_database_bytes`                                     | gauge   | La taille du fichier de base et de son journal d'écriture         |
| `perf_sentinel_hub_disclosure_import_rejected_total{reason}`           | counter | Publications de divulgation refusées, par motif                   |
| `perf_sentinel_hub_disclosure_deliveries_failed_total`                 | counter | Livraisons de webhook abandonnées après leur dernière tentative   |

Les deux familles de divulgation n'apparaissent que tant que les divulgations sont actives,
si bien qu'un Hub qui n'en a pas est collecté comme avant. `reason` prend huit constantes :
`unauthorized`, `gate_full`, `too_large`, `unsupported_media_type`, `malformed_report`,
`render_refused`, `internal_refused` et `conflict`. `render_refused` est le refus du moteur,
une empreinte qui ne se recalcule pas ou une attestation qui nomme un autre rapport. Les deux
compteurs repartent de zéro à chaque démarrage, comme celui de l'import des findings.

`outcome`, sur les lectures de détections, prend cinq constantes, publiées dès le démarrage
pour chaque daemon interrogé : `ok`, `aborted`, `unauthorized`, `failed` et `capped`.
`aborted` est un daemon qui a ouvert un nouveau registre ou l'a coupé pendant le passage, et
`capped` un registre de plus de 64 pages. Un daemon antérieur à perf-sentinel 0.37.0, ou dont
le registre est coupé, n'est jamais lu et reste à zéro. Un registre poussé ne compte que ses
refus, dans `detection_import_rejected_total`, dont les motifs sont ceux de l'import des
findings. Les trois compteurs de détections repartent de zéro à chaque démarrage.

Quatre partis pris dans cette forme.

Seul un daemon reçoit une série de source, et seulement un que le Hub a
réellement observé. Un backend de traces n'est jamais interrogé, et un daemon
sans ligne `source_state` n'a jamais été joint du tout, donc publier une valeur
pour l'un ou l'autre affirmerait une chose que le Hub n'a pas vue. Cette ligne
est aussi ce que la rétention supprime pour une source qu'elle a cessé
d'interroger, donc une source oubliée depuis longtemps devient silencieuse
plutôt que de passer au vert.

Un daemon jamais interrogé avec succès ne reçoit aucune série `last_success`.
Zéro se lirait comme "a réussi à l'instant", l'inverse de jamais.

`analysis_runs` est une gauge, pas un compteur `_total`. Un run passe d'un statut
à l'autre, et un run terminé expire sur `Hub:Analysis:RunRetention`, donc chaque
série descend autant qu'elle monte. Un run encore `pending` ou `running` n'est
jamais purgé quel que soit l'âge apparent de sa ligne, un worker s'apprêtant à y
écrire ou y écrivant déjà. Chaque statut est émis même à zéro, une gauge qui
disparaît se lisant comme un échec de collecte plutôt que comme "rien n'est dans
cet état".

`findings` compte les signatures distinctes par environnement, et le `status`
qu'elle porte est celui de l'environnement, calculé comme le calcule
`GET /api/findings?environment=` : un finding encore vu en recette ne garde pas
`active` la copie de la production. Les séries d'un environnement et d'un statut
totalisent donc les findings que cette lecture détient pour eux, toutes pages
confondues. `finding_type` et `severity` sont du texte libre envoyé par un
daemon, donc une valeur hors des douze types du moteur ou de ses trois
sévérités compte sous `other` au lieu d'ouvrir une série à elle. À l'inverse
d'`analysis_runs`, une combinaison vide ne publie aucune série. Les zéros
seraient chaque environnement multiplié par chaque type, sévérité et statut,
presque tous vides pour de bon, et la famille se lit au travers de `sum()`, où
une série absente vaut déjà zéro. Les comptes sont gardés 15 secondes après la fin d'un compte, ou neuf fois sa
durée quand c'est plus long, parce
que l'endpoint est anonyme et qu'un compte lit tous les findings de son
environnement. Une collecte dans cet intervalle reçoit les comptes précédents,
et toutes les autres familles restent calculées à la collecte. Une collecte
attend un nouveau compte cinq secondes au plus. Au-delà, elle reçoit les comptes
précédents pendant que le compte se termine pour une collecte suivante, et
`perf_sentinel_hub_findings_age_seconds` dit leur âge. Un compte grandit avec les
signatures stockées : sans cette borne, une base de plusieurs millions de
signatures le faisait durer plus que les 10 secondes du `scrape_timeout` de
Prometheus, et toutes les familles disparaissaient avec lui. La pause de neuf fois
la durée empêche un compte lent d'occuper la base plus d'un dixième du temps : avec
un cache de pages plus petit que la base, chaque compte la relit sur le disque, et
les imports concourent avec ces lectures.

`perf_sentinel_hub_database_bytes` se lit sur le système de fichiers, le fichier de
base plus son journal d'écriture. Un volume plein refuse toute écriture mais lit
encore, donc la collecte continue de le servir dans le cas pour lequel il existe.
Alertez sur sa croissance face au volume plutôt que sur les seules
métriques de volume du kubelet : un Hub dont la base est pleine ne stocke plus rien,
et ses sources montrent alors `database_full` comme dernière erreur, un code qui
parle du volume du Hub et pas du daemon, qui a répondu.

Un daemon qui voit pour la première fois plus de `Hub:NewSignaturesPerHourWarning`
signatures en une heure, 1000 par défaut, est signalé sur `/api/sources` et journalisé
une fois, jusqu'à ce qu'il repasse dessous. Une flotte saine se stabilise sur un
ensemble de signatures, donc une source qui en crée sans cesse de nouvelles met
quelque chose d'unique par trace dans une signature, et le Hub stocke une ligne par
signature reçue. Le contrôle tourne toutes les cinq minutes et ignore une source dont
tous les findings datent de la dernière heure, comme lors d'un premier contact. 0
le désactive.

La cardinalité est bornée par la configuration. `source` prend les identifiants
de `Hub:Sources`, fixés au démarrage et restreints à 1 à 64 caractères ASCII
alphanumériques, `.`, `_` ou `-`. `environment` prend les environnements de ces
mêmes sources, auxquels un caractère de contrôle est refusé au démarrage et qui
sont échappés à l'écriture. Le `status` des runs prend six constantes.
`findings` tient au plus environnements × 13 × 4 × 3 séries : douze types,
trois sévérités, un compartiment `other` sur chacun, et trois statuts. Rien de
ce qu'envoie un appelant n'atteint un libellé, et rien de ce qu'envoie un
daemon n'en atteint un sans être replié.

L'endpoint ne porte aucune authentification et reste ouvert sous `Hub:Auth`, pour que la
collecte n'ait pas besoin de session. Sans `Hub:MetricsPort`, il répond sur le port du
Hub, qu'il partage avec le lanceur : un proxy ouvert pour le navigateur sert aussi
`/metrics` tant que le chemin n'est pas exclu. Gardez-le derrière ce qui protège déjà le
reste du Hub.

`Hub:MetricsPort` (Helm `hub.metricsPort`) lui donne un port à lui : `/metrics` y répond
et nulle part ailleurs, et rien d'autre n'y répond. Le jugement porte sur le port que la
connexion a atteint, jamais sur l'en-tête `Host`. Le contrôle d'accès l'exige, voir
[ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md). Renseigner la clé ne fait pas écouter le Hub
sur ce port : le chart l'ajoute à `ASPNETCORE_HTTP_PORTS` (`"8080;9464"`), au conteneur et
au Service comme un port nommé `metrics` ; hors Helm, réglez
`ASPNETCORE_HTTP_PORTS="8080;9464"` à côté de `Hub__MetricsPort=9464`. Une fois démarré,
le Hub vérifie que ce port est l'un de ceux qu'il écoute, en plus d'un autre, sinon il
journalise l'événement `2002` en Critical et s'arrête avec le code 1 : le port principal
répondrait et `/metrics` serait injoignable partout, ou, le port des métriques seul, les
sondes et le lanceur recevraient `404`. Le chart refuse `8080`. L'Ingress continue de
router vers `http` seulement.

Le chart laisse la collecte en opt-in plutôt que de la supposer :

```yaml
service:
  annotations:
    prometheus.io/scrape: "true"
    prometheus.io/port: "9464"   # hub.metricsPort ; "8080" sans lui
    prometheus.io/path: /metrics
```

Un opérateur qui sélectionne des Services (un `ServiceMonitor` de Prometheus
Operator, un `VMServiceScrape` de VictoriaMetrics) trouve le Service par
`app.kubernetes.io/name: perf-sentinel-hub` et collecte son port `metrics` sur
`/metrics`, ou `http` sans `hub.metricsPort`.

### Les consommer

Quatre fichiers sous [`examples/`](../../examples), validés plutôt qu'esquissés.

| Fichier                                                                       | Est                                                           |
|-------------------------------------------------------------------------------|---------------------------------------------------------------|
| [`grafana-dashboard.json`](../../examples/grafana-dashboard.json)             | Douze panneaux sur neuf des dix-neuf familles, importable tel quel |
| [`FR/grafana-dashboard-FR.json`](../../examples/FR/grafana-dashboard-FR.json) | Le même tableau de bord, titres et descriptions en français   |
| [`prometheus-alerts.yml`](../../examples/prometheus-alerts.yml)               | Une règle, contrôlée par `promtool check rules`               |
| [`prometheus-scrape.yml`](../../examples/prometheus-scrape.yml)               | Un job de collecte pour un déploiement qui nomme ses cibles   |

Le moteur livre son propre tableau de bord pour ses propres métriques, et les
deux ne se recouvrent pas : aucun panneau d'ici ne lit une série de daemon, et
aucun panneau de là-bas ne lit une série du Hub. Importer les deux pour
surveiller une flotte et le Hub qui la collecte.

Les deux panneaux de findings comptent et ne listent jamais. L'historique de
chaque finding dans Grafana, un tableau qui suit le sélecteur de temps, est
servi par le tableau de bord des findings du moteur,
`examples/grafana-findings-dashboard.json` dans son dépôt, plutôt que par un
tableau de bord du Hub. À partir du moteur 0.24.0, ce tableau de bord lit
`GET /api/findings` au travers d'une seconde source de données Infinity qui vise
le Hub, à côté de celle qui vise un daemon.
[API-FR.md](API-FR.md#api-de-lecture) décrit les paramètres `environment`,
`from` et `to` que prend cette lecture. Avec le contrôle d'accès activé, cette source de
données a besoin d'un jeton machine, envoyé en `Authorization: Bearer`, voir
[ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md#jetons-machine).

### Pourquoi une seule alerte

Le Hub n'est sur le chemin de requête d'aucune production, et le push est le
chemin primaire : un daemon poste ses findings, les retient et les rejoue par
lots coalescés. Donc presque rien de ce que le Hub peut rapporter ne mérite de
réveiller quelqu'un, et une alerte qui se déclenche sur une condition qu'un
lecteur aurait vue sur un panneau n'est que du bruit. Quatre règles ont été
écrites puis retirées :

| Retirée                     | Pourquoi, et où la condition se voit à la place                                                                                                                                    |
|-----------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Une source est injoignable  | Elle surveille le filet de poll, pas le chemin de push, donc elle passe au rouge sur une flotte dont tous les daemons livrent. L'écran de flotte et deux panneaux la dessinent déjà |
| Une source est périmée      | Même angle mort, et quand le jour est écoulé le panneau de dernière réussite dessine la montée depuis un jour                                                                       |
| La file d'analyse s'accumule | Du travail soumis par des humains : une profondeur de 20, ce sont 20 personnes qui ont cliqué. `GET /api/status` le montre à celui-là même qui a soumis, et rien ne se perd à attendre |
| Des runs ont été interrompus | Se déclenche sur l'événement le plus banal qui soit, un redémarrage qui a attrapé un run en file, et ne se tait jamais puisque rien ne supprime ces lignes                          |

Ce qui survit est la seule condition qu'aucun tableau de bord ne peut montrer,
puisqu'un Hub mort ne publie aucune série et que tous les panneaux se vident
exactement comme sur une collecte mal configurée.

Le compteur d'imports est nouveau et ne mérite toujours pas de règle, ce qui
vaut d'être dit puisqu'on s'attendrait au contraire. `unauthorized` monte quand
une clé expire, et aussi quand n'importe qui poste un `source_id` inconnu, et le
Hub ne peut pas distinguer les deux : le seul label qui les séparerait est le
`source_id` de l'appelant, c'est-à-dire précisément la valeur non bornée qui ne
doit jamais atteindre un label. Alerter dessus reviendrait à donner à un inconnu
le moyen de vous réveiller, et `bad_request` aussi, dont la moitié query string
est contrôlée avant la clé. `gate_full` et `write_timeout` sont une
contre-pression que le daemon retraverse en rejouant, séparés parce qu'ils
nomment deux boutons différents : trop de téléverseurs à la fois, contre un
écrivain retenu par le poll ou par la rétention. La moitié de `bad_request` qui
exige une clé, comme `too_large`, se corrige dans le dépôt de l'exporteur
plutôt qu'ici. Les quatre relèvent du panneau, où un humain les
lit avec le contexte qui les distingue.

Deux manques subsistent, à nommer plutôt qu'à maquiller. Un push bloqué avant
d'arriver, par une politique réseau par exemple, ne produit aucune requête donc
aucun rejet : le compteur ne voit pas la panne la plus courante, et
`source_last_import_seconds` non plus, un daemon sans nouveau finding ne poussant
rien et ressemblant exactement à cela. Les distinguer demanderait une provenance
par finding que le Hub ne stocke pas. Et Prometheus ne détient aucune liste des
sources qui devraient exister, donc un daemon que le Hub n'a jamais joint est
silencieux plutôt qu'alarmant, ce qui relève de l'écran de flotte.

## Divulgations

Avec `Hub:Disclosures:ImportKey` posé, le Hub reçoit des divulgations périodiques d'une tâche
planifiée, voir [API-FR.md](API-FR.md#divulgations). Trois choses changent dans son
fonctionnement.

**Journaux de requêtes.** Le jeton d'un lien de partage voyage dans le chemin de la requête,
et la couche d'hébergement d'ASP.NET Core journalise chaque chemin en Information.
`appsettings.json` garde `Microsoft.AspNetCore` à Warning dans tous les environnements, et tant
que les divulgations sont actives le Hub règle aussi `Microsoft.AspNetCore.Hosting` à Warning
dans le code, si bien qu'aucun jeton n'atteint ses propres journaux. Le journal d'accès du contrôleur d'ingress
enregistre toujours chaque chemin, jetons compris : gardez-le aussi privé que les liens, ou
retirez-en les chemins `/share/`. Un jeton se révoque depuis le lanceur ou par
`DELETE /api/disclosures/{id}/share-links/{linkId}`.

**Webhooks.** Les livraisons tournent toutes les 15 secondes et reprennent après un
redémarrage. Une URL de webhook n'atteint jamais une ligne de journal, la journalisation des
requêtes du client HTTP comprise, puisque l'URL d'un webhook de messagerie porte son propre
secret. Une livraison qui abandonne après 8 tentatives compte sur
`perf_sentinel_hub_disclosure_deliveries_failed_total` et peut être renvoyée depuis le lanceur.

**Événements de journal.**

| Événement | Niveau      | Dit                                                                        |
|-----------|-------------|----------------------------------------------------------------------------|
| `2401`    | Information | Une divulgation a été publiée, avec son organisation, sa période et si elle a été rendue |
| `2402`    | Warning     | Les comptes de téléchargement des liens n'ont pu être écrits et sont perdus |
| `2403`    | Warning     | Un webhook n'a pu être joint, par son nom. La livraison est retentée        |
| `2404`    | Warning     | Une livraison a abandonné après sa dernière tentative                       |
| `2405`    | Warning     | Un passage de livraison a échoué en entier. Le suivant réessaie             |

## Sauvegarde

L'historique `first_seen` est la seule chose que le Hub stocke qu'aucun amont ne peut
reconstruire. Le tampon circulaire du daemon oublie, donc perdre le volume perd la
chronologie.

Le binaire livre une commande `backup` qui prend un instantané de la base vivante par le
`VACUUM INTO` de SQLite, sûr à côté de l'unique écrivain grâce au WAL. Elle lit
`Hub:DatabasePath` depuis la même configuration que le serveur, refuse d'écraser une
destination existante, et retire son fichier partiel quand la copie échoue.

L'instantané est une copie complète écrite sur le même volume, donc gardez au moins la
taille de la base elle-même de libre sur le PVC avant d'en démarrer un. Le défaut du chart
est de 1 Gio au total. Cette taille croît avec les jours d'observation que le Hub conserve,
une ligne d'environ 300 octets par finding, par daemon et par jour observé pendant la durée
de `Hub:Retention` : 2 000 signatures vues chaque jour sur trois daemons, au défaut de
180 jours, font environ 300 Mo, donc dimensionnez le volume pour la flotte plutôt que pour
le défaut. Les divulgations ajoutent leurs fichiers : le rapport, l'attestation, le bundle et
le PDF sont gardés dans la base, jamais purgés, si bien que la sauvegarde les emporte et
grossit à chaque publication.

```bash
kubectl exec deploy/perf-sentinel-hub -- /app/PerfSentinelHub backup /data/hub-backup-20260826.db
```

Datez le nom de fichier. Le garde-fou contre l'écrasement fait échouer un chemin fixe au
second passage, et l'image d'exécution chiselée n'a pas de shell pour supprimer un
reliquat.

### Sortir le fichier du cluster

`kubectl cp` ne peut pas tirer depuis le pod du Hub, qui n'a pas de tar. Montez le même
PVC dans un pod éphémère, copiez depuis là, et retirez l'instantané de ce pod.

Le volume est `ReadWriteOnce`, donc épinglez l'assistant au nœud où tourne le pod du Hub.
Faites-le tourner sous l'uid du Hub pour qu'il puisse lire et supprimer les fichiers, et
donnez-lui le contexte de sécurité qu'exige un namespace `restricted` :

```bash
NODE=$(kubectl get pod -l app.kubernetes.io/name=perf-sentinel-hub -o jsonpath='{.items[0].spec.nodeName}')
kubectl apply -f - <<EOF
apiVersion: v1
kind: Pod
metadata:
  name: hub-backup-fetch
spec:
  nodeName: ${NODE}
  securityContext:
    runAsNonRoot: true
    runAsUser: 1654
    runAsGroup: 1654
    seccompProfile: { type: RuntimeDefault }
  containers:
    - name: fetch
      image: busybox:1.37
      command: ["sleep", "3600"]
      securityContext:
        allowPrivilegeEscalation: false
        capabilities: { drop: ["ALL"] }
      volumeMounts: [{ name: data, mountPath: /data }]
  volumes:
    - name: data
      persistentVolumeClaim: { claimName: perf-sentinel-hub }
EOF
kubectl cp hub-backup-fetch:/data/hub-backup-20260826.db ./hub-backup.db
kubectl exec hub-backup-fetch -- rm /data/hub-backup-20260826.db
kubectl delete pod hub-backup-fetch
```

En local, `make backup DB=/chemin/vers/hub.db DEST=backup.db` enveloppe la même commande.

## Restauration

Remplacez le fichier de base pendant que le Hub est complètement arrêté. Ramenez le
Deployment à zéro et attendez que le pod se termine réellement avant de toucher au volume,
un arrêt gracieux pouvant prendre des dizaines de secondes :

```bash
kubectl scale deploy/perf-sentinel-hub --replicas=0
kubectl wait --for=delete pod -l app.kubernetes.io/name=perf-sentinel-hub --timeout=120s
```

Ensuite, depuis le pod assistant, copiez la sauvegarde par-dessus `/data/hub.db`,
supprimez les `hub.db-wal` et `hub.db-shm` périmés à côté, et remontez l'échelle.

## Jetons machine

Avec le contrôle d'accès activé, les jobs de CI et les tableaux de bord lisent le Hub avec
un jeton machine, voir [ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md#jetons-machine). Émettez
un jeton par équipe, avec les seuls projets de cette équipe par un rôle limité aux
findings : un jeton dans un outil partagé sert tout son périmètre à chaque utilisateur de
l'outil.

- **Révoquer** un jeton : supprimer son entrée de `hub.access.machineTokens`. La somme de
  contrôle du ConfigMap change et le pod redémarre : le jeton cesse aussitôt de
  fonctionner.
- **Faire tourner** un jeton : mettre à jour son Secret, puis
  `kubectl rollout restart deployment/<release>`. Le chart ne peut pas hacher un Secret
  qu'il ne possède pas : un Secret modifié seul ne redémarre rien. Il en va de même pour
  le secret du client OAuth et les clés des sources.
- Une entrée dont `expiresAt` est passé est refusée à chaque requête avec le `401` d'un
  jeton inconnu, et journalisée au démarrage en avertissement (événement `2001`, qui nomme
  le jeton, jamais sa valeur). Elle n'arrête jamais le Hub.

## Jetons de terminal

Les développeurs connectent la CLI avec `perf-sentinel login`, qui exige
`hub.auth.trustedProxyNetworks` renseigné avec les seuls pods du contrôleur d'ingress,
voir [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md#connexion-du-terminal). Un jeton se
révoque depuis le lanceur (son détenteur sur Votre accès, un admin sur Couverture) ou par
`perf-sentinel logout`, et prend fin de toute façon
avec la session qui l'a approuvé. La table et les codes en attente ne demandent rien à un
opérateur : les jetons expirés partent avec la passe de rétention quotidienne, et les codes
en attente vivent en mémoire, si bien qu'un redémarrage n'annule que les connexions en
cours. Événements, en Information sauf mention, jamais avec un code ni un jeton :

| Événement | Quand |
|-----------|-------|
| `2010` | Une connexion du terminal a été approuvée, journalisée au clic même si aucun terminal ne récupère le jeton : qui, son sujet, l'adresse qui a demandé, si elle venait d'un autre réseau que le navigateur qui approuve |
| `2011` | Une connexion du terminal a été refusée |
| `2012` | Un jeton de terminal a été révoqué : par qui (son détenteur, par `/auth/revoke`, ou un sujet depuis la page d'accès), son propriétaire et son id |
| `2013` | Warning : un code a été refusé parce que trop sont en attente, depuis cette adresse (10) ou en tout (200) |
| `2014` | Warning, au démarrage avec le contrôle d'accès : la connexion du terminal est coupée parce que `Hub:Auth:TrustedProxyNetworks` est vide |
| `2015` | Le terminal a récupéré le jeton d'une connexion approuvée : l'id du jeton, l'adresse qui a demandé, qui l'a approuvée et son sujet |

## Comptes locaux de secours

Un compte local se connecte sur la page du Hub pendant une panne du fournisseur, voir
[AUTHENTICATION-FR.md](AUTHENTICATION-FR.md#comptes-locaux). Son mot de passe n'atteint
jamais les values, une ligne de commande ni le journal du Hub : le Hub n'en détient que le
hash, depuis un Secret.

**En créer un.**

1. Hachez le mot de passe avec l'image. `hash-password` le lit sur stdin, jamais depuis un
   argument, et n'affiche que le hash ; le mot de passe compte 15 à 1 024 caractères.

   ```bash
   printf 'Password: ' >&2; IFS= read -rs password; echo
   printf '%s\n' "$password" | docker run --rm -i ghcr.io/institut-du-numerique-responsable/perf-sentinel-hub:<version> hash-password > ops.hash
   unset password
   ```

   La première ligne fonctionne sous bash et zsh ; `IFS=` garde les espaces de début et de
   fin, que la connexion compare tels que tapés. `printf` est une commande interne du shell,
   le mot de passe n'apparaît donc dans aucune liste de processus. Depuis un Hub en marche,
   `kubectl exec -i deploy/perf-sentinel-hub -- /app/PerfSentinelHub hash-password` lit
   stdin de la même façon. Sur un terminal à lui, `hash-password` le demande deux fois sans
   l'afficher.
2. Rangez-le, puis supprimez le fichier :
   `kubectl create secret generic perf-sentinel-hub-break-glass --from-file=ops=ops.hash`.
   Le hash n'est pas le mot de passe, mais gardez-le aussi privé : 600 000 itérations
   ralentissent une devinette hors ligne, elles ne l'empêchent pas.
3. Déclarez le compte et donnez-lui un groupe, puis `helm upgrade` :

   ```yaml
   hub:
     access:
       adminGroups: ["/hub-admins", "internal:break-glass"]
       groups:
         - { name: break-glass, members: ["local:ops"] }
       localAccounts:
         - { name: ops, secretName: perf-sentinel-hub-break-glass, secretKey: ops }
   ```

Gardez le mot de passe dans le coffre de l'équipe, et testez le compte une fois, par
`/auth/sign-in`, tant que le fournisseur fonctionne.

**Après chaque usage, changez-le.** Hachez un nouveau mot de passe, remplacez le Secret,
puis `kubectl rollout restart deployment/<release>` : le chart ne peut pas hacher un Secret
qui ne lui appartient pas, un Secret modifié ne fait donc rien redémarrer à lui seul. Le
redémarrage met fin à chaque session du compte. Les jetons de terminal qu'il a approuvés
vivent jusqu'à leur expiration, 8 heures au plus : révoquez-les depuis l'écran
Couverture du lanceur.

```bash
kubectl create secret generic perf-sentinel-hub-break-glass --from-file=ops=ops.hash \
  --dry-run=client -o yaml | kubectl apply -f -
kubectl rollout restart deployment/perf-sentinel-hub
```

**En retirer un.** Supprimez son entrée et son membre `local:<nom>`. La somme de contrôle de
la ConfigMap fait redémarrer le pod, et les sessions du compte prennent fin à leur requête
suivante.

**Freiné.** Cinq échecs depuis une adresse pour un nom démarrent une temporisation, de 30
secondes jusqu'à 15 minutes, et une adresse hache au plus 5 fois par minute. Il n'y a pas
de verrou à lever : les échecs d'un attaquant ralentissent sa propre adresse, pas celle de
l'opérateur. L'état vit en mémoire, un redémarrage l'efface donc.

| Événement | Quand |
|-----------|-------|
| `2020` | Warning : un compte local s'est connecté, avec son nom et l'adresse cliente |
| `2021` | Warning : une connexion locale a été refusée, avec le compte (`(unknown name)` pour un nom non déclaré, jamais ce qui a été tapé), l'adresse cliente et les échecs consécutifs |

## Revenir en arrière avec le contrôle d'accès activé

Ne revenez jamais à une image antérieure à la version qui applique le contrôle d'accès
tant que `hub.access.enabled` vaut `true`. Les versions précédentes ignorent les clés
`Hub:Access` et rien ne vérifie une configuration plus récente : elles **s'ouvrent**.
`/api/findings` redevient anonyme et chaque session voit chaque enregistrement.
`Hub:Auth:RequiredGroups` tient toujours dans ces versions, jusqu'à celle qui l'a ajouté :
seuls ses membres se connectent. Réglez d'abord `hub.access.enabled: false`, comme une
décision délibérée, puis revenez en arrière.

Revenir avant la version de stockage (version 9 du schéma) puis repartir en avant est sûr
pour les données : chaque démarrage complète les attributs, le service par source et les
paires d'incident qu'une version plus ancienne a laissés de côté.
