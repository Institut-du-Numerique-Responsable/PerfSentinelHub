# Mesures

Ce que pèse l'image publiée, en combien de temps elle répond après un
démarrage à froid, et combien de mémoire elle occupe au repos. Chaque chiffre
ci-dessous vient de l'image publiée `ghcr.io/institut-du-numerique-responsable/perf-sentinel-hub:0.3.3`,
pas d'un build local, et les commandes pour les reproduire terminent la page. La
seule exception, le démarrage sur une base remplie, dit comment elle a été
mesurée.

Le Hub est publié en NativeAOT : du C# 14 sur .NET 10 compilé à l'avance en un
seul exécutable natif, sans runtime .NET ni JIT dans l'image. La base est
`runtime-deps` chiselée, qui n'embarque que les bibliothèques système, et le
processus tourne sous l'utilisateur non root `1654`.

## En bref

| Chiffre                             | Valeur                                                                      |
|-------------------------------------|-----------------------------------------------------------------------------|
| Démarrage à froid, première réponse | 130 ms en médiane, de 120 à 138 ms sur 10 essais                            |
| Mémoire au repos                    | 20,3 à 20,8 Mio (21,3 à 21,8 Mo), 60 s après le démarrage, sur 3 conteneurs |
| Image compressée, amd64             | 39,8 Mo : base 21,4, Hub 11,1, Perf Sentinel 7,4                            |
| Image compressée, arm64             | 37,7 Mo : base 20,3, Hub 10,6, Perf Sentinel 6,8                            |

## Ce que contient l'image

L'image embarque le binaire du moteur Perf Sentinel, que le lanceur exécute en
sous-processus. Le détail ci-dessous sépare ce qui relève du Hub de ce qui
relève du moteur et de la base.

| Couche                     | amd64, compressé | arm64, compressé | arm64, sur disque |
|----------------------------|------------------|------------------|-------------------|
| Base chiselée              | 21,4 Mo          | 20,3 Mo          |                   |
| Exécutable du Hub          | 9,6 Mo           | 9,1 Mo           | 21,1 Mo           |
| Bibliothèque native SQLite | 0,7 Mo           | 0,8 Mo           | 1,5 Mo            |
| Interface du navigateur    | 0,8 Mo           | 0,8 Mo           |                   |
| Perf Sentinel 0.25.2       | 7,4 Mo           | 6,8 Mo           | 15,2 Mo           |

En amd64, une fois décompressés, les exécutables pèsent 20,4 Mo pour le Hub et
19,1 Mo pour le moteur.

## Conditions

- Machine : Apple M4 Pro, Docker 29.8.0, variante arm64 de l'image.
- Une source daemon configurée vers un port fermé, pour que chaque poll échoue
  vite et que rien ne soit jamais importé. La base reste vide.
- `/data` est un tmpfs appartenant à l'UID `1654`.
- Le démarrage à froid est chronométré du `docker run` au premier `200` sur
  `/`. Il inclut donc le démarrage du conteneur en plus de celui du Hub.
- La mémoire est le chiffre du conteneur donné par `docker stats`, hors cache
  de pages.
- Les tailles d'image sont celles des couches, lues dans le manifeste du
  registre pour chaque architecture.

## Ce que ces chiffres ne disent pas

Le chiffre de mémoire est un plancher. Un Hub qui importe des findings, garde
des jours d'observation, sert le lanceur et produit des rapports en occupe
davantage, et cette page n'en donne aucun chiffre tant qu'il n'a pas été mesuré
sur une vraie flotte. Les chiffres de démarrage et de mémoire valent pour
arm64 seulement. L'image amd64 a été pesée, pas exécutée.

## Démarrer sur une base remplie

Chaque démarrage complète ce qu'un build plus ancien a laissé de côté : depuis
l'enveloppe propre à chaque source, le service et les attributs de regroupement
d'une copie de finding, et les paires que partagent tous les findings gelés
d'une copie d'incident, voir
[ARCHITECTURE-FR.md](ARCHITECTURE-FR.md#ce-que-contient-la-base). Le premier
démarrage après la mise à jour trouve tout cela à faire. Mesuré sur deux bases
remplies, à travers `HubDatabase.InitializeAsync` dans un build Release des
tests plutôt que l'image native, avec le SQLite 3.53.4 embarqué, sur la machine
ci-dessus avec la base sur son SSD interne :

| Base                                                      | Taille   | Premier démarrage après la mise à jour | Chaque démarrage suivant |
|-----------------------------------------------------------|----------|----------------------------------------|--------------------------|
| 100 000 copies de findings, 10 000 incidents de 20        | 312 Mio  | 2,3 s                                  | 0,14 s                   |
| 100 000 copies de findings, 1 000 incidents de 1 000      | 1034 Mio | 6,5 s                                  | 0,11 s                   |

L'enveloppe de chaque copie fait environ 900 octets avec deux attributs de
regroupement, et chaque finding gelé environ 1 Ko avec deux, les mêmes deux dans
un incident. Le premier démarrage écrit 200 000 lignes d'attributs et 100 000
services, et dérive les paires de chaque incident, ce qui croît avec les
findings gelés tenus dans `Hub:Retention` : 200 000 dans la première base,
1 000 000 dans la seconde, où chaque incident tient le plafond du daemon de
1 000. Un démarrage suivant n'a rien à écrire et lit seulement quelles copies en
ont besoin.

Ce premier démarrage s'exécute avant que le Hub n'écoute, dans une seule
transaction. La sonde de démarrage du chart lui laisse 60 s et sa limite par
défaut 500m de CPU, donc une base qui tient plusieurs millions de findings gelés
sur des nœuds lents peut échouer la sonde, et un démarrage tué reprend de zéro.
Relevez alors `resources.limits.cpu` le temps de la mise à jour. Le premier
démarrage tient aussi chaque enveloppe qu'il complète, environ 90 Mo ici, dans
le stockage temporaire de SQLite, qui écrit dans `/tmp` au-delà de son cache de
2 Mo, et le chart monte `/tmp` comme un `emptyDir` de 32 Mio. Que le kubelet
compte ce fichier, que SQLite supprime dès son ouverture, dans la limite n'a pas
été vérifié.

## Portée d'accès sur 100 000 copies

La CTE `visible` que le contrôle d'accès placera devant une lecture de findings
restreinte, voir
[ARCHITECTURE-FR.md](ARCHITECTURE-FR.md#le-contrôle-daccès-avant-son-application),
retient 24 500 des 100 000 copies de finding à travers 12 motifs, six préfixes
de service et six valeurs d'attribut, en 0,2 s environ, trois passes de 202 à
203 ms. Mesuré par le test explicite
`The_visible_cte_over_100k_copies_and_12_patterns` dans un build Release, sur la
machine ci-dessus, la base sur son SSD interne, avec une ligne d'attribut par
copie :

```bash
./PerfSentinelHub.Tests/bin/Release/net10.0/PerfSentinelHub.Tests \
  -method '*.The_visible_cte_over_100k_copies_and_12_patterns' -explicit only -showLiveOutput
```

Une copie qu'aucun motif ne lit coûte la sonde de chaque motif, donc une portée
étroite coûte plus qu'une large : les mêmes 12 motifs écrits pour retenir 90 500
copies prennent 0,1 s. Rien n'exécute encore cette CTE.

## Reproduire

```bash
IMG=ghcr.io/institut-du-numerique-responsable/perf-sentinel-hub:0.3.3
docker pull "$IMG"

docker run -d --name hub -p 18080:8080 \
  --tmpfs /data:uid=1654,gid=1654 \
  -e Hub__Sources__0__Id=bench \
  -e Hub__Sources__0__Name=Bench \
  -e Hub__Sources__0__Environment=test \
  -e Hub__Sources__0__BaseUrl=http://127.0.0.1:9 \
  -e Hub__Sources__0__ImportApiKey="$(openssl rand -hex 16)" \
  "$IMG"

until [ "$(curl -s -o /dev/null -w '%{http_code}' http://127.0.0.1:18080/)" = 200 ]; do sleep 0.005; done

sleep 60
docker stats --no-stream --format '{{.MemUsage}}' hub
docker rm -f hub
```

Chronométrer ensemble le `docker run` et la boucle d'attente donne le démarrage
à froid. Les tailles de couche viennent du manifeste de chaque plateforme de
l'index d'image.
