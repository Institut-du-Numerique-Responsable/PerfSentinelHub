/**
 * Every French text the launcher shows, the twin of text-en.js: same keys, same
 * `{name}` placeholders. A key missing here shows its English text.
 */
(function (global) {
    'use strict';
    const text = global.PSL_TEXT || (global.PSL_TEXT = {});
    text.fr = {
        'shell.wordmark': 'Perf Sentinel Hub, nouvelle analyse',
        'shell.tabs': 'Écrans',
        'shell.tab.new': 'Nouvelle analyse',
        'shell.tab.recent': 'Récentes',
        'shell.tab.sources': 'Sources',
        'shell.tab.incidents': 'Incidents',
        'shell.tab.correlations': 'Corrélations',
        'shell.tab.recap': 'Récap',
        'shell.tab.disclosures': 'Divulgations',
        'shell.tab.coverage': 'Couverture',
        'shell.version.hub': 'hub',
        'shell.version.engine': 'moteur',
        'shell.version.unknown': 'inconnue',
        'shell.version.none': 'aucune',
        'shell.update.title': 'La dernière version publiée sur GitHub, lue par ce Hub à chaque vérification '
            + 'des mises à jour. Elle signale qu\'une version plus récente existe, pas que celle-ci est fautive.',
        'shell.update.news': 'mise à jour disponible',
        'shell.skew': 'flotte {oldest} → {engine}',
        'shell.identity.title': 'Établie par la connexion au Hub, ou par le proxy inverse placé devant lui. '
            + 'Il n\'y a aucun compte à gérer ici.',
        'shell.identity.access': 'Votre accès : qui vous êtes, vos groupes et ce que vous atteignez.',
        'shell.theme.auto': 'Système',
        'shell.theme.light': 'Clair',
        'shell.theme.dark': 'Sombre',
        'shell.theme.button': 'Thème : {label}. Cliquez pour passer au suivant.',
        'shell.lang.switch': 'Afficher le Hub en anglais',
        'shell.credit.tool': 'Un outil',
        'shell.credit.design': 'design et logo de',
        'help.about': 'À propos de {topic}',
        'help.label': 'Aide',
        'steps.head': '// avant de commencer',
        'terminal.copy': 'Copier',
        'terminal.copied': 'Copié',
        'terminal.copied_status': 'Copié.',
        'terminal.copy_refused.command': 'Ce navigateur a refusé la copie. La commande est sélectionnée, '
            + 'copiez-la avec votre raccourci habituel.',
        'terminal.copy_refused.file': 'Ce navigateur a refusé la copie. Le fichier est sélectionné, '
            + 'copiez-le avec votre raccourci habituel.',
        'terminal.download': 'Télécharger',
        'terminal.download_label': 'Télécharger {file}',
        'terminal.sent': '{file} est transmis à votre navigateur. C\'est lui qui choisit où l\'enregistrer, '
            + 'et il peut renommer un fichier dont le nom commence par un point.',
        'terminal.shells': 'Shell',
        'engine.need': 'Installez le binaire `perf-sentinel` sur la machine où vous lancerez la commande. Ce '
            + 'Hub fait tourner la version {version} et la commande est écrite pour elle, un moteur plus '
            + 'ancien peut refuser une option qu\'il n\'a pas encore. Prenez la même version : ',
        'engine.need_unknown': 'Installez le binaire `perf-sentinel` sur la machine où vous lancerez la '
            + 'commande. Ce Hub n\'indique aucune version du moteur, il n\'y en a donc aucune à reprendre ici : ',
        'engine.release': 'perf-sentinel {version} sur GitHub',
        'engine.releases': 'les versions publiées de perf-sentinel',
        'token.before': 'Cette source se trouve derrière un en-tête `{header}`. Lancez cette ligne dans le même '
            + 'terminal, avant la commande, avec vos propres identifiants à la place des points :',
        'token.after': 'La ligne d\'en-tête entière va dans la variable, nom compris, pas la valeur seule. Le Hub '
            + 'garde son propre en-tête pour cette source et ne le divulgue jamais. C\'est pourquoi la commande '
            + 'lit le vôtre dans une variable au lieu de porter un secret que `ps` montrerait à tous les '
            + 'utilisateurs de la machine.',
        'problem.generic': 'Le Hub a refusé la requête ({status}).',
        'problem.invalid_json': 'Le corps de la requête n\'est pas du JSON valide.',
        'problem.invalid_request': 'La requête est invalide.',
        'problem.unknown_source': 'Ce source_id est inconnu.',
        'problem.unknown_scope': 'Cet environnement ou ce source_id est inconnu.',
        'problem.unknown_caller': 'Le Hub ne sait pas qui fait la demande, et cette action se fait au nom de '
            + 'quelqu\'un.',
        'problem.foreign_origin': 'Une requête venue d\'un autre site est refusée.',
        'problem.machine_token_read_only': 'Un jeton machine ne fait que lire.',
        'problem.no_grant': 'Aucun droit de ce Hub ne vous concerne.',
        'problem.no_findings_read': 'Aucun droit ne vous donne findings.read sur ce Hub.',
        'problem.analysis_engine_missing': 'Aucun moteur d\'analyse n\'est configuré sur ce Hub.',
        'problem.detection_knob_unsupported': 'Un réglage de détection de cette requête demande un moteur '
            + 'plus récent que celui de ce Hub.',
        'problem.analysis_forbidden': 'Vous ne pouvez pas lancer cette analyse.',
        'problem.browser_session_required': 'Approuver une connexion ou gérer des jetons demande une session '
            + 'de navigateur.',
        'problem.browser_not_allowed': 'Un navigateur n\'appelle jamais cette route.',
        'problem.unknown_token': 'Ce jeton n\'existe pas.',
        'problem.session_ending': 'Votre session se termine dans l\'heure. Rechargez la page pour vous '
            + 'reconnecter.',
        'problem.invalid_user_code': 'Saisissez les huit lettres affichées par votre terminal.',
        'problem.no_pending_sign_in': 'Aucune connexion n\'attend ce code. Elle a déjà été tranchée, elle a '
            + 'expiré, ou le code a été mal saisi. Relancez perf-sentinel login.',
        'problem.too_many_user_codes': 'Trop de codes ne correspondent à aucune connexion. Patientez, puis '
            + 'réessayez.',
        'problem.actions_forbidden': 'Vous ne pouvez pas agir sur les findings.',
        'problem.action_read_only': 'Vous pouvez lire ce finding, mais pas agir dessus.',
        'problem.unknown_finding': 'Le Hub ne détient aucun finding de ce type.',
        'problem.action_exists': 'Ce finding a déjà une action. Modifiez-la avec PATCH.',
        'problem.no_action': 'Ce finding n\'a pas encore d\'action.',
        'problem.endpoint_labels_forbidden': 'Vous ne pouvez pas nommer les endpoints.',
        'problem.service_labels_forbidden': 'Vous ne pouvez pas nommer les services.',
        'problem.unknown_endpoint': 'Le Hub ne détient aucun finding sur cet endpoint.',
        'problem.unknown_service': 'Le Hub ne détient aucun finding de ce service.',
        'problem.endpoint_label_read_only': 'Vous pouvez lire cet endpoint, mais pas le nommer.',
        'problem.service_label_read_only': 'Vous pouvez lire ce service, mais pas le nommer.',
        'problem.acks_not_relayed': 'Cette source ne relaie aucun acquittement.',
        'problem.acks_forbidden': 'Vous ne pouvez pas acquitter de findings sur cette source.',
        'problem.ack_read_only': 'Vous pouvez lire ce finding sur cette source, mais pas l\'y acquitter.',
        'problem.unknown_finding_at_source': 'Le Hub ne détient aucun finding de ce type sur cette source.',
        'problem.unknown_ack_at_source': 'Le Hub ne détient aucun finding de ce type sur cette source, ni '
            + 'aucune copie de son acquittement.',
        'problem.daemon_unreachable': 'Le daemon est injoignable.',
        'problem.daemon_timeout': 'Le daemon n\'a pas répondu à temps.',
        'problem.daemon_refused_signature': 'Le daemon a refusé la signature, qu\'il ne juge pas canonique.',
        'problem.daemon_refused_credential': 'Le daemon a refusé l\'identifiant d\'acquittement du Hub.',
        'problem.daemon_ack_not_found': 'Ce finding n\'est pas acquitté sur ce daemon, ou seulement par sa '
            + 'baseline de CI, qu\'aucune API ne révoque.',
        'problem.daemon_already_acked': 'Ce finding est déjà acquitté sur le daemon ou par sa baseline de CI.',
        'problem.daemon_acks_disabled': 'Les acquittements sont désactivés sur ce daemon.',
        'problem.daemon_ack_store_full': 'Le stock d\'acquittements de ce daemon est plein.',
        'problem.daemon_unexpected_answer': 'Le daemon a donné une réponse que le Hub n\'attend pas.',
        'problem.not_a_daemon': 'Seul un daemon publie ses réglages. Cette source est un backend de traces.',
        'problem.invalid_recap_query': 'Les paramètres du récap sont invalides.',
        'problem.recap_reports_forbidden': 'Vous ne pouvez pas lire les rapports de récap.',
        'problem.invalid_card_query': 'Les paramètres des options de la carte sont invalides.',
        'problem.cards_forbidden': 'Vous ne pouvez pas créer de cartes.',
        'problem.card_sources_forbidden': 'Vous ne pouvez pas créer de cartes pour ces sources.',
        'problem.card_engine_missing': 'Le moteur de ce Hub ne sait pas encore faire de cartes.',
        'problem.cards_busy': 'Une autre carte est en cours de création. Réessayez dans quelques secondes.',
        'problem.card_not_rendered': 'Le moteur n\'a pas pu dessiner cette carte.',
        'problem.card_field_undrawable': 'La police de la carte ne sait pas dessiner un caractère de ce '
            + 'champ. Retirez-le, ou écrivez-le autrement.',
        'problem.card_organisation_undrawable': 'La police de la carte ne sait pas dessiner un caractère du '
            + 'nom de l\'organisation. Demandez à un administrateur de modifier Hub:Cards:Organisation.',
        'problem.card_domain_too_long': 'Le QR code de la carte ne peut pas contenir le domaine de cette '
            + 'organisation. Demandez à un administrateur un Hub:Cards:Domain plus court.',
        'problem.card_language_unsupported': 'Le moteur de ce Hub ne sait pas encore imprimer de cartes dans '
            + 'cette langue.',
        'problem.card_domain_missing': 'Renseignez Hub:Cards:Domain pour vérifier un rattachement.',
        'problem.card_attachment_unchecked': 'Le moteur n\'a pas pu vérifier le rattachement de cette carte.',
        'problem.card_key_forbidden': 'Vous ne pouvez pas gérer la clé de signature des cartes.',
        'problem.card_key_failed': 'Le moteur n\'a pas créé de clé de signature.',
        'problem.unknown_card_key': 'Le Hub ne détient aucune clé de carte avec cette empreinte.',
        'problem.invalid_revocation_date': 'La date de révocation doit se situer entre la création de la clé '
            + 'et aujourd\'hui.',
        'problem.disclosures_read_forbidden': 'Vous ne pouvez pas lire les divulgations.',
        'problem.disclosures_manage_forbidden': 'Vous ne pouvez pas gérer les divulgations.',
        'problem.withdrawn_not_shared': 'Une divulgation retirée ne se partage pas.',
        'problem.invalid_share_days': 'days doit être un nombre entier de jours, de 1 à 30.',
        'problem.invalid_withdraw_reason': 'reason est obligatoire, 500 caractères au plus et sans caractère '
            + 'de contrôle.',
        'problem.already_withdrawn': 'Cette divulgation est déjà retirée.',
        'problem.delivery_done': 'Cet envoi a déjà abouti.',
        'problem.disclosure_busy': 'Une divulgation est en cours de publication. Réessayez dans un instant.',
        'problem.open_period': 'Une carte couvre une période close. Choisissez-en une plus ancienne.',
        'problem.uncovered_days': 'Certains jours de cette période n\'ont pas été enregistrés, ses chiffres '
            + 'sont donc incomplets.',
        'problem.open_day': 'Certains jours de cette période sont encore en cours de décompte. Réessayez '
            + 'demain.',
        'problem.overflow': 'Certains jours de cette période comptaient trop de problèmes différents pour '
            + 'les compter un par un, ses chiffres sont donc incomplets.',
        'problem.previous_uncovered_days': 'Pas de courbe pour cette période. La courbe compare avec la '
            + 'période précédente, dont certains jours n\'ont pas été enregistrés. Une carte récit ou '
            + 'collective reste possible.',
        'problem.previous_open_day': 'Pas de courbe pour cette période. La courbe compare avec la période '
            + 'précédente, dont certains jours sont encore en cours de décompte. Une carte récit ou '
            + 'collective reste possible.',
        'problem.previous_overflow': 'Pas de courbe pour cette période. La courbe compare avec la période '
            + 'précédente, dont certains jours comptaient trop de problèmes différents pour les compter un '
            + 'par un. Une carte récit ou collective reste possible.',
        'problem.no_confirmed_fix': 'Aucune correction de cette période n\'est encore confirmée par la '
            + 'mesure.',
        'problem.unknown_fix': 'Cette correction ne fait pas partie des corrections confirmées de la '
            + 'période. Choisissez-en une dans la liste.',
        'problem.unprintable_fix': 'Le nom du service ou de l\'endpoint de cette correction est trop long ou '
            + 'contient un caractère caché, il ne peut donc pas figurer sur une carte.',
        'problem.no_total_io': 'La courbe a besoin de l\'activité totale de chaque jour, que cette période '
            + 'ou la précédente ne contient pas encore.',
        'problem.curve_needs_whole_sources': 'La courbe couvre des sources entières, et vous n\'en lisez '
            + 'qu\'une partie.',
        'problem.unprintable_scope': 'Un nom de source ou d\'environnement est trop long ou contient un '
            + 'caractère caché, il ne peut donc pas figurer sur une carte. Demandez à un administrateur de '
            + 'le renommer.',
        'problem.scope_too_long': 'Les noms d\'environnement et de source de cette carte sont trop longs '
            + 'ensemble pour y figurer. Choisissez un environnement ou une source.',
        'problem.unkept_fixes': 'Certaines corrections de cette période ont été marquées faites avant que ce '
            + 'Hub garde leur détail, il ne peut donc pas toutes les compter. Une carte récit ou courbe '
            + 'reste possible.',
        'error.run.source_unreachable': 'rien n\'a répondu à son adresse. Vérifiez que le daemon ou le backend '
            + 'est démarré et que le Hub a toujours une route vers lui, puis relancez.',
        'error.run.source_auth_failed': 'la source a répondu et refusé les identifiants du Hub. Renouvelez '
            + 'l\'en-tête d\'authentification ou la clé d\'API configurés dans le Secret de la source. Rien ne '
            + 'fonctionnera ici tant que ce n\'est pas fait.',
        'error.run.source_rejected_request': 'la source a répondu et refusé ces arguments, le plus souvent un '
            + 'nom de service inconnu ou une fenêtre qu\'elle ne conserve pas. Vérifiez le service auprès du '
            + 'backend et essayez une fenêtre plus courte.',
        'error.run.timeout': 'l\'analyse a dépassé le plafond de durée de ce Hub, ou le backend a mis trop '
            + 'longtemps à répondre. Une fenêtre large en est la cause habituelle\u00a0: divisez par deux le plafond '
            + 'de traces ou raccourcissez-la. Si l\'échec revient aussitôt quelle que soit la fenêtre, l\'adresse '
            + 'ne répond sans doute pas du tout, vérifiez l\'endpoint.',
        'error.run.output_too_large': 'la source a renvoyé plus de données qu\'une analyse n\'a le droit d\'en '
            + 'contenir. Resserrez la fenêtre ou baissez le plafond de traces pour en recevoir moins, puis '
            + 'relancez.',
        'error.run.binary_failed': 'le binaire d\'analyse a échoué pour une raison qu\'aucun autre code ne '
            + 'couvre, et rien n\'a été enregistré. Relancez-le une fois. Si l\'échec se répète, envoyez '
            + 'l\'identifiant de cette analyse à la personne qui exploite le Hub.',
        'error.run.invalid_request': 'les arguments ont été refusés avant le début de l\'analyse, donc rien n\'a '
            + 'été lu ni dépensé. Corrigez l\'identifiant de trace ou la valeur de lookback, puis soumettez de '
            + 'nouveau.',
        'error.run.hub_memory_low': 'le Hub a refusé de lancer le binaire d\'analyse\u00a0: il restait trop peu de '
            + 'mémoire à son conteneur, et le lancer quand même aurait fait tuer tout le Hub. Réessayez quand '
            + 'le Hub est plus calme, ou demandez à la personne qui l\'exploite de relever sa limite de mémoire.',
        'error.run.internal': 'le Hub lui-même a échoué, sans jamais toucher la source. Réessayez maintenant. '
            + 'S\'il échoue de la même façon, c\'est le Hub qui demande de l\'attention, pas votre requête.',
        'error.read.network_error': 'rien n\'a répondu à son adresse. Vérifiez que le daemon est démarré et que '
            + 'le Hub a toujours une route vers lui.',
        'error.read.http_error': 'le daemon a répondu avec un statut d\'erreur, il tourne donc et il est '
            + 'joignable. C\'est le daemon qui refuse la requête ou échoue à la traiter, pas un problème de '
            + 'réseau.',
        'error.read.timeout': 'le daemon n\'a pas répondu dans le délai HTTP de ce Hub. Il peut être occupé '
            + 'autant qu\'arrêté.',
        'error.read.invalid_status': 'le daemon a répondu, mais pas avec un statut que ce Hub sait lire. Son '
            + '/api/status doit porter une chaîne de version.',
        'error.read.response_too_large': 'le daemon a répondu avec plus de données que ce Hub n\'en lit d\'un '
            + 'coup. Une section `[daemon]` qui dépasse ce plafond mérite d\'être signalée.',
        'error.read.hub_busy': 'le Hub limite le nombre de lectures de daemon simultanées, et celle-ci a '
            + 'atteint la limite. Cela se résorbe en une seconde environ.',
        'error.read.invalid_findings': 'le daemon a répondu, mais pas avec une liste de findings que ce Hub '
            + 'sait lire. La cause habituelle est un daemon que ce Hub ne prend pas encore en charge.',
        'error.read.database_full': 'le daemon a répondu, mais la base de données du Hub est pleine et n\'a pas '
            + 'pu enregistrer la réponse. Agrandissez le volume du Hub ou raccourcissez Hub:Retention, puis '
            + 'cherchez une source qui crée anormalement beaucoup de signatures.',
        'error.read.storage_error': 'le daemon a répondu, mais la base de données du Hub a refusé d\'enregistrer '
            + 'la réponse. Vérifiez les journaux et le volume du Hub, pas le daemon.',
        'error.title.source_unreachable': 'Aucune connexion n\'a pu être ouverte.',
        'error.title.source_auth_failed': 'La source a refusé les identifiants du Hub.',
        'error.title.source_rejected_request': 'La source a refusé la requête.',
        'error.title.timeout': 'L\'analyse a dépassé le plafond d\'exécution.',
        'error.title.output_too_large': 'La source a renvoyé trop de données.',
        'error.title.binary_failed': 'Le binaire d\'analyse a échoué.',
        'error.title.invalid_request': 'Les arguments ont été refusés avant le début de l\'analyse.',
        'error.title.hub_memory_low': 'Il restait trop peu de mémoire au Hub pour la lancer.',
        'error.title.internal': 'Le Hub a échoué de son côté.',
        'health.database_full': 'base de données du Hub pleine',
        'health.unreachable': 'injoignable depuis {duration}',
        'health.signature_rate': '{count} nouvelles signatures dans la dernière heure',
        'health.unobserved': 'pas encore observée',
        'health.reachable': 'joignable',
        'unit.na': 'n.d.',
        'unit.s': 's',
        'unit.min': 'min',
        'unit.h': 'h',
        'unit.d': 'j',
        'unit.word.m.one': '{count} minute',
        'unit.word.m.many': '{count} minutes',
        'unit.word.h.one': '{count} heure',
        'unit.word.h.many': '{count} heures',
        'unit.word.d.one': '{count} jour',
        'unit.word.d.many': '{count} jours',
        'unit.byte': 'o',
        'unit.kb': 'Ko',
        'unit.mb': 'Mo',
        'skew.behind': '{count} {unit} de retard',
        'skew.ahead': '{count} {unit} d\'avance',
        'skew.unit.major.one': 'majeure',
        'skew.unit.major.many': 'majeures',
        'skew.unit.minor.one': 'mineure',
        'skew.unit.minor.many': 'mineures',
        'skew.unit.patch.one': 'correctif',
        'skew.unit.patch.many': 'correctifs',
        'run.args.none': 'aucun paramètre  ·  instantané de la mémoire du daemon',
        'new.sources.count': '{total} configurées',
        'new.sources.count_missing.one': '{runnable} sur {total} configurées · {missing} demande analyses.run',
        'new.sources.count_missing.many': '{runnable} sur {total} configurées · {missing} demandent '
            + 'analyses.run',
        'weight.invalid.label': 'pas un nombre',
        'weight.invalid.body': 'Une analyse demande au moins une trace. Faites glisser le curseur ou saisissez '
            + 'un nombre entre 1 et {cap}.',
        'weight.safe.label': 'confortable',
        'weight.safe.body': 'Bien en deçà de ce que le sink rend en entier. À ce nombre, le rapport est borné '
            + 'par ce que votre trafic fait de travers, pas par le sink.',
        'weight.heavy.label': 'lourd',
        'weight.heavy.body': 'Plus de traces donne plus de findings, et chacun d\'eux arrive dans le rapport. '
            + 'Les arbres de spans s\'arrêtent au plafond d\'embarquement, donc le poids en plus vient de la '
            + 'liste elle-même, pas des arbres.',
        'weight.ceiling.label': 'au plafond',
        'weight.ceiling.body': 'À ce nombre, le rapport garde encore chaque finding, et c\'est ce qui le rend '
            + 'lourd\u00a0: le fichier n\'a pas de plafond fixe et met un moment à s\'ouvrir. L\'analyse est aussi '
            + 'assez longue pour mériter d\'être surveillée face à la limite de durée de ce Hub.',
        'weight.over.label': 'au-delà du plafond strict',
        'weight.over.body': 'Le service refuse cette demande avant le début de l\'analyse. Rien n\'est lu et rien '
            + 'n\'est dépensé.',
        'daemon.monitor.api_key': 'Donnez d\'abord à `PERF_SENTINEL_DAEMON_API_KEY` la clé de lecture de ce '
            + 'daemon.',
        'daemon.monitor.header': 'Ce daemon se trouve derrière un en-tête `{header}`. `query monitor` n\'a pas '
            + 'd\'option pour lui, donc cette commande ne fonctionne que depuis un endroit qui atteint le daemon '
            + 'sans cet en-tête.',
        'sources.overline': '// sources',
        'sources.title': 'Santé de la flotte',
        'sources.sub': 'Chaque colonne est observée, sauf l\'environnement, que chaque source déclare elle-même. '
            + 'Les sources se configurent au déploiement, pas depuis cet écran.',
        'sources.hub_down': 'Le Hub ne répond pas, la santé de la flotte est donc inconnue. C\'est le Hub '
            + 'lui-même, pas une source en particulier. Rien n\'est affiché plutôt que des valeurs peut-être '
            + 'périmées.',
        'sources.col.source': 'Source',
        'sources.col.type': 'Type',
        'sources.col.env': 'Env. (déclaré)',
        'sources.col.health': 'Santé',
        'sources.col.last_success': 'Dernier succès',
        'sources.col.down_for': 'Injoignable depuis',
        'sources.col.producer': 'Producteur',
        'sources.col.last_error': 'Dernière erreur',
        'sources.group.daemons': 'daemons',
        'sources.group.backends': 'backends de traces',
        'sources.locked': 'le détail demande sources.read',
        'sources.ago': 'il y a {duration}',
        'sources.never': 'jamais',
        'sources.producer.unknown': 'inconnu',
        'sources.producer.unknown_title': 'Ce daemon publie une version de producteur, mais le Hub n\'a pas '
            + 'encore reçu de réponse réussie de sa part.',
        'sources.producer.none_title': 'Un backend de traces stocke des traces et ne détecte rien, il ne publie '
            + 'donc aucune version de producteur.',
        'sources.skew_title': 'perf-sentinel n\'a pas encore atteint la version 1.0, les détecteurs changent '
            + 'donc d\'une mineure à l\'autre. Le Hub compare deux chaînes de version et ne peut pas savoir si '
            + 'cette mineure a changé la détection.',
        'daemon.setting.environment': 'L\'étiquette que ce daemon appose sur chaque finding, qui fixe leur '
            + 'niveau de confiance\u00a0: staging se lit comme moyen, production comme élevé. Déclarée, comme la '
            + 'colonne de la ligne au-dessus.',
        'daemon.setting.sampling_rate': 'La part des traces reçues qu\'il analyse. En dessous de 100\u202f%, chaque '
            + 'agrégat d\'un instantané est un échantillon du trafic, pas le trafic.',
        'daemon.setting.max_active_traces': 'Combien de traces il corrèle en mémoire à la fois. Au-delà, la '
            + 'plus ancienne est évincée, et une trace évincée alors que des spans arrivent encore est analysée '
            + 'incomplète.',
        'daemon.setting.trace_ttl_ms': 'Combien de temps une trace attend d\'autres spans avant d\'être close et '
            + 'analysée.',
        'daemon.setting.max_events_per_trace': 'Le tampon circulaire à l\'intérieur d\'une trace. Ses spans les '
            + 'plus anciens tombent une fois qu\'il est plein, et le finding ne dit rien de ce qui est parti.',
        'daemon.setting.max_payload_size': 'Le plus grand corps de requête qu\'il accepte de désérialiser. Tout '
            + 'ce qui dépasse est refusé en entier, jamais tronqué.',
        'daemon.setting.ingest_queue_capacity': 'Les lots de spans en attente entre les récepteurs et la boucle '
            + 'd\'événements. Une file pleine renvoie la pression vers l\'émetteur sous la forme d\'un 503 OTLP.',
        'daemon.setting.analysis_queue_capacity': 'Les lots en attente de détection. Une file pleine jette des '
            + 'lots entiers, et l\'émetteur d\'un lot jeté n\'en sait rien.',
        'daemon.setting.memory_high_water_pct': 'La part de mémoire au-delà de laquelle il refuse les nouveaux '
            + 'spans plutôt que de croiser l\'OOM killer. Zéro désactive entièrement cette protection.',
        'daemon.setting.max_retained_findings': 'Les findings gardés pour l\'API de requête. Au-delà, les plus '
            + 'anciens sont évincés, donc un vieux problème peut quitter un instantané sans que personne l\'ait '
            + 'corrigé.',
        'daemon.setting.max_export_findings': 'Les findings que porte un instantané d\'export. La quality gate '
            + 'de cet instantané compte ceux-là et aucun autre.',
        'daemon.setting.max_retained_traces': 'Les arbres de spans gardés pour qu\'un export puisse les '
            + 'dessiner. Zéro n\'en garde aucun, et chaque finding de cet export s\'ouvre sans arbre.',
        'daemon.setting.per_service_labels': 'Si les séries de findings, de durées lentes et d\'I/O portent le '
            + 'service dont elles viennent. Désactivé, le label est vidé et tous les services se fondent en une '
            + 'seule série.',
        'daemon.setting.per_grouping_labels': 'La même chose pour le regroupement à côté, le namespace par '
            + 'défaut. Désactivé, ce label est vidé, et au-delà du plafond propre au daemon sur les paires '
            + '(service, regroupement), un regroupement se replie de lui-même dans `_other`.',
        'daemon.setting.api_enabled': 'Si l\'API de requête est servie. Le Hub lit ce daemon par elle, donc une '
            + 'analyse lancée d\'ici a besoin qu\'elle soit active.',
        'daemon.setting.listen_addr': 'Là où se lient les récepteurs OTLP et /metrics. Une adresse hors de la '
            + 'boucle locale expose les deux sans authentification.',
        'daemon.setting.json_socket': 'Socket Unix pour l\'ingestion NDJSON native, à côté d\'OTLP.',
        'daemon.setting.tls_configured': 'TLS sur les récepteurs OTLP. Le Hub apprend si un certificat et une '
            + 'clé sont définis, jamais où ils se trouvent.',
        'daemon.setting.ack_enabled': 'Le magasin d\'acquittements propre au daemon. Un finding acquitté reste '
            + 'dans les données et cesse de compter pour la gate.',
        'daemon.setting.ack_api_key_set': 'Si les routes d\'acquittement exigent une clé. Le Hub apprend qu\'elle '
            + 'existe, jamais ce qu\'elle vaut.',
        'daemon.setting.cors_allowed_origins': 'Les origines de navigateur auxquelles l\'API de requête répond. '
            + 'Vide, aucun en-tête CORS n\'est envoyé.',
        'daemon.setting.archive_configured': 'S\'il écrit une archive de rapport par fenêtre, qu\'une divulgation '
            + 'relira plus tard.',
        'daemon.setting.correlation_enabled': 'Si le corrélateur entre traces tourne.',
        'daemon.setting.correlation_window_ms': 'La fenêtre glissante sur laquelle deux findings comptent comme '
            + 'survenus ensemble.',
        'daemon.setting.correlation_lag_threshold_ms': 'Le plus grand écart entre deux findings qui compte '
            + 'encore comme simultané.',
        'daemon.setting.correlation_min_co_occurrences': 'Combien de fois une paire doit se produire avant '
            + 'd\'être signalée.',
        'daemon.setting.correlation_min_confidence': 'À quelle fréquence le second finding suit le premier, '
            + 'rapportée aux occurrences du premier, avant que la paire mérite d\'être signalée.',
        'daemon.setting.correlation_max_tracked_pairs': 'Plafond des paires suivies. Au-delà, les moins '
            + 'fréquentes sont évincées, et le daemon le signale plus haut quand cela arrive.',
        'daemon.setting.energy_source': 'D\'où vient le chiffre d\'énergie\u00a0: les backends de puissance qui ont '
            + 'mesuré chaque service couvert, et combien de services ont plutôt été modélisés à partir des '
            + 'compteurs d\'I/O. Calibré signifie que des facteurs de calibration ont recalé ce modèle. Suivent '
            + 'les hôtes de base de données ou de broker que le daemon compte dans ses totaux, chacun avec sa '
            + 'propre source.',
        'daemon.setting.api_version': 'La version de l\'API Electricity Maps sur laquelle ces chiffres ont été '
            + 'calculés.',
        'daemon.setting.emission_factor_type': 'Le cycle de vie compte toute la chaîne derrière l\'électricité, '
            + 'le direct ne compte que ce qu\'émet la production elle-même.',
        'daemon.setting.temporal_granularity': 'La finesse avec laquelle l\'intensité du réseau est résolue dans '
            + 'le temps.',
        'daemon.setting.electricity_maps': 'Si Electricity Maps est configuré pour l\'intensité du réseau en '
            + 'direct. Désactivé, chaque région a pris le flux de son opérateur de réseau quand il y en a un de '
            + 'configuré, ou la table embarquée, qui est un millésime et non une mesure.',
        'daemon.setting.grid_feeds': 'Les flux d\'opérateurs de réseau configurés, chacun donnant à ses régions '
            + 'une intensité en direct tirée des données de son opérateur. Ils ne comptent que les émissions '
            + 'directes, ils donnent donc moins qu\'un chiffre en cycle de vie, et Electricity Maps l\'emporte '
            + 'pour une région que les deux couvrent.',
        'daemon.setting.fingrid_dataset': 'Le jeu de données Fingrid utilisé. 266 compte la production '
            + 'finlandaise, 265 la consommation importations comprises, et les deux bases donnent des chiffres '
            + 'différents.',
        'daemon.setting.per_operation_coefficients': 'Si chaque type d\'opération porte son propre coefficient '
            + 'd\'énergie au lieu d\'une moyenne sur toutes les I/O.',
        'daemon.setting.use_hourly_profiles': 'Si le profil heure par heure du réseau est appliqué plutôt '
            + 'qu\'une moyenne plate.',
        'daemon.setting.embodied_per_request_gco2': 'Le carbone intrinsèque imputé à chaque requête, la part '
            + 'fabrication des chiffres et non l\'électricité.',
        'daemon.setting.network_energy_per_byte_kwh': 'Un coefficient que le moteur a déprécié et n\'applique '
            + 'plus, publié pour les configurations qui le définissent encore.',
        'detect.n_plus_one_min_occurrences': 'Combien de requêtes quasi identiques dans une trace comptent '
            + 'comme un N+1. Montez-le et les petites boucles cessent d\'être signalées.',
        'detect.window_duration_ms': 'À quel point ces requêtes doivent être rapprochées. Une fenêtre plus '
            + 'courte coupe une boucle lente en plusieurs groupes qui passent chacun sous le seuil.',
        'detect.slow_query_threshold_ms': 'Au-dessus de ce seuil, une opération est dite lente.',
        'detect.slow_query_min_occurrences': 'Combien de fois un modèle lent doit apparaître avant de mériter '
            + 'd\'être signalé. En dessous, une requête lente isolée reste invisible.',
        'detect.max_fanout': 'Le nombre de spans enfants sous un même parent avant que cela compte comme un '
            + 'fanout excessif. Le moteur avertit hors de 5 à 1\u202f000\u00a0: trop bas noie la liste, trop haut cache '
            + 'de vrais fan-outs.',
        'detect.chatty_service_min_calls': 'Le nombre d\'appels HTTP sortants dans une trace avant qu\'un service '
            + 'soit dit bavard. Le niveau critique se déclenche au triple.',
        'detect.pool_saturation_concurrent_threshold': 'Le pic de spans SQL simultanés sur un service avant que '
            + 'le pool de connexions soit dit à risque. Réglez-le sur la taille de pool que vous utilisez '
            + 'vraiment.',
        'detect.serialized_min_sequential': 'Le nombre d\'appels indépendants en séquence sous un même parent '
            + 'avant qu\'il vaille la peine de les paralléliser.',
        'detect.sanitizer_aware_classification': 'Comment une série de requêtes paramétrées identiques est lue '
            + 'une fois que l\'agent a masqué leurs littéraux. `auto` y voit un N+1 sur la seule portée ORM, '
            + '`strict` exige aussi que les durées s\'étalent, `never` la laisse en requête redondante, `always` '
            + 'signale un N+1.',
        'detect.sanitizer_aware_min_cv': 'À quel point ces durées doivent s\'étaler (écart type sur moyenne) '
            + 'avant que `strict` ou `auto` voient dans la série un N+1 plutôt qu\'une répétition servie depuis '
            + 'un cache. Le même seuil sert à lire un appel HTTP répété. Montez-le sur un runtime instable '
            + 'comme PHP-FPM, où les répétitions d\'une même requête en cache s\'étalent au-delà de 0,5.',
        'daemon.group.declared': '// déclaré',
        'daemon.group.declared.sub': 'Non mesuré. C\'est ce que le daemon dit de lui-même.',
        'daemon.group.ingestion': '// ingestion et mémoire',
        'daemon.group.ingestion.sub': 'Ce qu\'il reçoit, et ce qu\'il jette quand il ne suit plus.',
        'daemon.group.readers': '// ce qu\'il garde pour les lecteurs',
        'daemon.group.readers.sub': 'Les tampons circulaires derrière l\'API de requête et l\'export. Au-delà de '
            + 'chacun, le plus ancien part.',
        'daemon.group.labels': '// labels des métriques',
        'daemon.group.labels.sub': 'Comment /metrics découpe ses séries. En vider un replie son label, ce qui '
            + 'casse tout ce qui le lit déjà.',
        'daemon.group.listeners': '// récepteurs',
        'daemon.group.listeners.sub': 'Où il accepte des spans, et s\'il répond aux questions.',
        'daemon.group.subsystems': '// sous-systèmes',
        'daemon.group.subsystems.sub': 'Désactivés tant que personne ne les a activés.',
        'daemon.group.correlation': '// corrélation',
        'daemon.group.correlation.sub': 'Désactivée par défaut. Chaque champ sous le premier ne s\'applique que '
            + 'lorsqu\'elle est active.',
        'daemon.reading': 'Lecture de {name}.',
        'daemon.audience': '// pour les devops',
        'daemon.source_note': 'Publié par ce daemon via son API de requête. Le Hub le relaie et n\'en vérifie '
            + 'rien. Tout ici est en lecture seule, et chaque réglage ci-dessous se change là où le daemon est '
            + 'déployé, dans ses valeurs Helm ou son propre fichier de configuration, jamais depuis ce Hub.',
        'daemon.terminal.head': '// la même vue dans votre terminal',
        'daemon.terminal.sub': 'Les mêmes chiffres, plus les onglets que cet écran laisse de côté.',
        'daemon.terminal.copy': 'Copier la commande monitor de {name}',
        'daemon.terminal.note': '`query monitor` donne le détail de l\'énergie et du carbone que cet écran ne '
            + 'fait que résumer. Il relit à l\'intervalle choisi plus haut, et cette ligne change avec lui, donc '
            + 'le terminal et cette ligne ne sont jamais en désaccord sur la fréquence à laquelle le daemon est '
            + 'interrogé. Choisissez off et la commande perd `--refresh`, ce qui laisse au moteur son défaut de '
            + 'cinq secondes.',
        'daemon.error.returned': 'La lecture des réglages de ce daemon a renvoyé ',
        'daemon.error.meaning': '\u00a0: {meaning}',
        'daemon.error.unreached': 'le Hub n\'a pas pu l\'atteindre.',
        'daemon.error.sub': 'La ligne au-dessus montre encore le dernier état de collecte, une autre '
            + 'observation faite à un autre moment.',
        'daemon.error.retry': 'Cette ligne redemande d\'elle-même, et affiche le daemon dès qu\'il répond.',
        'daemon.verdict.ok': 'nominal',
        'daemon.verdict.near_capacity': 'proche de la capacité',
        'daemon.verdict.advised': 'conseils émis',
        'daemon.verdict.unknown': 'non mesurable',
        'daemon.now.head': '// en ce moment',
        'daemon.now.help': 'Lu sur le daemon à l\'intervalle ci-dessous. Chaque tick demande au daemon son seul '
            + 'statut, et une fois par minute l\'export complet tourne pour rafraîchir les conseils, donc '
            + 'l\'intervalle coûte une petite lecture, pas la lourde.',
        'daemon.gauge.traces': 'traces actives',
        'daemon.gauge.queue': 'file d\'analyse',
        'daemon.gauge.findings': 'findings stockés',
        'daemon.gauge.uptime': 'temps de fonctionnement',
        'daemon.unknown': 'inconnu',
        'daemon.now.lead': 'Chaque chiffre est affiché face au plafond qu\'il rencontre. En être proche n\'est '
            + 'pas un problème en soi, et cet écran ne le décide pas\u00a0: c\'est le daemon qui en décide, à partir '
            + 'de compteurs que le Hub ne voit pas.',
        'daemon.hints.head': '// ce que le daemon recommande',
        'daemon.hints.help': 'Ces conseils viennent de compteurs internes au daemon qu\'aucun rapport ni aucun '
            + 'tableau de bord ne porte. Le Hub relaie les phrases et n\'en écrit aucune.',
        'daemon.hints.sub': 'Écrit par le daemon, pas par le Hub.',
        'daemon.hints.unread': 'L\'export dont cet écran tire les conseils n\'a pas pu être lu\u00a0: `{reason}`. Ce '
            + 'que le daemon recommande en ce moment est inconnu, ce qui n\'est pas la même chose que rien.',
        'daemon.hints.none': 'Rien. Le daemon émet un conseil quand ses propres compteurs montrent qu\'un '
            + 'réglage est sous-dimensionné pour la charge qu\'il reçoit. Ce silence veut dire que ces compteurs '
            + 'étaient propres à l\'instant de la lecture, pas que les réglages sont bons.',
        'daemon.refresh.label': 'Intervalle de relecture',
        'daemon.refresh.off': 'off',
        'daemon.refresh.every': 'toutes les',
        'daemon.refresh.read': 'Lu il y a {duration}.',
        'daemon.refresh.stopped': 'Relecture arrêtée.',
        'daemon.refresh.next': 'Prochaine dans {seconds} s.',
        'daemon.settings.more': 'Les {count} réglages appliqués, et ce que ce daemon a changé',
        'daemon.settings.detects': 'Avec quoi ce daemon détecte',
        'daemon.config.api_disabled': 'Ce daemon ne sert pas sa configuration\u00a0: `api_enabled` est désactivé '
            + 'dans sa propre section `[daemon]`. Tout ce qui précède vient de l\'export.',
        'daemon.config.unreadable': 'Ce daemon a répondu pour sa configuration avec quelque chose que le Hub '
            + 'n\'a pas pu relayer, un statut d\'erreur ou un corps qui n\'est pas l\'objet `[daemon]`. Les jauges '
            + 'ci-dessus viennent du même daemon, qui répond bien.',
        'daemon.config.unanswered': 'Ce daemon n\'a pas répondu pour sa configuration, aucune n\'est donc '
            + 'affichée plutôt qu\'une copie d\'un moment antérieur.',
        'daemon.group.detection': '// seuils de détection',
        'daemon.group.detection.sub': 'Ce qui compte comme un problème. Les mêmes réglages que le lanceur '
            + 'permet de changer pour une analyse sur un backend.',
        'daemon.group.scoring': '// calcul du carbone',
        'daemon.group.scoring.sub': 'D\'où viennent les chiffres d\'énergie, pas ce qu\'ils valent.',
        'daemon.settings.preamble': 'Une valeur que ce daemon a changée est marquée, avec le défaut du moteur à '
            + 'côté. Comparée à perf-sentinel `{version}`, le binaire que ce Hub embarque. Cela couvre les '
            + 'sections `[daemon]` et `[detection]` et la moitié calcul de `[green]`. Les seuils de la gate '
            + 'sous `[thresholds]` ne sont pas publiés en section, donc une valeur définie là est réelle mais '
            + 'simplement invisible ici.',
        'daemon.settings.preamble_skew': ' Ce daemon exécute `{version}`, un défaut a donc pu bouger entre les '
            + 'deux, et une valeur marquée comme changée n\'est peut-être qu\'un autre défaut.',
        'daemon.settings.default': 'défaut {value}',
        'daemon.value.not_set': '(non défini)',
        'daemon.value.off': 'off',
        'daemon.value.none': '(aucun)',
        'daemon.value.configured': 'configuré',
        'daemon.value.unconfigured': 'non configuré',
        'daemon.value.set': 'défini',
        'daemon.value.unset': 'non défini',
        'daemon.value.yes': 'oui',
        'daemon.value.no': 'non',
        'daemon.settings.changed.one': '{count} changé',
        'daemon.settings.changed.many': '{count} changés',
        'daemon.hints.dropped.one': '{count} conseil de plus est arrivé que le Hub n\'en relaie dans une vue.',
        'daemon.hints.dropped.many': '{count} conseils de plus sont arrivés que le Hub n\'en relaie dans une '
            + 'vue.',
        'new.overline': '// nouvelle analyse',
        'new.title': 'Lancer une analyse',
        'new.no_source': 'Ce Hub n\'a aucune source configurée.',
        'new.no_grant': 'Aucun droit ne vous permet de lancer une analyse sur ce Hub. Un administrateur accorde '
            + 'l\'accès dans la configuration du Hub.',
        'new.terminal.head': '// vous préférez votre terminal\u202f?',
        'new.terminal.sub': 'La même analyse, écrite en toutes lettres.',
        'new.terminal.help': 'Le Hub exécute ce même binaire. Il manque ici la sortie JSON et la seconde '
            + 'commande qui la met en forme, qui n\'existent que pour que le Hub construise un tableau de bord. '
            + 'Un terminal n\'a besoin ni de l\'une ni de l\'autre.',
        'new.terminal.copy': 'Copier la commande d\'analyse',
        'new.terminal.same': 'C\'est la même requête que celle du bouton au-dessus, écrite avec les arguments du '
            + 'moteur. Elle tourne partout où perf-sentinel est installé et ne passe pas par ce Hub\u00a0: pas de '
            + 'worker, pas de file d\'attente, et aucun rapport gardé ici pendant {hours} heures.',
        'new.terminal.prints': 'Elle affiche ses findings dans le terminal. Il n\'y a pas de tableau de bord au '
            + 'bout, ni de lien à partager, c\'est le prix pour ne pas occuper un worker.',
        'new.terminal.trace': 'Un identifiant désigne exactement une trace, le moteur ne prend donc ici ni '
            + 'fenêtre ni plafond de traces, tout comme le formulaire au-dessus cesse de les proposer.',
        'new.terminal.fill_service': 'Renseignez le nom du service au-dessus. En l\'état, la commande en porte '
            + 'un vide, et le moteur le refuse.',
        'new.terminal.toml.one': 'Placez le `perf-sentinel.toml` ci-dessous là où vous lancez la commande. Le '
            + 'seuil que vous avez déplacé n\'a pas d\'option en ligne de commande, le moteur le lit donc dans ce '
            + 'fichier.',
        'new.terminal.toml.many': 'Placez le `perf-sentinel.toml` ci-dessous là où vous lancez la commande. Les '
            + 'seuils que vous avez déplacés n\'ont pas d\'option en ligne de commande, le moteur les lit donc '
            + 'dans ce fichier.',
        'new.toml.sub': 'Seulement les seuils que vous avez changés.',
        'new.toml.copy': 'Copier le fragment perf-sentinel.toml',
        'new.toml.defaults': 'Chaque seuil que ce fichier omet garde le défaut du moteur, et le Hub '
            + 'n\'enregistre qu\'une valeur qui s\'en écarte vraiment. Une analyse lancée depuis le bouton '
            + 'au-dessus porte les mêmes nombres, les deux sont donc comparables.',
        'new.toml.where': 'Placez-le dans le dossier d\'où vous lancez la commande. Le `-c` au-dessus le rend '
            + 'obligatoire, donc une analyse qui ne le trouve pas s\'arrête au lieu de revenir en silence aux '
            + 'défauts du moteur, qui sont justement les nombres dont vous venez de vous écarter.',
        'new.hub_down': 'Le Hub ne répond pas. C\'est le Hub lui-même et non une source en particulier, donc '
            + 'rien ne peut être lancé d\'ici tant qu\'il n\'est pas revenu. Rechargez la page dès qu\'il répond de '
            + 'nouveau.',
        'new.handoff.long': 'longue de {duration}.',
        'new.handoff.around': 'de {before} avant à {after} après.',
        'new.handoff.episode': 'd\'un épisode de corrélation de {name}, depuis l\'écran des corrélations, ',
        'new.handoff.incident': 'de l\'incident {kind} sur {name}, depuis l\'écran des incidents, ',
        'new.handoff.incident_kind': 'signalé',
        'new.handoff.window': 'Fenêtre {origin}{span}',
        'new.handoff.namespace': ' Une analyse prend un service et aucun namespace, celle-ci couvre donc '
            + '{service} dans tous les namespaces.',
        'new.handoff.daemon': ' Un daemon ne prend pas de fenêtre. Choisissez un backend de traces à gauche '
            + 'pour la lancer.',
        'new.trace_handoff.swap': 'Utiliser l\'autre trace',
        'new.trace_handoff.lead': 'Une trace de la dernière co-occurrence d\'une paire corrélée, depuis l\'écran '
            + 'des corrélations.',
        'new.trace_handoff.other': ' L\'autre trace de la paire est {trace}.',
        'new.trace_handoff.daemon': ' Un daemon ne prend pas d\'identifiant de trace. Choisissez un backend de '
            + 'traces à gauche pour la lancer.',
        'new.source.head': '// source',
        'new.source.label': 'Source',
        'new.source.note': 'Un contour en pointillés marque une valeur que la source déclare elle-même. Le Hub '
            + 'ne la mesure jamais. Un déploiement mal configuré peut étiqueter la production comme staging.',
        'new.source.aria_reachable': '{name}, joignable',
        'new.source.aria_unreachable': '{name}, injoignable',
        'new.source.producer': 'producteur {version}',
        'new.source.producer_unknown': 'producteur inconnu',
        'new.source.no_producer': 'pas de version de producteur',
        'new.params.pick': 'Choisissez une source pour voir ce qu\'elle demande.',
        'new.params.head': '// paramètres',
        'new.query.head': '// requête',
        'new.daemon.none': 'Aucun paramètre. Un instantané de daemon, c\'est ce qu\'il garde en mémoire à cet '
            + 'instant.',
        'new.daemon.sub': 'La fenêtre est son tampon circulaire, il n\'y a donc rien à élargir. Ce qu\'il garde '
            + 'et sa configuration figurent sur sa ligne dans Sources.',
        'new.daemon.open_row': 'Ouvrir sa ligne dans Sources',
        'new.field.trace': 'ID de trace',
        'new.field.trace_note': 'Un identifiant désigne exactement une trace, donc ni la fenêtre ni le plafond '
            + 'de traces ne s\'appliquent.',
        'new.field.service': 'Nom du service',
        'new.field.range': 'Période',
        'new.field.range_absolute': 'absolue, fixée à la soumission',
        'new.field.range_relative': 'relative au moment où l\'analyse démarre',
        'new.mode.label': 'Mode de sélection',
        'new.mode.service': 'Service',
        'new.mode.field': 'Choisir les traces par',
        'new.mode.gloss': 'l\'un ou l\'autre, jamais les deux',
        'new.range.last': 'Depuis {span}',
        'new.range.aria': 'Période, {window}',
        'new.range.wide': 'Une fenêtre plus large ne rapporte pas plus de données. L\'analyse s\'arrête toujours '
            + 'au plafond de traces, le résultat est donc un échantillon réparti sur la période plutôt que la '
            + 'période elle-même.',
        'new.range.timeout': 'Toute la recherche doit tenir dans le plafond de {seconds} secondes, qui est '
            + 'd\'habitude la première limite atteinte. Attendez-vous à un timeout plutôt qu\'à un résultat.',
        'new.range.retention': 'Cette source déclare garder {duration} de traces. Une fenêtre qui va au-delà '
            + 'revient incomplète, ou est refusée avec source_rejected_request.',
        'new.range.undeclared': 'Personne n\'a déclaré jusqu\'où cette source garde ses traces, le Hub ne peut '
            + 'donc pas savoir si elle peut répondre à cette fenêtre.',
        'new.picker.apply_range': 'Appliquer la période',
        'new.picker.order': 'Le début doit précéder la fin.',
        'new.picker.future': 'La fin ne peut pas être dans le futur.',
        'new.picker.selected': 'sélection de {duration}',
        'new.picker.from': 'Du',
        'new.picker.to': 'Au',
        'new.picker.custom': 'Relative sur mesure',
        'new.picker.quick': 'Périodes rapides',
        'new.picker.unit': 'Unité',
        'new.picker.apply': 'Appliquer',
        'new.picker.last': 'Depuis',
        'new.traces.label': 'Traces max.',
        'new.traces.help': 'Ce nombre et la fenêtre partent vers le backend dans une seule recherche, la '
            + 'fenêtre comme bornes de temps, ce nombre comme limite. C\'est un plafond et non un objectif, donc '
            + 'une fenêtre qui contient moins de traces en renvoie moins. Quand elle en contient plus, le '
            + 'backend choisit lesquelles et elles ne sont pas réparties uniformément, resserrez donc la '
            + 'fenêtre quand vous avez besoin de couvrir une plage précise. Les traces reviennent entières et '
            + 'non échantillonnées, ce nombre borne donc ce que coûte l\'analyse.',
        'new.traces.topic': 'traces max.',
        'new.traces.gloss': 'combien de traces reviennent, pas jusqu\'où on remonte',
        'new.weighed.head': '// ce qu\'ont pesé vos propres analyses',
        'new.weighed.sub': 'Mesuré sur cette source, pas prédit.',
        'new.weighed.row': '{traces} traces, {findings} findings, il y a {duration}',
        'new.traces.at_least': 'au moins 1',
        'new.traces.over': 'au-delà du plafond strict de {cap}, le service refusera',
        'new.traces.cap': 'plafond strict {cap}',
        'new.band.safe': 'sûr',
        'new.band.heavy': 'lourd',
        'new.band.cap': 'plafond',
        'new.sink.floor': 'Le plancher. Polices, styles et tableau de bord lui-même, présents dans chaque '
            + 'rapport, qu\'il ait trouvé un problème ou aucun.',
        'new.sink.every': 'chaque finding',
        'new.sink.every_body': 'Chaque analyse que ce Hub exécute affiche chaque finding trouvé, quelle que '
            + 'soit la taille. Le nombre du tableau de bord est le nombre trouvé.',
        'new.sink.trees': '{count} arbres',
        'new.sink.trees_body': 'Les arbres de spans embarqués, pour les findings à l\'impact cumulé le plus '
            + 'fort. Les autres s\'ouvrent sans arbre et le disent, avec l\'identifiant de trace pour lire '
            + 'celle-ci seule, ce que le mode ID de trace au-dessus lance directement. Fixé par la personne qui '
            + 'exploite ce Hub.',
        'new.sink.offenders': 'Plafond strict des principaux responsables embarqués pour l\'onglet Carbone, '
            + 'quelle que soit la taille de l\'analyse. Le classement complet reste calculé, seul l\'embarquement '
            + 'est plafonné.',
        'new.sink.no_ceiling': 'pas de plafond',
        'new.sink.no_ceiling_body': 'Le fichier n\'a pas d\'objectif de taille propre une fois chaque finding '
            + 'gardé. Une analyse qui trouve beaucoup produit un rapport qui met un moment à s\'ouvrir.',
        'new.sink.head': '// ce qui revient, et ce qui le plafonne',
        'new.sink.sub': 'D\'après le sink et les réglages de ce Hub, pas des prédictions.',
        'new.traces.ack': 'J\'accepte une analyse longue et un rapport lourd.',
        'new.skew.title': '{name} exécute {version}, {gap} sur le binaire {engine} embarqué dans le Hub.',
        'new.skew.behind': 'perf-sentinel n\'a pas encore atteint la version 1.0, les détecteurs changent donc '
            + 'd\'une mineure à l\'autre. Un détecteur ajouté dans {engine} ne tourne pas du tout sur ce '
            + 'producteur, et son absence ressemble exactement à un service sain. Lisez un faible nombre de '
            + 'findings de cette source comme non mesuré, pas comme sain.',
        'new.skew.ahead': 'Les enveloppes sont additives, rien ne casse donc. Les findings d\'un détecteur que '
            + 'ce Hub ne connaît pas arrivent sans nom. Le Hub compare deux chaînes de version et ne peut pas '
            + 'savoir si cette mineure a changé la détection.',
        'new.upgrade.get': 'Obtenir {engine}\u00a0: ',
        'new.upgrade.release': 'notes de version et binaires',
        'new.upgrade.or_chart': ', ou le chart d\'où ce daemon est déployé, ',
        'new.upgrade.charts': 'chaque version du chart et le moteur qu\'elle embarque',
        'new.upgrade.chart_is': '. Le chart lui-même est ',
        'new.upgrade.end': '.',
        'new.unreachable.title': '{name} est injoignable depuis {duration}.',
        'new.unreachable.last_success': 'Dernier contact réussi il y a {duration}.',
        'new.unreachable.returned': 'La dernière tentative a renvoyé ',
        'new.unreachable.cost': 'Lancer maintenant occupera un worker et finira presque certainement avec le '
            + 'même code.',
        'new.unreachable.anyway': 'La lancer quand même',
        'new.cost.traces': 'traces',
        'new.cost.cap': 'Plafond strict par analyse',
        'new.cost.cap_note': 'Le service refuse tout ce qui le dépasse.',
        'new.cost.timeout': 'Timeout',
        'new.cost.timeout_note': 'Ensuite, l\'analyse est tuée et marquée timeout.',
        'new.cost.workers': 'workers',
        'new.cost.queued.one': '1 tâche en attente',
        'new.cost.queued.many': '{count} tâches en attente',
        'new.cost.workers_note': 'Autant d\'analyses à la fois sur tout le Hub.',
        'new.cost.retention': 'Conservation du rapport',
        'new.cost.retention_note': 'Ensuite, le fichier est supprimé. Les liens meurent.',
        'new.cost.head': '// ce que coûte cette analyse',
        'new.cost.sub': 'Publié par le service, pas supposé.',
        'new.block.source': 'Choisissez une source.',
        'new.block.engine': 'Ce Hub n\'a aucun moteur d\'analyse configuré.',
        'new.block.unreachable': 'Confirmez que vous voulez lancer l\'analyse sur une source injoignable.',
        'new.block.trace': 'Saisissez un identifiant de trace.',
        'new.block.service': 'Saisissez un nom de service.',
        'new.block.over': 'Le plafond de traces dépasse ce que le service accepte.',
        'new.block.invalid': 'Une analyse demande au moins une trace.',
        'new.block.heavy': 'Confirmez l\'analyse longue et le rapport lourd.',
        'new.submit.daemon': 'Prend un instantané de ce que {name} garde en mémoire. Aucune requête n\'est '
            + 'envoyée à un backend de traces. {queue}',
        'new.submit.trace': 'Récupère une trace par son identifiant dans {name}. {queue}',
        'new.submit.service': 'Lit jusqu\'à {count} traces de {service} sur {window} dans {name}. {queue}',
        'new.submit.a_service': 'un service',
        'new.submit.selected_window': 'la fenêtre choisie',
        'new.submit.last': 'une période de {span} jusqu\'au lancement',
        'new.queue.none': 'Rien n\'attend avant elle.',
        'new.queue.one': 'En attente derrière 1 tâche.',
        'new.queue.many': 'En attente derrière {count} tâches.',
        'new.submit.button': 'Lancer l\'analyse',
        'new.submit.refused': 'Le Hub a refusé la requête.',
        'status.queued': 'en attente',
        'status.running': 'en cours',
        'status.succeeded': 'réussie',
        'status.failed': 'échouée',
        'status.interrupted': 'interrompue',
        'status.expired': 'expirée',
        'status.empty': 'vide',
        'run.unknown.title': 'Aucune analyse avec cet identifiant.',
        'run.unknown.sub': 'Ce Hub ne détient aucune analyse sous cet identifiant. Le lien est peut-être mal '
            + 'saisi, ou vient d\'un autre Hub.',
        'run.all': 'Toutes les analyses',
        'run.queued.headline': 'En attente d\'un worker.',
        'run.queued.sub': 'Tous les workers sont occupés. Rien n\'a encore été lu dans {name}, donc rien n\'a été '
            + 'dépensé.',
        'run.running.headline': 'Lecture de {name}.',
        'run.running.sub': 'Un worker tient cette tâche. La prochaine étape est un résultat ou un échec, sans '
            + 'rien entre les deux.',
        'run.empty.headline': 'Elle a réussi, et elle ne contient rien.',
        'run.empty.sub': 'Ce n\'est ni un échec ni une erreur. La source a répondu correctement, et sa réponse '
            + 'comptait zéro trace.',
        'run.succeeded.headline_caveat.one': '{findings} findings, et une réserve à lire d\'abord.',
        'run.succeeded.headline_caveat.many': '{findings} findings, et {caveats} réserves à lire d\'abord.',
        'run.succeeded.headline': '{findings} findings.',
        'run.succeeded.sub': 'Le rapport est prêt et sera supprimé dans {duration}.',
        'run.interrupted.headline': 'Le service a redémarré pendant l\'analyse.',
        'run.interrupted.sub': 'Elle s\'est arrêtée après {duration} de travail. Il s\'agit de la reprendre, pas '
            + 'd\'une erreur à examiner.',
        'run.expired.headline': 'Ce rapport a été supprimé.',
        'run.expired.sub': 'Les rapports vivent {hours} heures. Celui-ci a expiré il y a {duration} et le '
            + 'fichier n\'existe plus.',
        'run.failed.headline': 'Échec\u00a0: {code}.',
        'run.failed.sub': 'Par conception, le Hub n\'expose pas la sortie d\'erreur du processus. Il donne un '
            + 'code parmi huit, et voici ce que ce code signifie.',
        'run.log.accepted': 'acceptée',
        'run.log.accepted_detail': 'la requête a été validée et mise en file',
        'run.log.started': 'démarrée',
        'run.log.started_daemon': 'lecture de la mémoire du daemon',
        'run.log.started_backend': 'lecture de {kind}',
        'run.log.running': 'en cours',
        'run.log.running_detail': 'plus aucun événement avant le retour du moteur',
        'run.log.waiting': 'en attente',
        'run.log.waiting_detail': 'tous les workers sont occupés, rien n\'a encore été lu',
        'run.log.succeeded': 'réussie',
        'run.log.written_retained': 'rapport écrit, conservé {hours} h',
        'run.log.failed': 'échouée',
        'run.log.interrupted': 'interrompue',
        'run.log.interrupted_detail': 'le Hub a redémarré, l\'analyse a été abandonnée et pas rejouée',
        'run.log.written': 'rapport écrit',
        'run.log.deleted': 'supprimé',
        'run.log.deleted_detail': 'durée de conservation atteinte, le rapport n\'existe plus',
        'run.log.label': 'Événements du service',
        'run.log.head': '// événements du service',
        'run.log.note': 'Seulement ce que le Hub a réellement enregistré.',
        'run.log.closing_live': 'Le moteur ne signale rien entre le début et la fin. Il n\'y a ni pourcentage à '
            + 'afficher ni heure d\'arrivée à prédire, cet écran n\'affiche donc ni l\'un ni l\'autre. Seulement '
            + 'les événements ci-dessus, le temps passé, et le plafond auquel le service abandonne. '
            + 'Attendez-vous à une ligne de plus, pas à un flux.',
        'run.log.closing_instant': 'Cette analyse a été lue et terminée en une étape, donc chaque ligne '
            + 'ci-dessus a été écrite en même temps. C\'est un reçu de ce qui s\'est passé, pas un flux.',
        'run.log.closing': 'Chaque ligne ci-dessus est un horodatage écrit par le Hub. Rien ici n\'est '
            + 'interpolé.',
        'run.elapsed': '// durée écoulée',
        'run.ceiling.queued': 'Le plafond de {seconds} secondes démarre quand un worker prend la tâche, pas '
            + 'maintenant.',
        'run.ceiling.total': 'Temps total pendant lequel l\'analyse a occupé un worker.',
        'run.ceiling.past': 'Plafond de {seconds} s dépassé. L\'analyse aurait déjà dû être tuée et marquée '
            + 'timeout.',
        'run.ceiling.left': 'Arrêt strict à {seconds} s, puis l\'analyse est tuée et marquée timeout. Il reste '
            + '{duration} avant le plafond.',
        'run.fact.source': 'source',
        'run.fact.type': 'type',
        'run.fact.window': 'fenêtre',
        'run.fact.parameters': 'paramètres',
        'run.fact.none': 'aucun',
        'run.fact.requested_by': 'demandée par',
        'run.fact.detected_by': 'détectée par',
        'run.fact.producer': 'producteur',
        'run.fact.engine': 'moteur',
        'run.fact.unknown': 'pas encore connu',
        'run.fact.expires': 'expire',
        'run.request': '// requête',
        'run.expiry.pending': 'pas avant qu\'elle réussisse',
        'run.expiry.in': 'dans {duration}',
        'run.outcome.result': 'résultat',
        'run.outcome.gate_passed': 'La quality gate est passée. Le tableau de bord contient tout le détail.',
        'run.outcome.gate_failed': 'La quality gate n\'est pas passée. Le tableau de bord contient tout le '
            + 'détail.',
        'run.count.gate': 'quality gate',
        'run.count.findings': 'findings',
        'run.count.found': 'trouvés',
        'run.count.critical': 'critiques',
        'run.count.warning': 'avertissements',
        'run.count.info': 'infos',
        'run.count.traces': 'traces lues',
        'run.outcome.open': 'Ouvrir le tableau de bord',
        'run.outcome.open_note': 'S\'ouvre sur cette origine. Le lien meurt dans {duration}.',
        'run.outcome.empty': 'résultat vide',
        'run.outcome.empty_body': '{name} n\'avait rien à faire analyser au moteur. Le rapport existe, et il est '
            + 'vierge. L\'ouvrir montrera un tableau de bord vide. C\'est le résultat attendu, pas un défaut '
            + 'd\'affichage.',
        'run.outcome.wait_rerun': 'Attendre et la relancer',
        'run.outcome.open_blank': 'Ouvrir quand même le tableau de bord vide',
        'run.outcome.empty_note': 'Une quality gate qui passe sur zéro trace n\'a rien mesuré.',
        'run.outcome.failed_body': '{name}\u00a0: {meaning}',
        'run.outcome.unnamed': 'elle a échoué pour une raison sans nom.',
        'run.outcome.rerun': 'La relancer',
        'run.outcome.check_source': 'Vérifier la source',
        'run.outcome.failed_note': 'Rien n\'a été enregistré, donc rien n\'expire.',
        'run.outcome.resume': 'reprise',
        'run.outcome.resume_body': 'Le Hub ne rejoue jamais de lui-même une analyse interrompue. Une relance '
            + 'silencieuse pourrait envoyer une seconde requête lourde à {name} sans que personne l\'ait '
            + 'demandée, la décision vous revient donc. Les paramètres sont inchangés et prêts à repartir.',
        'run.outcome.resume_button': 'Reprendre avec les mêmes paramètres',
        'run.outcome.expired': 'expirée',
        'run.outcome.expired_body': 'La durée de conservation ne se règle pas ici. Relancer la même analyse '
            + 'produit un nouveau rapport avec un nouveau compte à rebours. Il ne reproduira pas l\'ancien, '
            + 'parce que la source a évolué depuis.',
        'run.trimmed': 'élagué',
        'run.trimmed_body': '{found} findings ont été trouvés et {kept} sont dans le rapport. Le sink a écarté '
            + 'le reste pour tenir dans son budget, les critiques en dernier, donc ce qui reste est ce qui '
            + 'comptait le plus.',
        'run.resubmit_refused': 'Le Hub a refusé la nouvelle soumission.',
        'run.hub_down': 'Le Hub ne répond pas. Cette page garde ce qu\'elle a lu en dernier et redemande.',
        'new.advanced.warning': 'Attention, réglages experts.',
        'new.advanced.head': '// réservé aux utilisateurs avancés',
        'new.advanced.what': ' · ce que l\'analyse recherche',
        'new.advanced.title': 'Pour les opérateurs qui savent ce que font ces seuils.',
        'new.advanced.body': 'Déplacés sans précaution, ils cachent de vrais problèmes ou noient le rapport '
            + 'sous le bruit, et le sens qui produit l\'un ou l\'autre change d\'un seuil à l\'autre. En cas de '
            + 'doute, n\'y touchez pas.',
        'new.advanced.lead': 'Ce sont les seuils de détection du moteur. Ils décident de ce qui compte comme un '
            + 'problème, pas de la façon dont le rapport est écrit\u00a0: en monter un n\'allège pas l\'analyse, cela '
            + 'fait seulement que le moteur cesse de signaler les cas plus petits. Une analyse enregistre ceux '
            + 'que vous avez changés, et la liste des analyses récentes signale les comptes issus de seuils '
            + 'différents, parce qu\'ils ne sont pas comparables.',
        'new.advanced.reset': 'Rétablir tous les seuils',
        'new.knob.reset': 'Remettre {name} à {value}',
        'new.toml.head': '// perf-sentinel.toml',
        'new.cost.queued.none': 'aucune tâche en attente'
    };
})(globalThis);
