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
| `/` et les fichiers du lanceur                                   | session, sinon redirection vers le fournisseur                             |
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
qu'il envoie soit en `https`. Seul le schéma est pris des en-têtes transmis.

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
