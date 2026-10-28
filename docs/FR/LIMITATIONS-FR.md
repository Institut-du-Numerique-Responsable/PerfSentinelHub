# Limites

Tout ce qui suit est déjà écrit ailleurs dans ces documents, à côté de la
fonction que cela contraint. Cette page les rassemble pour qu'on puisse lire les
bornes d'un seul tenant avant de déployer, plutôt que de les découvrir une par
une.

## Ce que le Hub déclare au lieu de le mesurer

`Environment` et `RetentionHours` sont repris de la configuration tels quels et
ne sont confrontés à rien. Un déploiement mal configuré peut étiqueter de la
production en staging sans que rien ne le contredise. Le lanceur marque la
moitié déclarée d'une ligne par un contour en pointillé. Voir
[CONFIGURATION-FR.md](CONFIGURATION-FR.md).

Un backend de traces n'est jamais interrogé. Seul un daemon l'est, donc une
source Tempo ou d'API de requêtage Jaeger n'affiche ni version de producteur ni
dernier succès. C'est la conception, pas un défaut.

## Ce qu'un poll peut dire, et ce qu'il ne peut pas

Le daemon perf-sentinel 0.11.x plafonne `/api/findings` à 1 000 lignes. Le Hub
utilise exactement ce plafond et avertit quand il est atteint, parce que
l'instantané peut être incomplet, sauf si le daemon pousse vers le Hub et
qu'une poussée de findings est arrivée dans les deux dernières heures (voir DEPLOYMENT-FR.md). Une couverture à fort volume passe par
l'exportateur push borné, pas par le poll.

Un poll qui omet un finding ne le résout pas. Le tampon circulaire du daemon
peut simplement l'avoir évincé, et absent n'est pas la même chose que disparu.
Seule la rétention retire une ligne.

Le Hub relève les jours où chaque source a observé un finding, et ce relevé
commence le jour où la version qui l'écrit est déployée. Rien n'est rempli
après coup : pour un jour antérieur, le Hub ne détient que le `first_seen` et le
`last_seen` d'un finding, pas les jours entre les deux. La copie d'un finding
propre à une source commence de la même façon, à sa première observation après
la mise à niveau.

Une fenêtre `from` et `to` sur `/api/findings` retient des jours entiers, coupés
à minuit UTC sur l'horloge du Hub, donc une fenêtre plus étroite qu'un jour liste
quand même ce qu'une source a observé à n'importe quelle heure de ce jour. La
présence est supposée là où rien n'a été relevé : entre le `first_seen` d'un
finding pour une source et le premier jour que le Hub a relevé pour elle, et
jusqu'à son `last_seen` pour une paire sans aucun jour relevé, ce qui est le cas
de toutes les paires d'une base antérieure au relevé. Sans cette règle, un Hub
mis à niveau se lirait vide sur tout son passé. Avec elle, un finding disparu
puis revenu avant son premier jour relevé se lit comme continu.

La rétention (`Hub:Retention`) retire un jour relevé alors que le finding
lui-même peut rester, et le premier jour relevé ne bouge pas. Une fenêtre qui
tombe tout entière sur des jours retirés, après ce premier jour, ne liste donc
rien, même pour un finding présent tout du long.

Le Hub reflète les acquittements actifs de chaque daemon et n'en est jamais le
propriétaire. Le miroir a la fraîcheur du dernier poll de ce daemon, sauf si un
acquittement relayé par le Hub l'a rafraîchi depuis, donc un acquittement pris
ou révoqué directement sur le daemon apparaît au poll suivant. Un daemon
antérieur à 0.24.0 n'est jamais interrogé, parce qu'il ne sait pas lister sa
baseline de CI, et ses findings sont jugés sur leur seule enveloppe, comme
avant.

Un daemon qui liste 1 000 acquittements ou plus a atteint son propre plafond, et
le Hub consigne cette lecture `truncated`. Les acquittements qu'il a listés
apparaissent dans `acks`, mais une liste dont la fin manque ne peut pas dire
qu'un finding n'est pas acquitté, donc elle ne décide pas de
`include_acked=false` et cette source retombe sur l'enveloppe.

Le relais d'acquittement écrit un acquittement d'exécution sur un seul daemon,
celui qui se trouve derrière la source nommée dans la route. Un finding que
portent trois sources demande trois acquittements, et rien n'en diffuse un seul.
Un acquittement tenu par la baseline de CI d'un daemon ne peut pas être révoqué
par le relais, ni par l'API du daemon elle-même : le daemon répond `404`, et la
baseline change par l'édition de son fichier, sous revue. Une révocation est
relayée pour un finding que le Hub détient sur cette source, ou pour un
acquittement qu'il reflète depuis elle. Une fois le finding retiré par la
rétention, son acquittement ne peut être révoqué depuis le Hub qu'après qu'un
poll l'a listé, ce qui n'arrive jamais avec un daemon antérieur à 0.24.0. Voir
[API-FR.md](API-FR.md#relais-dacquittement).

La joignabilité est à sens unique. Un daemon qui pousse avec succès prouve
qu'il peut joindre le Hub, pas que le Hub peut le joindre, et seul un poll
réussi efface `unreachable_since_ms`. Une source dont le push arrive alors que
son poll échoue continue de rapporter `unreachable_since`. Voir
[OPERATIONS-FR.md](OPERATIONS-FR.md).

## Ce qu'affirment `status` et `lineage`

`status` est une présomption, pas un verdict. Il est dérivé à la lecture depuis
des données que le Hub détient déjà, et `likely_resolved` signifie que
l'endpoint bat encore sans le finding, pas que quelqu'un l'a corrigé.

Le lignage relie une signature mutée à sa devancière seulement quand exactement
un finding stocké correspond. L'ambiguïté n'enregistre rien, parce que nommer
l'un de plusieurs candidats serait une supposition. Voir [API-FR.md](API-FR.md).

`last_seen` est la dernière détection du daemon source, ramenée à l'horloge du Hub
quand celle du daemon avance. Une horloge de daemon en retard n'est pas corrigée,
donc ses findings vieillissent d'autant plus tôt. Un producteur qui omet
`stored_at_ms` est daté par le poll qui l'a lu, ce qui garde un tel finding `active`
tant que son anneau le retient.

## Ce qu'affirme un compte de détections

Une lecture de `/api/findings` avec `from` ou `to` sert combien de fois les daemons
ont détecté chaque finding, d'après les registres de détections de perf-sentinel
0.37.0 et suivants. Le compte n'est exact que quand `detections_complete` vaut true.
Sinon c'est une borne basse, la somme de ce qui est connu, avec ses raisons dans
`detections_gaps`. Null signifie que rien n'est connu, jamais zéro. Voir
[API-FR.md](API-FR.md#ce-que-le-hub-ajoute-à-un-finding).

Une détection est un finding que le daemon émet, compté avant que son anneau, ses
acquittements ou son push ne le voient. Un finding acquitté compte toujours, comme
son gaspillage compte toujours dans `io_waste_ratio`. Une trace peut produire
plusieurs findings d'une même signature, un appel redondant aux paramètres
différents, un groupe fanout ou serialized par parent, plusieurs valeurs de
regroupement, et chacun compte. Les findings slow entre traces comptent avec 0 I/O
évitable. Un fragment de trace réanalysé après que sa fenêtre l'a évincé compte tel
que le détecteur l'a émis. `seen_count` garde son sens, les détections que l'anneau
du daemon retient encore, et n'est pas un volume.

Un daemon ne garde son registre qu'en mémoire, donc tout arrêt, propre ou non, est un
trou : le jour où un registre démarre et le dernier jour d'un registre arrêté se
lisent `restart`. Le Hub additionne les registres successifs d'une source. Un daemon
lu par poll seul perd ce qu'il a compté entre la dernière lecture du Hub et son
arrêt. Un daemon qui pousse envoie un dernier tour pendant son drain.

Rien n'est extrapolé. Sous échantillonnage ou délestage, le compte est ce que le
daemon a analysé, et le Hub sert le plus petit taux d'échantillonnage des jours
comptés dans `detections_sampling_rate`. Un consommateur qui extrapole à partir de lui
doit le dire. Certaines pertes n'ont aucun signal au daemon : une ingestion refusée en
`413`, un message gRPC trop gros, le socket JSON et la troncature par trace. Un jour
peut donc se lire complet alors que des traces n'ont jamais atteint le détecteur.

Un poll atteint un pod par requête, donc plusieurs pods derrière un Service ne peuvent
pas tous être lus. Deux registres d'une source lus par poll et vivants en même temps
se lisent `multi_instance`, et cette source n'est jamais complète. Donnez à chaque pod
sa propre source, ou laissez chaque pod pousser : les registres poussés
s'additionnent sans cette raison. Le chevauchement compare le `started_ms` du daemon à
l'heure de lecture du Hub, donc une horloge de daemon en retard sur celle du Hub peut
faire lire un redémarrage comme `multi_instance`.

La fenêtre compte des jours UTC entiers sur l'horloge du Hub, du jour de `from`, jamais
avant le jour que garde la rétention, au jour de `to`, jamais après le jour du Hub. Les
jours antérieurs à la mise à jour n'ont pas de ligne et se lisent inconnus. Avec des
motifs d'accès, une source qui a compté un finding sans en tenir de copie n'est listée
qu'à un appelant qui lit cette source entière, et une lecture restreinte n'atteint que
les findings qui ont au moins une copie dans sa portée. Une source retirée de
`Hub:Sources` sort du compte, puisque le Hub n'a aucun nom à servir pour elle.

## À quoi un finding est attribué

Le moteur range un groupe N+1 ou d'appels redondants qui traverse plusieurs
services sous le service et le regroupement de son premier span. Un second
service appelant n'apparaît que dans `pattern.occurrences_by_service`. Le Hub
garde cette attribution : `/api/findings?service=` ne trouve que le premier
service, et les attributs de regroupement qu'il enregistre pour le finding sont
ceux du premier span. Les appels en éventail et sérialisés sont attribués au
span parent.

Le Hub enregistre, par source, chaque attribut de regroupement qu'une source a
capté sur un finding, et le service que cette source a remonté. La copie d'une
source écrite par une version antérieure sans enveloppe propre n'a ni l'un ni
l'autre tant que cette source ne remonte pas de nouveau le finding.

Ajouter un attribut au `[detection] grouping_attributes` d'un daemon change plus
que ce que le Hub enregistre. Sur les spans qui ne portent aucun des attributs
précédents, le nouveau devient l'identité de regroupement : un groupe de
détection se scinde en un par valeur, et chacun peut passer sous son seuil. Voir
[DEPLOYMENT-FR.md](DEPLOYMENT-FR.md).

## Ce qu'un run ne promet pas

Les rapports sont supprimés après `Hub:Analysis:ReportRetention`, 24 heures par
défaut. Ce n'est pas une piste d'audit, et un lien partagé hier est déjà mort.
Le run garde ses paramètres, donc il peut être relancé tel quel.

Un run encore en cours à l'arrêt du service revient en `interrupted` et n'est
jamais rejoué de lui-même. Une reprise silencieuse lancerait une seconde requête
lourde vers un backend que personne n'a demandé d'interroger deux fois.

La taille d'un rapport n'est pas un réglage. La cible de 5 Mio du sink est une
constante privée, sans option, sans variable d'environnement et sans clé de
configuration. Quand il écarte des findings pour tenir, le run enregistre
combien ont survécu, relu depuis le fichier rendu.

Les huit codes d'échec viennent d'une heuristique sur un jeu borné de marqueurs
dans la sortie d'erreur du moteur, pas d'un contrat. La sortie d'erreur brute ne
quitte jamais le processus.

Les comptes de runs aux seuils de détection différents ne sont pas comparables.
Relever un seuil n'allège pas un run, il empêche le détecteur de rapporter les
cas les plus petits.

## Ce que le Hub n'authentifie pas

Cette section décrit le Hub avec `Hub:Auth:Enabled` désactivé, le défaut. Activé,
le Hub connecte lui-même les utilisateurs du navigateur et seules les routes
machines restent ouvertes, voir [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md). Avec
`Hub:Access` activé en plus, ces routes demandent elles aussi un jeton machine et chaque
lecture est réduite aux grants de l'appelant, voir
[ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md) et
[Ce que le contrôle d'accès ne couvre pas](#ce-que-le-contrôle-daccès-ne-couvre-pas).

Cinq endpoints réclament une preuve, `POST /api/import/findings`,
`POST /api/import/incidents`, `POST /api/import/correlations`, `POST /api/import/detections`
et `POST /api/import/disclosures`, dont la `X-API-Key` est comparée par empreinte, et
`/share/` sert les fichiers d'une divulgation à quiconque détient un jeton valide. Tous les
autres ne réclament rien : `/api/status`, `/api/sources`, `/api/findings`,
`/api/correlations`, `/api/analyses` aussi bien pour la liste que pour la requête qui lance
un run, `/reports/`, `/metrics`, et le lanceur lui-même. Qui joint le port lit tous les
findings de tous les tenants, modèles de requêtes SQL et noms d'endpoints compris.

Le `by` et le `reason` d'un acquittement reflété sont lisibles sur la route de
lecture ouverte, `/api/findings`, qui reste ouverte aux machines quand
`Hub:Auth` est activé, sauf si `Hub:Access` l'est aussi. C'est qui a acquitté un finding et pourquoi, dans les
mots saisis sur le daemon, comme l'était déjà `acknowledged_by`.

L'en-tête d'identité attribue, il n'authentifie pas.
`Hub:Analysis:IdentityHeader` est enregistré sur un run comme une déclaration
faite par un proxy, et le Hub n'en vérifie rien. C'est le bon comportement
derrière un proxy authentifiant, et aucune défense sans lui. Si le réseau n'est
pas la frontière, posez ce proxy devant ou activez `Hub:Auth`. Voir
[DEPLOYMENT-FR.md](DEPLOYMENT-FR.md).

Sauf si `Hub:Access` est activé, le relais d'acquittement sait qui demande et n'a pas de rôles. Tout appelant que
le Hub peut nommer, un utilisateur connecté ou, une fois
`Hub:AckRelay:TrustIdentityHeader` posé, celui que nomme l'en-tête du proxy,
peut acquitter ou révoquer sur toutes les sources qui portent un identifiant
d'acquittement. Le daemon n'a pas non plus de rôle par utilisateur : sa clé
d'acquittement est une seule clé, et le Hub la détient. Restreignez qui se
connecte, côté fournisseur ou avec `Hub:Auth:RequiredGroups`, et ne posez pas
l'identifiant sur une source que personne ne doit acquitter depuis le Hub. Le
nom envoyé alimente la piste d'audit du daemon et n'y autorise rien. Le daemon
en garde 256 octets, donc un nom long qu'il a fallu encoder en pourcent peut
être coupé.

Le relais n'a ni jeton antiforgery ni politique CORS, il lit donc
`Sec-Fetch-Site` et refuse toute valeur autre que `same-origin`, `cross-site`
comme `same-site` comme le `none` que porte une adresse tapée à la main. Un
appelant qui n'envoie pas du tout cet en-tête passe, puisque curl, un job de CI
et un greffon d'IDE ne le posent pas. Ce qui défend ce cas, c'est le type de
contenu seul : le Hub prend un type de contenu JSON et rien d'autre, ce qu'un
formulaire d'une autre origine ne sait pas produire et qu'un fetch d'une autre
origine ne peut pas envoyer sans un preflight auquel le Hub ne répond jamais.
C'est un plancher et non une frontière, et ce plancher n'est que celui du
relais : `POST /api/analyses` et `POST /api/incidents/refresh` ne jugent ni le
site ni le type de contenu. Un Hub joignable depuis une session de navigateur a
donc sa place derrière un proxy qui ne relaie pas des origines arbitraires.

## Ce que le contrôle d'accès ne couvre pas

Avec `Hub:Access` activé, un appelant ne lit et n'écrit que ce qu'un grant lui donne, voir
[ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md). Voici les limites de ce modèle.

À qui un finding est attribué :

- Un N+1 ou un groupe redondant qui traverse plusieurs services est listé sous le service
  et le regroupement du premier span. Un second service appelant n'apparaît que dans
  `pattern.occurrences_by_service`, donc son projet ne voit pas le finding. Le fan-out et
  les appels sérialisés sont attribués au span parent.
- Une signature replie tous les regroupements. Un projet qui reconnaît un regroupement
  d'une copie lit l'enveloppe la plus récente de cette copie, qui peut décrire un autre
  regroupement du même service et du même endpoint.
- Un attribut ou un nom de service est posé par qui déploie l'application, par quiconque
  peut envoyer de l'OTLP à un daemon, et par qui détient la clé d'import d'une source,
  pour cette source seulement. La comparaison se fait par copie : une valeur plantée
  n'expose que les copies de la source où elle a été plantée. Dans cette source :
  - des spans sous le service et l'endpoint d'une autre équipe avec `team=evil` font lire
    au projet d'evil l'enveloppe la plus récente de cette copie, qui peut être celle de
    l'autre équipe ;
  - les mêmes spans sans attribut de regroupement, ou avec une autre valeur, privent
    l'équipe propriétaire de son grant par attribut : `403` sur chaque acquittement et
    chaque révocation de ce finding à cette source, et sur un run de backend de traces de
    ce service, tant que les spans arrivent, plus `Hub:Retention` ;
  - un acquittement porte sur la signature, aveugle au regroupement. Celui qu'écrit un
    grant par attribut fait aussi taire les regroupements de cette signature que le Hub
    n'a pas vus sur ce daemon, et ceux qui apparaîtront, donc il peut faire taire le même
    finding pour le déploiement d'une autre équipe sur ce daemon. Le Hub ne lui impose
    aucune expiration.

  Là où plusieurs équipes peuvent émettre le même nom de service, donnez à l'équipe
  propriétaire un grant de source ou de service pour `acks.write` et `analyses.run`.
- Lancer un run sur un backend de traces demande que toutes les copies du service dans
  l'environnement du backend s'accordent. Nommez l'environnement d'un backend de traces
  comme les daemons qui remontent ses services.
- Une copie enregistrée avant la version 9 du schéma ne correspond à aucun motif
  d'attribut ni à aucune partie de service tant que sa source ne la remonte pas à nouveau.
- Ajouter un attribut de regroupement change l'identité des spans qui ne portaient aucun
  des précédents.

Un rapport de backend de traces montre tous les services que ses traces traversent. Un
rapport se lit sur le sujet de son run, `traces.read` sur le service demandé, ou sur le
backend entier pour un trace ID, et le Hub ne filtre pas le HTML écrit par le moteur. Qui
détient `traces.read` sur un service lit, dans ses rapports, les spans, findings et
compteurs de tous les services d'autres équipes que ces traces atteignent. C'est voulu, un
problème naît souvent ailleurs. Ne donnez `traces.read` qu'à qui peut voir des traces
d'autres équipes : les rôles intégrés `reader` et `member` le portent, un rôle avec
`findings.read` seul ne le porte pas.

`max_confidence` et `lineage` dans une enveloppe ne sont pas réduits aux copies de
l'appelant, et une ligne de source écrite avant l'existence de la copie par source sert
l'enveloppe partagée jusqu'à ce que cette source remonte à nouveau le finding.

La fraîcheur d'une décision :

- les groupes du fournisseur sont lus à la connexion, ils ont donc 8 heures au plus, et un
  jeton de terminal garde ceux de la session qui l'a approuvé, pas plus longtemps que
  cette session ;
- `Hub:Auth:RequiredGroups`, les groupes internes, les admins, les grants et les jetons
  machine s'appliquent au redémarrage qui les déploie ;
- un jeton machine renouvelé demande un rollout restart, un jeton retiré part avec la
  modification de ConfigMap qui relance le pod.
- un jeton de terminal révoqué est refusé dès sa requête suivante ;
- le nouveau hash d'un compte local demande un rollout restart, qui met fin aux sessions
  de navigateur de ce compte ; les jetons de terminal qu'il a approuvés vivent jusqu'à leur
  expiration, 8 heures au plus, sauf si un admin les révoque.

La connexion du terminal n'est pas offerte tant que `Hub:Auth:TrustedProxyNetworks` est
vide. Derrière un proxy qui y figure, l'adresse que montre la page d'approbation et que
comptent les limites par adresse est celle du client, telle que le proxy la rapporte : un
proxy qui transmet tel quel un `X-Forwarded-For` fourni par le client laisse ce client
choisir l'adresse qu'il montre.

Comptes locaux :

- Le frein vit en mémoire, et un redémarrage l'efface.
- Il n'y a pas de déconnexion, pour une session locale comme pour une autre.
- Une adresse hache au plus 5 fois par minute, et un seul hachage tourne à la fois. Un
  attaquant aux nombreuses adresses, deux douzaines d'adresses IPv4 ou de /64 IPv6 à
  quelques centaines de millisecondes par hachage de 600 000 itérations, peut encore
  garder la place de hachage occupée : l'opérateur l'attend alors jusqu'à 10 secondes, et
  reçoit `503` au-delà. Une limite de débit à l'ingress sur `/auth/sign-in/local` élève
  cette barre.
- Deviner en ligne est borné par cette place, le budget par adresse, la temporisation et
  le minimum de 15 caractères, pas empêché. Choisissez un mot de passe long et aléatoire.

Hors du modèle :

- Le `/metrics` propre d'un daemon reste ouvert. Avec des libellés par service ou par
  regroupement, il nomme des services et des regroupements, sans porter aucun finding.
- Un tableau de bord qui lit un daemon ou un backend de traces avec son propre
  identifiant montre ce que cet identifiant lit, à qui l'outil laisse entrer. Un jeton
  machine dans un outil partagé aussi : une datasource Grafana sert tout le périmètre du
  jeton à chaque utilisateur de Grafana, et un jeton dans les IDE des développeurs est un
  identifiant partagé qui survit aux départs. Un jeton par équipe, avec les seuls projets
  de cette équipe, limite les deux.
- L'identité affichée dans `requested_by` et le `by` d'un acquittement est ce que porte le
  `Hub:Auth:IdentityClaim` du fournisseur. Un utilisateur qui peut s'y renommer change ce
  libellé, jamais ses droits, qui suivent `Hub:Auth:SubjectClaim`.
- Un proxy devant le Hub qui injecte ou transmet `Authorization` casse chaque requête du
  navigateur une fois l'accès activé : le Hub juge le bearer avant le cookie.
- Les rôles combinent les seules permissions du Hub. Un nouveau type d'action demande une
  nouvelle permission dans le Hub.

## Ce qui se joue hors du Hub

Une source daemon qui a un identifiant d'acquittement n'a besoin de rien de ce qui suit :
avec un moteur 0.31.0 ou plus récent, son rapport acquitte à travers le Hub. Sinon, un
rapport vivant exige deux choses que le Hub ne contrôle pas : le
`[daemon.cors] allowed_origins` du daemon doit porter l'origine depuis laquelle
le Hub sert ses rapports, et le lecteur doit pouvoir joindre ce daemon
directement, à son `PublicUrl` quand le Hub le lit par un nom que seul le cluster
résout. Un daemon derrière un ingress à préfixe de chemin reçoit un rapport
statique à la place, parce que le `--daemon-url` du moteur prend une origine et
rien d'autre. Voir [LAUNCHER-FR.md](LAUNCHER-FR.md).

Le Hub porte la cousine de cette contrainte et doit lui-même être servi à la
racine d'une origine. Le lanceur appelle `/api/status`, `/api/sources`,
`/api/analyses` et `/reports/` en chemins absolus, rien ne les réécrit, et un
ingress à préfixe de chemin laisse le navigateur réclamer un préfixe auquel le
Hub ne répond jamais. Voir [DEPLOYMENT-FR.md](DEPLOYMENT-FR.md).

## Échelle et observabilité

Une seule réplique, et ce n'est pas un réglage. SQLite n'a qu'un écrivain et le
volume est `ReadWriteOnce`, donc le chart pose `replicas: 1`.

`GET /metrics` couvre la joignabilité, la file d'analyses, les comptes de runs
et les findings stockés de chaque environnement, et rien d'autre. Les findings
sont comptés par environnement, type, sévérité et statut, jamais par service ni
par endpoint, et les comptes ont jusqu'à 15 secondes. Aucune série ne porte la
durée d'une purge de rétention ni le débit d'import, donc une alerte sur ces
points, ou sur les findings d'un seul service, doit lire `/api/findings` ou les
journaux. Voir [OPERATIONS-FR.md](OPERATIONS-FR.md#métriques).
