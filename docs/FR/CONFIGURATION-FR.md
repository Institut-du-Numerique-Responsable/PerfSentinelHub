# Configuration

Les variables d'environnement suivent la forme .NET `Hub__...`. Helm expose les mêmes
réglages sous `hub` et `sources`.

[`examples/appsettings.reference.json`](../../examples/appsettings.reference.json) fixe
chaque réglage à la valeur que le Hub utilise déjà, annotée. La copier entièrement ne
change rien, c'est l'inventaire plutôt qu'un point de départ. Un test la maintient
exhaustive, ce qui compte ici parce que .NET ignore en silence une clé de configuration
qu'il ne reconnaît pas : un nom mal orthographié ne produit aucune erreur et se lit comme
un bug du Hub plutôt que comme une faute de frappe dans votre fichier.

## Réglages

| Réglage                            | Défaut                                                                         | Validation                                                                                              |
|------------------------------------|--------------------------------------------------------------------------------|---------------------------------------------------------------------------------------------------------|
| `Hub:DatabasePath`                 | `/data/hub.db`                                                                 | Chemin absolu                                                                                           |
| `Hub:PollInterval`                 | `01:00:00`                                                                     | Durée positive                                                                                          |
| `Hub:HttpTimeout`                  | `00:00:10`                                                                     | Durée positive                                                                                          |
| `Hub:MaxConcurrentPolls`           | `4`                                                                            | 1 à 32                                                                                                  |
| `Hub:Retention`                    | `180.00:00:00` (180 jours)                                                     | Durée positive                                                                                          |
| `Hub:ResolutionGrace`              | `7.00:00:00` (7 jours)                                                         | Positive, inférieure à `Retention`                                                                      |
| `Hub:NewSignaturesPerHourWarning`  | `1000`                                                                         | 0 ou plus, 0 le désactive. Un daemon au-delà est signalé sur `/api/sources` et journalisé               |
| `Hub:DefaultReadLimit`             | `1000`                                                                         | 1 à `MaxReadLimit`                                                                                      |
| `Hub:MaxReadLimit`                 | `10000`                                                                        | 1 à 10000                                                                                               |
| `Hub:MetricsPort`                  | aucun                                                                          | 1 à 65535. Le seul port où répond `/metrics`, voir [OPERATIONS-FR.md](OPERATIONS-FR.md#métriques)        |
| `Hub:Analysis:EngineBinaryPath`    | aucun                                                                          | Optionnel, chemin absolu vers le binaire perf-sentinel. Absent, les runs d'analyse sont indisponibles   |
| `Hub:Analysis:ReportDirectory`     | `/data/reports`                                                                | Absolu, accessible en écriture. Les rapports rendus vivent ici                                          |
| `Hub:Analysis:IdentityHeader`      | `X-Forwarded-User`                                                             | En-tête qu'un reverse proxy renseigne avec l'identité du demandeur, ignoré sous `Hub:Auth`              |
| `Hub:Analysis:Workers`             | `2`                                                                            | 1 à 16                                                                                                  |
| `Hub:Analysis:MaxTracesCap`        | `2000`                                                                         | 1 à 10000, la limite propre du moteur sur `--max-traces`                                                |
| `Hub:Analysis:MaxTracesEmbedded`   | `50`                                                                           | 0 à 10000. Arbres de spans embarqués dans le rapport. Le poser fait sortir le sink du ciblage de taille |
| `Hub:Analysis:Timeout`             | `00:05:00`                                                                     | Positive, une heure au plus                                                                             |
| `Hub:Analysis:MinFreeMemoryMiB`    | `256`                                                                          | 0 à 65536, 0 le désactive. Mémoire libre qu'il faut au conteneur pour qu'un run lance le moteur         |
| `Hub:Analysis:ReportRetention`     | `1.00:00:00` (24 heures)                                                       | Durée positive                                                                                          |
| `Hub:Analysis:RunRetention`        | `30.00:00:00` (30 jours)                                                       | Positive, plus longue que `ReportRetention`. Quand la ligne d'un run terminé est supprimée              |
| `Hub:UpdateCheck:Enabled`          | `true`                                                                         | Si le Hub demande à GitHub la release publiée la plus récente                                           |
| `Hub:UpdateCheck:Interval`         | `1.00:00:00` (1 jour)                                                          | Au moins 15 minutes                                                                                     |
| `Hub:UpdateCheck:EngineEndpoint`   | API des releases GitHub de `Institut-du-Numerique-Responsable/perf-sentinel`   | HTTPS absolue, sans identifiants, ni query, ni fragment                                                 |
| `Hub:UpdateCheck:HubEndpoint`      | API des releases GitHub de `Institut-du-Numerique-Responsable/PerfSentinelHub` | HTTPS absolue, sans identifiants, ni query, ni fragment                                                 |
| `Hub:Auth:*`                       | désactivé                                                                      | Connexion OAuth2 du navigateur, limitable par groupe, voir [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md) |
| `Hub:AckRelay:TrustIdentityHeader` | `false`                                                                        | Laisse le relais d'acquittement lire son appelant dans `Analysis:IdentityHeader`, voir plus bas         |
| `Hub:Access:*`                     | désactivé                                                                      | Contrôle d'accès, voir plus bas et [ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md)                          |
| `Hub:Sources`                      | aucune                                                                         | Au moins une source                                                                                     |

## Réglages par source

| Réglage                          | Défaut   | Validation                                                                                                                         |
|----------------------------------|----------|------------------------------------------------------------------------------------------------------------------------------------|
| `Sources[].Id`                   | aucun    | Unique, 1 à 64 caractères ASCII alphanumériques, `.`, `_` ou `-`                                                                   |
| `Sources[].Name`                 | aucun    | Non vide                                                                                                                           |
| `Sources[].Environment`          | aucun    | Non vide                                                                                                                           |
| `Sources[].Kind`                 | `daemon` | L'un de `daemon`, `tempo`, `jaeger_query`. Seul un daemon est pollé, et seul un daemon peut porter une clé d'import                |
| `Sources[].RetentionHours`       | aucun    | Backends de traces seulement, d'une heure à dix ans                                                                                |
| `Sources[].BaseUrl`              | aucune   | Obligatoire. HTTP(S) absolue, sans identifiants, query ni fragment                                                                 |
| `Sources[].PublicUrl`            | aucune   | Optionnelle, même forme que `BaseUrl`. Cible des commandes affichées et des rapports live                                          |
| `Sources[].AuthHeaderName/Value` | aucun    | Les deux absents ou les deux présents, sans saut de ligne. Le `[daemon] read_api_key` d'un daemon va ici en `X-API-Key`            |
| `Sources[].PublicAuthHeaderName` | aucun    | Exige `PublicUrl`, sans espace ni caractère de contrôle. L'en-tête nommé par les commandes affichées à la place d'`AuthHeaderName` |
| `Sources[].AckHeaderName/Value`  | aucun    | Daemons seulement, les deux ou aucun. Le `[daemon.ack] api_key` du daemon, envoyé sur les seuls acquittements relayés              |
| `Sources[].ImportApiKey`         | aucune   | Identifiant de push optionnel, au moins 32 caractères, fourni via un Secret                                                        |

`Hub:DatabasePath` et `Hub:Analysis:ReportDirectory` valent par défaut `/data/hub.db` et
`/data/reports`, qui sont les chemins du conteneur. Tous deux sont validés par
`Path.IsPathFullyQualified`, qui refuse une barre oblique initiale sans lettre de lecteur sur
Windows, donc un hôte Windows ou macOS doit poser les deux sous peine de voir le Hub refuser
de démarrer en nommant la clé fautive.

`RetentionHours` est déclaré et non mesuré, aucune API de backend ne l'exposant. Il porte
la même réserve que `Environment` : il garde une affirmation périmée jusqu'à ce que
quelqu'un l'édite.

`BaseUrl` conserve un préfixe de chemin, donc `https://gw/perf-sentinel/` polle
`https://gw/perf-sentinel/api/status`.

`BaseUrl` est l'adresse où le Hub joint une source. Un Hub dans le cluster lit un nom de
Service que rien hors du cluster ne résout : une commande affichée depuis ce Hub ne
tournerait pas sur un poste, et un rapport live y enverrait le navigateur du lecteur.
`PublicUrl` est l'adresse vue de l'extérieur : un hôte d'Ingress, ou
`http://localhost:14318` pour des lecteurs qui font eux-mêmes le port-forward. Les
commandes affichées et les rapports live l'utilisent. Les polls, lectures et runs du Hub
gardent `BaseUrl` et restent sur le réseau du cluster. Sans `PublicUrl`, les deux
utilisent `BaseUrl`.

Les commandes affichées nomment l'en-tête d'`AuthHeaderName`, ce qui convient à un
port-forward : il joint le même daemon. Un Ingress qui authentifie à sa façon prend
`PublicAuthHeaderName` à la place. C'est un nom sans valeur, puisque le Hub n'appelle
jamais la route publique et que le lecteur fournit son propre identifiant.

Un Hub servi en HTTPS a besoin d'un `PublicUrl` en HTTPS pour que ses rapports soient
live : les navigateurs bloquent les appels HTTP depuis une page HTTPS, sauf vers
`localhost` pour la plupart d'entre eux.

## L'identifiant d'acquittement

`AckHeaderName` et `AckHeaderValue` forment un second identifiant, distinct de la paire de
lecture. Le Hub ne l'envoie que sur un seul type de requête, l'acquittement ou la révocation
qu'il relaie vers ce daemon, et garde la paire de lecture pour tout le reste. C'est le
`[daemon.ack] api_key` du daemon, envoyé en `X-API-Key`, ou en `Authorization` avec une
valeur `Bearer`. Sans lui le Hub n'écrit jamais sur ce daemon, et `/api/sources` rapporte
`ack_relay: false` pour la source. Ne renseigner ni l'un ni l'autre est la façon de tenir
une source à l'écart de la page d'acquittement, où sa ligne ne propose alors rien, un
daemon de CI dont les acquittements ne changent que par pull request par exemple, voir
[LAUNCHER-FR.md](LAUNCHER-FR.md#pull-requests-uniquement-dans-un-environnement). Comme
`AuthHeaderValue`, la valeur va dans un Secret : sous Helm ce sont `ackSecretName` et
`ackSecretKey`, et seul le nom de l'en-tête atteint la ConfigMap.

Le Hub refuse de démarrer quand la paire est posée sur un backend de traces, qui n'a pas de
route d'acquittement, ou quand elle porte la même clé que l'`AuthHeaderValue` de la source,
un schéma `Bearer` étant ignoré de part et d'autre. Le daemon refuse un `read_api_key` égal
à sa clé d'acquittement pour la même raison : une clé de lecture qui peut écrire est une
clé d'écriture. Un Hub qui lit un daemon avec sa clé d'acquittement a donc besoin d'un
`[daemon] read_api_key` sur ce daemon avant de pouvoir relayer.

Le relais doit savoir qui acquitte. Une session `Hub:Auth` le dit toujours. L'en-tête posé
par un reverse proxy, `Hub:Analysis:IdentityHeader`, est une affirmation que le Hub ne peut
pas vérifier, il ne nomme donc l'appelant qu'une fois `Hub:AckRelay:TrustIdentityHeader` à
`true`, `hub.ackRelay.trustIdentityHeader` sous Helm. Ne le posez que derrière un proxy qui
écrit lui-même l'en-tête et retire celui qu'un client aurait envoyé. Sans l'un ni l'autre,
le relais ne peut identifier personne et refuse tous les appelants, et le Hub journalise un
avertissement au démarrage, qui nomme les sources dont l'identifiant ne sera jamais envoyé.

## Contrôle d'accès

`Hub:Access` dit qui atteint quels findings, quelles sources et quels runs : projets, rôles,
attributions, groupes internes, administrateurs et jetons machine. Tout cela est de la
configuration, la base ne stocke ni utilisateur ni appartenance, et sous Helm le bloc entier
est `hub.access`, transmis tel quel au Hub. Comment il s'applique à chaque route, les jetons
machine et les sessions sont dans [ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md). Désactivé,
le défaut, rien sous `Hub:Access` n'est validé ni utilisé et le Hub répond exactement comme
avant, mais les valeurs sont tout de même liées, donc `ExpiresAt` doit déjà être une date
ISO 8601 comme `2027-06-30T00:00:00Z`, sans quoi le Hub ne démarre pas. Chaque liste est
vide par défaut. Avec `Enabled` activé, chaque règle ci-dessous est contrôlée au démarrage,
chaque message nomme sa clé et son indice et ne reprend jamais un jeton ; le schéma du chart
contrôle les règles entre clés au `helm upgrade`, avant que `Recreate` n'arrête le pod en
service.

| Réglage                      | Validation                                                                                                                                                                      |
|------------------------------|---------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `Hub:Access:Enabled`         | `false` par défaut. Exige `Hub:Auth:Enabled`, `Hub:MetricsPort`, une entrée dans `Admins` ou `AdminGroups`, et `Hub:AckRelay:TrustIdentityHeader` désactivé                   |
| `Hub:Auth:SubjectClaim`      | `sub` par défaut, lu seulement avec `Enabled` : `sub`, `oid`, `uuid` ou `account_id`, des identifiants qu'un fournisseur ne réattribue jamais                                   |
| `Hub:Auth:TrustedProxyNetworks` | Vide par défaut. Réseaux CIDR dont `X-Forwarded-For` et `X-Forwarded-Proto` sont crus, sur un saut ; chaque entrée doit s'analyser. La connexion du terminal (`perf-sentinel login`) n'est offerte que s'il est renseigné, voir [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md#connexion-du-terminal), et `LocalAccounts` l'exige |
| `Hub:Access:Admins`          | Sujets qui tiennent le rôle intégré `admin` sur `*` : 1 à 256 caractères, sans caractère de contrôle, sans préfixe `machine:`, et `local:<nom>` seulement pour un compte local déclaré. Pas vide avec `AdminGroups` |
| `Hub:Access:AdminGroups`     | Références de groupe qui tiennent `admin` sur `*` : `internal:<nom>` ou un groupe du fournisseur, voir plus bas                                                                 |
| `Hub:Access:Groups[]`        | `{ Name, Members[] }`, référencé comme `internal:<Name>`. Nom de 1 à 64 lettres ASCII, chiffres, `.`, `_` ou `-`, unique sans tenir compte de la casse ; membres comme `Admins` |
| `Hub:Access:Projects[]`      | `{ Name, Patterns[] }`, nommé comme un groupe, avec au moins un motif `{ Source, Service, Attribute }` qui pose une partie ou plus                                              |
| `Hub:Access:Roles[]`         | `{ Name, Permissions[] }`, nommé comme un groupe mais ni `reader`, ni `member`, ni `admin`, casse ignorée ; permissions ci-dessous, chacune une fois, jamais `access.admin`     |
| `Hub:Access:Grants[]`        | `{ Group, Project, Role }`, les trois posés. `Project` est un projet déclaré ou `*`, `Role` intégré ou déclaré, et `admin` ne s'attribue que sur `*`                            |
| `Hub:Access:MachineTokens[]` | `{ Name, Group, Token, ExpiresAt }`, voir plus bas                                                                                                                              |
| `Hub:Access:LocalAccounts[]` | `{ Name, PasswordHash }`, voir plus bas                                                                                                                                         |

**Permissions et rôles.** Les permissions sont `findings.read`, `traces.read`,
`sources.read`, `analyses.run` et `acks.write`, plus `access.admin`, que seul le rôle
intégré `admin` porte. Les rôles intégrés sont `reader` (`findings.read`, `traces.read`,
`analyses.run`), `member` (`reader` et `acks.write`) et `admin` (les six, sur `*`
seulement). `reader` n'est pas en lecture seule : il lance des analyses. Un rôle en lecture
seule est un rôle configuré, comme `auditor` avec `findings.read` et `traces.read`.

**Motifs.** Un enregistrement est dans un projet quand l'un de ses motifs correspond. Une
partie est exacte, ou un préfixe : au moins un caractère, puis un seul `*` à la fin. Un `*`
seul est refusé dans chaque partie : `*` n'existe que comme projet d'une attribution, qui
couvre aussi les enregistrements qu'aucun projet ne prend. `Source` prend les caractères
d'un identifiant de source et doit nommer, ou préfixer, une source configurée. `Service`
fait 1 à 256 caractères sans caractère de contrôle. `Attribute` s'écrit `clé=valeur`, coupé
au premier `=`, donc `team=a=b` a la clé `team` : la clé est exacte, de 1 à 256 caractères
sans `*`, et la valeur une partie de 1 à 256. Une clé d'attribut ne correspond que sur les
daemons qui la captent dans `[detection] grouping_attributes`, voir
[DEPLOYMENT-FR.md](DEPLOYMENT-FR.md#les-attributs-de-regroupement-se-choisissent-sur-chaque-daemon).
Chaque comparaison est ordinale : la casse compte, et `%` ou `_` ne veulent rien dire.

**Références de groupe.** `internal:<nom>`, préfixe en minuscules, nomme un groupe interne
déclaré. Tout le reste est un groupe du fournisseur, écrit comme le champ userinfo
`Hub:Auth:GroupsClaim` l'envoie, qui doit alors nommer un champ. Une référence au
fournisseur est refusée quand elle vaut `*`, est vide, dépasse 256 caractères, contient un
caractère de contrôle, ou commence par `internal:` dans une autre casse.

**Les groupes du fournisseur dans `AdminGroups` et dans les attributions** doivent être des
noms que seule l'organisation peut créer : le chemin complet du groupe sur Keycloak
(`/teams/shop-admin`, « Full group path » activé dans le mapper), le chemin complet sous le
groupe propre à l'organisation sur GitLab (`acme/hub-admins`), jamais un nom de premier
niveau seul. `Hub:Auth:GroupsClaim` doit nommer un champ que le fournisseur gère et que
l'utilisateur ne peut pas modifier. Là où les utilisateurs créent des groupes de n'importe
quel nom, gitlab.com et GitLab auto-hébergé par défaut, quiconque le fournisseur connecte
peut créer un `platform-admins` de premier niveau et le retrouver dans sa revendication de
groupes : un groupe administrateur du fournisseur n'y est sûr que si
`Hub:Auth:RequiredGroups` limite aussi la connexion aux membres de l'organisation, voir
[AUTHENTICATION-FR.md](AUTHENTICATION-FR.md). `Admins`, par sujet, reste la voie de secours
quand un groupe administrateur du fournisseur est mal configuré.

**Les jetons machine** sont des identifiants pour les jobs de CI et les tableaux de bord,
et tiennent au plus `findings.read`. `Name` suit `^[a-z0-9][a-z0-9._-]{0,63}$` et est
unique. `Group` est un groupe `internal:` sans membre, puisque ses attributions sont taillées
pour une machine et qu'une personne ajoutée plus tard les gagnerait en silence. `Token` fait
32 à 512 caractères de l'alphabet `b64token` de la RFC 6750, `A-Z`, `a-z`, `0-9`, `-`, `.`,
`_`, `~`, `+` et `/` avec des `=` finaux permis, ne commence pas par `pshub_`, diffère de
tout autre jeton machine et de toute clé d'import, et est rogné comme une clé d'import.
`ExpiresAt` est posé, à 365 jours au plus ; un jeton déjà expiré n'empêche pas le Hub de
démarrer. Sous Helm, un élément est `{ name, group, expiresAt, secretName, secretKey }` :
le jeton n'est jamais dans les values, et avec `enabled` le chart le lit dans ce Secret pour
`Hub__Access__MachineTokens__<index>__Token`. Le chart refuse un nom dupliqué ou mal formé,
une référence de Secret manquante et un `expiresAt` qui n'est pas une date et heure
ISO 8601. Aucun message ne reprend jamais un jeton. Une entrée expirée est journalisée au
démarrage (événement `2001`) et refusée à chaque requête.

**Les comptes locaux** sont des comptes de secours pour les pannes du fournisseur,
connectés sur la page du Hub en tant que `local:<nom>`, voir
[AUTHENTICATION-FR.md](AUTHENTICATION-FR.md#comptes-locaux). `Name` suit
`^[a-z0-9][a-z0-9._-]{0,63}$` et est unique. `PasswordHash` est la ligne qu'affiche
`PerfSentinelHub hash-password`, `$pbkdf2-sha256$i=600000$<sel>$<clé>`, rognée comme un
jeton ; toute autre forme ou tout autre nombre d'itérations est refusé. `Admins` ou les
`Members` d'un groupe doivent lister `local:<nom>`, et `Hub:Auth:TrustedProxyNetworks` ne
doit pas être vide. Sous Helm, un élément est `{ name, secretName, secretKey }` : avec
`enabled` le chart lit le hash dans ce Secret pour
`Hub__Access__LocalAccounts__<index>__PasswordHash`, et refuse un nom dupliqué ou mal
formé et une référence de Secret manquante ; le schéma refuse des comptes locaux sans
`auth.trustedProxyNetworks`. Aucun message ne reprend jamais un hash.

## Ce qu'est une source, et ce qui est mesuré

La liste est de la configuration, jamais une découverte. Rien n'est détecté
automatiquement, le lanceur ne peut pas ajouter de source, et le Hub refuse de démarrer
sans aucune.

Cela coupe chaque ligne de l'écran de flotte en deux. `Id`, `Name`, `Environment`, `Kind`,
`BaseUrl`, `PublicUrl` et `RetentionHours` sont déclarés : repris de ce fichier tels quels et jamais
confrontés à quoi que ce soit. `reachable`, `last_success`, `unreachable_since`,
`producer_version` et `last_error` sont observés, écrits par le poll. Un contour en
pointillé marque la moitié déclarée dans le lanceur, et c'est pourquoi un déploiement mal
configuré peut étiqueter de la production en staging sans que rien ne le contredise.

Seul un daemon est interrogé. Son `api/status` fournit `producer_version`, et son
`api/findings` est le filet derrière le push.

Où cela se déclare : `Hub:Sources` dans `appsettings.json`, les variables d'environnement
`Hub__Sources__N__*`, ou `sources[]` dans les valeurs Helm. Les trois sont le même
réglage, et `Kind` décide dans quelle moitié de l'écran une ligne atterrit.

```yaml
sources:
  - id: checkout-prod
    name: Checkout production
    environment: production
    kind: daemon
    baseUrl: http://perf-sentinel.observability:4318
    importSecretName: hub-import-keys    # le secret de push, jamais en clair
    importSecretKey: checkout-prod
    ackHeaderName: X-API-Key             # optionnel, laisse le Hub relayer les acquittements vers ce daemon
    ackSecretName: hub-ack-keys          # le [daemon.ack] api_key du daemon, jamais en clair
    ackSecretKey: checkout-prod
  - id: victoria-eu
    name: Victoria Traces EU
    environment: staging
    kind: jaeger_query                   # Victoria Traces parle l'API de requêtage Jaeger
    baseUrl: http://victoria-traces.observability:10428
    retentionHours: 72
```

La même paire en variables d'environnement, un indice par source :

```bash
Hub__Sources__0__Id=checkout-prod
Hub__Sources__0__Kind=daemon
Hub__Sources__0__BaseUrl=http://perf-sentinel.observability:4318
Hub__Sources__0__AckHeaderName=X-API-Key
Hub__Sources__0__AckHeaderValue="$ACK_API_KEY"   # depuis votre coffre à secrets, jamais en clair
Hub__Sources__1__Id=victoria-eu
Hub__Sources__1__Kind=jaeger_query
Hub__Sources__1__BaseUrl=http://victoria-traces.observability:10428
Hub__Sources__1__RetentionHours=72
```

Un backend de traces n'est jamais contacté tant que personne ne lance d'analyse : aucune
route du Hub ne lit un Tempo. Le Hub ne fait que lancer le moteur contre lui, avec la
sous-commande qu'implique son type, `tempo` pour `tempo` et `jaeger-query` sinon. C'est
pourquoi une telle source n'affiche ni version de producteur ni dernier succès, et ce
n'est pas un défaut.

## Où le Hub se connecte

Chaque requête sortante va vers un `Sources[].BaseUrl` configuré, avec une exception. Une
fois par jour, le Hub demande à l'API des releases GitHub la version publiée la plus
récente du moteur et de lui-même, pour que la pastille de version puisse dire que ce que
vous faites tourner n'est plus le plus récent. C'est un GET non authentifié, qui ne porte
aucun identifiant de votre déploiement.

Pour un déploiement sans sortie réseau, réglez `Hub:UpdateCheck:Enabled` à `false`. La
pastille n'affiche alors rien plutôt que d'affirmer que vous êtes à jour, ce qu'elle
affiche aussi quand la requête échoue.

## Une source https avec une CA privée

Le Hub valide le certificat d'une source contre le magasin de confiance du conteneur. Un
daemon en cluster portant un certificat auto-signé ou émis en interne est refusé avec
`PartialChain` tant que sa CA n'y est pas.

L'image d'exécution est chiselée et n'a pas de shell, donc `update-ca-certificates` n'y
est pas exécutable. Pointez plutôt `SSL_CERT_FILE` vers un bundle monté depuis une
ConfigMap :

```yaml
env:
  - name: SSL_CERT_FILE
    value: /etc/perf-sentinel-hub/certs/bundle.crt
```

Le bundle doit être les racines publiques et votre CA concaténées, dans cet ordre, et non
la CA seule. La variable remplace le fichier par défaut au lieu de s'y ajouter, et un Hub
qui ne fait confiance qu'à votre CA ne peut rien joindre sur l'internet public.

Vérifié contre l'exécution sur laquelle cette image est bâtie : sans la variable le
certificat privé est refusé et le TLS public fonctionne, avec le bundle concaténé les deux
fonctionnent.

Le moteur d'analyse que démarre le lanceur hérite de cette variable. Depuis perf-sentinel
0.25.5, il fait confiance aux certificats de `SSL_CERT_FILE` en plus de ses propres racines
embarquées, donc le même bundle permet au lanceur d'analyser une source Tempo ou Jaeger
servie avec un certificat privé. Le moteur lit aussi `HTTPS_PROXY`, `ALL_PROXY` et
`NO_PROXY` pour ses appels `https://` : un Hub qui porte `HTTPS_PROXY` les fait passer par
ce proxy, donc une source `https://` du cluster doit figurer dans `NO_PROXY`.
