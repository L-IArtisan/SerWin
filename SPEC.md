# SerWin — Cahier des charges

> Comprendre, gérer et reprendre la main sur les services et processus Windows — en connaissance de cause.

*Version du document : 1.0 — 2026-11-09*

---

## 1. Vision

Windows exécute des centaines de services et processus dont les descriptions officielles n'expliquent presque rien. Les outils existants sont soit trop techniques (`services.msc`, Process Explorer), soit dangereux (« optimiseurs » qui désactivent tout en un clic).

SerWin vise à :

1. **Démocratiser** la compréhension des services et processus : à quoi ils servent, ce qui casse si on les coupe, s'ils sont utiles *sur cette machine*.
2. **Expliquer pourquoi un service désactivé redémarre**, en identifiant les mécanismes de relance.
3. **Permettre d'agir en sécurité**, avec un filet de protection systématique (sauvegarde, historique, annulation).
4. **Descendre progressivement dans les entrailles de Windows**, du mode Simple au mode Expert.

**Principe directeur :** comprendre avant d'agir. Pas d'action de masse aveugle.

---

## 2. Public cible

| Profil | Statut | Besoins |
|---|---|---|
| **B — Utilisateur avancé / bidouilleur** | **Cible principale** | Comprendre les mécanismes (triggers, tâches, WaaSMedic, GPO), agir finement |
| **A — Curieux non technique** | Supporté via le mode Simple | Verdicts clairs, garde-fous, annulation facile |
| **C — Technicien / admin** | Hors périmètre v1 | Multi-machines, rapports, export — éventuelle version ultérieure |

### Niveaux de complexité

Un sélecteur global **Simple / Avancé / Expert** modifie à la fois :
- le **vocabulaire** des fiches (trois rédactions par service) ;
- les **actions** et onglets visibles.

- **Mode Simple par défaut** au premier lancement.
- Écran d'accueil : « Tu es plutôt : curieux / bidouilleur / expert ? » (v0.6).

---

## 3. Choix techniques

| Sujet | Décision |
|---|---|
| Langage / UI | **C# / .NET 8+ (LTS), WPF** + bibliothèque **WPF-UI** (style Fluent Windows 11) |
| Plateforme cible | Windows 11 (Famille, Pro, Entreprise) ; détection édition + build via `EditionID` / `CurrentBuild` |
| Accès système | `ServiceController` + P/Invoke `advapi32` (triggers, recovery, ACL), `Microsoft.Win32.Registry`, `TaskScheduler` (NuGet), `System.Management` (WMI), `System.Diagnostics.Eventing.Reader` (journaux) ; ETW via `Microsoft.Diagnostics.Tracing.TraceEvent` (v2) |
| Privilèges | Manifest `asInvoker` : consultation **sans droits admin**. Bouton 🛡️ « Passer en mode administrateur » → relance élevée en conservant le contexte (service sélectionné) |
| Arrière-plan | **Aucun en v1.** Chronologie reconstruite à l'ouverture à partir des journaux d'événements |
| Réseau | **Zéro connexion réseau. Aucune télémétrie.** Engagement écrit dans le README |
| Langues | **Français (source) + anglais** dès la v1. UI via `.resx`, langue suivant Windows, modifiable dans Réglages |
| Distribution | **Exécutable portable** self-contained single-file sur GitHub Releases, puis **winget**. Installateur éventuel plus tard |
| Signature | **SignPath Foundation** (gratuit pour l'open source), demandée à la première version utilisable |
| CI | **GitHub Actions** : build, tests, publication reproductible du binaire |
| Licences | Code **GPL-3.0** ; base de connaissances **CC BY-SA 4.0**. CLA pour les contributeurs externes (préserve l'option d'une future version C) |

---

## 4. Base de connaissances

### 4.1 Principes

- Un **fichier JSON par service** (et par processus système documenté), versionné dans le dépôt.
- **Intégrée à l'exécutable** (ressource embarquée) ; mise à jour avec chaque version de l'app.
- Affichage discret : « Base de connaissances : version AAAA.MM (N fiches) ».
- Les services **non documentés** affichent les données observées (description Windows, exécutable, éditeur / signature, dépendances, triggers, tâches liées) avec le badge « non documenté ».
- Une explication par IA **n'est pas prévue** (incompatible avec le zéro réseau). Réévaluable plus tard en option explicite.

### 4.2 Schéma d'une fiche (indicatif)

```json
{
  "name": "wuauserv",
  "displayName": { "fr": "Windows Update", "en": "Windows Update" },
  "category": "maintenance",
  "risk": "moyen",
  "bootCritical": false,
  "summary": { "fr": "Télécharge et installe les mises à jour Windows.", "en": "…" },
  "levels": {
    "simple":  { "fr": "…", "en": "…" },
    "avance":  { "fr": "…", "en": "…" },
    "expert":  { "fr": "…", "en": "…" }
  },
  "ifDisabled": [ { "fr": "Plus de mises à jour de sécurité", "en": "…" } ],
  "revivers": [
    { "type": "service", "target": "WaaSMedicSvc" },
    { "type": "task", "target": "\\Microsoft\\Windows\\UpdateOrchestrator\\*" },
    { "type": "trigger", "target": "network" }
  ],
  "usefulIf": { "printers": ">0" },
  "verdictIfNot": { "fr": "…", "en": "…" },
  "privacy": false,
  "defaults": {
    "Professional:26100": { "startType": "Manual", "triggers": ["…"] },
    "Core:26100":         { "startType": "Manual" }
  },
  "verification": {
    "status": "config-verified",
    "build": "26100.2314",
    "edition": "Professional",
    "date": "2026-11-12"
  }
}
```

Une fiche dont la traduction manque s'affiche dans la langue source avec le badge « non traduit ».

### 4.3 Niveaux de vérification

| Statut | Signification | Méthode |
|---|---|---|
| 📝 **Brouillon** | Rédigé à partir de la documentation publique | Rédaction (Claude), relecture (Ludovic) |
| 🔍 **Configuration vérifiée** | Triggers, dépendances, tâches et compte confirmés sur un build donné | **Automatique** via l'outil CLI `SerWin.Verify` |
| ✅ **Comportement observé** | Désactivation testée, effets et relances constatés | Manuel : Windows Sandbox (tests courts) ou machine réelle sauvegardée (observations longues) |

- L'app affiche le statut et signale « ⚠️ Fiche non vérifiée sur ta version » si le build ou l'édition diffère.
- `SerWin.Verify` sert aussi aux contributeurs (ex. utilisateurs de Windows Famille) pour soumettre leurs résultats.

**Objectifs v1.0 :** 150+ fiches services, ~50 fiches processus, **100 % en « Configuration vérifiée »**, les **30 services les plus sensibles** en « Comportement observé ».

---

## 5. Fonctionnalités

### 5.1 Interface générale

```
┌──────────────────────────────────────────────────────────────┐
│ SerWin            [🔍 Rechercher…]      Niveau: [Simple ▾]  │
├────────────┬──────────────────────┬──────────────────────────┤
│ ▸ Services │ Filtres: Tous ▾      │  Windows Update          │
│ ▸ Processus│ ● Windows Update  🟠 │  🟠 Risque moyen         │
│ ▸ Suggest. │ ● Spouleur impr.  🟢 │ [Comprendre][Qui relance]│
│ ▸ Alertes  │ ○ Xbox Live       🟢 │ [Agir][Technique]        │
│ ▸ Historique│ ● RPC            🔴 │                          │
│ ▸ Réglages │ …                    │  Sert à : …              │
└────────────┴──────────────────────┴──────────────────────────┘
```

- **Pastilles de risque** : 🟢 sûr · 🟠 prudence · 🔴 bloqué.
- **Filtres rapides** : en cours · désactivé mais relancé · tiers (non-Microsoft) · non documenté · différent du défaut Windows.
- **Onglets de la fiche service** :
  - *Comprendre* : fiche de la base, verdict « Chez toi ».
  - *Qui le relance* : analyse statique + chronologie.
  - *Agir* : actions selon le niveau.
  - *Technique* (masqué en Simple) : chemin binaire, compte, clé de registre, commande `sc` / `reg` équivalente.

### 5.2 Services — actions par niveau

| Niveau | Actions |
|---|---|
| **Simple** | Démarrer, arrêter, type de démarrage (Automatique / Manuel / Désactivé) |
| **Avancé** | **Neutralisation complète** : désactivation + désactivation des tâches planifiées liées + suppression des triggers + recovery « aucune action ». Aperçu détaillé de chaque modification avant validation |
| **Expert** | **Verrouillage ACL** de la clé `HKLM\SYSTEM\CurrentControlSet\Services\<nom>` pour empêcher la réécriture de `Start` |

Les manipulations via **TrustedInstaller** (services protégés comme WaaSMedicSvc) sont reportées en **v2**.

### 5.3 Sécurité (obligatoire dès la v0.2)

1. **Sauvegarde automatique avant chaque modification** : configuration complète du service, tâches liées, ACL → JSON.
2. **Historique** horodaté avec **Annuler** par entrée et **Tout restaurer**.
3. **Liste rouge — blocage absolu** : services indispensables au démarrage (RpcSs, DcomLaunch, LSM, etc.). Aucune exception, y compris en Expert.
4. **Liste orange** : services importants non vitaux (pare-feu, Defender…) → avertissement renforcé + confirmation explicite.
5. **Point de restauration Windows proposé** avant toute action Avancée ou Expert.
6. Liste rouge équivalente pour les processus (csrss, lsass, wininit, smss, services.exe…).

### 5.4 État de référence et données

- **Photographie complète au premier lancement** : tous les services (type de démarrage, triggers, recovery, compte), tâches liées, ACL des clés sensibles. « Tout restaurer » revient à cet état.
- **Comparaison avec les défauts Windows** (issus de la base, par édition / build) : « N services diffèrent du réglage d'usine » — détecte les modifications d'anciens « optimiseurs ».
- **Stockage** : `%ProgramData%\SerWin\` (photographie, sauvegardes, historique), en **JSON lisible** (restaurable à la main via `sc` / `reg`).
- Réglages : « Exporter l'historique » (ZIP) et « Supprimer toutes les données SerWin ».

### 5.5 Qui le relance ?

**Analyse statique (v1)** : « qui *pourrait* le relancer ? »
1. Triggers de service
2. Actions de récupération (recovery)
3. Dépendances (services dépendants)
4. Tâches planifiées référençant le service
5. Services « réparateurs » connus (ex. WaaSMedicSvc)
6. Stratégies de groupe / MDM (clés `Policies`)
7. Mécanismes connus issus de la base (logiciels tiers, mises à jour Windows)

**Chronologie (v1)** : « qui l'a *réellement* relancé ? »
- Lecture du journal **Système** : événements **7040** (changement du type de démarrage) et **7036** (changement d'état).
- Corrélation temporelle avec le journal **`Microsoft-Windows-TaskScheduler/Operational`** (désactivé par défaut → l'app propose de l'activer, action admin unique).
- Affichage type : « mardi 14:02 — type de démarrage repassé à Automatique (7040) — tâche X exécutée à 14:02 ».

**Surveillance ETW (v2)** : identification du processus exact ayant écrit la valeur `Start` ou démarré le service.

### 5.6 Processus (v1)

- Arbre **parent → enfant**.
- **svchost dépliés** : services hébergés par chaque instance.
- **Éditeur et signature** ; badge ⚠️ si non signé ou lancé depuis un emplacement suspect (`%TEMP%`, `Downloads`…).
- **Consommation CPU / RAM / disque par service**.
- Fiches de connaissance pour ~50 processus système courants.
- Terminer un processus (soumis à la liste rouge processus).

Reporté en v2 : DLL chargées, handles, connexions réseau.

### 5.7 Chez toi : contexte et suggestions

- **Détection du contexte** : imprimantes, Bluetooth, Wi-Fi, écran tactile, appartenance à un domaine, applis Xbox, Hyper-V, etc.
- Règles `usefulIf` dans chaque fiche → verdict « Chez toi : utile ✔ / inutile ».
- Écran **« Suggestions pour ta machine »** : liste des services jugés inutiles, chacun **avec son explication**, **à cocher un par un**, puis validation (passe par sauvegarde + historique).
- Catégorie dédiée **Vie privée** (télémétrie, DiagTrack…).
- **Pas de profils en un clic** (« Gaming », « PC léger »…) : contraire à la mission.

---

## 6. Feuille de route

| Jalon | Contenu |
|---|---|
| **v0.1 — Lecture seule** | Liste des services, recherche, filtres, onglets *Comprendre* et *Technique*, détection édition/build, sélecteur de niveau, **30 fiches**. Photographie au premier lancement |
| **v0.2 — Agir en sécurité** | Démarrer / arrêter / type de démarrage, élévation, sauvegarde, historique, annulation, listes rouge et orange, point de restauration |
| **v0.3 — Qui le relance** | Analyse statique, chronologie 7040/7036, activation du journal du Planificateur. Outil `SerWin.Verify` |
| **v0.4 — Processus** | Arbre, svchost dépliés, signatures, consommation par service, fiches processus |
| **v0.5 — Chez toi** | Détection du contexte, écran de suggestions, catégorie vie privée, comparaison avec les défauts Windows |
| **v0.6 — Expert** | Neutralisation complète, verrouillage ACL, écran d'accueil par profil |
| **v1.0 — Publication** | 150+ fiches FR/EN vérifiées, traduction complète de l'UI, signature SignPath, winget, README (engagement zéro réseau) |
| **v2** | ETW (coupable exact), agent de notification (zone de notification), TrustedInstaller, DLL / handles / réseau |
| **Plus tard** | Version technicien (C), vérification de mises à jour opt-in, installateur |

En continu : enrichissement de la base (rédaction Claude, vérification Ludovic).

---

## 7. Environnement de développement et de test

- **Développement directement sous Windows 11 Pro** (dual boot avec Linux).
- Installations minimales : **SDK .NET**, **Git**, **VS Code**, **Claude Code**.
- **Windows Sandbox** pour tous les tests destructifs (neutralisation, ACL, observation de relances courtes).
- Machine réelle :
  - points de restauration activés sur `C:` ;
  - **image disque complète avant la v0.2** (Clonezilla ou `dd` depuis Linux) ;
  - observations longues uniquement sur des services non critiques, après sauvegarde.
- Les variantes d'édition non testables localement (Famille, Entreprise) sont marquées « non vérifié » et confiées à la communauté via `SerWin.Verify`.

---

## 8. Structure de dépôt envisagée

```
SerWin/
├── SPEC.md
├── README.md
├── LICENSE                     (GPL-3.0)
├── src/
│   ├── SerWin.App/             (WPF, UI)
│   ├── SerWin.Core/            (accès SCM, registre, tâches, journaux, processus)
│   ├── SerWin.Knowledge/       (chargement / validation de la base)
│   └── SerWin.Verify/          (CLI de vérification des fiches)
├── knowledge/
│   ├── LICENSE                 (CC BY-SA 4.0)
│   ├── schema.json
│   ├── services/*.json
│   └── processes/*.json
├── tests/
└── .github/workflows/
```

---

## 9. Hors périmètre (explicitement exclu de la v1)

- Profils d'optimisation en un clic
- Toute connexion réseau, télémétrie, mise à jour automatique
- Explications générées par IA
- Gestion multi-machines / à distance
- Programme résident en arrière-plan
- Manipulations TrustedInstaller
- Inspection DLL / handles / réseau des processus
