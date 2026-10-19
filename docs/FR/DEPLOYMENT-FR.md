# Formes de déploiement

Une question à laquelle le reste de cette documentation ne répond pas : quand
les environnements sont cloisonnés les uns des autres, faut-il un Hub par
cluster ou un Hub central qui les collecte tous. Cette page dit les flux
qu'exige chaque forme et ce que la moins chère abandonne, pour que la réponse
se lise avant de déployer quoi que ce soit.

## Chaque flux, et dans quel sens il va

| Flux                       | Sens    | Ce qu'il porte                                                                                                  |
|----------------------------|---------|-----------------------------------------------------------------------------------------------------------------|
| daemon vers Hub            | entrant | le chemin principal des findings, `POST /api/import/findings` avec `X-API-Key`                                  |
| Hub vers daemon            | sortant | le poll, lecture de la liste `api/acks` comprise, la joignabilité, et `api/export/report` au lancement d'un run |
| Hub vers daemon, relais    | sortant | un acquittement ou une révocation, envoyés avec l'`AckHeaderName/Value` de la source, jamais sans lui           |
| Hub vers backend de traces | sortant | pendant un run, et jamais autrement                                                                             |
| navigateur vers Hub        | entrant | le lanceur et les rapports qu'il ouvre                                                                          |
| greffon d'IDE ou job de CI | entrant | `GET /api/findings`, rien d'autre                                                                               |
| job de CI vers Hub, divulgations | entrant | `POST /api/import/disclosures` avec sa propre `X-API-Key`, une fois par période, quand les divulgations sont actives |
| Hub vers destinataires de webhook | sortant | un `POST` signé par divulgation publiée ou retirée, vers chaque entrée de `Hub:Disclosures:Webhooks` |
| n'importe qui vers Hub, liens de partage | entrant | `GET /share/{token}/{name}`, les fichiers d'une divulgation sous un jeton qui expire |
| Hub vers api.github.com    | sortant | la vérification de version, que `Hub:UpdateCheck:Enabled` désactive                                             |

Le Hub n'initie jamais rien vers une CI. Un build lance le moteur en mode
batch, il n'y a pas de daemon dedans, donc rien à interroger. La même topologie
est dessinée dans [ARCHITECTURE-FR.md](ARCHITECTURE-FR.md).

## Trois formes, et le prix de chacune

| Forme                  | Réseau à ouvrir       | Ce qui fonctionne                                                                                                    | URL pour les clients machine |
|------------------------|-----------------------|----------------------------------------------------------------------------------------------------------------------|------------------------------|
| un Hub par cluster     | rien                  | tout                                                                                                                 | une par environnement        |
| Hub central, deux sens | deux flux par cluster | tout                                                                                                                 | une seule                    |
| Hub central, push seul | un flux par cluster   | findings oui, joignabilité et dépliage d'une rangée daemon et run sur une source daemon et relais d'acquittement non | une seule                    |

Un Hub déployé dans le cluster qu'il collecte joint ses daemons et ses backends
de traces en ClusterIP, donc la première forme n'ouvre aucune règle de
pare-feu. Elle le paie en adresses : chaque client machine doit en connaître
une par environnement, et aucun écran ne montre la flotte entière d'un seul
tenant.

Un Hub central inverse ce marché. Une adresse, un écran de flotte, et un jeu de
règles de pare-feu par cluster à maintenir. Ouvrir les deux sens conserve
toutes les fonctions. N'ouvrir que le sens entrant coûte moins cher et coûte
les fonctions listées plus bas, ce qui est un vrai choix et non une
installation dégradée.

## Ce qu'un poll rattrape, et ce qu'il ne peut pas

Le Hub interroge `api/findings?limit=1000&include_acked=true` immédiatement au
démarrage puis à chaque `Hub:PollInterval`. Cela resynchronise le contenu
courant du tampon du daemon. Ce n'est pas un journal. Ce que le tampon a évincé
pendant l'absence du Hub est perdu, et aucune limite ne le ramène. Un poll qui
revient pile sur le plafond est journalisé comme possiblement tronqué, parce
que l'instantané peut être en deçà de ce que le daemon détient. Face à un daemon en
perf-sentinel 0.31.2 ou plus récent qui ne pousse pas, le Hub compte aussi les findings
sortis de l'anneau entre deux polls avant qu'il les lise : il les journalise
(événement 1104), publie `perf_sentinel_hub_source_missed_findings_total` et ajoute un
conseil à ceux du daemon dans l'écran de la flotte. Relevez `[daemon]
max_retained_findings`, raccourcissez `Hub:PollInterval` ou activez l'export Hub du
daemon. Voir [LIMITATIONS-FR.md](LIMITATIONS-FR.md).

## Ce que le push seul abandonne

Cinq choses exigent que le Hub joigne le daemon, et non l'inverse. La
joignabilité est écrite par le poll et par rien d'autre, donc une source en
push seul n'affiche aucun dernier succès. Déplier une rangée daemon dans
l'écran de flotte lit cette source à la demande. Lancer un run sur une source
daemon commence par lui demander `api/export/report`. Relayer un acquittement
ou une révocation écrit sur le daemon, quel que soit l'identifiant
d'acquittement que porte la source. Et un finding qui a cessé de récidiver
pendant une coupure n'est plus jamais poussé, parce que seule la récidive le
pousse.

Une chose borne la gravité de tout cela. L'exportateur du daemon pousse une
signature dès sa découverte ou dès que sa sévérité empire, puis en rafraîchit une
encore active au plus une fois par heure, donc un problème persistant revient de
lui-même dans l'heure, poll ou pas. Ce que le poll aurait récupéré,
c'est le finding qui s'est tu, et la fenêtre entre un redémarrage et le
prochain rafraîchissement naturel.

## Quand un Hub central ne joint pas un backend

Rien ne devient inanalysable. Le lanceur imprime chaque run sous forme de ligne
de commande du moteur, donc un backend que le Hub central ne joint pas reste
analysable depuis un terminal qui, lui, le joint. Voir
[LAUNCHER-FR.md](LAUNCHER-FR.md).

## Ce qui doit être vrai de l'adresse

Le Hub doit être servi à la racine d'une origine, et par défaut il n'authentifie
aucun de ses lecteurs. Les deux contraintes façonnent le proxy inverse posé
devant lui plutôt que le Hub lui-même, donc à lire avant d'écrire l'ingress.
Voir [LIMITATIONS-FR.md](LIMITATIONS-FR.md). `Hub:Auth` lève la seconde pour le
navigateur sans proxy, voir [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md). Avec des
comptes locaux déclarés, la page `/auth/sign-in` du Hub précède le fournisseur et
`Hub:Auth:TrustedProxyNetworks` doit nommer les pods du contrôleur d'ingress : le frein
de connexion s'appuie sur l'adresse propre de chaque client. Une limite de débit à
l'ingress sur `/auth/sign-in/local` ajoute une barre que le Hub ne peut pas élever seul.

Le chart écrit cet ingress quand `ingress.enabled` est activé. Il publie le
Service à la racine de `ingress.host`, jamais sous un chemin, et avec
`ingress.tlsSecretName` il sert cet hôte en TLS, terminé au contrôleur. Le Hub
lit `X-Forwarded-Proto`, donc l'URI de redirection qu'il envoie au fournisseur
OAuth, `https://<hôte>/auth/callback`, est en `https`. `ingress.className` et
`ingress.annotations` sont transmis tels quels au contrôleur.

L'Ingress publie toutes les routes. Avec `Hub:Auth` désactivé, le défaut,
aucune ne demande qui appelle, le lanceur et `POST /api/analyses` compris. Avec
`Hub:Auth` activé, les routes machines restent ouvertes : `/api/findings`, que lisent
les plugins d'IDE et les jobs de CI et qui renvoie toutes les findings de toutes
les sources, les lectures d'incidents et de corrélations des dashboards, `/metrics`
et `/health`. Ne publiez l'hôte que sur un réseau
autorisé à les lire, ou limitez qui le joint avec les annotations de liste
d'adresses sources du contrôleur. Avec `Hub:Access` activé en plus,
ces lectures exigent une session ou un jeton machine et répond à chaque
appelant avec son propre périmètre, et `/metrics` ne répond que sur
`hub.metricsPort`, que l'Ingress ne route pas, voir
[ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md). Un proxy ou un contrôleur devant
le Hub ne doit alors ni injecter ni transmettre d'en-tête `Authorization` : une
requête qui porte un bearer est jugée sur lui seul, et chaque requête du
navigateur recevrait `401`. Hors Helm, un Hub qui reçoit `Hub__MetricsPort=9464`
a aussi besoin de `ASPNETCORE_HTTP_PORTS="8080;9464"`, sinon il s'arrête au
démarrage.

Le contrôleur transmet les en-têtes qu'envoie un client, `X-Forwarded-User`
compris. Laissez `hub.ackRelay.trustIdentityHeader` désactivé derrière cet
Ingress, sauf si le contrôleur pose lui-même cet en-tête depuis un proxy
d'authentification, sinon quiconque joint l'hôte acquitte au nom de son choix.
`Hub:Auth` exige TLS devant le Hub, par `ingress.tlsSecretName` ou par le
contrôleur : ses cookies sont `Secure`, un navigateur les écarte en HTTP simple,
et chaque connexion est refusée. L'import accepte des corps jusqu'à 2 Mio,
au-delà du 1 Mio qu'ingress-nginx laisse passer par défaut, donc un daemon qui
pousse par cet hôte demande de relever
`nginx.ingress.kubernetes.io/proxy-body-size` en conséquence. Un import de divulgation prend
jusqu'à 8 Mio, donc la tâche qui publie les divulgations par cet hôte demande `8m`.

## La CI lit, elle n'alimente pas

Un build produit du SARIF et du JSON, et ni l'un ni l'autre n'a sa place dans
le Hub. `last_seen` est l'horloge d'observation du Hub, `status` est déduit
d'un endpoint qui bat encore, et un build est un tir synthétique sur une
branche. En importer un rendrait `status` faux et ferait passer les findings
d'une branche pour ceux de la production. Un job de CI qui veut l'état de la
flotte appelle `GET /api/findings` comme n'importe quel autre lecteur. Voir
[API-FR.md](API-FR.md).

Une divulgation périodique n'est pas non plus un produit de build, et ce n'est pas un
finding. C'est un rapport sur une période close, construit par `perf-sentinel disclose` à
partir des archives des daemons de production et signé par une tâche planifiée, souvent un
workflow de CI pour son identité OIDC. Le Hub le garde et le montre tel qu'il est arrivé, voir
[API-FR.md](API-FR.md#divulgations).

## Les attributs de regroupement se choisissent sur chaque daemon

Le Hub n'ajoute aucun réglage de regroupement à lui. Il enregistre, par source,
chaque attribut qu'un daemon a capté sur un finding, et un daemon ne capte que
les attributs que liste son propre `[detection] grouping_attributes`. Une clé
comme `team` n'arrive au Hub que depuis les daemons qui la listent, donc
listez-la sur chaque daemon dont les findings doivent la porter, et posez-la sur
les spans, en attribut de ressource ou de span, dans les applications.

Une liste explicite remplace le défaut du moteur au lieu de le compléter. Gardez
les défauts en tête :

```toml
[detection]
grouping_attributes = ["k8s.namespace.name", "service.namespace", "team"]
```

Le premier attribut présent sur un span est l'identité de regroupement du
finding. Sur les spans qui ne portent aucun des défauts, depuis Compose ou une
VM par exemple, la clé ajoutée devient cette identité, ce qui change trois
choses sur ce daemon : un groupe de détection se scinde en un par valeur, donc
un compte peut passer sous son seuil, le daemon liste une ligne par valeur, et
le label `grouping` de ses métriques suit. Voir
[LIMITATIONS-FR.md](LIMITATIONS-FR.md).
