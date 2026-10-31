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
            + 'utilisateurs de la machine.'
    };
})(globalThis);
