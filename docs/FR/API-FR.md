# API HTTP

Quatre surfaces, et elles ne se recouvrent pas. Un daemon pousse vers l'API d'import. Un
greffon d'IDE ou un job de CI lit l'API de lecture. Le navigateur utilise l'API d'analyse et
le relais d'acquittement, lit `/api/incidents` pour son écran d'incidents, et ne lit
`/api/findings` que sur la page d'acquittement, par signature.

## API d'import

`POST /api/import/findings?source_id=<id>` accepte l'enveloppe du daemon
`{"producer_version":"…","findings":[…]}` avec `X-API-Key`. Une requête porte de 1 à 100
findings et 2 Mio au plus. La réponse n'est envoyée qu'une fois l'upsert idempotent par
signature commité. Un corps qui répète un nom de propriété dans un même objet est un `400`,
car le Hub lit un finding avec deux parseurs qui en garderaient des copies différentes.

Quatre imports tournent à la fois, ce qui borne la mémoire des requêtes indépendamment du
nombre de daemons. Les écritures sont sérialisées face aux chemins de poll et de
rétention. Un import qui ne peut pas prendre le verrou d'écriture en cinq secondes reçoit
`503 Retry-After: 1`, et les exportateurs des daemons conservent puis rejouent leurs lots
fusionnés. La rétention purge par tranches bornées, pour qu'une purge longue ne rejette
pas les imports pendant toute sa durée.

Un push met à jour les findings et les observations par source, rien d'autre. Il n'efface
jamais le `unreachable_since_ms` du chemin de poll. Une source que le Hub ne peut pas
joindre continue de rapporter `unreachable_since` alors que son daemon pousse avec succès,
et c'est juste : la joignabilité est un fait sur la route du Hub vers le daemon, qu'un
push n'exerce pas.

## API de lecture

| Endpoint                             | Renvoie                                                                                                                                                                                                            |
|--------------------------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `GET /api/status`                    | La version du Hub, celle du moteur qu'il lancerait (`engine_version`, null quand aucun n'est configuré), et ce que coûte un run : workers, profondeur de file, plafond de traces, timeout, rétention de rapport    |
| `GET /api/sources`                   | Chaque source configurée avec son kind et son dernier état de collecte connu                                                                                                                                       |
| `GET /api/findings`                  | Les findings, filtrés par `service`, `finding_type`, `severity`, `status`, `signature`, `grouping`, `environment`, `source_id`, `from`, `to`, `offset`, `limit`, `include_acked`                                   |
| `GET /api/findings/{traceId}`        | Les findings d'une trace d'exemple                                                                                                                                                                                 |
| `GET /api/sources/{sourceId}/daemon` | Les réglages appliqués d'un daemon et son propre compte rendu. Voir plus bas                                                                                                                                       |
| `GET /api/incidents`                 | Les incidents enregistrés par les daemons interrogés, du plus récent au plus ancien, filtrés par `service`, `kind`, `namespace`, `environment`, `source_id`, `offset`, `limit`. Sans leurs findings, voir plus bas |
| `GET /api/incidents/{id}`            | Un incident entier, findings figés compris                                                                                                                                                                         |
| `POST /api/incidents/refresh`        | Lit maintenant l'anneau d'incidents de chaque daemon, puis répond exactement comme `GET /api/incidents`. Mêmes paramètres. Voir plus bas                                                                           |
| `GET /api/access`                    | Avec `Hub:Access:Enabled` seulement : qui est l'appelant et ce qu'il atteint, plus la configuration pour un admin. Voir [plus bas](#get-apiaccess)                                                                 |
| `GET /metrics`                       | Format texte Prometheus, voir [OPERATIONS-FR.md](OPERATIONS-FR.md#métriques)                                                                                                                                       |
| `GET /health/live`                   | Si le process est debout                                                                                                                                                                                           |
| `GET /health/ready`                  | Positif après l'initialisation de SQLite                                                                                                                                                                           |

Sur `/api/sources`, les horodatages sont null pour une source jamais observée, ce qu'un
lecteur ne doit pas confondre avec l'epoch. `producer_version` est null pour un backend de
traces, parce qu'un backend stocke des traces et ne détecte rien.

`acks_state` et `acks_read_ms` disent ce qu'a donné la dernière lecture des acquittements
d'un daemon et quand elle a été prise, null tous les deux quand aucune n'a eu lieu. Les
états sont ceux d'`incidents_state`, où `absent` couvre aussi un daemon antérieur à 0.24.0,
qui n'est jamais interrogé, plus `truncated` : la liste a atteint le plafond du daemon,
mille acquittements, donc sa fin peut manquer.

`ack_relay` vaut true quand le Hub détient un identifiant pour écrire des acquittements sur
ce daemon, voir [CONFIGURATION-FR.md](CONFIGURATION-FR.md#lidentifiant-dacquittement). Ni son
en-tête ni sa valeur ne sont jamais publiés.

Sur `/api/findings`, `include_acked` vaut `true` par défaut. À `false`, il ne liste un
finding que tant qu'au moins une source du périmètre le porte non acquitté, toutes les
sources comptant quand la lecture n'a pas de périmètre, voir
[Comment `include_acked` juge un finding](#comment-include_acked-juge-un-finding).
`signature` est une correspondance exacte. Le Hub la borne et laisse sa forme au daemon :
au-delà de 1 024 caractères, ou avec un caractère de contrôle, elle répond `400`. Un
`service`, `finding_type`, `severity`, `status`, `signature` ou `grouping` donné vide ou
composé seulement de blancs se lit comme absent, ce qu'un tableau de bord envoie pour son
choix "All". Les lignes suivent un ordre total, `last_seen` décroissant puis `signature`, et
`offset` en saute autant avant que `limit` ne s'applique. `offset` va de 0 à 1 000 000, et
une valeur hors de cette plage est un `400` plutôt qu'une page ramenée dans les bornes.

`grouping` est le filtre du même nom du daemon, une correspondance exacte sur la valeur de
regroupement effective d'un finding, le premier `[detection] grouping_attributes` porté par
ses spans, par défaut `k8s.namespace.name`. C'est une chaîne libre comme `service`, donc une
valeur inconnue donne une page vide. La signature ignore le regroupement, donc un même
finding peut être vu dans plusieurs namespaces alors que son enveloppe ne nomme que le
dernier : le Hub garde chaque regroupement dans lequel une source l'a remonté, et le filtre
retient n'importe lequel d'entre eux, remonté par une source du périmètre. Le push d'un
daemon fusionne une signature entre regroupements, alors que sa liste a une ligne par
regroupement, donc c'est la collecte qui enregistre chacun d'eux. Il ne restreint pas `from` et
`to` : une lecture fenêtrée liste un finding vu dans la fenêtre et vu dans ce regroupement,
pas forcément les deux à la fois.

`environment` et `source_id` donnent un périmètre à la lecture, les sources d'un
environnement ou une seule source. Ce sont des ensembles fermés, les sources configurées du
Hub, et une valeur hors de ces ensembles répond `400` plutôt qu'une page vide, parce qu'une
faute de frappe ne doit pas se lire "aucun finding". Donné vide ou composé seulement de
blancs, chacun se lit comme absent, comme les filtres ci-dessus, donc il ne répond ni `400`
ni une page vide. `environment` se résout en chaque source configurée avec lui. Donné avec
`source_id`, les deux s'intersectent, donc une source hors de l'environnement nommé ne liste
rien, la réponse d'une paire de filtres qui s'excluent. Sans l'un ni l'autre, la lecture
couvre toute la flotte, comme elle l'a toujours fait. Une réponse avec périmètre décrit ce
périmètre et non la flotte, voir
[Ce que le Hub ajoute à un finding](#ce-que-le-hub-ajoute-à-un-finding).

`from` et `to` bornent la lecture à une fenêtre d'observation, en millisecondes depuis
l'epoch, chacun facultatif et tous deux inclus. Un finding est listé quand une source du
périmètre l'a observé dans la fenêtre, donc la fenêtre respecte `environment` et
`source_id`. Elle choisit des lignes et ne les réécrit pas : `first_seen`, `last_seen` et
`status` décrivent toujours le finding tel qu'il est maintenant. Une valeur qui n'est pas
un entier positif ou nul sans fioriture, signe compris, répond `400`, et un `from` supérieur
à `to` aussi. Donné vide ou composé seulement de blancs, chacun se lit comme absent et
laisse son côté ouvert.

La fenêtre a la granularité du jour. Le Hub relève les jours où chaque source a observé un
finding, des jours UTC entiers sur sa propre horloge, donc une borne qui tombe dans un jour
prend ce jour entier. Avant le premier jour que le Hub a relevé pour une source, cette
source est supposée avoir porté le finding de son `first_seen` au début de ce jour, et
jusqu'à son `last_seen` quand aucun jour n'a jamais été relevé, ce qui est le cas d'une base
écrite avant que le relevé n'existe. [LIMITATIONS-FR.md](LIMITATIONS-FR.md) dit ce que
coûte cette supposition.

### La vue daemon

`GET /api/sources/{sourceId}/daemon` lit à la demande plutôt que depuis le poll. Les
réglages ne changent jamais sans un redémarrage dont le Hub n'a aucun signal, et ce sont
les jauges qui font l'intérêt de la vue.

**Un échec est une observation, pas une panne.** Une source inconnue répond `404` et un
backend de traces `400`, puisqu'il ne fait tourner aucun daemon. Un daemon qui ne répond
pas est rapporté `state: "unreachable"` avec un code d'erreur, jamais en `502` : le Hub
relaie la santé d'une source, il n'échoue pas lui-même.

**Ce qu'il relaie verbatim.** `config` est la section `[daemon]` du daemon. Elle vaut null
avec `config_unavailable_reason: "api_disabled"` quand ce daemon ne sert aucune API de
query, ce qui est une affirmation de configuration et non une panne. `detection_config`,
`scoring_config` et `energy_model` viennent de l'export du daemon, là où `/api/config` ne
les porte pas. `warnings` est le conseiller de tuning du daemon lui-même : le Hub relaie
ces phrases et n'en écrit aucune.

**Ce qu'il borne.** Un conseil au-delà de deux mille caractères est coupé avec une ellipse
visible. Tout ce qui dépasse la centaine de conseils est compté dans `warnings_dropped`
plutôt que disparu en silence. Une lecture d'export ratée est nommée dans
`hints_unavailable_reason` au lieu de se lire comme un bulletin de santé vierge.

**Ce qu'il dérive.** Deux choses. `state`, selon qu'une jauge a franchi 90 % de son
plafond, la même ligne que trace le monitor du daemon. Et `energy_source`, la provenance du
chiffre d'énergie de l'instantané, formulée comme le moteur la formule depuis la 0.26.0
(`source scaphandre_rapl on 1 of 2 services · rest modeled from I/O counts`, null quand
aucune énergie n'a été calculée). Il lit la couverture par service, jamais `energy_model`,
qui nomme la source ou le niveau d'intensité carbone et non un backend d'énergie. Il porte aussi `daemon_defaults`,
`detection_defaults` et `defaults_engine_version`, pour qu'un lecteur puisse marquer ce
qu'un daemon a réellement changé. Ces défauts sont ceux du binaire que ce Hub embarque,
donc la version est nommée plutôt que supposée, et un daemon sur une autre mineure est
signalé plutôt que jugé.

**À quelle fréquence il lit.** Une ligne ouverte relit à l'intervalle que choisit son
lecteur. `?refresh=status` fait de ce tic une simple lecture de statut au lieu des trois
que prend la vue complète, l'export étant la lourde et ne tournant au plus qu'une fois par
minute. Une ligne dont la lecture a échoué relit avec cette même requête bon marché, et la
première qui répond est suivie aussitôt d'une lecture complète, de sorte qu'une ligne
laissée ouverte se rétablit d'elle-même.

Ce rythme ne peut pas affamer le daemon. Le plafond de 32 requêtes concurrentes du moteur
est cantonné à sa route d'ingestion OTLP précisément pour que `/api` et `/health` restent
réactifs, les tics de statut ne prennent aucune place de lecture au Hub, et les lectures
complètes sont bornées à deux à la fois sur des connexions mutualisées. La surface de
query du daemon est HTTP(S) seulement par conception, son port gRPC étant l'ingestion
OTLP, donc le Hub ne lui parle aucun RPC.

### Incidents

Un daemon perf-sentinel à partir de 0.20.0 avec `[daemon.incidents]` activé enregistre un
incident quand l'alerte de l'exploitant le lui poste, et gèle les findings des minutes qui
précèdent. Le Hub copie cet enregistrement à chaque poll du daemon et ne redérive rien :
les findings d'un incident sont ceux du daemon, gelés à la capture et consolidés une fois,
et le finding du Hub de même signature peut avoir évolué depuis. La copie survit à
l'anneau du daemon, qui meurt avec lui. Une copie est la capture d'un daemon, à la clé
de l'`id` de l'incident et du `source_id` ensemble : l'id hache le service, le genre et
l'horodatage de l'alerte et rien du daemon, donc deux daemons nourris de la même alerte
listent le même id et chacun garde ses propres findings gelés. De deux captures du même
incident par un même daemon la plus riche est gardée, puisqu'Alertmanager répète une
alerte active et qu'un daemon redémarré entre temps gèle une fenêtre que son anneau
n'atteint plus. Les copies expirent avec la rétention des findings, sur l'horloge du Hub
et jamais sur le `at_ms` de l'alerte.

Le poll lit l'anneau du daemon page par page sous un plafond de corps de 4 Mio. Une page
qui le dépasse est relue à la moitié de sa taille depuis le même décalage, jusqu'à un seul
incident, puisque le daemon embarque jusqu'à mille findings par incident et qu'une page
pleine d'un daemon chargé ne tient jamais alors qu'un incident seul tient toujours. Un
incident qui dépasse le plafond à lui seul est classé `response_too_large`, et une page
qui n'est pas un tableau JSON `invalid_incidents`.

Le poll lit la route avec `AuthHeaderName` et `AuthHeaderValue` de la source, donc une clé
de lecture s'écrit `AuthHeaderName=X-API-Key` avec le `[daemon] read_api_key` du daemon en
valeur. Le résultat de la lecture est `incidents_state` sur `/api/sources` : `ok`, `absent`
(un daemon antérieur à 0.20.0 répond 404 et un daemon au magasin désactivé 503, aucun des
deux n'est une panne), `unauthorized`, `error`, ou null tant qu'aucun poll n'a tourné.
Aucun de ces états ne touche la joignabilité de la source : les findings ont été collectés
au même poll, et une clé refusée ne doit pas rétrograder chaque finding rapporté par ce
daemon.

`GET /api/incidents` liste les copies du plus récent au plus ancien, une ligne par
`(id, source_id)`, sans leurs findings, qui peuvent atteindre le millier par incident et
ne sont jamais lus pour la liste. Chacune porte les champs du daemon plus `source_id`,
`source_name`, `environment`, `first_seen`, `last_seen` (horloge du Hub), `finding_count`
et `capture` : `complete` quand `oldest_finding_ms` est au plus égal à `window_from_ms`,
`partial` quand l'anneau avait déjà évincé une partie de la fenêtre, `empty` quand il ne
tenait rien. Parmi les champs du daemon, `namespace` est le label de l'alerte qu'un daemon
0.20.0 porte quand l'alerte en nommait un, relayé tel que le daemon l'a écrit et absent
sinon : une étiquette pour la lecture et le filtrage, jamais une clé.

La liste filtre sur `service`, `kind`, `namespace`, `environment` et `source_id`, et
pagine avec `offset` et `limit`. `service` et `namespace` sont des chaînes libres comparées
à l'identique, et une valeur inconnue donne une page vide. `kind`, `environment` et
`source_id` sont des ensembles fermés, les cinq genres du daemon et les sources configurées
du Hub, et une valeur hors de ces ensembles répond `400` plutôt qu'une page vide, parce
qu'une faute de frappe ne doit pas se lire "aucun incident". Chacun de ces cinq filtres,
donné vide ou composé seulement de blancs, se lit comme absent, ensembles fermés compris,
donc il ne répond ni `400` ni une page vide. `environment` se résout en chaque source
configurée avec lui. Donné avec `source_id`, les deux s'intersectent, donc une source hors
de l'environnement nommé ne liste rien, la réponse d'une paire de filtres qui s'excluent.

`GET /api/incidents/{id}` renvoie un incident entier, findings compris, la
copie la plus riche quand plusieurs sources tiennent l'id. Un finding dont `first_seen_ms`
dépasse `at_ms` n'a démarré qu'après le redémarrage. Comme `/api/findings`, les deux
répondent à quiconque atteint le port du Hub :
la clé de lecture d'un daemon protège le daemon, et le Hub réexpose les findings gelés
derrière ce qui protège le Hub.

`POST /api/incidents/refresh` lit la flotte à la demande, comme le fait la vue daemon, et
l'écran des incidents l'appelle à chaque ouverture. Le poll est le plancher plutôt que
l'unique chemin : un exploitant alerté d'un OOM kill ouvre l'écran dans la minute et
`Hub:PollInterval` vaut une heure, donc un écran nourri du seul poll n'afficherait rien
jusqu'au suivant. Un POST parce que la route écrit dans le magasin, et parce qu'un GET
serait mis en cache et préchargé, ce qu'une lecture de flotte ne doit jamais être. Elle
s'étale sur chaque source de kind `daemon`, bornée par `Hub:MaxConcurrentPolls` exactement
comme le worker de poll, et répond le même corps que `GET /api/incidents` avec les mêmes
paramètres, validés à l'identique, de sorte qu'un écran n'a besoin que d'un aller-retour.
Les filtres resserrent la réponse, jamais la lecture : l'éventail couvre toute la flotte
quoi que demande la requête. Elle partage l'isolation des pannes du poll : un 401, un
404, un 503 ou une page trop grosse classe son propre `incidents_state` et ne touche jamais
la joignabilité de la source, et un daemon qui refuse ne coûte rien aux autres.

Ce qu'elle garantit aux daemons : une source dont la dernière lecture a moins de dix
secondes est sautée et sa copie stockée est servie à la place, pour qu'une boucle de
rechargement, ou cinq personnes devant le même écran, ne puissent pas prendre la flotte
d'assaut. Ce plancher est une constante et non un réglage, parce qu'il protège les daemons
de ce Hub plutôt qu'il n'exprime une préférence. Une seconde garantie le précède, une
barrière de deux rafraîchissements simultanés, qui répond `503` avec `Retry-After: 1`
au-delà, chaque rafraîchissement tamponnant une page par daemon sous le plafond de corps
de 4 Mio. `incidents_read_ms` sur `/api/sources` porte la date de chaque copie, null aux
côtés d'un `incidents_state` null quand aucune n'a jamais été prise : sans elle, une flotte
calme et une copie périmée se lisent pareil.

### Ce que le Hub ajoute à un finding

Chaque finding du daemon est conservé comme un document JSON opaque et additif. Le Hub y
ajoute `first_seen`, `last_seen`, `max_confidence`, `status`, un `lineage` optionnel,
`sources`, une entrée par source ayant rapporté le finding, et un `acks` optionnel. Une
entrée de `sources` porte l'`id` de la source, celui que liste `/api/sources` et sur lequel
filtre `source_id`, son `name`, son `environment` et sa `producer_version`, et la fraîcheur
de son observation. Les clients d'IDE doivent ignorer les champs inconnus, comme ils le font
avec l'API du daemon.

`acks` liste les acquittements actifs que le Hub a reflétés depuis les daemons, une entrée
par source de `sources` dont le daemon en tient un sur cette signature, dans le même ordre.
Il est absent quand aucune source n'en tient, donc un finding que personne n'a acquitté se
lit comme il s'est toujours lu. Une entrée porte le `source_id`, la `source` de
l'acquittement, `daemon` pour un acquittement pris à l'exécution et `toml` pour un
acquittement de la baseline de CI, puis `by`, `reason` quand le daemon en a donné une, `at`,
et `expires_at` quand l'acquittement expire. `at` et `expires_at` sont le texte du daemon,
relayé tel qu'il est venu. Un acquittement est actif tant qu'il n'a pas d'expiration ou que
son expiration est encore à venir sur l'horloge du Hub, et seule une source dont la dernière
lecture des acquittements a donné `ok` ou `truncated` est listée, puisque les lignes que
laisse une lecture échouée ne prouvent rien. Le nom est réservé comme les autres champs du
Hub : une propriété `acks` envoyée par un daemon est écartée, et l'`acknowledged_by` du
daemon est relayé tel quel à côté de la liste du Hub.

`first_seen` vient de l'enveloppe du daemon (`first_seen_ms`), borné à l'heure
d'observation du Hub et à un plancher de bon sens en millisecondes Unix. Ni une horloge de
daemon en avance ni un bug d'unité en secondes ne peuvent le fausser, et il retombe sur
l'heure d'observation quand un producteur omet le champ.

`last_seen` est délibérément l'horloge d'observation du Hub. La rétention, l'ordonnancement
et les comparaisons de fraîcheur s'appuient dessus, donc il ne vient jamais d'une horloge
distante.

Lue avec `environment` ou `source_id`, une enveloppe décrit ce périmètre et non la flotte.
Le document du daemon est la copie de la source du périmètre qui a vu le finding en dernier,
donc `severity` juge cette copie. `first_seen` est le plus ancien et `last_seen` le plus
récent sur les sources du périmètre, `status` est dérivé de ce `last_seen` et des battements
de ces seules sources, et `sources` et `acks` ne listent qu'elles. `max_confidence` reste à
l'échelle de la flotte, délibérément, la plus haute confiance qu'une source ait jamais
rapportée. La copie propre à une source commence à sa première observation après la mise à
niveau qui l'enregistre. Jusque-là, sa ligne sert la copie que la flotte partage, la plus
fraîche toutes sources confondues.

### Comment `include_acked` juge un finding

`include_acked=false` juge chaque source du périmètre qui porte le finding, d'après celle de
ses deux vues que le Hub a lue en dernier. Quand la dernière lecture des acquittements de
cette source a donné `ok` et n'est pas plus ancienne que la dernière observation du finding
par la source, le miroir décide : le finding y est acquitté quand le miroir tient un
acquittement actif sur sa signature. Sinon c'est la copie de l'enveloppe propre à la source
qui décide, par un `acknowledged_by` non null, ce qui est tout ce que le Hub jugeait avant
de refléter les acquittements. Une source dont les acquittements n'ont jamais été lus, une
lecture échouée et une liste `truncated` retombent toutes sur l'enveloppe, et une source qui
n'a pas encore de copie propre est jugée sur la copie que la flotte partage, dans une
lecture à périmètre aussi. Le finding est listé tant qu'au moins une de ces sources le porte
non acquitté, donc un finding acquitté en production reste listé pour la recette, et pour la
flotte qui comprend la recette. Le filtre s'applique avant la limite de page.

### Comment `status` est dérivé

Dérivé à la lecture, jamais stocké, depuis des données que le Hub garde déjà :

| Valeur            | Sens                                                                    |
|-------------------|-------------------------------------------------------------------------|
| `active`          | Vu dans le délai `Hub:ResolutionGrace`, 7 jours par défaut              |
| `likely_resolved` | S'est tu, mais son endpoint bat encore depuis une source joignable      |
| `not_observed`    | Rien ne prouve rien : un endpoint silencieux, ou une flotte injoignable |

C'est une présomption et non un verdict. Un finding qui part par rétention part toujours
en silence, mais un lecteur peut désormais distinguer "l'endpoint tourne sans le finding"
de "personne ne regarde". `?status=<valeur>` filtre, et le filtre s'applique avant la
limite de page. Dans une lecture avec périmètre, le statut est celui du périmètre : un
finding que la production rapporte encore peut être `not_observed` pour la recette, et seul
un battement venu d'une source du périmètre l'y rend `likely_resolved`.

### Filiation

`first_seen` vaut par signature, donc un finding dont le template normalisé change reçoit
une nouvelle signature et un nouveau `first_seen`. Depuis le schéma v2, le Hub relie une
telle mutation à son prédécesseur à l'import, quand exactement un finding stocké :

- partage le service, le détecteur et l'endpoint,
- porte un hash de template différent,
- a été vu dans les 30 derniers jours et strictement avant le lot entrant,
- et n'est pas lui-même déjà supplanté.

Une signature change aussi quand l'**endpoint** change, et ce chemin-là n'est pas relié.
La règle ci-dessus fige l'endpoint et cherche un template qui a bougé, donc l'inverse, un
template stable sur un endpoint renommé, ne trouve aucun candidat et le successeur ne
porte pas de bloc `lineage`. Une montée de version du moteur qui lui apprend à résoudre un
endpoint qu'il rendait jusque-là en `unknown` produit exactement ce cas.

L'ambiguïté n'enregistre rien, nommer l'un de plusieurs candidats serait une supposition.

Une enveloppe reliée porte un objet `lineage` avec `original_first_seen`, la naissance la
plus ancienne de la chaîne, et `predecessors`, sa longueur. Les deux sont dénormalisés sur
le maillon le plus récent, de sorte que la filiation complète survit à la purge par
rétention de chaque étape antérieure. L'heuristique est conservatrice et non destructive :
les deux lignes restent des findings distincts, et le prédécesseur vieillit par la
rétention ordinaire.

## Relais d'acquittement

Deux routes écrivent sur le daemon derrière une source, avec la clé d'acquittement de ce
daemon. Toutes deux sont un `POST` à corps JSON, et aucune n'est jamais ouverte : sous
`Hub:Auth` elles demandent une session.

| Endpoint                                   | Corps                                             | Effet                                      |
|--------------------------------------------|---------------------------------------------------|--------------------------------------------|
| `POST /api/sources/{sourceId}/acks`        | `{"signature":"…","reason":"…","expires_at":"…"}` | Acquitte le finding sur ce daemon          |
| `POST /api/sources/{sourceId}/acks/revoke` | `{"signature":"…"}`                               | Révoque l'acquittement que tient ce daemon |

`expires_at` est optionnel, et un acquittement qui n'en porte pas est permanent. Un `by`
dans le corps est ignoré : le Hub nomme lui-même l'appelant.

Le Hub juge une requête dans cet ordre, et une requête qu'il refuse n'atteint jamais le
daemon. Avec le contrôle d'accès, des étapes propres s'y ajoutent, voir
[Avec le contrôle d'accès](#avec-le-contrôle-daccès) :

1. **L'appelant.** L'utilisateur connecté sous `Hub:Auth`. Sinon la valeur de
   `Hub:Analysis:IdentityHeader`, et seulement quand `Hub:AckRelay:TrustIdentityHeader` est
   posé, voir [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md#comment-ça-marche). Sans personne
   à nommer, un `403`. Il vient en premier pour qu'un appelant sans nom n'apprenne rien des
   sources.
2. **La source.** Un id inconnu, un backend de traces et un daemon sans identifiant
   d'acquittement répondent tous le même `404`. `ack_relay` sur `/api/sources` dit quelles
   sources relaient.
3. **D'où vient la requête.** Un en-tête `Sec-Fetch-Site` présent et différent de
   `same-origin` est un `403`, et un type de contenu que le cadriciel ne lit pas comme du
   JSON (`application/json` et les types suffixés `+json`) est un
   `415`. Le Hub n'a ni jeton antiforgery ni politique CORS, ces deux contrôles sont donc sa
   défense contre une page d'une autre origine : un navigateur pose le premier de lui-même,
   et le second ne traverse pas les origines sans un preflight auquel rien ici ne répond.
4. **La place.** Deux relais tournent à la fois, et un de plus reçoit `503` avec
   `Retry-After: 1`. Un corps de plus de 8 Kio est un `413`.
5. **Le corps.** `signature` est obligatoire, de 1 024 caractères au plus, sans caractère
   de contrôle, et ni `.` ni `..`, qui sortiraient du chemin d'acquittement du daemon. Sa
   forme reste la règle du daemon. `reason` est obligatoire sur un acquittement, non blanc,
   et tenu aux mêmes bornes. `expires_at` est un instant RFC 3339 dans le futur. Tout le
   reste est un `400`.
6. **La paire.** Le Hub doit détenir ce finding sur cette source. Une révocation passe
   elle aussi à ce titre, et également sur un acquittement de ce finding que le Hub reflète
   depuis cette source, qui peut survivre au finding. Sinon un `404`. Un daemon acquitte
   n'importe quelle signature canonique, c'est donc ce qui borne le relais aux findings que
   le Hub connaît.

Le Hub écrit alors lui-même la requête du daemon, et rien de ce que l'appelant a envoyé ne
voyage en octets. C'est un `POST`, ou un `DELETE` pour une révocation, sur
`api/findings/{signature}/ack`, la signature encodée en pourcent pour qu'une signature qui
contient une barre oblique reste un seul segment de chemin. Elle porte l'identifiant
d'acquittement de la source et jamais celui de lecture. Le corps d'un acquittement est
`{"by":"…","reason":"…","expires_at":"…"}`, `expires_at` réécrit en UTC à la seconde.
L'appelant voyage en `by` sur un acquittement et en `X-User-Id` sur une révocation, inchangé
quand il est en ASCII imprimable sans espace à ses extrémités, encodé en pourcent sinon, et
sous la même forme des deux côtés pour que les deux se comparent égaux sur le daemon.
L'échange entier dispose de trois fois `Hub:HttpTimeout`.

| Le daemon répond      | Le Hub répond                                                                                |
|-----------------------|----------------------------------------------------------------------------------------------|
| un `2xx`              | `204`                                                                                        |
| `400`                 | `400`, le daemon a refusé la signature comme non canonique                                   |
| `401`                 | `502`, le daemon a refusé l'identifiant d'acquittement du Hub                                |
| `404` à la révocation | `404`, pas acquitté sur ce daemon, ou seulement par sa baseline de CI                        |
| `409`                 | `409`, déjà acquitté sur le daemon ou par sa baseline de CI                                  |
| `503`                 | `503`, les acquittements sont désactivés sur ce daemon                                       |
| `507`                 | `507`, le store d'acquittements du daemon est plein                                          |
| rien à temps          | `504`                                                                                        |
| autre chose           | `502`, un `404` sur un acquittement compris, qui veut dire que le daemon n'a pas cette route |

Un `401` du daemon n'est jamais relayé tel quel. Il dit que la clé détenue par le Hub est
fausse, ce qu'aucune connexion ne répare, et le lanceur répond à un `401` en se rechargeant
vers la connexion. Chaque refus que le Hub formule lui-même porte `{"detail":"…"}`. Le
`413`, le `415` et le `503` d'une porte pleine n'ont pas de corps.

Après un `2xx` le Hub relit la liste d'acquittements de ce daemon, de sorte que `acks[]` et
`include_acked=false` suivent aussitôt au lieu d'attendre le poll suivant. Il le fait pour
une source que le poll a déjà jointe, puisque cette lecture se décide sur la version du
daemon, et un échec de cette lecture ne change jamais le `204` en erreur : le daemon a pris
l'écriture. Chaque relais journalise une ligne, l'événement `1310` quand le daemon a
répondu et `1311` sinon, avec la source, l'action, l'appelant et le statut. La raison et
l'identifiant ne sont jamais journalisés.

## API d'analyse

Une analyse est un run du binaire perf-sentinel contre une source configurée, produisant
le dashboard HTML autonome que rend le moteur.

| Endpoint                 | Fait                                                                                   |
|--------------------------|----------------------------------------------------------------------------------------|
| `POST /api/analyses`     | Prend `{"source_id": "...", "request": {...}}`, répond `202` avec l'identifiant du run |
| `GET /api/analyses`      | Liste les runs récents, le plus récent d'abord                                         |
| `GET /api/analyses/{id}` | Renvoie un run                                                                         |
| `GET /reports/{id}.html` | Sert le rapport d'un run réussi, depuis la même origine que le reste                   |

La forme de la requête suit le kind de la source : `{}` pour un daemon, qui ne prend aucun
paramètre, `{service, lookback | from_ms + to_ms, max_traces}` pour un backend de traces,
ou `{trace_id}`. Les exclusions propres au moteur sont appliquées avant toute mise en
file, donc une combinaison impossible est refusée plutôt que découverte trois minutes plus
tard sous la forme d'un run échoué.

### Deux invocations, pas une

Les sous-commandes de query émettent du texte, du JSON ou du SARIF, et seule `report`
écrit du HTML. La source est donc lue vers un JSON de rapport, puis ce JSON est rendu. Une
source daemon saute la première étape, son propre `/api/export/report` en renvoyant déjà
un.

Les deux invocations tournent depuis `Hub:Analysis:ReportDirectory`. Le moteur cherche un
`.perf-sentinel.toml` relatif à son propre répertoire de travail, donc le laisser non
réglé permettrait à un fichier égaré, posé à côté du répertoire depuis lequel le Hub a été
lancé, de décider des seuils de détection de tous les runs.

### Surcharges de détection

Une requête peut porter un objet `detection` qui surcharge les seuils du moteur :
`n_plus_one_min_occurrences`, `window_duration_ms`, `slow_query_threshold_ms`,
`slow_query_min_occurrences`, `max_fanout`, `chatty_service_min_calls`,
`pool_saturation_concurrent_threshold`, `serialized_min_sequential`,
`sanitizer_aware_classification` (une valeur parmi `auto`, `strict`, `always`, `never`)
et, à partir du moteur 0.18.0, `sanitizer_aware_min_cv` (un décimal de `0.01` à `10`).

Les bornes reflètent le validateur du moteur, et `GET /api/status` les publie avec chaque
défaut sous `detection_knobs`. Chaque entrée nomme son `kind` : `integer` et `decimal`
portent `min`, `max` et un `default` numérique, `choice` porte ses `choices` et un
`default` chaîne. Un réglage que la section `[detection]` du moteur sondé ne lit pas est
retiré de la liste et refusé à la soumission par un 400 qui nomme les deux versions,
plutôt qu'écrit dans la configuration du run et refusé par le moteur à l'exécution, si
bien que `sanitizer_aware_min_cv` n'apparaît qu'une fois le binaire embarqué en 0.18.0 ou
plus. Une valeur égale au défaut est écartée plutôt
qu'enregistrée, de sorte qu'un run ne porte que ce qui s'écarte de la configuration
standard. Les surcharges sont écrites dans un TOML propre au run, remis aux deux
invocations via `-c`, et supprimé à la fin du run.

Ces seuils décident de ce qui compte comme un problème, pas de la façon dont le rapport
est écrit. En relever un n'allège pas un run, cela empêche le détecteur de rapporter les
cas les plus petits. C'est pourquoi les comptes de runs aux seuils différents ne sont pas
comparables, et pourquoi le lanceur le dit. Une source daemon n'en prend aucun : elle
détecte avec sa propre configuration, et le Hub ne fait que lire ce qu'elle a trouvé.

### Taille du rapport

Ce n'est pas un réglage. La cible de 5 Mio du sink est une constante privée, sans flag,
sans variable d'environnement et sans clé de configuration. Un rapport bâti depuis une
requête de backend plafonne autour de 4 Mo, parce que la part de ce budget réservée aux
arbres de spans embarqués n'est jamais dépensée : une requête de backend renvoie des
findings, pas des spans. Quand le sink écarte effectivement des findings pour tenir, le
run enregistre combien ont survécu, relu depuis le fichier rendu, et le panneau de
résultat le dit au-dessus du lien.

### Échec et expiration

Chaque échec est l'un de huit codes : `source_unreachable`, `source_auth_failed`,
`source_rejected_request`, `timeout`, `output_too_large`, `binary_failed`,
`invalid_request`, `internal`.

La sortie d'erreur brute ne quitte jamais le process. Elle est lue pour nommer un
responsable, puisque "le backend nous a refusés" et "le binaire a cassé" n'ont pas le même
responsable, et cette classification est une heuristique sur un ensemble borné de
marqueurs plutôt qu'un contrat.

Les rapports sont supprimés `Hub:Analysis:ReportRetention` après leur succès, et le run
est marqué expiré en gardant ses paramètres. La ligne elle-même survit ensuite jusqu'à
`Hub:Analysis:RunRetention`, trente jours par défaut, après quoi elle est supprimée et
`GET /api/analyses/{id}` répond `404`. Ce n'est pas une piste d'audit, et un lien partagé
hier est déjà mort. Un run encore en attente ou en cours n'est jamais supprimé, quel que
soit l'âge apparent de sa ligne.

Un run encore en cours à l'arrêt du service revient `interrupted` et n'est jamais rejoué
de lui-même. Une reprise silencieuse lancerait une seconde requête lourde vers un backend
que personne n'a demandé d'interroger deux fois.

## Avec le contrôle d'accès

Avec `Hub:Access:Enabled`, chaque route sauf l'import, `/health` et `/metrics` (anonyme,
sur `Hub:MetricsPort` seulement) exige une session ou un jeton machine, et chaque réponse couvre ce que couvrent les grants de l'appelant. Le modèle
est dans [ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md) ; ce qui change sur le fil :

- **Identifiants.** Une session du navigateur, ou `Authorization: Bearer <jeton machine>`.
  Une requête qui porte un bearer est jugée sur lui seul. Un jeton inconnu ou expiré reçoit `401`, même
  à côté d'un cookie valide, avec
  `WWW-Authenticate: Bearer error="invalid_token"`. Un jeton machine ne fait que lire :
  toute autre méthode reçoit `403 {"detail":"A machine token only reads."}`. Une écriture
  du navigateur sans `Sec-Fetch-Site: same-origin` reçoit
  `403 {"detail":"A request from another site is refused."}`.
- **`/api/status` et `/api/sources`** répondent `403 {"detail":"No grant on this Hub
  reaches you."}` à un appelant qui ne détient aucune permission nulle part.
  `/api/sources` ne liste que les sources où l'appelant détient quelque chose, chacune avec
  `permissions` : `findings.read`, `sources.read`, `analyses.run` et `acks.write` pour un
  démon, `traces.read` et `analyses.run` pour un backend de traces. Le champ est absent sans
  contrôle d'accès.
- **Les deux routes de findings** sont filtrées par `findings.read` : un finding est listé
  quand l'une de ses copies est dans le périmètre, et ses `sources[]`, `acks[]`, son
  statut, ses première et dernière observations, et les filtres `include_acked`, `grouping`
  et de fenêtre sont jugés sur les seules copies dans le périmètre. Les pages sont des
  tranches de cette liste unique. Un `source_id` ou un `environment` hors du `/api/sources`
  de l'appelant reçoit le même `400` qu'un inconnu. Un appelant qui ne détient
  `findings.read` nulle part reçoit `403` plutôt que `[]` : une porte de CI dont le jeton
  n'a aucun grant échoue.
- **Les incidents** suivent `findings.read` de la même façon ; `/api/incidents/{id}` sert
  la copie la plus riche dans le périmètre, et `404` s'il n'y en a aucune. Un
  rafraîchissement ne lit que les démons où l'appelant lit quelque chose.
- **La vue d'un démon** exige `sources.read` sur cette source entière, et répond sinon
  `404` comme une source inconnue, avant le `400` d'un backend de traces.
- **Les acks**, dans l'ordre plus haut : la source doit être dans le `/api/sources` de
  l'appelant, sinon le même `404` ; puis, avant de prendre une place du relais ou de lire le
  corps, l'appelant doit détenir `acks.write` quelque part sur cette source, sinon
  `403 {"detail":"You may not acknowledge findings at this source."}` ; puis, sur le
  finding détenu dans cette source, une copie que l'appelant ne peut pas lire reçoit
  exactement le `404` d'un inconnu, et une copie qu'il lit sans pouvoir l'écrire `403`. La
  révocation d'un ack reflété dont le Hub ne détient plus le finding exige la source
  entière. `expires_at` reste au choix de l'appelant : aucune expiration n'est imposée.
- **Les analyses.** Une seule règle décide qui voit la ligne d'un run et ouvre son
  rapport : un run de démon exige `findings.read` et `sources.read` sur le démon entier ;
  un run sur un backend de traces exige `traces.read` sur le service demandé, ou sur le
  backend entier pour un trace ID. Lancer un run exige cela et `analyses.run`, sinon
  `403 {"detail":"You may not start this analysis."}` ; une source hors de la liste de
  l'appelant est le `400` d'un `source_id` inconnu. `GET /api/analyses` ne liste que les
  runs visibles, jamais `403` ; un run caché et son rapport répondent `404`. Le JSON d'un
  run est inchangé. **Un rapport de traces montre chaque service que ses traces
  traversent**, comptes de `result` compris. Les rapports sont rendus statiques : pas de
  `--daemon-url`.

Depuis la CI :

```sh
curl -fsS -H "Authorization: Bearer $HUB_TOKEN" \
  "https://hub.example.com/api/findings?environment=production&status=active&severity=critical"
```

### `GET /api/access`

Montée seulement avec `Hub:Access:Enabled` : sans contrôle d'accès elle répond `404`, et le
lanceur masque son onglet Accès sur cette réponse. Tout appelant la lit, y compris celui
qu'aucun grant n'atteint, car c'est ainsi qu'il trouve l'identifiant qu'un administrateur
lui demande. Elle n'écrit jamais et ne liste jamais la valeur ni l'empreinte d'un jeton
machine.

```json
{
  "identity": "ada@example.com",
  "subject": "2f6d0c1e-8a4b-4e2f-9c71-0d3b5a6e7f81",
  "caller": "session",
  "expires_at_ms": 1760040000000,
  "admin": false,
  "groups": [{"name": "/teams/shop", "origin": "provider"}, {"name": "oncall", "origin": "internal"}],
  "reach": [{"group": "/teams/shop", "project": "shop", "role": "member",
             "permissions": ["findings.read", "traces.read", "analyses.run", "acks.write"]}],
  "projects": [{"name": "shop", "patterns": [{"source": null, "service": null, "attribute": "team=shop"}]}],
  "configuration": null,
  "unmatched": null,
  "conflicts": null
}
```

- `identity` est le nom affiché que renvoie `/api/status` ; `subject` est la clé
  d'autorisation que listent `Admins` et les `Members` d'un groupe interne. `caller` vaut
  `session` ou `machine_token`. `expires_at_ms` est la fin de la session, ou l'expiration
  configurée du jeton.
- `groups` sont les groupes effectifs de l'appelant : les groupes du fournisseur que la
  configuration nomme, gardés à la connexion, et les groupes internes qui listent le sujet,
  nommés sans `internal:`.
- `reach` porte les grants de l'appelant avec ce que chacun lui donne : un jeton machine
  garde `findings.read`, et `access.admin` n'apparaît que dans la session d'un admin. Une
  entrée `Admins` ou `AdminGroups` se lit comme le rôle `admin` sur `*`, avec `group` nul
  pour `Admins`. `projects` sont les projets que `reach` nomme.
- **Pour un admin seulement**, les trois derniers champs sont remplis :
  - `configuration` : `projects`, `roles` (`built_in` marque `reader`, `member` et
    `admin`), `grants`, `internal_groups` avec leurs `members`, `admins`, `admin_groups`,
    et `machine_tokens` sous la forme `{name, group, expires_at_ms, active}`, `active`
    signifiant non expiré.
  - `unmatched` : les copies de findings vues dans `Hub:Retention` qu'aucun motif d'aucun
    projet ne lit, en `services` (`{service, source_ids, findings}`), `attributes`
    (`{key, value, findings}`) et `sources`, chaque source configurée qu'aucun motif ne
    nomme dans sa partie source (`{source_id, findings}`, `findings` nul pour un backend de
    traces). `findings` compte des copies, une par signature et source. Chaque liste est
    triée par compte, puis par nom, et s'arrête à 200 entrées ; `truncated` vaut vrai
    quand l'une a été coupée.
  - `conflicts` : `{copies, truncated}`, les copies sur lesquelles une clé qu'utilise un
    motif de projet porte deux valeurs, ou manque à l'un des groupements sous lesquels la
    copie a été vue, sous la forme `{signature, source_id, service, key, values,
    identities_without_key}`, triées par source puis signature, 200 au plus. Ce sont les
    copies où un grant d'attribut lit sans pouvoir acquitter, voir
    [ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md#lire-les-conflits).
