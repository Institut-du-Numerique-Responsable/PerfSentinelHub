# Authentification

Le Hub peut connecter lui-même les utilisateurs du navigateur auprès d'un
fournisseur OAuth2, si bien qu'aucun proxy authentifiant n'a besoin de se tenir
devant lui. C'est désactivé par défaut, et désactivé le Hub n'authentifie
personne, comme le décrit [LIMITATIONS-FR.md](LIMITATIONS-FR.md).

## Ce que ça couvre

Avec `Hub:Auth:Enabled`, chaque route exige une session, sauf celles qu'appelle
une machine :

| Route                                                            | Accès                                                                      |
|------------------------------------------------------------------|----------------------------------------------------------------------------|
| `/` et les fichiers du lanceur                                   | session, sinon redirection vers le fournisseur, ou vers la page de connexion du Hub avec un [compte local](#comptes-locaux) |
| `/api/status`, `/api/sources`, `/api/incidents`, `/api/analyses` | session, sinon `401`                                                       |
| `/reports/`                                                      | session, `401` dans le cadre du lanceur, redirection si ouvert directement |
| `POST /api/sources/{sourceId}/acks` et `/acks/revoke`            | session, sinon `401`, et l'acquittement est pris au nom de la session      |
| `/api/findings`                                                  | ouvert, pour les plugins IDE et les jobs CI, sauf contrôle d'accès         |
| `POST /api/import/findings`                                      | sa propre `X-API-Key`, comme avant                                         |
| `/health/live`, `/health/ready`, `/metrics`                      | ouvert, pour les sondes et le scrape ; `/metrics` sur `Hub:MetricsPort` s'il est renseigné |

L'API répond `401` plutôt qu'une redirection, car un fetch ne peut pas suivre
une redirection vers l'origine du fournisseur. Le lanceur se recharge sur un
`401`, et c'est ce rechargement qui renvoie le navigateur se connecter.

Les routes laissées ouvertes servent toujours qui joint le port :
`/api/findings` renvoie tous les findings de toutes les sources. Gardez pour
elles la frontière réseau, une NetworkPolicy ou un ingress qui ne publie pas ces
chemins. Avec `Hub:Access:Enabled`, `/api/findings` exige aussi une session ou un jeton
machine, et chaque appelant atteint ce que couvrent ses grants, voir
[ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md).

Sans `Hub:Auth:RequiredGroups`, tout utilisateur que le fournisseur connecte
entre : restreignez qui peut utiliser le Hub côté fournisseur, en assignant le
client à un groupe ou à des utilisateurs. Avec, seuls entrent les membres des
groupes qu'il liste, voir [Restreindre à des groupes](#restreindre-à-des-groupes).

## Comment ça marche

Les handlers OAuth2 et cookie du framework lui-même, sans package ajouté. Le
navigateur passe par le flux authorization code avec PKCE, le Hub échange le
code sur le token endpoint, puis appelle l'endpoint userinfo avec l'access token
et enregistre comme utilisateur le champ nommé par `Hub:Auth:IdentityClaim`. Un
userinfo sans ce champ fait échouer la connexion plutôt que d'ouvrir une session
sans nom. Une connexion échouée, celle-là, un utilisateur qui annule sur
l'écran du fournisseur ou un utilisateur hors des groupes exigés, répond `403`
avec une même ligne pour tous les cas, renvoyant vers la page d'accueil du Hub
ou vers son administrateur, et journalise la raison en warning (événement
`1900`). Aucun token n'est conservé : la session est un cookie chiffré
`hub_session`, `Secure`, `HttpOnly`, `SameSite=Lax`, valable 8 heures et
prolongé tant qu'il sert, ou non prolongé sous `Hub:Auth:RequiredGroups`. Il n'y
a pas de bouton de déconnexion.

L'utilisateur connecté est ce qu'affiche la barre du haut et ce qu'un run
enregistre comme `requested_by`. `Hub:Analysis:IdentityHeader` est ignoré pour
un utilisateur connecté, puisque n'importe quel client peut envoyer cet en-tête.

Le [relais d'acquittement](API-FR.md#relais-dacquittement) tient l'appelant à
une règle plus stricte qu'un run. Un run enregistre cet en-tête comme une
affirmation que personne n'a vérifiée. Le relais dépense la clé d'écriture d'un
daemon au nom de l'appelant, il prend donc l'utilisateur connecté, et ne prend
l'en-tête que lorsque `Hub:AckRelay:TrustIdentityHeader` vaut `true`, le réglage
d'un Hub placé derrière un proxy qui écrit lui-même l'en-tête et retire celui
qu'un client aurait envoyé. Avec une session, l'en-tête y est ignoré aussi. Sans
session ni ce réglage, le relais répond `403` à tout le monde, voir
[CONFIGURATION-FR.md](CONFIGURATION-FR.md#lidentifiant-dacquittement). Quiconque
est nommé peut acquitter sur toutes les sources qui relaient, voir
[LIMITATIONS-FR.md](LIMITATIONS-FR.md#ce-que-le-hub-nauthentifie-pas), sauf avec le
contrôle d'accès, voir [ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md#acquittements).

Les deux routes du relais jugent aussi d'où vient la requête. Un navigateur qui
envoie `Sec-Fetch-Site: same-origin` passe, et toute autre valeur est un `403`,
`cross-site` comme `same-site` comme le `none` que porte une adresse tapée à la
main. Un appelant qui n'envoie pas du tout cet en-tête passe, parce que curl, un
job de CI et un greffon d'IDE ne le posent pas, et les refuser fermerait le
relais à tout ce qui n'est pas un navigateur. Ce qui tient ce dernier cas, c'est
le type de contenu : le Hub prend un type de contenu JSON et rien d'autre, ce
qu'un formulaire d'une autre origine ne sait pas produire et qu'un fetch d'une
autre origine ne peut pas envoyer sans un preflight auquel le Hub ne répond
jamais. Un Hub joignable depuis une session de navigateur devrait malgré tout se
tenir derrière un proxy qui ne relaie pas des origines arbitraires.

Les clés qui chiffrent le cookie vivent dans un répertoire `keys` à côté de
`Hub:DatabasePath`, sur le volume de données, si bien qu'un redémarrage ne
déconnecte personne. Le Hub journalise au démarrage qu'aucun chiffreur XML n'est
configuré : les clés reposent en clair sur le volume, qui porte la base sous la
même confiance.

TLS s'arrête en général à l'ingress. Le Hub fait confiance à
`X-Forwarded-Proto` venant de n'importe quel pair, pour que l'URI de redirection
qu'il envoie soit en `https`. Seul le schéma est pris des en-têtes transmis,
jusqu'à ce que `Hub:Auth:TrustedProxyNetworks` nomme le proxy : dès lors
`X-Forwarded-Proto` et `X-Forwarded-For` sont tous deux crus, venant de ces
réseaux seulement et sur un saut, si bien que le Hub voit l'adresse propre de
chaque client et qu'un client qui lui parle directement ne peut pas en forger.

## Réglages

| Réglage                            | Défaut                 | Validation                                                                                         |
|------------------------------------|------------------------|----------------------------------------------------------------------------------------------------|
| `Hub:Auth:Enabled`                 | `false`                | Rien de ce qui suit n'est exigé tant que c'est désactivé, et `RequiredGroups` doit alors être vide |
| `Hub:Auth:AuthorizationEndpoint`   | aucun                  | HTTPS absolu (HTTP en loopback seulement), sans identifiants ni fragment                           |
| `Hub:Auth:TokenEndpoint`           | aucun                  | Idem                                                                                               |
| `Hub:Auth:UserInformationEndpoint` | aucun                  | Idem                                                                                               |
| `Hub:Auth:ClientId`                | aucun                  | Obligatoire                                                                                        |
| `Hub:Auth:ClientSecret`            | aucun                  | Obligatoire, fourni par un Secret                                                                  |
| `Hub:Auth:Scopes`                  | `openid profile email` | Séparés par des espaces                                                                            |
| `Hub:Auth:IdentityClaim`           | `email`                | Le champ userinfo enregistré comme utilisateur, un nom affiché                                     |
| `Hub:Auth:SubjectClaim`            | `sub`                  | La clé d'autorisation, lue seulement avec le contrôle d'accès : `sub`, `oid`, `uuid` ou `account_id` |
| `Hub:Auth:GroupsClaim`             | `groups`               | Le champ userinfo des groupes, lu avec `RequiredGroups` ou le contrôle d'accès                     |
| `Hub:Auth:RequiredGroups`          | aucun                  | Aucune entrée vide, refusé si `Enabled` est désactivé, voir plus bas                               |
| `Hub:Auth:TrustedProxyNetworks`    | aucun                  | Réseaux CIDR du seul proxy (`10.42.200.0/28`) dont les en-têtes transmis sont crus ; la connexion du terminal et les comptes locaux l'exigent |

Déclarez `https://<hôte du hub>/auth/callback` comme URI de redirection du
client, et faites du client un client confidentiel.

Sous Helm :

```yaml
hub:
  auth:
    enabled: true
    authorizationEndpoint: https://sso.example.com/realms/acme/protocol/openid-connect/auth
    tokenEndpoint: https://sso.example.com/realms/acme/protocol/openid-connect/token
    userInformationEndpoint: https://sso.example.com/realms/acme/protocol/openid-connect/userinfo
    clientId: perf-sentinel-hub
    clientSecretName: perf-sentinel-hub-oauth
    clientSecretKey: client-secret
    identityClaim: preferred_username
```

Le secret atteint le pod comme `Hub__Auth__ClientSecret`, jamais par les values.

## Fournisseurs

Les endpoints ci-dessous viennent de la documentation de chaque fournisseur. Les
tests du Hub jouent le fournisseur avec un faux.

| Fournisseur     | Autorisation, token, userinfo                                                                                                             | Scopes                 | `IdentityClaim`      | `SubjectClaim` |
|-----------------|-------------------------------------------------------------------------------------------------------------------------------------------|------------------------|----------------------|----------------|
| Keycloak        | `https://<hôte>/realms/<realm>/protocol/openid-connect/auth`, `.../token`, `.../userinfo`                                                 | `openid profile email` | `preferred_username` | `sub`          |
| Entra ID        | `https://login.microsoftonline.com/<tenant>/oauth2/v2.0/authorize`, `.../oauth2/v2.0/token`, `https://graph.microsoft.com/oidc/userinfo`  | `openid profile email` | `email`              | `sub`          |
| Google          | `https://accounts.google.com/o/oauth2/v2/auth`, `https://oauth2.googleapis.com/token`, `https://openidconnect.googleapis.com/v1/userinfo` | `openid profile email` | `email`              | `sub`          |
| GitLab          | `https://<hôte>/oauth/authorize`, `https://<hôte>/oauth/token`, `https://<hôte>/oauth/userinfo`                                           | `openid profile email` | `preferred_username` | `sub`          |
| Bitbucket Cloud | `https://bitbucket.org/site/oauth2/authorize`, `https://bitbucket.org/site/oauth2/access_token`, `https://api.bitbucket.org/2.0/user`     | `account`              | `username`           | `account_id`   |

Bitbucket n'est pas un fournisseur OpenID : son userinfo est la ressource REST de
l'utilisateur, et ses scopes sont ceux accordés au consumer OAuth.

## Restreindre à des groupes

`Hub:Auth:RequiredGroups` liste les groupes dont les membres peuvent utiliser le
Hub, et appartenir à l'un d'eux suffit. À la connexion, le Hub lit le champ
userinfo nommé par `Hub:Auth:GroupsClaim`, une liste de chaînes ou une chaîne
seule, et compare chaque entrée à la liste : à l'identique, casse comprise, les
noms tels que le fournisseur les envoie. Une entrée qui n'est pas une chaîne est
ignorée. Un userinfo sans ce champ, ou qui ne nomme aucun des groupes, fait
échouer la connexion. L'utilisateur reçoit la même ligne `403` que pour toute
autre connexion échouée, qui ne dit pas pourquoi. Le warning (événement `1900`)
nomme l'utilisateur et lequel des deux cas c'était, jamais ses groupes. Vide, le
défaut, le champ n'est pas lu du tout.

L'appartenance n'est lue qu'à la connexion. Avec `RequiredGroups`, la session
n'est pas prolongée tant qu'elle sert : elle prend fin 8 heures après la
connexion, et la connexion suivante relit les groupes. Un membre retiré chez le
fournisseur garde son accès jusqu'à la fin de sa session, 8 heures au plus. La
liste, elle, est vérifiée à chaque requête : une session retient le groupe qui
l'a admise, si bien que renseigner `RequiredGroups`, ou en retirer un groupe,
ferme les sessions qu'il ne couvre plus dès que le Hub redémarre avec, y
compris celles ouvertes avant la barrière. Leurs utilisateurs repassent par la
connexion. Sans ce réglage, la session est prolongée comme avant.

La liste est de la configuration, déployée avec le reste, et le Hub n'a aucun
écran pour la modifier. Renseignée alors que `Hub:Auth:Enabled` est désactivé,
elle arrête le Hub au démarrage plutôt que de laisser une barrière qui ne
contrôle personne, et le schéma du chart la refuse de la même façon. Sous Helm :

```yaml
hub:
  auth:
    enabled: true
    # Les endpoints et le client du fournisseur, comme plus haut.
    groupsClaim: groups
    requiredGroups:
      - /perf-sentinel-hub
```

Tous les fournisseurs ne mettent pas les groupes dans leur userinfo :

- **Keycloak** le fait dès que le client a un mapper *Group Membership* avec
  *Token Claim Name* `groups` et *Add to userinfo* activé, sur son scope dédié
  ou sur un client scope *Default*. Sur un client scope *Optional*, le mapper
  ne s'applique que si ce scope est demandé : ajoutez son nom à
  `Hub:Auth:Scopes`. Avec *Full group path* activé, un groupe est envoyé sous la
  forme de son chemin, `/parent/enfant`, et `RequiredGroups` l'écrit ainsi.
  Désactivé, c'est le nom seul du groupe.
- **GitLab** liste dans `groups` les chemins des groupes dont l'utilisateur est
  membre, directement ou par un groupe parent.
- **Entra ID** et **Google** non. Leur userinfo porte le nom et l'email de
  l'utilisateur et aucun groupe, et celui d'Entra ID ne peut pas être étendu,
  si bien que `RequiredGroups` ne peut pas servir avec eux en l'état.
  Restreignez qui se connecte côté fournisseur à la place.
- La ressource utilisateur de **Bitbucket Cloud** ne liste aucun groupe non plus.

## Avec le contrôle d'accès

`Hub:Access:Enabled` décide, à l'intérieur des sessions que `RequiredGroups` laisse
entrer, de ce que chaque appelant atteint. Les deux sont indépendants et chacun exige
`Hub:Auth:Enabled` ; réglez les deux. Le modèle, les routes et les jetons machine sont
dans [ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md). Ce qui change à la connexion :

- **Le sujet décide, l'identité s'affiche.** Le champ userinfo que nomme
  `Hub:Auth:SubjectClaim` (par défaut `sub`, voir le tableau des fournisseurs) est la clé
  à laquelle se comparent `Hub:Access:Admins` et les membres des groupes internes.
  `IdentityClaim` garde son sens : le nom affiché, enregistré dans `requested_by` et dans
  le `by` d'un ack, jamais comparé. Un userinfo dont le champ sujet manque, n'est pas une
  chaîne, est vide ou dépasse 256 caractères fait échouer la connexion.
- **Préfixes réservés.** Un sujet ou une identité qui commence par `local:` ou
  `machine:`, casse ignorée, fait échouer la connexion. Sans contrôle d'accès, l'identité
  n'est qu'un nom affiché et une telle valeur reste acceptée.
- **Groupes.** `GroupsClaim` est lu une fois par connexion et alimente les deux portes,
  selon les règles ci-dessus. La session ne garde que les groupes du fournisseur qu'un
  grant ou `Hub:Access:AdminGroups` nomme, et jamais une valeur qui commence par
  `internal:`. Les noms écartés sont journalisés en Debug (événement `1902`), ce qui
  montre comment le fournisseur les écrit. Un grant donné à un groupe du fournisseur
  atteint un utilisateur connecté à sa connexion suivante.
- **Groupes d'admins du fournisseur.** `AdminGroups` peut nommer un groupe du
  fournisseur. Chez un fournisseur où les utilisateurs créent des groupes de n'importe
  quel nom (gitlab.com, et GitLab auto-hébergé par défaut), n'importe qui peut créer un
  groupe de premier niveau de ce nom : une telle entrée n'est sûre qu'avec
  `RequiredGroups` qui limite la connexion aux membres de l'organisation, et le groupe
  nommé par un chemin complet sous le groupe de l'organisation. `Admins` n'échappe pas à
  `RequiredGroups` : ces personnes ont quand même besoin d'un groupe listé pour se
  connecter.
- **La session n'est plus renouvelée**, comme sous `RequiredGroups` : elle prend fin
  8 heures après la connexion. Une session ouverte avant l'activation du contrôle d'accès
  ne porte aucun sujet et repasse par la connexion.
- **`Hub:Analysis:IdentityHeader` n'est jamais lu**, pour le statut, `requested_by` et
  les acks, et `Hub:AckRelay:TrustIdentityHeader` doit rester `false`.
- **`reader` n'est pas en lecture seule** : le rôle intégré lance des analyses.

Chaque refus répond la même ligne `403` et journalise l'événement `1900` avec sa raison.

## Connexion du terminal

Avec le contrôle d'accès actif, `perf-sentinel login` obtient pour le développeur un
jeton personnel pour la CLI, sans copier de secret. Il suit le flux d'autorisation
d'appareil de la RFC 8628, mené par le Hub lui-même :

1. La CLI demande un code au Hub (`POST /auth/device`) et l'affiche avec la page
   `/device` du Hub.
2. Le développeur ouvre `/device` dans un navigateur connecté au Hub et **tape** le code.
   Aucun lien ne le porte : un code envoyé par quelqu'un d'autre ne peut pas arriver
   prérempli.
3. La page montre d'où et quand le code a été demandé, l'adresse et l'heure, qui est
   connecté et quand le jeton expirera. Quand la demande vient d'un autre réseau que
   celui du navigateur, elle le dit, et **Approve** demande un second clic
   (**Approve anyway**). Un code venu d'un autre réseau a la forme d'un hameçonnage.
4. Une fois approuvé, la consultation suivante de la CLI (`POST /auth/device/token`)
   reçoit le jeton.

**Qui approuve.** Seule une session de navigateur approuve, vérifie ou gère des jetons ;
un jeton bearer jamais, si bien qu'un jeton ne peut pas en créer un autre. Une session à
qui il reste moins d'une heure repasse d'abord par le fournisseur, si bien que chaque
jeton vit au moins une heure.

**Ce que porte le jeton.** Le sujet, l'identité et les groupes du fournisseur de la
session qui approuve, figés à l'approbation, et le groupe `RequiredGroups` qui l'a
admise. Les groupes internes, les listes d'admins et `RequiredGroups` sont lus à chaque
requête, comme pour une session. Un jeton de terminal n'est **jamais admin**, même
approuvé par un admin : il porte les droits de la personne, dont `hub ack` et
`hub analyze` ont besoin, et les pouvoirs sur tout le Hub restent à une session de
navigateur. Un code hameçonné ne livre donc au plus que les droits de la victime.

**Durée et révocation.** Un jeton expire quand la session qui l'a approuvé expire, au
plus 8 heures après cette connexion ; il n'y a pas de jeton de rafraîchissement. Une
personne détient au plus 10 jetons vivants, et le 11e retire le plus ancien.
`perf-sentinel logout` révoque son jeton (`POST /auth/revoke`). Le lanceur liste les
jetons de l'appelant sur Votre accès, et ceux de tous sur l'écran Couverture d'un admin,
chacun avec un bouton **Revoke**. La révocation s'applique à la requête suivante. Seul le SHA-256 d'un jeton
est conservé, jamais le jeton. Sous `RequiredGroups`, un jeton dont le groupe qui l'a
admis a été retiré de la liste cesse de fonctionner au redémarrage suivant, comme une
session.

**Il exige `Hub:Auth:TrustedProxyNetworks`.** Derrière un ingress, sans cette liste,
chaque requête porte l'adresse du contrôleur d'ingress : la page montrerait la même
adresse pour chaque connexion, celle d'un attaquant comprise, et la limite par adresse
des codes en attente bloquerait toute connexion de terminal pour un seul client bruyant.
Le Hub ne peut pas savoir si un proxy le précède, donc avec la liste vide il ne mappe
aucune route du flux d'appareil, journalise l'événement `2014` au démarrage et la CLI
indique que le Hub n'offre pas de connexion du terminal. La liste des jetons et la
révocation restent disponibles. Un Hub sans proxy devant liste `127.0.0.1/32`.

Listez la plage la plus étroite qui contient le proxy et rien d'autre. Toute adresse de
la liste peut fixer l'adresse cliente que le Hub enregistre, donc une plage partagée avec
d'autres charges, comme le CIDR des pods de tout le cluster, laisse n'importe quel pod
qui s'y trouve falsifier cette adresse : il passe la limite par adresse et peut emprunter
l'adresse d'une victime pour masquer l'avertissement d'autre réseau. Donnez aux pods du
contrôleur d'ingress une plage à eux, ou laissez une NetworkPolicy n'admettre vers le Hub
que ces pods-là dans la plage listée.

```yaml
hub:
  auth:
    trustedProxyNetworks: ["10.42.200.0/28"]   # les seuls pods du contrôleur d'ingress
```

L'ingress doit lui-même voir l'adresse du client. Un Service LoadBalancer en
`externalTrafficPolicy: Cluster` la remplace par l'adresse d'un nœud avant l'ingress :
chaque connexion semble alors venir d'une poignée d'adresses, la page d'approbation
n'avertit jamais d'un autre réseau, et la limite par adresse est partagée par tous ceux
qui passent par un même nœud. Utilisez `externalTrafficPolicy: Local`, ou le protocole
PROXY entre le load balancer et l'ingress.

**Limites.** Au plus 200 codes en attente, 10 par adresse cliente (un client IPv6 compte
par son /64) ; un code dure 10 minutes et sert une fois. La CLI consulte toutes les
5 secondes, et 5 de plus chaque fois qu'elle consulte trop tôt (`slow_down`). Dix codes
mal tapés ou inconnus en 10 minutes suspendent les approbations d'une personne pour le
reste de ces 10 minutes. Les codes en attente vivent en mémoire, si bien qu'un
redémarrage annule les connexions en cours.

## Comptes locaux

Avec le contrôle d'accès actif, un compte local est l'entrée quand le fournisseur est en
panne. Se connecter par le fournisseur reste le chemin normal : déclarez un ou deux
comptes de secours, gardez leurs mots de passe dans un coffre, et changez-les après
chaque usage.

**En déclarer un.** `Hub:Access:LocalAccounts` liste des `{ Name, PasswordHash }`. Le
compte se connecte en tant que `local:<name>`, comme sujet et comme identité, et
`Hub:Access:Admins` ou les `Members` d'un groupe interne doivent lister `local:<name>` :
un compte qui n'atteindrait rien arrête le Hub au démarrage. Le nom suit
`^[a-z0-9][a-z0-9._-]{0,63}$`. Le hash est la ligne qu'affiche
`PerfSentinelHub hash-password` (voir
[OPERATIONS-FR.md](OPERATIONS-FR.md#comptes-locaux-de-secours)), livrée depuis un Secret,
jamais par les values. Un sujet ou une identité du fournisseur commençant par `local:`
fait échouer la connexion, si bien qu'aucun utilisateur du fournisseur ne peut se faire
passer pour un compte local.

**La page de connexion.** Avec un compte local déclaré, une requête de page sans session
va vers la page `/auth/sign-in` du Hub au lieu du fournisseur. Elle propose d'abord
**Sign in with single sign-on** et, repliée en dessous, **Sign in with a local account**.
Le Hub sert cette page sans contacter personne, elle s'ouvre donc pendant une panne du
fournisseur ; le fournisseur n'est joint que lorsque l'utilisateur clique sur
l'authentification unique, et l'utilisateur revient sur la page demandée. L'API répond
toujours `401`, et le lanceur se recharge vers la page. Sans compte local déclaré rien ne
change : une page va droit au fournisseur.

**La session.** 8 heures fixes depuis la connexion, jamais renouvelée. Elle ne porte aucun
groupe du fournisseur : ses groupes sont les groupes internes qui listent
`local:<name>`, lus à chaque requête, et elle est admin quand `Admins` la liste ou
qu'`AdminGroups` nomme l'un de ces groupes. `Hub:Auth:RequiredGroups` ne s'y applique
pas : le compte est déclaré dans la même configuration, et c'est l'entrée quand le
fournisseur ne l'est pas. Elle peut approuver une connexion du terminal, dont le jeton
finit avec la session.

**Une rotation met fin aux sessions.** Une session porte une empreinte du hash
configuré. Un nouveau hash, une fois le pod redémarré, met fin à chaque session de ce
compte, et retirer le compte y met fin à leur requête suivante. Les jetons de terminal
que le compte a approuvés vivent jusqu'à leur expiration, 8 heures au plus ; un admin les
révoque depuis l'écran Couverture.

**Deviner.** Le Hub hache un mot de passe à la fois, PBKDF2-HMAC-SHA256 à 600 000
itérations, et un mot de passe compte 15 à 1 024 caractères. Un nom inconnu coûte le même
hachage qu'un nom déclaré et reçoit la même réponse, `403` "The name or the password is
wrong.", octet pour octet, si bien que ni la réponse ni son délai ne distinguent les noms.

- **Temporisation.** Une adresse cliente et un nom ont cinq échecs gratuits, puis
  attendent 30 secondes, doublées à chaque échec suivant jusqu'à 15 minutes ; un succès
  l'efface. Il n'y a pas de verrou : un attaquant ne ralentit que sa propre adresse,
  jamais celle de l'opérateur.
- **Budget.** Une adresse hache au plus 5 fois par minute, quels que soient les noms.
- Au-delà de l'un ou l'autre, `429` avec `Retry-After`, sans hachage. Une requête attend
  jusqu'à 10 secondes la place de hachage, 8 au plus en attente, puis reçoit `503` et
  `Retry-After: 1`.
- Le frein vit en mémoire, et un redémarrage l'efface.
- Le formulaire n'est accepté que depuis la page du Hub : un `Sec-Fetch-Site` autre que
  `same-origin`, ou une `Origin` étrangère, reçoit `403`.

**Il exige `Hub:Auth:TrustedProxyNetworks`.** Sans elle, chaque client derrière l'ingress
partage une adresse, et n'importe qui pourrait garder le compte de secours freiné. Le Hub
refuse de démarrer avec un compte local et une liste vide. Un Hub sans proxy devant liste
`127.0.0.1/32`.

**Journalisation.** Chaque connexion locale journalise l'événement `2020` en Warning, avec
le compte et l'adresse cliente. Chaque tentative refusée journalise l'événement `2021` en
Warning, avec le compte, l'adresse et les échecs consécutifs. Un nom non déclaré est
journalisé `(unknown name)` : ce peut être un mot de passe tapé dans le mauvais champ.
Les réponses `429` et `503` ne sont pas journalisées.

```yaml
hub:
  auth:
    trustedProxyNetworks: ["10.42.200.0/28"]   # les seuls pods du contrôleur d'ingress
  access:
    adminGroups: ["/hub-admins", "internal:break-glass"]
    groups:
      - { name: break-glass, members: ["local:ops"] }
    localAccounts:
      - { name: ops, secretName: perf-sentinel-hub-break-glass, secretKey: ops }
```
