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
            + 'reste possible.'
    };
})(globalThis);
