# Contrôle d'accès

Plusieurs équipes partagent un Hub. Avec le contrôle d'accès, chaque appelant atteint ce
que couvrent ses grants et rien d'autre : dans le navigateur, et pour les machines comme
les jobs de CI, les tableaux de bord et les plugins d'IDE. Il est désactivé par défaut,
et désactivé le Hub répond exactement comme sans lui.

Le contrôle d'accès est de la configuration, sous `Hub:Access` (Helm `hub.access`). Le Hub
ne stocke aucun utilisateur ni aucune appartenance : les personnes et leurs groupes chez
le fournisseur viennent du fournisseur d'identité à la connexion, et tout le reste des
values. Une modification s'applique au redémarrage suivant, et un déploiement du chart
redémarre le pod.

## Avant de l'activer

`Hub:Access:Enabled` (Helm `hub.access.enabled`) refuse de démarrer, et le schéma du chart
le refuse au `helm upgrade`, sauf si :

- `Hub:Auth:Enabled` est activé, voir [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md) ;
- `Hub:MetricsPort` est renseigné, puisque `/metrics` ne répond alors que sur ce port
  (voir [OPERATIONS-FR.md](OPERATIONS-FR.md#métriques)) ;
- `Hub:Access:Admins` ou `Hub:Access:AdminGroups` contient une entrée ;
- `Hub:AckRelay:TrustIdentityHeader` est désactivé : seule une session ou un jeton du Hub
  nomme un appelant.

Vérifiez aussi ces points, qu'aucune validation ne peut contrôler :

- **Les plugins d'IDE.** Une fois l'accès activé, `/api/findings` exige un identifiant.
  Le plugin JetBrains en envoie un à partir de la version qui suit la 0.1.0, qui ajoute un
  identifiant par endpoint (robintra/perf-sentinel-jetbrains-plugin#31). Un plugin plus
  ancien reçoit `401` sans moyen de s'en sortir. Installez d'abord cette version sur chaque
  IDE qui lit le Hub.
- **Les tableaux de bord.** La datasource Hub de Grafana a besoin d'un jeton machine, voir
  [Jetons machine](#jetons-machine).
- **`Hub:Auth:RequiredGroups`.** Recommandé avec l'accès : c'est la porte extérieure, qui
  peut ouvrir une session, et l'accès décide de ce que chaque session atteint à
  l'intérieur. Sans lui, tout compte que le fournisseur connecte obtient une session, qui
  n'atteint rien sans grant (`/api/status` répond `403`) mais charge quand même le
  launcher.
- **Le réseau.** Les utilisateurs ne doivent pas atteindre directement les démons et les
  backends de traces : le contrôle d'accès couvre ce que sert le Hub.
- **Un proxy devant le Hub** ne doit ni injecter ni transmettre d'en-tête
  `Authorization`. Une requête qui porte un bearer est jugée sur ce bearer seul : un proxy
  qui envoie son propre jeton (oauth2-proxy avec `--pass-authorization-header` ou
  `--set-authorization-header`, par exemple) transforme chaque requête du navigateur en
  `401`.

## Le modèle

### Sujet et identité

Chaque appelant a deux chaînes. Le **sujet** est la clé d'autorisation : `Admins` et les
membres des groupes internes listent des sujets, et rien d'autre n'est comparé.
L'**identité** est le nom que les gens lisent : la barre du haut, `/api/status`, le
`requested_by` d'un run et le `by` d'un ack. Rien n'est autorisé sur l'identité : un
utilisateur qui se renomme chez le fournisseur change une étiquette, pas ses droits.

| Appelant              | Sujet                                 | Identité                                | Groupes                                                  |
|-----------------------|---------------------------------------|-----------------------------------------|----------------------------------------------------------|
| Session du navigateur | valeur de `Hub:Auth:SubjectClaim`     | valeur de `Hub:Auth:IdentityClaim`      | groupes du fournisseur à la connexion, groupes internes  |
| Jeton machine         | `machine:<nom>`                       | `machine:<nom>`                         | son seul groupe configuré                                |
| Compte local          | `local:<nom>`                         | `local:<nom>`                           | groupes internes seuls                                   |

Une session du navigateur porte aussi le nom que le fournisseur donne à l'utilisateur, lu
dans `Hub:Auth:DisplayNameClaim` (par défaut `name`). Il s'affiche comme responsable d'une
action et ne sert jamais à l'accès.

`Hub:Auth:SubjectClaim` (par défaut `sub`) n'accepte que `sub`, `oid`, `uuid` et
`account_id`, des identifiants qu'un fournisseur ne réattribue jamais. `sub` convient à
Keycloak, Google, GitLab et Entra ID ; Bitbucket Cloud n'a pas de `sub` et prend
`account_id`. Un userinfo dont le champ sujet manque, n'est pas une chaîne, est vide ou
dépasse 256 caractères fait échouer la connexion. De même pour un sujet ou une identité
qui commence par `local:` ou `machine:` (casse ignorée) : ces préfixes sont réservés.

### Groupes

Les **groupes du fournisseur** sont lus à la connexion dans le champ du userinfo que nomme
`Hub:Auth:GroupsClaim`, celui que lit `RequiredGroups`, avec les mêmes règles : une liste
de chaînes ou une chaîne seule. La session ne garde que les groupes du fournisseur qu'un
grant ou `AdminGroups` nomme, jamais une valeur qui commence par `internal:`. Les noms
écartés sont journalisés en Debug (événement `1902`), ce qui montre l'orthographe exacte
qu'envoie le fournisseur : `acme/shop` chez GitLab, `/shop` chez Keycloak avec *Full group
path* activé.

Un groupe du fournisseur dans un grant ou dans `AdminGroups` doit être un nom que seule
l'organisation peut créer : le chemin complet sous son propre groupe chez GitLab
(`acme/hub-admins`, jamais un `hub-admins` nu), le chemin complet du groupe chez Keycloak,
et un champ `GroupsClaim` que les utilisateurs ne peuvent pas modifier. Là où les
utilisateurs créent des groupes de n'importe quel nom (gitlab.com, et GitLab auto-hébergé
par défaut), n'importe qui pourrait créer un groupe de premier niveau du bon nom : une
entrée du fournisseur dans `AdminGroups` n'y est sûre qu'avec `Hub:Auth:RequiredGroups`
réglé sur le groupe de l'organisation.

Les **groupes internes** (`Hub:Access:Groups`) sont déclarés dans les values, référencés
`internal:<nom>`, et listent des sujets, `local:<nom>` pour un compte local.
L'appartenance est lue à chaque requête.

### Projets

Un projet est une liste de motifs ; un enregistrement appartient au projet quand un motif
lui correspond. Un motif renseigne un ou plusieurs de :

- `source` : un identifiant de source configurée ;
- `service` : un nom de service ;
- `attribute` : `clé=valeur`, découpé au premier `=`, comparé aux attributs de
  regroupement qu'un démon a capturés. Une clé n'atteint le Hub que depuis les démons dont
  `[detection] grouping_attributes` la liste, voir [DEPLOYMENT-FR.md](DEPLOYMENT-FR.md).

Une partie est exacte, ou un préfixe qui se termine par un seul `*` (`checkout-*`). Les
comparaisons sont exactes, casse comprise. Un `*` seul n'est pas un motif : `*` est une
cible de grant, qui couvre chaque enregistrement, y compris ceux qu'aucun projet ne
couvre.

Un finding est comparé source par source : sa copie dans chaque source a le service propre
à cette source et les attributs que cette source a remontés. Un finding est listé quand
l'une de ses copies est dans le périmètre, et la réponse est construite à partir des
seules copies dans le périmètre.

### Rôles et permissions

| Permission      | Permet                                                                                 |
|-----------------|----------------------------------------------------------------------------------------|
| `findings.read` | Les findings, les incidents, le côté lecture d'un ack, le rapport d'un run de démon    |
| `traces.read`   | Les runs sur un backend de traces et leurs rapports                                    |
| `sources.read`  | La vue des réglages d'un démon, et avec `findings.read` et `analyses.run` un run de démon ; source seulement |
| `analyses.run`  | Lancer un run                                                                          |
| `acks.write`    | Acquitter un finding, ou révoquer un ack                                               |
| `actions.write` | Déclarer et modifier une action sur un finding                                         |
| `labels.write`  | Nommer un endpoint ou un service en mots du métier                                     |
| `cards.write`   | Créer une carte à partager à partir des sources qu'elle couvre                         |
| `disclosures.read` | L'onglet Disclosures, le détail de chaque divulgation et ses fichiers               |
| `disclosures.manage` | Les liens de partage, les retraits, les relances de livraison et les rendus d'une divulgation |
| `recaps.read`   | Lire tous les rapports de récap : la liste, un rapport et sa page (niveau Hub)         |

| Rôle intégré | Permissions                                                  |
|--------------|--------------------------------------------------------------|
| `reader`     | `findings.read`, `traces.read`, `analyses.run`               |
| `member`     | `reader` plus `acks.write`, `actions.write`, `labels.write` et `cards.write` |
| `admin`      | Tout, sur `*` seulement, plus la configuration de l'accès    |

`reader` n'est pas en lecture seule : il lance des analyses. Un rôle en lecture seule se
configure, par exemple `auditor: [findings.read, traces.read]`.

`sources.read` est au niveau de la source : il ne compte que sur `*` ou par un motif qui
ne renseigne que `source`. De même pour `analyses.run` et `findings.read` pour un run de
démon.

`disclosures.read`, `disclosures.manage` et `recaps.read` sont au niveau du Hub : une
divulgation ou un rapport de récap n'appartient à aucune source. Le rôle intégré `admin` les porte, `reader` et `member` jamais,
et un jeton machine ne les détient jamais. Un rôle configuré peut les porter, attribué sur `*`
seulement : le Hub refuse de démarrer sur l'attribution d'un tel rôle à un projet.

### Grants et admins

Un grant donne à un groupe un rôle sur un projet ou sur `*`. Les admins détiennent chaque
permission sur `*` : les sujets de `Admins` (la voie de secours quand un groupe d'admins
du fournisseur se casse), les groupes de `AdminGroups`, et les groupes qui ont le rôle
intégré `admin` sur `*`. Un [compte local](#comptes-locaux) listé là, ou dans un groupe
d'admins, est la voie qui ne dépend en rien du fournisseur.

### Ce que voit un appelant

| Route                                                                  | Exige                                                              | Refus                       |
|------------------------------------------------------------------------|--------------------------------------------------------------------|-----------------------------|
| `/` et les fichiers du launcher                                        | n'importe quel appelant                                            | redirection ou `401`        |
| `GET /api/status`, `GET /api/sources`                                  | une permission quelque part                                        | `403`                       |
| `GET /api/findings`, `GET /api/findings/{traceId}`                     | `findings.read`, filtré                                            | `403` si détenue nulle part |
| `GET /api/recap`                                                       | `findings.read`, filtré                                            | `403` si détenue nulle part |
| `GET /api/recap/reports`, `GET /api/recap/reports/{id}`, `GET /recaps/{id}/{view}.html` | `recaps.read` sur `*`, ou le créateur d'un rapport à la demande avec `findings.read` | `403` (liste), `404` (un rapport) |
| `POST /api/recap/reports`                                              | `findings.read`, filtré, et quelqu'un à nommer                     | `403`                   |
| `GET /api/cards/keys`, `POST /api/cards/keys/rotate` et `/revoke`     | `access.admin`, et quelqu'un à nommer                              | `403`                   |
| `GET /api/incidents`, `GET /api/incidents/{id}`                        | `findings.read`, filtré ; la copie la plus riche dans le périmètre | `403` ; `404`               |
| `POST /api/incidents/refresh`                                          | `findings.read` ; ne lit que les démons dans le périmètre          | `403`                       |
| `GET /api/correlations`                                                | `findings.read`, filtré sur les épisodes dont l'appelant lit les deux services à cette source | `403` s'il n'est détenu nulle part |
| `GET /api/sources/{sourceId}/daemon`                                   | `sources.read` sur cette source entière                            | `404`                       |
| `POST /api/sources/{sourceId}/acks` et `/acks/revoke`                  | `findings.read` et `acks.write` sur le finding dans cette source   | `404`, `403`                |
| `POST` et `PATCH /api/actions`                                         | `findings.read` et `actions.write` sur une copie du finding, et quelqu'un à nommer | `404`, `403` |
| `PUT /api/labels`                                                      | `findings.read` et `labels.write` sur une copie d'un finding de l'endpoint, et quelqu'un à nommer | `404`, `403` |
| `PUT /api/labels/services`                                             | `findings.read` et `labels.write` sur une copie d'un finding du service, et quelqu'un à nommer | `404`, `403` |
| `POST /api/analyses`                                                   | `analyses.run` et la règle de lecture du run, ci-dessous           | `403`                       |
| `GET /api/analyses`, `GET /api/analyses/{id}`                          | la règle de lecture du run                                         | filtré ; `404`              |
| `GET /reports/{id}.html`                                               | la règle de lecture du run                                         | `404`                       |
| `GET /metrics`                                                         | anonyme, sur `Hub:MetricsPort` seulement                           | `404` ailleurs              |
| `GET /api/access`                                                      | tout appelant, même sans grant ; davantage pour un admin, plus bas |                             |
| `GET /api/disclosures`, `GET /api/disclosures/{id}` et ses fichiers    | `disclosures.read` sur `*`                                         | `403`                       |
| `POST` et `DELETE` sous `/api/disclosures/{id}/`                       | `disclosures.manage` sur `*`, et quelqu'un à nommer                | `403`                       |
| `GET /share/{token}/{name}`                                            | anonyme, le jeton seul                                             | `404`                       |
| `POST /api/import/findings`, `POST /api/import/incidents`, `POST /api/import/correlations`, `POST /api/import/detections`, `POST /api/import/disclosures`, `/health/*` | inchangé                                                           |                             |

`GET /api/sources` ne liste que les sources où l'appelant détient quelque chose, chacune
avec un tableau `permissions` (absent sans contrôle d'accès). Une source absente de cette
liste reçoit partout la réponse d'une source inconnue, `400` sur un filtre et `404` sur
une route.

### Codes de statut

- **`401`** : aucun identifiant valide. Un bearer refusé reçoit aussi
  `WWW-Authenticate: Bearer error="invalid_token"`.
- **`404`**, avec le corps que reçoit un enregistrement absent : un enregistrement hors du
  périmètre de lecture de l'appelant, que la requête lise ou écrive.
- **`403 {"detail": "…"}`** : un enregistrement que l'appelant lit mais ne peut pas
  écrire ; une action refusée à cause de qui est l'appelant (une machine qui écrit, le
  lancement d'une analyse, aucune permission du tout, un listing dont la permission n'est
  détenue nulle part) ; une écriture depuis un autre site.
- Un listing répond `403` plutôt que `[]` quand sa permission n'est détenue nulle part :
  une porte de CI dont le jeton n'a aucun grant échoue bruyamment au lieu de passer sur
  une liste vide.

Une écriture depuis un navigateur doit porter `Sec-Fetch-Site: same-origin`, que tout
navigateur actuel envoie : une page sur un sous-domaine voisin ne peut pas poster avec le
cookie de l'utilisateur. Une requête avec un bearer n'est pas celle d'un navigateur et
n'y est pas tenue. Chaque réponse porte `Content-Security-Policy: frame-ancestors 'self'` :
aucun autre site ne peut encadrer le launcher et cliquer sur ses boutons.

### Analyses et rapports

Un run se lit, et son rapport s'ouvre, sous une seule règle :

| Run                          | Règle de lecture                                              | Le lancement exige aussi        |
|------------------------------|---------------------------------------------------------------|---------------------------------|
| Démon D                      | `findings.read` et `sources.read` sur D entier                | `analyses.run` sur D            |
| Backend de traces, service X | `traces.read` qui couvre X sur ce backend                     | `analyses.run` de même          |
| Backend de traces, trace ID  | `traces.read` sur le backend entier                           | `analyses.run` de même          |

Un run de service est couvert quand la `source` d'un motif (s'il y en a une) correspond au
backend, son `service` (s'il y en a un) correspond à X, et, pour un motif d'attribut,
quand chaque copie de X que le Hub détient dans l'environnement du backend porte cet
attribut, avec au moins une copie. Le moteur n'exporte aucun regroupement pour un span :
ces copies sont la seule preuve de l'équipe à qui appartient un service. Un run sur un
trace ID exige le backend entier : un grant de service ne peut pas faire récupérer au Hub
une trace arbitraire.

Il n'y a pas de clause du demandeur : qui a lancé un run le lit tant que son grant tient.

**Un rapport de traces montre chaque service que ses traces traversent**, avec les
comptes de `result`. Le moteur écrit le rapport et le Hub ne peut pas le filtrer, et les
utilisateurs doivent voir d'où vient un problème, même dans le service d'une autre équipe.
Ne donnez `traces.read` qu'à ceux qui peuvent voir des traces d'autres équipes ; les rôles
intégrés `reader` et `member` la portent.

Avec l'accès activé, un rapport que rend le Hub est statique : un rapport live acquitterait
directement auprès du démon, en contournant `acks.write`.

### Acquittements

Un ack est jugé sur la seule copie du finding dans cette source. Un appelant qui ne peut
pas lire la copie reçoit le `404` d'un finding inconnu ; celui qui la lit sans pouvoir
l'écrire reçoit `403`. Un appelant qui ne détient `acks.write` nulle part dans la source
est refusé avant la lecture du corps de la requête.

`acks.write` par un motif d'attribut exige que chaque regroupement que le Hub a enregistré
pour cette copie porte l'attribut avec une valeur qui correspond. Le démon indexe un ack
sur la signature, aveugle aux regroupements : un ack fait aussi taire des regroupements
que le Hub n'a jamais observés et ceux qui apparaîtront. **Un ack fait par un grant
d'attribut peut faire taire le même finding pour le déploiement d'une autre équipe sur ce
démon**, jusqu'à son expiration ou sa révocation. Le Hub n'impose aucune expiration. Là où
plusieurs équipes peuvent émettre le même nom de service, donnez à l'équipe propriétaire
un grant de source ou de service pour `acks.write` et `analyses.run`.

### Actions

Une action est globale à la signature, comme un ack auprès de son démon : elle est donc jugée
sur chaque copie du finding que l'appelant lit, dans n'importe quelle source, et permise
quand l'une d'elles est inscriptible. Un appelant qui n'en lit aucune reçoit le `404` d'un
finding inconnu ; celui qui en lit mais n'en peut écrire aucune reçoit `403`. Un appelant qui
ne détient `actions.write` nulle part est refusé avant la lecture du corps de la requête, et
un appelant que le Hub ne sait pas nommer l'est avant tout. Contrairement à un ack, une
action ne touche aucun démon et ne fait rien taire : c'est une note que tout le Hub partage
sur qui travaille sur le finding.

### Libellés d'endpoint

Un libellé est global à un couple `(service, endpoint)` : il est donc jugé sur chaque copie de
chaque finding de cet endpoint que l'appelant lit, dans n'importe quelle source, et permis
quand l'une d'elles est inscriptible sous `labels.write`. Un appelant qui n'en lit aucune
reçoit le `404` d'un endpoint inconnu ; celui qui en lit mais n'en peut écrire aucune reçoit
`403`. Un appelant qui ne détient `labels.write` nulle part est refusé avant la lecture du
corps de la requête, et un appelant que le Hub ne sait pas nommer l'est avant tout. Un
libellé ne touche aucun démon : c'est un nom que tout le Hub partage, pour le recap et les
cartes qui le citent. Le nom d'un service, `PUT /api/labels/services`, se juge de la même façon
sur chaque copie de chaque finding de ce service.

### Votre accès et Couverture

Deux écrans du lanceur lisent `GET /api/access` ([API-FR.md](API-FR.md#get-apiaccess)).
**Votre accès**, derrière la pastille d'identité, montre à chaque appelant qui il est, son
identifiant (le sujet à mettre dans `Admins` ou `Members`), ses groupes, ce qu'atteignent
ses grants et ses propres jetons de terminal. Un appelant qu'aucun grant n'atteint reçoit
le même écran, avec un bloc à copier pour un administrateur. **Couverture**, un onglet
réservé aux admins, commence par chaque service, valeur d'attribut et source qu'aucun
projet ne couvre dans `Hub:Retention`, pour repérer un nouveau venu et l'affecter dans la
configuration, avec un motif YAML par entrée prêt à coller. Viennent ensuite les conflits
ci-dessous, chaque jeton de terminal vivant et toute la configuration, repliée par partie.
Aucun des deux écrans n'écrit rien des droits : ils changent dans la configuration
(valeurs Helm `hub.access`) et s'appliquent au redémarrage suivant. Leur seule écriture
est le bouton **Revoke** des tableaux de jetons de terminal, qui demande un second clic.

### Lire les conflits

Un conflit est une copie de finding qu'un motif d'attribut d'un projet lit sans pouvoir
l'écrire. Pour une clé qu'utilise un motif de projet, la copie en porte deux valeurs
(`values` les liste), ou a été vue sous un regroupement où la clé manque
(`identities_without_key` compte ces regroupements, les observations sans regroupement
comprises). Deux déploiements d'un même service rattachés à des équipes différentes, ou
des spans émis sous le nom de service d'une autre équipe, en produisent un. Sur ces
copies, un grant `acks.write` passant par l'attribut est refusé (`403`) alors que la copie
reste lisible. Répondez-y par un grant de source ou de service pour
l'équipe propriétaire du service, ou en corrigeant l'attribut de regroupement au démon.

## Jetons machine

Un jeton machine est un identifiant pour un job de CI ou un tableau de bord. Il détient au
plus `findings.read`, par les grants de son groupe : quoi que disent les grants, une
machine n'acquitte jamais, ne lance aucun run et n'ouvre aucune vue de démon.

```yaml
hub:
  access:
    groups:
      - { name: ci-billing, members: [] }   # le groupe d'un jeton n'a aucun membre
    roles:
      - { name: ci-reader, permissions: [findings.read] }
    grants:
      - { group: "internal:ci-billing", project: billing, role: ci-reader }
    machineTokens:
      - { name: ci-billing, group: "internal:ci-billing", expiresAt: "2027-06-30T00:00:00Z",
          secretName: perf-sentinel-hub-machine-tokens, secretKey: ci-billing }
```

Le jeton lui-même vit dans le Secret, 32 à 512 caractères (`A-Z a-z 0-9 - . _ ~ + /`, `=`
final permis), sans jamais commencer par `pshub_`. Le chart le passe au pod en
`Hub__Access__MachineTokens__<index>__Token` ; le Hub n'en garde que l'empreinte SHA-256.

```sh
curl -fsS -H "Authorization: Bearer $HUB_TOKEN" \
  "https://hub.example.com/api/findings?environment=production&status=active"
```

Un jeton sans grant reçoit `403` sur `/api/findings` : une porte construite dessus échoue
au lieu de passer sur rien. Un jeton inconnu ou expiré reçoit `401`. Une entrée expirée
est journalisée au démarrage (événement `2001`) et n'arrête jamais le Hub.

- **Révoquer** : supprimer l'entrée. Le ConfigMap change et le pod redémarre.
- **Faire tourner** : mettre à jour le Secret, puis
  `kubectl rollout restart deployment/<release>`.
- **Un jeton par équipe**, avec les seuls projets de cette équipe. Un jeton dans un outil
  partagé sert tout son périmètre à chaque utilisateur de l'outil : une datasource Grafana
  qui tient un jeton sur `*` rouvre chaque finding à chaque utilisateur de Grafana. Un
  jeton copié dans les IDE des développeurs est un identifiant partagé lié à aucun compte,
  qui survit aux départs.

## Jetons de terminal

Un jeton de terminal (`pshub_…`) est l'identifiant par personne : `perf-sentinel login`
en obtient un par le flux d'appareil du Hub, approuvé dans un navigateur connecté, comme
le décrit [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md#connexion-du-terminal). Il est
envoyé en `Authorization: Bearer`, comme un jeton machine, et il est jugé comme la
personne qui l'a approuvé :

- **Droits.** Le sujet et les groupes du fournisseur de la session qui approuve, figés à
  l'approbation, avec les groupes internes et les grants lus à chaque requête.
  Contrairement à un jeton machine il écrit aussi : acks et analyses sont enregistrés
  sous l'identité de la personne.
- **Jamais admin.** Pas même approuvé par un admin ; `GET /api/access` rapporte
  `"caller": "terminal_token"` et `"admin": false`.
- **`RequiredGroups`.** Le jeton garde le groupe qui a admis la session, et il est refusé
  dès que ce groupe quitte `Hub:Auth:RequiredGroups`, comme une session.
- **Jamais un navigateur.** Un jeton n'approuve aucune connexion et ne liste ni ne
  révoque aucun jeton : ces routes lisent le seul cookie de session.
- **Durée.** Jusqu'à la fin qu'aurait eue la session qui approuve, 8 heures au plus ; au
  plus 10 par personne.

La connexion du terminal n'est offerte qu'avec `Hub:Auth:TrustedProxyNetworks` renseigné,
car la page d'approbation et les limites par adresse ont besoin de l'adresse propre de
chaque client.

## Comptes locaux

`Hub:Access:LocalAccounts` déclare des comptes de secours pour les pannes du fournisseur,
chacun `{ Name, PasswordHash }`, le hash livré depuis un Secret. En déclarer un place la
page de connexion du Hub devant l'authentification unique, si bien qu'une requête de page
n'atterrit jamais chez un fournisseur qui ne répond pas. Le compte se connecte en tant
que `local:<nom>`, atteint ce que lui donnent les groupes internes qui listent ce sujet et
`Admins`, et n'est pas soumis à `Hub:Auth:RequiredGroups`. Le Hub refuse de démarrer
quand un compte n'est listé nulle part, ou quand `Hub:Auth:TrustedProxyNetworks` est
vide : le frein s'appuie sur l'adresse cliente. La page, le frein, le hash et la
journalisation sont dans [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md#comptes-locaux) ;
créer et changer un mot de passe dans
[OPERATIONS-FR.md](OPERATIONS-FR.md#comptes-locaux-de-secours).

## Sessions

Avec l'accès activé, une session prend fin 8 heures après la connexion, quel que soit son
usage, comme sous `RequiredGroups`. Une session locale aussi, et un nouveau hash pour son
compte y met fin au redémarrage qui le charge. Les groupes du fournisseur sont lus à la connexion :
un groupe qu'un utilisateur rejoint ou quitte chez le fournisseur s'applique à sa
connexion suivante, 8 heures plus tard au plus. Tout le reste, groupes internes, admins,
grants et `RequiredGroups`, est lu à chaque requête : une modification s'applique au
redémarrage qui la déploie, sessions ouvertes comprises. Une session ouverte avant
l'activation de l'accès ne porte aucun sujet et se reconnecte.

## Limites connues

- Un N+1 ou un groupe redondant qui traverse des services est listé sous le service du
  premier span : le projet d'un autre service ne le voit pas.
- L'enveloppe la plus récente d'un finding peut décrire un autre regroupement du même
  service et du même endpoint que celui qu'un projet a reconnu.
- Quiconque peut envoyer de l'OTLP à un démon peut fixer un attribut ou un nom de service.
  La comparaison se fait par copie : une valeur plantée n'expose que les copies de la
  source où elle a été plantée, mais des spans sous le nom de service d'une autre équipe
  avec une autre valeur refusent à cette équipe ses écritures par attribut (acks et runs)
  tant qu'ils arrivent, plus `Hub:Retention`.
- Une copie enregistrée avant la version 9 du schéma ne correspond à aucune partie
  d'attribut ni de service tant que sa source ne la remonte pas à nouveau.
- `max_confidence` et `lineage` dans une enveloppe restent à l'échelle de la flotte.
- Le `/metrics` propre d'un démon, et tout tableau de bord qui lit un démon ou un backend
  avec son propre identifiant, sont hors du modèle.

Voir [LIMITATIONS-FR.md](LIMITATIONS-FR.md#ce-que-le-contrôle-daccès-ne-couvre-pas) pour
chacune en détail, avec les rapports de traces, la fraîcheur et ce qui reste hors du modèle.

## Revenir en arrière

Avec l'accès activé, ne revenez jamais à une image antérieure à la version qui
l'applique. Les versions précédentes ignorent `Hub:Access` et **s'ouvrent** :
`/api/findings` redevient anonyme et chaque session voit chaque enregistrement
(`RequiredGroups` tient toujours, jusqu'à la version qui l'a ajouté). Réglez d'abord
`hub.access.enabled: false`, comme une décision, puis revenez en arrière. Voir
[OPERATIONS-FR.md](OPERATIONS-FR.md#revenir-en-arrière-avec-le-contrôle-daccès-activé).

## Un exemple complet

```yaml
hub:
  metricsPort: 9464
  auth:
    enabled: true
    # Endpoints et client comme dans AUTHENTICATION-FR.md.
    identityClaim: email
    subjectClaim: sub
    groupsClaim: groups                       # mapper Keycloak avec "Full group path" activé
    requiredGroups: ["/perf-sentinel-hub"]
    trustedProxyNetworks: ["10.42.200.0/28"]  # les seuls pods du contrôleur d'ingress, pour perf-sentinel login
  access:
    enabled: true
    admins: ["2f6d0c1e-8a4b-4e2f-9c71-0d3b5a6e7f81"]   # un sub : voie de secours
    adminGroups: ["/hub-admins", "internal:break-glass"]
    groups:
      - { name: break-glass, members: ["local:ops"] }   # un compte local, pour une panne du fournisseur
    localAccounts:
      - { name: ops, secretName: perf-sentinel-hub-break-glass, secretKey: ops }
    projects:
      - { name: shop,    patterns: [ { attribute: "team=shop" } ] }
      - { name: billing, patterns: [ { service: "invoice-*" } ] }
      - { name: lab,     patterns: [ { source: lab-daemon } ] }
    roles:
      - { name: auditor, permissions: [findings.read, traces.read] }
    grants:
      - { group: /teams/shop,    project: shop,    role: member }
      - { group: /teams/billing, project: billing, role: member }
      - { group: /teams/lab,     project: lab,     role: member }
      - { group: /leads,         project: "*",     role: auditor }
```

Mettez entre guillemets `"*"`, `"internal:…"` et `"team=…"` dans les mappings YAML en
ligne.
