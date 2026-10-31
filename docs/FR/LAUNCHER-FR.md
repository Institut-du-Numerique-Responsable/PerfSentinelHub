# Le lanceur

Le Hub sert une interface de navigateur sur `/`, depuis la même origine que les rapports
qu'elle ouvre. HTML, CSS et JavaScript simples. Sans framework, sans étape de build et
sans requête réseau : les deux polices sont en base64 dans `wwwroot/fonts.css` et chaque
icône est un SVG en ligne.

Six écrans : démarrer une analyse, suivre un run, lister les runs récents, lire la santé
de la flotte, lire les incidents enregistrés par les daemons, suivre les corrélations
qu'ils ont poussées. Avec `Hub:Access` actif, la
pastille d'identité en haut à droite ouvre [Votre accès](#votre-accès), et un
administrateur dispose d'un septième onglet, [Couverture](#lécran-couverture). Un onglet
Disclosures apparaît quand `/api/status` dit `disclosures`, c'est-à-dire tant que les
divulgations sont actives et que l'appelant les lit, voir
[L'écran des divulgations](#lécran-des-divulgations). Un autre écran n'a pas d'onglet et ne
s'atteint que par un lien, voir [La page d'acquittement](#la-page-dacquittement).

Chaque changement de route nomme l'écran dans l'onglet du navigateur et place le focus sur
son titre, pour qu'un lecteur d'écran annonce où le lecteur est arrivé. Sous 1400 px, les
icônes des onglets et le libellé du thème s'effacent pour que la barre tienne sur une
ligne, et sous 760 px les onglets prennent une ligne à eux qui défile latéralement au lieu
de la page.

## Le formulaire suit la source

Le formulaire s'adapte au `kind` de la source sélectionnée plutôt que d'offrir un
sélecteur indépendant entre live et historique. Un tel sélecteur laisserait un opérateur
composer des états impossibles, comme une fenêtre de trois heures contre un daemon qui en
garde dix minutes.

Un lien depuis l'écran des incidents arrive ici avec le formulaire rempli : le service de
l'incident, et sa fenêtre en plage absolue dont la fin est retenue à maintenant. Un bandeau
au-dessus des paramètres nomme l'incident, sous la forme `ns/service` quand il portait un
namespace, et dit qu'une analyse prend un service et aucun namespace, donc qu'elle couvre
ce service dans chacun d'eux. La source ne fait pas partie du lien, puisqu'un
daemon ne prend aucune fenêtre : sous un daemon le bandeau le dit et demande un backend de
traces.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-sources-dark.png">
  <img alt="L'écran de santé de la flotte avec une ligne daemon dépliée" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-sources.png">
</picture>

## Les jauges

Une jauge est teintée dès qu'elle approche d'un plafond qu'elle a publié. Rouge à partir
de 90 %, la ligne du conseiller du moteur lui-même et celle qui fait passer le verdict
d'une ligne à "près du plafond". Ambre à partir de 75 %, le palier propre au Hub, un cran
avant. L'anneau de findings n'est jamais teinté et ne change jamais un verdict : il se
remplit sur tout daemon qui tourne assez longtemps, et le Hub garde ce qu'il évince. Quand
un poll constate que certains en sont sortis avant qu'il ait pu les lire, les conseils le disent.

Chaque lecture montre ce qui a bougé depuis la précédente, montant depuis le chiffre
auquel cela se rapporte puis s'effaçant : rouge pour une hausse, vert pour une baisse.
Chacun de ces chiffres compte vers un plafond, donc la hausse est la direction qui coûte
quelque chose. L'uptime n'est ni teinté ni suivi, n'ayant pas de plafond et une seule
direction possible.

## Ce que le navigateur retient

Les replis qu'un lecteur a ouverts, dans le `localStorage` de ce navigateur, sous une
seule clé et comme replis ouverts seulement. Une ligne, son bloc terminal, ses réglages et
les groupes qu'ils contiennent reviennent tels qu'ils ont été laissés, et une ligne
laissée ouverte relit son daemon à la visite suivante sans qu'on la clique.

Rien d'autre que ces noms n'est stocké. Un navigateur qui refuse le stockage démarre
simplement tout replié.

Le thème est à trois positions : système, clair, sombre. Seul le clair ou le sombre résolu
atteint le DOM, de sorte que les feuilles de style voient deux valeurs et jamais trois. La
position est stockée sous `perf-sentinel:theme` dans `localStorage` et dans
`sessionStorage`, le second parce que le dashboard rendu lit cette clé exacte depuis cette
origine. C'est ce passage de relais qui impose que le lanceur et les rapports partagent
une origine.

## La langue

Le lanceur se lit en anglais ou en français. Le bouton FR/EN de la barre du haut montre la langue de
l'écran et glisse au survol vers l'autre, celle qu'un clic affiche. Le changement redessine l'écran sur
place : la route, les filtres et un formulaire à moitié rempli restent tels quels. Une première visite
suit le navigateur, la première variante française ou anglaise de sa liste de langues, sinon l'anglais.
Un clic stocke le choix sous `perf-sentinel:lang` dans `localStorage`, et dès lors le choix stocké
l'emporte sur le navigateur. Un navigateur qui refuse le stockage garde le choix tant que la page est
ouverte.

Les nombres, les dates et les durées suivent la langue : `1,937,000`, `3.1%` et `2026-10-29 14:05`
s'écrivent `1 937 000`, `3,1 %` et `29/10/2026 14:05` en français. Ce que le moteur écrit reste en
anglais : les recommandations du daemon, les avertissements d'un run, le correctif suggéré d'un
finding, les limites d'une divulgation et la raison pour laquelle une application en a été écartée.
Chacun de ces textes porte `lang="en"`, pour qu'un lecteur d'écran le lise en anglais, et en français
une petite étiquette EN le précède. Le rapport d'analyse est la page du moteur et reste en anglais lui
aussi. Chaque commande imprimée garde la syntaxe du moteur dans les deux langues, pour qu'une commande
copiée s'exécute de la même façon.

## Les commandes imprimées

Chaque commande imprimée porte un onglet par shell, parce que la différence n'est pas
cosmétique. Un shell POSIX continue une ligne par une barre oblique inverse et échappe une
apostrophe en fermant puis rouvrant. PowerShell continue par un accent grave et double
l'apostrophe, et son ensemble de mots nus est plus étroit puisque la virgule y est
l'opérateur de tableau.

L'onglet qu'ouvre une première visite suit la plateforme, Windows recevant PowerShell. Le
choix propre du lecteur est retenu ensuite et s'applique d'un coup à toutes les commandes
de la page.

Aucune des deux commandes ne porte de valeur d'exemple. Le point d'accès est le `PublicUrl`
configuré de la source, ou son `BaseUrl` quand elle n'en déclare pas, et la commande du monitor porte l'intervalle de relecture choisi
sur cette ligne, de sorte qu'une ligne copiée est exécutable telle quelle et ne contredit
pas l'écran d'où elle vient. La seule chose qu'un opérateur tape encore est le nom de
service, qui lui appartient et qui est montré vide plutôt que deviné.

Les deux commandes disent où obtenir le moteur, puisque ni l'une ni l'autre ne passe par
le Hub. La note lie la release de la version exacte que ce Hub fait tourner, celle pour
laquelle les flags sont orthographiés. Sans version sondée, le lien retombe sur la liste
des releases plutôt que d'inventer un tag.

### Le run en ligne de commande

Le lanceur imprime le run sous forme de ligne de commande du moteur, pour qu'un opérateur
puisse l'emporter dans un terminal. Elle est bâtie depuis l'objet même que le formulaire
poste, jamais depuis le formulaire, de sorte que la commande imprimée et le run soumis ne
peuvent pas diverger. Le point d'accès est la seule différence voulue : une source qui
déclare un `PublicUrl` l'imprime, tandis que le Hub lance le run sur son `BaseUrl`.

C'est une commande et non les deux que le Hub lance : la sortie JSON et l'étape de rendu
existent pour que le Hub puisse bâtir un dashboard, et un terminal n'a besoin ni de l'une
ni de l'autre.

Les valeurs sont protégées pour un shell POSIX par des apostrophes simples, la seule forme
qui tienne pour un nom de service portant un `$` ou une apostrophe. Une source
authentifiée imprime `--auth-header-env` plutôt que son jeton, que le Hub détient et ne
divulgue jamais. L'en-tête nommé est le `PublicAuthHeaderName` de la source quand elle en
déclare un, voir [CONFIGURATION-FR.md](CONFIGURATION-FR.md).

Les surcharges de détection n'ont pas de flag en ligne de commande, donc un run qui en a
changé une porte `-c perf-sentinel.toml`, et le fichier est imprimé à côté de la commande,
prêt à copier ou à télécharger. Le nom est sans point parce que le moteur ne découvre que
le `.perf-sentinel.toml` pointé, qu'un téléchargement peut ne pas préserver, donc la
commande nomme le fichier au lieu de s'en remettre à cette découverte.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-report-dark.png">
  <img alt="Un rapport rendu, ouvert dans le lanceur" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-report.png">
</picture>

## Les rapports live

Un rapport rendu depuis une source daemon qui a un identifiant d'acquittement devient live
à travers ce Hub : le rendu passe `--hub-relay` (moteur 0.31.0 ou plus récent), et les
contrôles Ack et Revoke du dashboard appellent le [relais d'acquittement](API-FR.md#relais-dacquittement)
à l'origine même du Hub, au nom du lecteur et selon ses droits. Aucune clé du daemon
n'arrive dans le navigateur, le daemon n'a besoin d'aucune entrée CORS, et le lecteur ne
le joint jamais. C'est vrai avec ou sans contrôle d'accès.

Sinon, sans contrôle d'accès, un rapport devient live directement sur le daemon quand le `PublicUrl` de ce daemon,
ou son `BaseUrl` quand il n'en déclare pas, est une origine nue. Le rendu passe `--daemon-url`, et les contrôles de rafraîchissement et
d'acquittement du dashboard parlent alors à ce daemon depuis le navigateur du lecteur.

Deux conditions se trouvent hors du Hub : le `[daemon.cors] allowed_origins` du daemon
doit porter l'origine depuis laquelle ce Hub sert ses rapports, et le lecteur doit pouvoir
joindre le daemon directement. Un Hub qui polle son daemon par un nom de Service a besoin
pour cela d'un `PublicUrl`, voir [CONFIGURATION-FR.md](CONFIGURATION-FR.md).

Un daemon derrière un ingress à préfixe de chemin reçoit un rapport statique, parce que le
flag du moteur prend une origine et rien d'autre. Il en va de même de toute source daemon
quand le binaire configuré ne prend pas `--daemon-url` du tout : le moteur le déclare à
l'intérieur de sa feature `daemon`, donc un binaire bâti sans elle refuse l'argument au
lieu de l'ignorer, et un run qui le passerait ne rendrait rien. Le Hub interroge le
binaire une fois au démarrage, par `report --help`, et rend en statique quand la réponse
est non ou illisible.

Les liens de rapport se partagent à portée réseau du Hub et meurent avec la fenêtre de
rétention.

## La ligne de santé de la flotte

La santé d'une ligne indique `reachable`, `not yet observed` avant le premier poll, ou
`unreachable` avec sa durée. `Hub database full` remplace ce dernier quand le poll a
échoué sur le volume du Hub lui-même plutôt que sur le daemon, et un daemon qui crée
sans cesse de nouvelles signatures affiche son compte de la dernière heure en ton
d'avertissement tant qu'il répond.

La ligne d'un daemon se déplie sur les jauges qu'il rapporte face à leurs plafonds et les
conseils qu'il écrit sur son propre tuning, dont `/metrics` ne porte ni les uns ni les
autres.

La ligne relit à un intervalle que le lecteur choisit, le même réglage que porte
`query monitor --refresh` plus une position éteinte. Une lecture ne remplace que les
jauges et les conseils : les réglages ne changent pas sans redémarrage, donc les
reconstruire jetterait des groupes ouverts pour rien. Replier la ligne arrête les
lectures.

Les réglages eux-mêmes sont un clic plus loin, groupés et repliés, chaque groupe montrant
combien de ses valeurs s'écartent des défauts du moteur. La ligne se termine par la
commande `perf-sentinel query monitor` pour la même vue dans un terminal. Quand l'en-tête
publié pour ce daemon, `PublicAuthHeaderName` ou à défaut `AuthHeaderName`, est
`X-API-Key`, une ligne sous la commande indique de poser d'abord
`PERF_SENTINEL_DAEMON_API_KEY` à la clé de lecture du daemon, puisque le monitor envoie
cette variable en `X-API-Key` et sous aucune autre forme. Le moteur lit la clé dans
l'environnement, donc la commande elle-même reste la même dans tous les shells. Tout autre
en-tête, celui d'un Ingress par exemple, est nommé à la place, comme un en-tête que
`query monitor` ne sait pas envoyer.

## L'écran des incidents

Chaque ligne est un incident qu'un daemon a enregistré quand l'alerte de l'exploitant le
lui a posté, dans l'ordre où le moniteur du daemon imprime ses colonnes : début, namespace,
service, kind, fin, findings, capture, source. Le namespace est le label d'alerte que le
daemon a lu à côté du service, vide quand l'alerte n'en portait pas. Le daemon en est
l'auteur et le Hub copie son enregistrement, donc l'écran lit ce que le daemon a gelé et ne
redérive rien.

Ouvrir l'écran lit chaque daemon avant de dessiner, donc les lignes sont ce que la flotte
tient maintenant et non ce que le dernier poll a laissé : un exploitant alerté d'un OOM
kill attendrait sinon un poll horaire devant un tableau vide. Un bouton répète cette
lecture à la demande et s'éteint tant qu'une lecture est en vol. Aucun des deux ne peut
prendre la flotte d'assaut, un daemon lu il y a moins de dix secondes étant sauté et sa
copie stockée servie à la place.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-incidents-dark.png">
  <img alt="L'écran des incidents avec une ligne dépliée sur ses findings figés" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-incidents.png">
</picture>

Une ligne se déplie sur les findings que le daemon a gelés pour cet incident, chacun placé
avant l'incident ou après le redémarrage d'après son propre horodatage face à celui de
l'incident. La colonne capture porte la lecture du daemon de jusqu'où son anneau
remontait encore : complete, partial ou empty. Les durées sont relatives, jamais des
dates, et l'horodatage exact est dans l'infobulle. Chaque finding montre la requête
normalisée sur laquelle il a été levé, entière et repliée sur plusieurs lignes, puisqu'un
même endpoint en porte souvent plusieurs, et son impact estimé, les opérations d'I/O
évitables du dashboard des findings : traces vues fois opérations en trop par trace, un
tiret quand le daemon a le calcul green coupé. Les findings s'ouvrent du plus grave au
moins grave, puis du plus vu au moins vu, un clic ou une touche sur l'en-tête d'une colonne
les trie selon elle, et un champ au-dessus du tableau les restreint par endpoint ou par
gabarit. Cinq sélecteurs restreignent la liste. Kind et service restent
visibles, namespace, environnement et daemon se replient derrière More filters, qui dit
combien d'entre eux sont posés. Celui du namespace apparaît dès qu'une ligne en a porté un. C'est le Hub qui les applique, donc les lignes plus anciennes
qu'un bouton charge ensuite, par centaine et depuis le magasin plutôt que depuis les
daemons, suivent le même filtre. Sous le tableau, une ligne par daemon dit quand sa copie a
été lue, ou qu'elle ne l'a jamais été, pour qu'une flotte calme ne se lise jamais comme une
copie périmée. Un daemon qui a refusé la clé du Hub sur cette route reçoit un bandeau
nommant le réglage à corriger, et ses findings restent collectés.

Une ligne dépliée porte un bouton Analyse this window sous la note qui décrit la fenêtre,
qui ouvre New analysis sur le service et la fenêtre de l'incident, décrit sous Le
formulaire suit la source. La source reste au choix de l'exploitant, parce qu'un daemon ne
prend aucune fenêtre et que le daemon de l'incident lui-même analyserait son instantané en
mémoire plutôt que la fenêtre. Le lien garde ses paramètres, donc il se partage et se
recharge, et l'onglet New lui-même n'en porte aucun. Quand une source du Hub détient un
identifiant d'acquittement, chaque finding de la ligne dépliée se termine par un lien Ack, qui ouvre
[La page d'acquittement](#la-page-dacquittement) sur ce finding, le daemon de l'incident
coché. Sans identifiant, une ligne sous le tableau dit que les findings s'acquittent dans chaque
daemon.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-handoff-dark.png">
  <img alt="New analysis ouvert sur la fenêtre d'un incident, sous le bandeau qui le nomme" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-handoff.png">
</picture>

## L'écran des corrélations

Chaque ligne est une paire de findings qui n'ont pas cessé de se suivre, l'un se déclenchant
quelques secondes après l'autre, telle que l'ont poussée les daemons qui ont `[daemon.correlation]`
et `[daemon.hub_export]` actifs : le côté qui se déclenche en premier, celui qui le suit, le nombre
d'épisodes de la paire sur la période, le dernier, marqué actif tant qu'il dure, son pic de
confiance, le délai médian entre les deux et le daemon. Les lignes sont construites dans le
navigateur à partir des épisodes de la période, mille au plus, et une ligne sous le tableau dit
quand la lecture s'est arrêtée là. Une période, les dernières 24 heures par défaut, un service
cherché des deux côtés, un daemon et un interrupteur "active now only" resserrent la liste.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-correlations-dark.png">
  <img alt="L'écran des corrélations avec une paire dépliée sur sa frise et ses épisodes" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-correlations.png">
</picture>

Une ligne se déplie en une frise de la période, chaque épisode en vert sur les incidents du même
daemon en rouge, puis la liste de ses épisodes : durée, pics et incidents que chacun recouvre, qui
ouvrent l'écran des incidents sur cet incident. Analyse this window ouvre New analysis sur la
fenêtre de l'épisode, élargie d'une minute de chaque côté, et sur le service du côté qui se
déclenche en premier, comme le fait l'écran des incidents. Explain the last pair, affiché quand le
Hub a un backend de traces, ouvre New analysis sur la trace d'un finding de la dernière
co-occurrence de la paire, l'autre trace restant à un clic dans le bandeau.

Un incident déplié liste les corrélations actives pendant sa durée : les épisodes de son daemon qui
recouvrent la fenêtre de l'incident, jusqu'à sa fin dès qu'il en a une, chacun avec un lien vers
tous les épisodes de sa paire. Ce lien, `#/correlations?pair_key=<clé>`, est aussi celui du tableau
des corrélations de Grafana : l'écran s'ouvre sur cette paire sur 180 jours, dépliée, avec un
bouton Show every pair pour en sortir. Un daemon qui n'a poussé aucune corrélation le dit, pour qu'un bloc
vide ne se lise jamais comme un bloc calme. Sur un téléphone, les tableaux défilent latéralement
dans leur cadre, comme ceux des incidents, et la page ne défile pas.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-incident-correlations-dark.png">
  <img alt="Un incident déplié qui liste les corrélations actives pendant sa durée" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-incident-correlations.png">
</picture>

## L'écran du récapitulatif

Le récapitulatif sert d'appui à la revue d'équipe : ce qui gaspille le plus, et ce qui a bougé depuis la
période précédente de même durée. Aucun modèle de langage ne le rédige : chaque phrase est fixée à
l'avance et chaque chiffre vient des registres de détections des daemons. Il lit `GET /api/recap` ([l'API](API-FR.md#recap)), donc son
périmètre, ses filtres et son volume de détections sont ceux de la liste des findings. Une période
(jour, semaine ou mois, la semaine par défaut), un jour quelconque de cette période, un environnement, un
service, un namespace et la bascule Engineering ou Management la resserrent, et chaque choix s'écrit dans
l'adresse, `#/recap?period=day&view=pilotage`, de sorte qu'une vue s'envoie en lien. La période se compte
en jours UTC et le dit, alors que chaque horodatage de la page, le moment où une action a été prise ou
terminée compris, s'affiche à votre heure avec son fuseau (`2026-10-29 14:32 CET`), l'instant UTC au survol.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-recap-dark.png">
  <img alt="L'écran du récapitulatif : la période, quatre tuiles, puis les findings à traiter d'abord avec leur tendance et leur action" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-recap.png">
</picture>

La page se lit comme un ordre du jour. Sous la ligne de période, les notes (traces échantillonnées,
traces délestées sous pression mémoire) et, quand un daemon a redémarré ou qu'un jour n'est pas encore
scellé, un bandeau qui dit que la comparaison n'est pas exacte. Quatre tuiles suivent : les I/O évitables,
l'estimation carbone (un ordre de grandeur, "at least" quand une source n'envoie pas de coefficient, jamais
une clé de tri), les findings actifs avec les acquittés comptés à part, et les actions avec ce que le moteur
a confirmé. Tant que la période est en cours, les deux premières le disent, et les tendances ne comparent
que ses jours clos aux mêmes jours de la période précédente. Sept "?" expliquent la période, les tuiles, le
classement et ce qui a bougé.

What to fix first liste les findings non acquittés, ceux qui ont des I/O évitables d'abord par volume, les
autres par sévérité sous un séparateur, dix au départ et tous derrière Show all. Une carte porte le type, le
service, l'endpoint, la suggestion, une barre d'ampleur relative à la première ligne et la tendance. Replié
dessous, ouvert sur la première carte : la requête ou l'appel, le correctif suggéré avec son framework et son
lien de référence (seule une adresse http ou https devient un lien), l'endroit dans le code, la confiance, la
signature et l'historique de l'action. Take déclare l'action au nom sous lequel le Hub vous connaît, le
sélecteur à côté la fait passer entre to do, in progress et done, et le verdict du moteur sur une action
terminée (confirmed, still detected, came back, waiting for a sealed day) apparaît à côté. Acknowledge ouvre
la page d'acquittement. Name this, ou Rename, donne à l'endpoint un nom métier qui titre la ligne, en
Management aussi. Le propriétaire d'une action se lit comme un nom : celui que la connexion a donné, sinon
un nom tiré de l'identité (`alice.martin@acme.com` se lit Alice Martin), l'identité au survol. Un refus,
pas d'identité ou pas de permission, montre dans la carte la phrase même du Hub.

What moved liste les findings nouveaux, disparus, en hausse, en baisse, revenus et terminés mais encore
détectés, les groupes qui contiennent quelque chose seulement. Disparu est une présomption, pas la preuve
que le correctif est parti. Le premier jour d'une période, rien n'est comparé, et la page le dit au lieu de
prétendre que rien n'a bougé.

La vue Management lit le même classement en mots simples : les cinq premiers problèmes sous forme d'une
phrase fixe par type, le nom de l'endpoint quand quelqu'un l'a donné (sinon l'endpoint sous le service), le
chiffre en opérations inutiles, la tendance et l'action, en lecture seule. Elle ne porte aucun mot
technique, ses sept textes d'aide non plus.

### Rapports

Sous What moved, Reports (Rapports en français) liste les copies figées des périodes closes, la plus récente d'abord, depuis
`GET /api/recap/reports` ([l'API](../API.md#recap-reports)). La section est absente pour un appelant que le
Hub ne laisse pas les lire : il faut `recaps.read`, ou `findings.read` pour les rapports qu'on a générés
soi-même. Une ligne nomme la période et l'origine du rapport, `Week 2026-10-19 to 2026-10-25 · weekly-shop`
pour un abonnement, `Day 2026-10-28 · on demand by alice` pour un rapport demandé par quelqu'un, avec
`· incomplete` quand un jour n'était pas scellé ou qu'un daemon avait redémarré. Dessous viennent le moment
de l'écriture, à votre heure, et le sort du rapport : une pastille par livraison
(`webhook ops-chat: delivered`, `email pm@example.test: failed (550), 1 try`,
`webhook ops: pending (500), 3 tries`), verte une fois livrée, rouge une fois en échec, grise tant que le
Hub réessaie. Un rapport qui n'est parti nulle part dit pourquoi : `Archived, not sent: nothing moved` (une
période où rien n'a bougé est archivée et jamais livrée), `Archived, no destination` pour un abonnement
sans webhook ni adresse, et `On demand, not sent` pour un rapport généré à la demande, qui n'est jamais
livré.

Management et Engineering téléchargent la page figée de chaque vue, un fichier autonome qui s'ouvre hors
ligne, en clair comme en sombre, sans autre script que les deux qui règlent le thème et les heures. Un
rapport ne porte que les vues avec lesquelles il a été figé.

Generate a report fige la période et les filtres que l'écran montre, à votre nom, à partir des copies que
vous pouvez lire : sans date choisie, c'est la dernière période close, et un jour d'une période qui n'est
pas finie est refusé avec la phrase du Hub dans la section. Il refuse aussi quand le Hub ne sait pas qui
demande, ce qui est le cas d'un navigateur derrière aucune connexion ni proxy d'identité, et quand votre
accès ne porte aucun `findings.read`. Un succès ajoute la ligne aussitôt.

Un rapport garde une seule langue : celle de l'écran pour un rapport généré ici, le `Language` de
l'abonnement pour un rapport planifié. Ses deux téléchargements s'ouvrent dans cette langue. Un rapport
envoyé par mail arrive à chaque adresse dans la langue de cette adresse, voir
[la configuration](CONFIGURATION-FR.md).

## Cartes à partager

Sous les rapports, quand le Hub signe des cartes (`Hub:Cards:Organisation` est réglé et vous pouvez en
créer), Share a result fait d'un résultat mesuré une image ou un carrousel à publier. Comme pour le
récapitulatif, aucun modèle de langage ne rédige une carte : le Hub calcule chaque chiffre et le moteur
imprime des phrases fixées à l'avance. La section lit et écrit `/api/cards/*` ([l'API](API-FR.md#cartes-à-partager)). Elle reste fermée derrière Create a card.
Quand le moteur du Hub n'a pas la sous-commande `card`, un bandeau le dit à la place de la section.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-card-dark.png">
  <img alt="La section carte : quoi raconter, la période close et ce qu'elle contient, vos mots, la langue et le format de la carte, puis l'aperçu avec la vérification du rattachement" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-card.png">
</picture>

Le formulaire suit l'ordre dans lequel on décide. Quoi raconter : une histoire (une correction
confirmée, en mots de tous les jours), une courbe (la part du travail évitable, période après période)
ou un collectif (combien de corrections la mesure a confirmées, et où). La période est une semaine, un
mois, un trimestre, un semestre ou une année, choisie avec des flèches qui s'arrêtent à la dernière
période close : une période en cours ne peut pas être choisie. Elle part de la période du filtre du
récapitulatif quand celle-ci est close. Dessous, l'écran dit ce que contient la période avant toute
signature : les jours enregistrés et les sources, les jours avec redémarrage, la part du trafic que
couvrent les chiffres, et l'environnement, repris du filtre du récapitulatif. Une carte couvre tous les
services de cet environnement : les filtres de service et de namespace ne s'appliquent pas. Une période
que la carte ne peut pas couvrir affiche la raison du Hub et désactive Preview et Download ; une courbe
dont la période précédente n'est pas entière propose à la place une carte histoire ou collectif.

Une histoire liste les corrections que la mesure a confirmées dans la période, celle au plus fort
volume avant la correction cochée d'abord, au-delà de cinq derrière Show all. Son nom de tous les
jours est pré-rempli avec le nom retenu pour l'endpoint, et une case retient un nouveau nom pour tout
le monde, quand votre accès porte `labels.write` ; sinon la case est grisée, une phrase dit pourquoi,
et la carte imprime quand même le nom. Le titre et l'équipe (48 caractères chacun) et le nom de tous
les jours (80) s'impriment tels que saisis, dans les deux langues de carte. Un caractère que la police
de la carte ne sait pas dessiner, un emoji par exemple, est signalé sous son champ pendant la saisie,
et le refus du moteur arrive au même endroit.

La carte se télécharge en image (PNG), en image à imprimer ou agrandir (SVG), nette à toute taille,
en image plus légère à archiver (JPEG XL), ou en carrousel (PDF). Le JPEG XL garde les pixels du
PNG, sans perte : gardez-le pour archiver ou envoyer, et publiez le PNG, car
[LinkedIn n'accepte pas le JPEG XL](https://www.linkedin.com/help/linkedin/answer/a564109). Preview
rend la carte : l'image dans un cadre clair, quel que soit le thème du Hub, le PNG pour un choix
JPEG XL, le carrousel dans la visionneuse PDF du navigateur. Un choix modifié après un aperçu le
grise et le dit, sauf un passage entre PNG et JPEG XL, qui montre la même image. Download fait
toujours la carte à partir du formulaire tel qu'il est. Sous l'aperçu, la ligne que la page de
vérification imprimera, et Verify attachment, qui rend la carte une fois de plus et fait vérifier par
le moteur sa clé sous le domaine de l'organisation, par `cloudflare-dns.com` et le domaine lui-même,
le seul appel de la section qui sort du Hub. Quand l'un des deux endroits qui publient la clé, la
ligne DNS et le fichier `.well-known`, ne répond pas, la vérification dit « Could not check »,
jamais rattachée. Une clé révoquée, une autre clé que celle publiée ou une carte datée hors de la
période de sa clé se lisent comme ce verdict, pas comme une vérification en échec. La vérification
dit aussi si le fichier de la carte est celui qui a été signé, et
liste les chiffres tels que signés, exacts, à comparer avec l'aperçu, qui les arrondit vers le bas.
Sans `Hub:Cards:Domain`, une phrase dit que la page de
vérification montre les cartes du Hub comme « Signer not verified ». La langue de la carte, anglais ou
français, est retenue dans ce navigateur.

Les administrateurs ont aussi Signing key and organisation, replié et accessible même section fermée :
le nom de l'enregistrement et la ligne DNS de chaque clé, le fichier `.well-known` indenté pour la
lecture et copié tel que servi, Rotate key, et par clé un Revoke qui demande sur place le premier jour
où elle a pu fuiter, sans date par défaut, et dit ce que la révocation change avant qu'on la confirme.

## L'écran des divulgations

Les divulgations périodiques qu'une tâche planifiée a publiées, voir
[API-FR.md](API-FR.md#divulgations). Une frise des douze derniers trimestres au plus, jusqu'au
trimestre en cours, ouvre l'écran : chaque case lit le niveau d'intégrité de la divulgation du
trimestre, interne, retirée, "not published", ou le nombre de jours qui restent au trimestre en
cours. Une légende dessous distingue les deux sortes de marque. Un niveau en pointillé est ce
que le rapport déclare de lui-même. Une marque pleine est ce que ce Hub a recalculé à l'arrivée
du rapport, l'empreinte et une attestation qui la nomme. La signature se vérifie sur la machine
du lecteur, jamais ici.

Une carte par divulgation suit, période la plus récente d'abord : la période et ses dates, ses
pastilles, ses chiffres principaux, quand elle a été publiée et quand une copie signée l'a
remplacée, si le PDF a été rendu ici, et des boutons pour télécharger le PDF et chaque
fichier. Un bandeau dit quand une divulgation a été retirée, par qui et pourquoi, quand une
divulgation officielle ne porte aucune signature, et quand le Hub ne l'a pas rendue, avec la
raison. L'écran se termine sur les commandes qu'un job de CI lance pour produire, signer et
publier la suivante.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-disclosures-dark.png">
  <img alt="L'écran des divulgations : la frise des trimestres, puis une carte par divulgation, dont une retirée" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-disclosures.png">
</picture>

Une carte ouvre l'écran propre à la divulgation :

- **Intégrité**, qui a vérifié quoi : l'empreinte et le lien de l'attestation, vérifiés par ce
  Hub et la version de son moteur, la signature, que le lecteur vérifie avec `verify-hash`
  contre le signataire attendu, et l'audit, seulement déclaré. Puis le `content_hash`, le
  `report_uuid`, le signataire attendu et la commande de vérification à copier.
- **Fichiers**, chacun téléchargeable, avec `SHA256SUMS` pour `sha256sum -c`.
- **Liens de partage**, avec leur état, leur auteur, leur expiration et leur nombre de
  téléchargements. Un nouveau lien s'affiche une seule fois, avec l'adresse de son PDF et le
  `report.json` à côté, que lit `verify-hash --url`.
- **Livraisons**, chaque événement de webhook par le nom du webhook, son état, ses tentatives
  et son dernier statut.
- **Applications**, et **Méthode et limites** avec les avertissements, lus dans `report.json`
  tels que le rapport les énonce. Un rapport v1.8 dont le total contient l'énergie d'hôtes de
  base de données ou de broker ajoute une ligne `Database hosts` ou `Broker hosts`, qu'aucune
  application ne porte. Chaque part est calculée sur le `total_energy_kwh` du rapport, comme
  dans le PDF. Une ligne d'hôtes indique `in K of N windows` quand les totaux ne l'ont tenue
  qu'une partie de la période.

Un détenteur de `disclosures.manage` que le Hub sait nommer, par une session ou par l'en-tête
d'identité de confiance, dispose aussi de New share link, de Render again sur une divulgation
non rendue, de Retry sur une livraison qui n'est pas passée, et de Withdraw, qui demande une
raison et dit que l'étape est définitive. Sur un Hub sans contrôle d'accès, c'est quiconque le
Hub sait nommer. Une divulgation interne ou retirée n'offre aucun lien de partage.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-disclosure-dark.png">
  <img alt="Une divulgation : ses chiffres, ses applications, qui a vérifié quoi, ses fichiers et son lien de partage" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-disclosure.png">
</picture>

## La page d'acquittement

Un écran acquitte ou révoque un seul finding, et aucun onglet n'y mène. Les acquittements
se font depuis le dashboard Grafana des findings, le rapport HTML, le TUI ou la CLI, et
cette page est l'endroit où arrive un lien Grafana.

Trois entrées :

- `/?ack=<signature>&environment=<environment>&source_id=<id>`, chaque valeur encodée en
  pourcent et les deux dernières facultatives. Une query et non un hash, parce qu'un hash
  se perd quand le fournisseur d'identité demande un mot de passe en chemin. Au chargement
  le lanceur la convertit en la route ci-dessous, et la query quitte la barre d'adresse.
- `#/ack?signature=<signature>&environment=<environment>&source_id=<id>`, la route
  elle-même. `environment` et `source_id` sont facultatifs et ne décident que des lignes
  cochées au départ. Une valeur blanche se lit comme absente, ce que Grafana envoie pour le
  choix All d'une variable. Un environnement de plus de 256 caractères ou qui porte un
  caractère de contrôle rend le lien incomplet.
- Le lien Ack de chaque finding d'un incident déplié, qui nomme le daemon de l'incident.

La page lit le finding sur `/api/findings` par sa signature exacte et en montre le type, la
sévérité, le service, l'endpoint, le gabarit d'opération, la première et la dernière
observation, et le statut. Un lien sans signature, ou dont la signature dépasse les bornes
du Hub, dit qu'il est incomplet, et une signature pour laquelle le Hub ne détient aucun
finding le dit aussi. Aucun des deux ne retombe sur un autre écran.

Une seconde lecture de la même route, fenêtrée sur les 7 derniers jours UTC, aujourd'hui
compris, du début du jour UTC d'il y a six jours jusqu'à maintenant sur l'horloge du
navigateur, ajoute les détections à la carte. Les faits gagnent
`Detections (7 days)`, le compte sur toutes les sources, et `Avoidable I/O (7 days)` quand les
daemons ont compté l'I/O supplémentaire. Un compte qui reste en deçà, un jour encore ouvert
par exemple, se lit comme une borne basse avec un signe ≥, et comme `?` quand aucun jour de la
fenêtre n'est connu. Sous les faits, une ligne par source donne son compte, son I/O évitable
et, entre parenthèses, pourquoi il reste en deçà, en toutes lettres. Les raisons sont listées
dans [Ce que le Hub ajoute à un finding](API-FR.md#ce-que-le-hub-ajoute-à-un-finding). Une
fenêtre sans détection connue du Hub se lit "No detection known in the last 7 days". Tout
échec de cette seconde lecture, une requête refusée ou une réponse qui n'est pas une liste,
laisse la carte et le formulaire tels quels et ajoute la seule ligne "The Hub could not read
the detections of the last 7 days."

Dessous, un seul formulaire : une raison obligatoire, une expiration facultative, et une
ligne par source qui porte le finding. L'expiration est un jour, envoyé comme la dernière
seconde de ce jour en UTC, et une expiration vide est un acquittement permanent. Quand
aucune source qui porte le finding ne peut recevoir d'acquittement du Hub, la page affiche
à la place la commande `perf-sentinel ack` du daemon. Une ligne sous le champ relit
l'expiration en UTC, "Holds until 2026-11-30 23:59:59 UTC.", à côté de la date
que le navigateur affiche dans sa propre langue.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-ack-dark.png">
  <img alt="La page d'acquittement sur un finding porté par quatre daemons, une ligne par cas" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-ack.png">
</picture>

Un acquittement masque un finding, donc le contexte du lien décide des lignes cochées au
départ, parmi celles où l'on peut faire quelque chose. Quand le lien nomme une source,
cette ligne seule est cochée. Sinon, quand il nomme un environnement, les lignes de cet
environnement le sont. Sans aucun contexte, une ligne n'est cochée au départ que si elle
est la seule où l'on peut faire quelque chose. Toute autre ligne reste visible et se coche
à la main, donc un lien ouvert depuis la production n'écrit jamais en staging ni sur un
daemon de CI sans que le lecteur le demande.

| La source                                                      | Sa ligne propose                                  |
|----------------------------------------------------------------|---------------------------------------------------|
| relaie, et le Hub n'en reflète aucun acquittement              | Acknowledge                                       |
| relaie, et tient un acquittement pris à l'exécution            | Revoke, à côté de qui l'a pris, quand et pourquoi |
| relaie, et tient un acquittement de la baseline de CI          | rien                                              |
| relaie, et son état d'acquittement est inconnu                 | les deux boutons, et la ligne nomme `acks_state`  |
| n'a pas d'identifiant d'acquittement, ou n'est plus configurée | rien, et la ligne dit lequel des deux             |

Une source relaie quand `ack_relay` vaut true sur `/api/sources`. Une lecture
d'acquittements conclut à `ok` et à `truncated`, les deux états depuis lesquels le Hub
reflète des acquittements. Partout ailleurs l'état d'acquittement est inconnu : le Hub ne
distingue pas un finding non acquitté d'un finding déjà acquitté, ce qui est le cas de tout
daemon antérieur à 0.24.0 et de tout daemon dont la dernière lecture d'acquittements a
échoué. Le relais répondrait quand même, donc on peut toujours agir sur une telle ligne, et
les deux boutons s'y appliquent quand elle est cochée. Le daemon refuse celui qui ne
s'applique pas, l'acquittement d'un finding déjà acquitté ou la révocation d'un finding qui
ne l'est pas, et sa réponse s'affiche dans la ligne de résultat de cette source. Une ligne
dont le Hub reflète déjà l'acquittement garde Revoke, ou rien pour un acquittement de la
baseline, quoi que dise l'état.

Un acquittement de la baseline ne se révoque pas ici parce qu'il ne se révoque nulle part à
l'exécution : l'API du daemon elle-même lui répond `404`, et la baseline change par
l'édition de son fichier, sous revue, par une pull request. Voir
[LIMITATIONS-FR.md](LIMITATIONS-FR.md).

Un seul envoi acquitte sur chaque source cochée, parce que les acquittements vivent dans le
magasin propre à chaque daemon et que rien n'en diffuse un seul. Le lanceur envoie une
requête de relais par source, l'une après l'autre, puis imprime une ligne par source, avec
la raison du Hub lui-même en cas de refus, et relit le finding pour que les lignes montrent
le nouvel état. Une source qui refuse ne coûte rien aux autres. Une ligne que le lecteur a
décochée le reste à travers cette relecture, donc un second envoi n'atteint jamais une
source qu'il avait écartée.

Qui peut s'en servir relève de la règle du relais, pas de la page : une session `Hub:Auth`,
ou l'identité que pose un proxy une fois `Hub:AckRelay:TrustIdentityHeader` activé. Tout
autre appelant reçoit le `403` du relais à l'envoi, et l'acquittement est pris au nom sous
lequel le Hub connaît l'appelant. Voir [API-FR.md](API-FR.md#relais-dacquittement) et
[AUTHENTICATION-FR.md](AUTHENTICATION-FR.md#comment-ça-marche).

### Pull requests uniquement dans un environnement

Une équipe qui n'acquitte que par pull request dans un environnement, un daemon de CI par
exemple, ne donne à cette source aucun identifiant d'acquittement : `AckHeaderName` et
`AckHeaderValue` n'y sont pas renseignés, voir
[CONFIGURATION-FR.md](CONFIGURATION-FR.md#lidentifiant-dacquittement). Sa ligne ne propose
alors rien, et le Hub n'a aucun chemin d'écriture vers ce daemon. Les acquittements de sa
baseline restent reflétés et affichés sur la ligne, `source` valant `toml` dans `acks` sur
`/api/findings`, et ne se changent que dans le fichier.

Ne mettez pas `[daemon.ack] enabled = false` sur ce daemon pour fermer les acquittements à
l'exécution, parce que le daemon cesse alors de charger aussi la baseline TOML, voir
[ACK-WORKFLOW-FR.md](https://github.com/Institut-du-Numerique-Responsable/perf-sentinel/blob/main/docs/FR/ACK-WORKFLOW-FR.md)
du moteur.

## Quand aucune autorisation ne vous concerne

Avec `Hub:Access` activé, `/api/status` répond `403` à un appelant connecté qu'aucune autorisation
n'atteint. Le Hub répond, donc le lanceur n'affiche pas son bandeau d'injoignabilité. Chaque
route affiche [Votre accès](#votre-accès) sous le bandeau « Aucune autorisation de ce Hub ne vous concerne. Un
administrateur accorde les accès dans la configuration du Hub. » Les onglets et la
pastille de version disparaissent, puisque chaque onglet mènerait au même refus, et la
pastille d'identité nomme toujours l'appelant. À la place d'une portée vide, un bloc à
copier réunit ce dont un administrateur a besoin : le nom, l'identifiant qu'il inscrit dans
`Admins` ou `Members`, et les groupes qu'une autorisation peut nommer. Tout autre échec de
`/api/status` garde le bandeau d'injoignabilité. Voir
[ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md).

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-no-grant-dark.png">
  <img alt="Le bandeau sans autorisation au-dessus de l'accès de l'appelant, avec la demande à envoyer à un administrateur" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-no-grant.png">
</picture>

Avec le contrôle d'accès, chaque source de `/api/sources` porte les `permissions` de
l'appelant sur elle. Le formulaire Nouvelle analyse ne propose que les sources dont la liste
contient `analyses.run` et dit combien il en laisse de côté, par exemple « 2 of 4
configured · 2 need analyses.run ». La ligne d'un démon sur l'écran Sources ne se déplie
que là où la liste contient `sources.read`, que sa vue exige, et affiche sinon « details
need sources.read » sous son nom. Sans liste, comme sans contrôle d'accès, rien n'est
retenu.

## Votre accès

La pastille d'identité mène à `#/access` quand `GET /api/access` répond, c'est-à-dire avec
`Hub:Access` actif ; un `404` la laisse en simple texte, et un lien `#/access` ouvre alors
une page qui dit que ce Hub n'a pas de page d'accès, comme toute route inconnue dit qu'il
n'a pas d'écran à cette adresse. La page ne change aucun droit, car les droits
sont déclarés dans la configuration du Hub (valeurs Helm `hub.access`) et nulle part
ailleurs. Sa seule action révoque un jeton de terminal. Elle montre :

1. **Connecté en tant que.** L'identité, le moyen (session de navigateur, jeton de
   terminal ou jeton machine) et jusqu'à quand, le fait qu'un changement de groupe chez le
   fournisseur s'applique à la prochaine connexion, et « Your id, for an administrator »,
   le sujet en texte sélectionnable.
2. **Vos groupes.** Des pastilles marquées fournisseur ou interne.
3. **Ce que vous atteignez.** Projet, groupe, rôle et permissions par autorisation. Une autorisation sur
   `*` se lit « Everything, including records no project covers ».
4. **Vos jetons de terminal**, tirés de `GET /api/tokens` : ceux que `perf-sentinel login`
   a obtenus pour cet appelant, avec l'adresse d'où la connexion a été demandée, la
   création, l'expiration et la dernière utilisation (« never » avant la première).
   **Revoke** demande une seconde fois, sous la forme **Confirm revoke**, et quitter le
   bouton ou appuyer sur Échap le remet en place. Le clic confirmé envoie
   `DELETE /api/tokens/{id}`, dit dans la carte ce qui s'est passé (le terminal reçoit un
   `401` à sa requête suivante, ou le jeton n'existait déjà plus, ou il reste valide et
   pourquoi) et place le focus sur la carte. Voir
   [AUTHENTICATION-FR.md](AUTHENTICATION-FR.md#connexion-du-terminal).
5. **Projets que vous atteignez**, pour un membre : chaque projet que ces autorisations nomment,
   avec ses motifs écrits `source lab-daemon · service checkout-* · attribute team=shop`.
   Un administrateur reçoit à la place un lien vers Couverture.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-access-dark.png">
  <img alt="L'accès d'un membre de deux groupes d'équipe, avec son jeton de terminal" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-access.png">
</picture>

## L'écran Couverture

L'onglet Coverage n'apparaît que pour un administrateur ; un membre qui suit un lien
`#/coverage` arrive sur son propre accès. L'écran commence par ce qui demande un
changement :

1. **À traiter.** Les services, valeurs d'attribut et sources qu'aucun projet ne couvre,
   avec leurs nombres de findings et, par leur nom, les sources où chacun a été vu. Les
   findings qu'aucun projet ne couvre ne se lisent que par des autorisations sur `*`. Chaque liste
   s'arrête à ses 200 premières entrées et le dit.
2. **Motifs à coller.** Une ligne YAML par service, attribut et source non couverts, avec
   un bouton Copy, prête à placer sous les `patterns` d'un projet de
   `hub.access.projects`. Les valeurs sont entre guillemets, donc un `*` ou un `=` garde
   son sens.
3. **Déploiements en désaccord sur un attribut de projet.** Les copies de finding qu'un
   motif d'attribut de projet lit sans pouvoir y écrire, parce que la copie porte deux
   valeurs de la clé ou a aussi été vue sous un groupement sans elle. Une autorisation d'attribut
   les lit mais ne peut pas les acquitter, voir
   [ACCESS-CONTROL-FR.md](ACCESS-CONTROL-FR.md#lire-les-conflits).
4. **Les jetons de terminal de tous**, avec le même Revoke.
5. **Configuration**, telle que le Hub l'a chargée à son dernier démarrage, un pli par
   partie avec son nombre : projets, rôles (les intégrés marqués), autorisations, groupes
   internes et leurs membres, admins par identifiant et par groupe, jetons machine avec
   leur expiration en « expired », « in less than a day » ou « in N days », jamais leur
   valeur, et comptes locaux par nom, jamais leur hash. Une partie vide affiche « None
   declared. »

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-coverage-dark.png">
  <img alt="L'écran Couverture d'un admin : ce qu'aucun projet ne couvre, les motifs à coller, les conflits, chaque jeton de terminal et la configuration avec les autorisations dépliées" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/launcher-coverage.png">
</picture>

La page de connexion du terminal, `/device`, n'est pas un écran du lanceur : le Hub la sert
à part, avec une politique qui n'autorise ni l'encadrement ni le script en ligne, pour un
navigateur qui approuve un `perf-sentinel login`. Le champ du code prend le focus et
accepte le code en majuscules ou minuscules, avec ou sans son tiret. Une fois le code
vérifié, le focus passe sur l'endroit et l'heure de la demande. Un code venu d'un autre
réseau change **Approve** (**Autoriser**) en **Approve anyway** (**Autoriser quand même**), ce que la page annonce, et le résultat
est annoncé et prend le focus une fois les boutons disparus.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/hub-device-dark.png">
  <img alt="La page d'approbation du terminal avec un code en attente saisi, avant Approve ou Deny" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/hub-device.png">
</picture>

## La page de connexion

Avec un [compte local](AUTHENTICATION-FR.md#comptes-locaux) déclaré, un navigateur sans
session arrive sur la page de connexion du Hub, `/auth/sign-in`, au lieu de celle du
fournisseur. Ce n'est pas non plus un écran du lanceur : une page autonome, styles en
ligne et sans script, puisque les fichiers du lanceur sont derrière la connexion. Elle
porte le logo du lanceur, en ligne, sa police, DM Sans écrite dans la feuille de style en
polices `data:`, et sa palette, claire ou sombre selon le système. **Sign in with single sign-on**
(**Se connecter par authentification unique** en français) vient d'abord et mène au
fournisseur ; **Sign in with a local account** (**Se connecter avec un compte local**) est replié en dessous, avec la mention
qu'il sert quand l'authentification unique est indisponible et que chaque connexion
locale est journalisée. Après une tentative refusée, le pli s'ouvre avec la raison,
jamais avec ce qui a été tapé. Dans les deux cas, le navigateur revient sur la page
demandée. Le lanceur lui-même ne change pas : sur un `401` il se recharge, et c'est ce
rechargement qui atteint cette page. Une page qui s'affiche que le fournisseur réponde
ou non, c'est le but : le lanceur n'envoie jamais un navigateur droit vers un
fournisseur en panne.

Le serveur écrit cette page et la page d'approbation du terminal en anglais ou en français : `?lang=en`
ou `?lang=fr` d'abord, sinon la dernière langue choisie avec le bouton du lanceur (le cookie
`perf-sentinel-lang` qu'il écrit), sinon l'`Accept-Language` du navigateur, sinon l'anglais. Une session
expirée revient donc dans la langue que le lecteur a choisie. Chacune porte le bouton
FR/EN du lanceur, en lien vers la même page dans l'autre langue, et une connexion refusée répond dans la
langue du formulaire d'où elle vient. Une langue choisie sur ces pages n'est pas stockée.

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/hub-sign-in-dark.png">
  <img alt="La page de connexion du Hub, le pli du compte local ouvert" src="https://raw.githubusercontent.com/Institut-du-Numerique-Responsable/PerfSentinelHub/main/docs/img/hub/hub-sign-in.png">
</picture>

## Sûreté

Rien de ce qui vient du serveur n'est jamais écrit avec `innerHTML`. Chaque chaîne
affichée est un nœud de texte.
